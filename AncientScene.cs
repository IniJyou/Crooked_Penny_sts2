using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CrookedPenny;

internal sealed class AncientScene(AncientEventModel ancient, EventOption[] options) : ICoinScene
{
    private static readonly ConditionalWeakTable<AncientEventModel, AncientScene> Scenes = new();
    private static readonly AsyncLocal<EventOption?> Executing = new();
    private static readonly AsyncLocal<AncientEventModel?> DeferringEnd = new();
    private static readonly Func<EventOption, Func<Task>?> Callback = AccessTools.MethodDelegate<Func<EventOption, Func<Task>?>>(
        AccessTools.PropertyGetter(typeof(EventOption), "OnChosen"));
    private static readonly Action<EventOption, bool> MarkChosen = AccessTools.MethodDelegate<Action<EventOption, bool>>(
        AccessTools.PropertySetter(typeof(EventOption), nameof(EventOption.WasChosen)));
    private static readonly Action<AncientEventModel> Done = AccessTools.MethodDelegate<Action<AncientEventModel>>(
        AccessTools.Method(typeof(AncientEventModel), "Done"));
    private static readonly Action<EventSynchronizer, Player, EventOption> SaveChoice =
        AccessTools.MethodDelegate<Action<EventSynchronizer, Player, EventOption>>(
            AccessTools.Method(typeof(EventSynchronizer), "SaveEventOptionToHistory"));
    private static readonly AccessTools.FieldRef<EventSynchronizer, List<Task>> PendingTasks =
        AccessTools.FieldRefAccess<EventSynchronizer, List<Task>>("_pendingOptionTasks");

    private CoinKey Key => new(CoinScene.Ancient, ancient.Owner!.NetId);
    private CoinResult _result;
    private int _round = 1;
    private int _choices = 2;
    private bool _waiting;
    private bool _busy;
    private bool _startedVanilla;
    private VBoxContainer? _panel;
    private NEventRoom? _screen;
    public bool IsBusy => _busy;

    public void Rejected()
    {
        _waiting = false;
        Render();
    }

    public void BeforeFlip()
    {
        if (!RunManager.Instance.ShouldSave || ancient.Owner!.RunState.CurrentRoom is not EventRoom room)
            return;

        var snapshot = RunManager.Instance.ToSave(room);
        snapshot.PreFinishedRoom!.IsPreFinished = false;
        var entries = RunManager.Instance.EventSynchronizer.Events.ToDictionary(model => model.Owner!.NetId,
            model => model.IsFinished ? "finished" : "choosing");
        CoinCheckpoint.Save(CoinScene.Ancient, snapshot, entries, ancient.Owner.NetId);
    }

    public bool CanApply(CoinCommand command)
    {
        if (command.Player != ancient.Owner!.NetId || ancient.IsFinished || _startedVanilla || _busy)
            return false;

        return command.Action switch
        {
            CoinAction.Flip => _result == CoinResult.Pending,
            CoinAction.Claim or CoinAction.Pass => _result == CoinResult.Lost,
            CoinAction.Select => _result == CoinResult.Won && command.Round == _round &&
                command.Option >= 0 && command.Option < options.Length && !options[command.Option].IsLocked,
            _ => false
        };
    }

    public void Apply(CoinCommand command)
    {
        _waiting = false;
        if (command.Action == CoinAction.Flip)
        {
            _result = command.Won ? CoinResult.Won : CoinResult.Lost;
            _choices = command.Multiplier;
            Render();
            return;
        }

        _busy = true;
        Render();
        var task = TaskHelper.RunSafely(Settle(command));
        PendingTasks(RunManager.Instance.EventSynchronizer).Add(task);
    }

    private async Task Settle(CoinCommand command)
    {
        try
        {
            if (_result == CoinResult.Lost)
            {
                if (command.Action == CoinAction.Claim)
                    await PlayerCmd.GainGold(1, ancient.Owner!);
                Done(ancient);
                return;
            }

            var option = options[command.Option];
            MarkChosen(option, true);
            Executing.Value = option;
            DeferringEnd.Value = ancient;
            try
            {
                await Callback(option)!();
                SaveChoice(RunManager.Instance.EventSynchronizer, ancient.Owner!, option);
            }
            finally
            {
                Executing.Value = null;
                DeferringEnd.Value = null;
            }

            if (_round++ == _choices)
                Done(ancient);
        }
        finally
        {
            _busy = false;
            Render();
            CoinNetwork.Drain(Key);
        }
    }

    private void Attach(NEventRoom screen)
    {
        if (_screen == screen)
            return;

        _screen = screen;
        var box = new PanelContainer
        {
            AnchorLeft = 1, AnchorRight = 1, OffsetLeft = -467, OffsetRight = -30, OffsetTop = 150
        };
        _panel = new VBoxContainer();
        box.AddChild(_panel);
        screen.AddChild(box);
        Render();
    }

    private void Render()
    {
        if (_panel is not { } panel || !GodotObject.IsInstanceValid(panel))
            return;

        panel.GetParent<Control>().Visible = !ancient.IsFinished && !_startedVanilla;
        foreach (var child in panel.GetChildren())
        {
            panel.RemoveChild(child);
            child.QueueFree();
        }

        if (ancient.IsFinished || _startedVanilla)
            return;

        var flip = CoinUi.FlipButton(CoinScene.Ancient);
        if (_result == CoinResult.Pending)
            flip.Pressed += () => Request(CoinAction.Flip);
        else
        {
            CoinUi.ShowResult(flip, _result);
            _screen?.Layout?.DisableEventOptions();
            _screen?.Layout?.ClearOptions();
        }
        flip.Disabled |= _waiting;
        panel.AddChild(flip);

        if (_result == CoinResult.Lost)
        {
            AddAction(CoinUi.Text("领取 1 金币", "Claim 1 gold"), CoinAction.Claim);
            AddAction(CoinUi.Text("跳过金币", "Skip the gold"), CoinAction.Pass);
        }
        else if (_result == CoinResult.Won)
        {
            panel.AddChild(new Label { Text = CoinUi.Text($"第 {_round}/{_choices} 次选择", $"Choice {_round}/{_choices}") });
            for (var i = 0; i < options.Length; i++)
            {
                if (options[i].IsLocked)
                    continue;

                var button = AddAction(options[i].Title.GetFormattedText(), CoinAction.Select, i);
                button.Icon = options[i].Relic?.Icon;
                button.ExpandIcon = true;
                button.TooltipText = options[i].Description.GetFormattedText();
            }
        }
    }

    private Button AddAction(string label, CoinAction action, int option = 0)
    {
        var button = CoinUi.CreateButton(label);
        button.Disabled = _busy || _waiting;
        button.Pressed += () => Request(action, option);
        _panel!.AddChild(button);
        return button;
    }

    private void Request(CoinAction action, int option = 0)
    {
        if (_waiting || !CanApply(new CoinCommand(ancient.Owner!.NetId, action, option, _round)))
            return;

        _waiting = true;
        CoinNetwork.Request(Key, action, option, _round);
        Render();
    }

    [HarmonyPatch(typeof(AncientEventModel), "SetInitialEventState")]
    private static class TrackOptions
    {
        private static void Postfix(AncientEventModel __instance)
        {
            if (__instance.IsFinished || !__instance.CurrentOptions.Any(option => option.Relic != null && !option.IsLocked))
                return;

            var scene = Scenes.GetValue(__instance, ancient => new AncientScene(ancient, ancient.CurrentOptions.ToArray()));
            CoinNetwork.Register(scene.Key, scene);
        }
    }

    [HarmonyPatch(typeof(AncientEventModel), "BeforeEventStarted")]
    private static class SkipRepeatHealing
    {
        private static bool Prefix(AncientEventModel __instance, ref Task __result)
        {
            var owner = __instance.Owner!;
            if (!CoinCheckpoint.IsRestoring(CoinScene.Ancient, owner.RunState))
                return true;

            __result = Task.CompletedTask;
            return false;
        }
    }

    [HarmonyPatch(typeof(AncientEventModel), "SetInitialEventState")]
    private static class RestoreFinishedChoice
    {
        private static void Prefix(AncientEventModel __instance, ref bool isPreFinished)
        {
            var owner = __instance.Owner!;
            if (CoinCheckpoint.Take(CoinScene.Ancient, owner.NetId, owner.RunState) == "finished")
                isPreFinished = true;
        }
    }

    [HarmonyPatch(typeof(NEventRoom), "SetOptions")]
    private static class AttachPanel
    {
        private static void Postfix(NEventRoom __instance, EventModel eventModel)
        {
            if (eventModel is AncientEventModel ancient && Scenes.TryGetValue(ancient, out var scene))
                scene.Attach(__instance);
        }
    }

    [HarmonyPatch(typeof(EventSynchronizer), "ChooseOptionForEvent")]
    private static class GuardVanillaChoice
    {
        private static bool Prefix(EventSynchronizer __instance, Player player)
        {
            if (__instance.Events.FirstOrDefault(model => model.Owner == player) is not AncientEventModel ancient ||
                !Scenes.TryGetValue(ancient, out var scene))
                return true;

            if (scene._waiting || scene._result != CoinResult.Pending)
                return false;

            scene._startedVanilla = true;
            scene.Render();
            return true;
        }
    }

    [HarmonyPatch(typeof(AncientEventModel), "Done")]
    private static class DelayDone
    {
        private static bool Prefix(AncientEventModel __instance) => DeferringEnd.Value != __instance;
    }

    [HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), [typeof(RelicModel), typeof(Player), typeof(int)])]
    private static class CopyChosenRelic
    {
        private static void Prefix(ref RelicModel relic)
        {
            if (ReferenceEquals(Executing.Value?.Relic, relic))
                relic = (RelicModel)relic.ClonePreservingMutability();
        }
    }
}
