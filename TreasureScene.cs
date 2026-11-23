using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CrookedPenny;

internal sealed class TreasureScene(TreasureRoomRelicSynchronizer synchronizer, IRunState run) : ICoinScene
{
    private static readonly ConditionalWeakTable<TreasureRoomRelicSynchronizer, TreasureScene> Active = new();
    private static readonly Action<TreasureRoomRelicSynchronizer> EndVanillaVoting =
        AccessTools.MethodDelegate<Action<TreasureRoomRelicSynchronizer>>(
            AccessTools.Method(typeof(TreasureRoomRelicSynchronizer), "EndRelicVoting"));
    private static readonly CoinKey Key = new(CoinScene.Treasure, 0);
    private static readonly RelicPickingFightMove[] FightMoves = Enum.GetValues<RelicPickingFightMove>();
    private const int SlotsPerPage = 16;

    private readonly List<RelicModel?> _relics = synchronizer.CurrentRelics!.Cast<RelicModel?>().ToList();
    private readonly HashSet<ulong> _decided = [];
    private readonly Dictionary<ulong, int> _votes = [];
    private readonly TaskCompletionSource _finished = new();
    private SortedSet<int>? _availableSlots;
    private int _page;
    private int _round;
    private bool _settling;
    private bool _waiting;
    private bool _lost;
    private bool _hasFlipped;
    private int _lastMultiplier;
    private VBoxContainer? _view;
    private string? _lastRound;
    private bool IsSolo => run.Players.Count == 1;
    public bool IsBusy => _settling;

    public void Rejected()
    {
        _waiting = false;
        Render();
    }

    public void BeforeFlip()
    {
        if (_hasFlipped || !RunManager.Instance.ShouldSave)
            return;

        var snapshot = RunManager.Instance.ToSave(null);
        snapshot.PreFinishedRoom = new SerializableRoom { RoomType = RoomType.Treasure };
        var ids = _relics.Select(relic => CoinCheckpoint.Serialize(relic!.Id)).ToArray();
        CoinCheckpoint.Save(CoinScene.Treasure, snapshot, new Dictionary<ulong, string> { [0] = System.Text.Json.JsonSerializer.Serialize(ids) });
    }

    public bool CanApply(CoinCommand command)
    {
        if (_finished.Task.IsCompleted || _lost || _settling || !run.Players.Any(player => player.NetId == command.Player))
            return false;

        if (_round == 0)
            return command.Action is CoinAction.Flip or CoinAction.Pass && !_decided.Contains(command.Player);

        return command.Round == _round && !_votes.ContainsKey(command.Player) &&
            (command.Action == CoinAction.Pass && run.Players.Count == 1 ||
            command.Action == CoinAction.Select && command.Option >= 0 && command.Option < _relics.Count && _relics[command.Option] != null);
    }

    public void Apply(CoinCommand command)
    {
        if (command.Player == RunManager.Instance.NetService.NetId)
            _waiting = false;

        if (_round == 0)
        {
            _decided.Add(command.Player);
            _hasFlipped |= command.Action == CoinAction.Flip;
            if (command.Action == CoinAction.Flip && !command.Won)
            {
                _lost = true;
                _relics.Clear();
                Render();
                TaskHelper.RunSafely(FinishFailure());
                return;
            }

            if (command.Action == CoinAction.Flip)
                _lastMultiplier = command.Multiplier;

            if (command.Action == CoinAction.Flip && command.Multiplier > 1)
            {
                var originals = _relics.ToArray();
                _relics.EnsureCapacity(originals.Length * command.Multiplier);
                for (var copy = 1; copy < command.Multiplier; copy++)
                    _relics.AddRange(originals);
            }

            if (_decided.Count == run.Players.Count)
            {
                _availableSlots = new SortedSet<int>(Enumerable.Range(0, _relics.Count));
                _page = 0;
                _round = 1;
            }
        }
        else if (command.Action == CoinAction.Pass)
        {
            Finish();
        }
        else
        {
            _votes.Add(command.Player, command.Option);
            if (_votes.Count == run.Players.Count)
            {
                _settling = true;
                TaskHelper.RunSafely(AllocateRound(command.Seed));
            }
        }

        Render();
    }

    private async Task FinishFailure()
    {
        if (run.Players.Count == 1)
            return; // The local player can claim the coin or continue.

        foreach (var player in run.Players)
            await PlayerCmd.GainGold(1, player);

        Finish();
    }

    private async Task AllocateRound(int seed)
    {
        var rng = new MegaCrit.Sts2.Core.Random.Rng((ulong)seed);
        var groups = run.Players.GroupBy(player => _votes[player.NetId]).OrderBy(group => group.Key);
        var assigned = new Dictionary<ulong, (Player Player, int Slot)>();
        foreach (var group in groups)
        {
            var contenders = group.ToList();
            var winner = contenders.Count == 1 ? contenders[0] : RelicPickingResult.GenerateRelicFight(
                contenders, _relics[group.Key]!, group.Key, () => rng.NextItem(FightMoves)).player!;
            assigned.Add(winner.NetId, (winner, group.Key));
        }

        if (assigned.Count < run.Players.Count)
        {
            var chosenSlots = assigned.Values.Select(award => award.Slot).ToHashSet();
            var leftovers = _availableSlots!.Where(slot => !chosenSlots.Contains(slot)).ToList();
            foreach (var player in run.Players.Where(player => !assigned.ContainsKey(player.NetId)))
            {
                if (leftovers.Count == 0)
                    break;
                var index = rng.NextInt(leftovers.Count);
                assigned.Add(player.NetId, (player, leftovers[index]));
                leftovers.RemoveAt(index);
            }
        }

        var received = new List<string>();
        foreach (var player in run.Players)
        {
            if (!assigned.TryGetValue(player.NetId, out var award))
                continue;

            var original = _relics[award.Slot]!;
            _relics[award.Slot] = null;
            _availableSlots!.Remove(award.Slot);
            await RelicCmd.Obtain(original.ToMutable(), award.Player);
            foreach (var teammate in run.Players.Where(other => other != award.Player))
                teammate.RelicGrabBag.MoveToFallback(original);

            if (player.NetId == RunManager.Instance.NetService.NetId && _view != null)
                received.Add(original.Title.GetFormattedText());
        }

        if (received.Count > 0)
            _lastRound = string.Join(", ", received);
        _settling = false;
        _votes.Clear();
        _round++;
        if (_availableSlots!.Count == 0)
            Finish();
        else
            Render();
        CoinNetwork.Drain(Key);
    }

    private void Finish()
    {
        EndVanillaVoting(synchronizer);
        _finished.TrySetResult();
    }

    private void Attach(NTreasureRoomRelicCollection collection)
    {
        var box = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -340, OffsetRight = 340, OffsetTop = -210, OffsetBottom = 210
        };
        CoinUi.StylePanel(box);
        _view = new VBoxContainer();
        _view.AddThemeConstantOverride("separation", 6);
        box.AddChild(_view);
        collection.AddChild(box);
        Render();
        _view.FindChildren("*", "Button", true, false).OfType<Button>()
            .FirstOrDefault(button => !button.Disabled)?.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void Render()
    {
        if (_view is not { } view || !GodotObject.IsInstanceValid(view))
            return;

        foreach (var child in view.GetChildren())
        {
            view.RemoveChild(child);
            child.QueueFree();
        }

        var localId = RunManager.Instance.NetService.NetId;
        if (_lost)
        {
            var result = CoinUi.CreateLabel(CoinUi.Text("掷空", "Lost the flip"), accent: true);
            result.HorizontalAlignment = HorizontalAlignment.Center;
            view.AddChild(result);
            if (run.Players.Count == 1)
            {
                AddButton(view, CoinUi.Text("领取 1 金币", "Claim 1 gold"), async () =>
                {
                    if (_settling || _finished.Task.IsCompleted)
                        return;
                    _settling = true;
                    await PlayerCmd.GainGold(1, run.Players[0]);
                    Finish();
                });
                AddButton(view, CoinUi.Text("继续", "Continue"), () => { if (!_settling) Finish(); });
            }
            else
                view.AddChild(CoinUi.CreateLabel(CoinUi.Text("每位玩家获得 1 金币。", "Each player receives 1 gold.")));

            return;
        }

        if (_round == 0)
        {
            var summary = CoinUi.CreateLabel(CoinUi.Text($"已决定 {_decided.Count}/{run.Players.Count} · {_relics.Count} 件遗物",
                $"Decided {_decided.Count}/{run.Players.Count} · {_relics.Count} relics"), accent: true);
            summary.HorizontalAlignment = HorizontalAlignment.Center;
            view.AddChild(summary);
            var flip = CoinUi.AddFlipButton(view, CoinScene.Treasure);
            flip.Disabled = _waiting || _decided.Contains(localId);
            flip.Pressed += () => Request(CoinAction.Flip);
            AddButton(view, CoinUi.Text("跳过弯币", "Skip the coin"), () => Request(CoinAction.Pass), flip.Disabled);
        }
        else
        {
            var summary = CoinUi.CreateLabel(CoinUi.Text($"第 {_round} 轮 · 已选择 {_votes.Count}/{run.Players.Count}",
                $"Round {_round} · Chosen {_votes.Count}/{run.Players.Count}"), accent: true);
            summary.HorizontalAlignment = HorizontalAlignment.Center;
            view.AddChild(summary);
            if (_lastRound != null)
                view.AddChild(CoinUi.CreateLabel(CoinUi.Text($"上一轮获得 {_lastRound}", $"Received in the last round: {_lastRound}")));
        }

        if (_lastMultiplier > 0)
            view.AddChild(CoinUi.CreateLabel(CoinUi.Text($"最近一次掷中 ×{_lastMultiplier}", $"Last successful flip ×{_lastMultiplier}"), accent: true));

        RenderSlots(view, localId);
    }

    private void RenderSlots(VBoxContainer view, ulong localId)
    {
        var count = _availableSlots?.Count ?? _relics.Count;
        var pageCount = Math.Max(1, (count + SlotsPerPage - 1) / SlotsPerPage);
        _page = Math.Clamp(_page, 0, pageCount - 1);

        if (pageCount > 1)
        {
            var pages = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            AddButton(pages, CoinUi.Text("上一页", "Previous"), () => { _page--; Render(); }, _page == 0);
            pages.AddChild(CoinUi.CreateLabel(CoinUi.Text($"{_page + 1}/{pageCount} 页 · {count} 件", $"{_page + 1}/{pageCount} · {count} relics")));
            AddButton(pages, CoinUi.Text("下一页", "Next"), () => { _page++; Render(); }, _page == pageCount - 1);
            view.AddChild(pages);
        }

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 280), SizeFlagsVertical = Control.SizeFlags.ExpandFill, FollowFocus = true };
        var list = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(list);
        view.AddChild(scroll);
        IEnumerable<int> slots = _availableSlots is { } available ? available : Enumerable.Range(0, _relics.Count);
        foreach (var slot in slots.Skip(_page * SlotsPerPage).Take(SlotsPerPage))
        {
            var relic = _relics[slot]!;
            var button = AddButton(list, relic.Title.GetFormattedText(), () => Request(CoinAction.Select, slot),
                _round == 0 || _settling || _waiting || _votes.ContainsKey(localId));
            button.Icon = relic.Icon;
            button.ExpandIcon = true;
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.TooltipText = relic.DynamicDescription.GetFormattedText();
        }
    }

    private void Request(CoinAction action, int slot = 0)
    {
        var localId = RunManager.Instance.NetService.NetId;
        if (_waiting || !CanApply(new CoinCommand(localId, action, slot, _round)))
            return;

        _waiting = true;
        CoinNetwork.Request(Key, action, slot, _round);
        Render();
    }

    private static Button AddButton(Container parent, string label, Action pressed, bool disabled = false)
    {
        var button = CoinUi.CreateButton(label);
        button.Disabled = disabled;
        button.Pressed += pressed;
        parent.AddChild(button);
        return button;
    }

    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.BeginRelicPicking))]
    private static class TrackTreasure
    {
        private static bool Prefix(IRunState ____runState, ref List<RelicModel>? ____currentRelics)
        {
            if (CoinCheckpoint.Take(CoinScene.Treasure, 0, ____runState) is not { } data)
                return true;

            var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(data)!;
            ____currentRelics = ids.Select(id => ModelDb.GetById<RelicModel>(CoinCheckpoint.Deserialize<ModelId>(id))).ToList();
            return false;
        }

        private static void Postfix(TreasureRoomRelicSynchronizer __instance, IRunState ____runState)
        {
            Active.Remove(__instance);
            if (__instance.CurrentRelics is not { Count: > 0 })
                return;

            var scene = new TreasureScene(__instance, ____runState);
            Active.Add(__instance, scene);
            CoinNetwork.Register(Key, scene);
        }
    }

    [HarmonyPatch(typeof(TreasureRoom), nameof(TreasureRoom.DoNormalRewards))]
    private static class PreserveOpenedChestRewards
    {
        private static bool Prefix(ref Task<int> __result)
        {
            if (!CoinCheckpoint.IsRestoring(CoinScene.Treasure, RunManager.Instance.DebugOnlyGetState()))
                return true;

            __result = Task.FromResult(0);
            return false;
        }
    }

    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), nameof(NTreasureRoomRelicCollection.InitializeRelics))]
    private static class AddTreasureView
    {
        private static bool Prefix(NTreasureRoomRelicCollection __instance, List<Control> ____holderLayouts)
        {
            if (!Active.TryGetValue(RunManager.Instance.TreasureRoomRelicSynchronizer, out var scene))
                return true;

            foreach (var layout in ____holderLayouts)
                layout.Hide();
            scene.Attach(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), nameof(NTreasureRoomRelicCollection.RelicPickingFinished))]
    private static class WaitForCoins
    {
        private static bool Prefix(ref Task __result)
        {
            if (!Active.TryGetValue(RunManager.Instance.TreasureRoomRelicSynchronizer, out var scene))
                return true;

            __result = scene._finished.Task;
            return false;
        }
    }

    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.SkipRelicLocally))]
    private static class SoloContinue
    {
        private static bool Prefix(TreasureRoomRelicSynchronizer __instance)
        {
            if (!Active.TryGetValue(__instance, out var scene) || !scene.IsSolo)
                return true;

            if (scene._lost && !scene._settling)
                scene.Finish();
            else
                scene.Request(CoinAction.Pass);
            return false;
        }
    }
}
