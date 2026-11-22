using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CrookedPenny;

internal sealed class RewardScene(RewardsSet set) : ICoinScene
{
    private static readonly ConditionalWeakTable<RewardsSet, RewardScene> Batches = new();
    private static readonly Action<NRewardsScreen> UpdateScreen = AccessTools.MethodDelegate<Action<NRewardsScreen>>(
        AccessTools.Method(typeof(NRewardsScreen), "UpdateScreenState"));
    private static readonly AccessTools.FieldRef<NRewardsScreen, List<Control>> Rows =
        AccessTools.FieldRefAccess<NRewardsScreen, List<Control>>("_rewardButtons");
    private static readonly AccessTools.FieldRef<NRewardsScreen, List<Control>> SkippedRows =
        AccessTools.FieldRefAccess<NRewardsScreen, List<Control>>("_skippedRewardButtons");

    private CoinKey Key => new(CoinScene.Rewards, set.Player.NetId, set.Id);
    private NRewardsScreen? _screen;
    private Button? _button;
    private bool _waiting;
    private CoinResult _result;

    public void Rejected()
    {
        _waiting = false;
        if (_button is { } button && GodotObject.IsInstanceValid(button))
            button.Disabled = _result != CoinResult.Pending;
    }

    public void BeforeFlip()
    {
        if (!RunManager.Instance.ShouldSave)
            return;

        if (set.Room is not CombatRoom combat)
        {
            if (set.Player.RunState.CurrentRoom is EventRoom room)
                EventRewardCheckpoint.Save(room, set, Batches.Select(pair => pair.Key));
            return;
        }

        var snapshot = RunManager.Instance.ToSave(null);
        snapshot.PreFinishedRoom = new SerializableRoom
        {
            RoomType = combat.RoomType, EncounterId = combat.Encounter.Id, IsPreFinished = true,
            GoldProportion = combat.GoldProportion, ParentEventId = combat.ParentEventId,
            ShouldResumeParentEvent = combat.ShouldResumeParentEventAfterCombat,
            EncounterState = combat.Encounter.SaveCustomState()
        };
        var entries = new Dictionary<ulong, string>();
        foreach (var pair in Batches.Where(pair => ReferenceEquals(pair.Key.Room, combat)))
            entries[pair.Key.Player.NetId] = RewardCheckpoint.Capture(pair.Key.Rewards);
        CoinCheckpoint.Save(CoinScene.Rewards, snapshot, entries, set.Player.NetId);
    }

    public bool CanApply(CoinCommand command) => command.Action == CoinAction.Flip && command.Player == set.Player.NetId &&
        _result == CoinResult.Pending && set.Rewards.Any(reward => !reward.SuccessfullySelected);

    public void Apply(CoinCommand command)
    {
        var pending = set.Rewards.Where(reward => !reward.SuccessfullySelected).ToArray();
        Reward[] additions = command.Won
            ? pending.Where(RewardCopies.CanCopy)
                .SelectMany(reward => Enumerable.Range(1, command.Multiplier - 1).Select(_ => RewardCopies.Copy(reward))).ToArray()
            : [new GoldReward(1, set.Player)];

        _result = command.Won ? CoinResult.Won : CoinResult.Lost;
        set.Rewards.AddRange(additions);
        if (!command.Won)
        {
            foreach (var reward in pending)
            {
                set.Rewards.Remove(reward);
                reward.OnSkipped();
            }
        }

        if (_screen is { } screen && GodotObject.IsInstanceValid(screen) && _button is { } button)
        {
            foreach (var reward in additions)
                AddRow(screen, reward);

            if (!command.Won)
            {
                // Add the replacement before removing old rows so the screen never sees an empty batch.
                var discarded = Rows(screen).Where(row => row != Rows(screen)[^1]).ToArray();
                foreach (var row in discarded)
                {
                    SkippedRows(screen).Remove(row);
                    screen.RewardCollectedFrom(row);
                }
            }

            UpdateScreen(screen);
            CoinUi.ShowResult(button, _result);
        }

        Log.Info($"[CrookedPenny] Reward set {set.Id}: {_result}, {additions.Length} added.");
    }

    private void Attach(NRewardsScreen screen)
    {
        if (set.Rewards.All(reward => reward.SuccessfullySelected))
            return;

        _screen = screen;
        var container = screen.GetNode<Control>("%RewardsContainer");
        var scene = (set.Room ?? set.Player.RunState.CurrentRoom) is EventRoom ? CoinScene.EventRewards : CoinScene.Rewards;
        var coinArea = new Control
        {
            AnchorLeft = 0.74f, AnchorRight = 0.74f, AnchorTop = 0.23f, AnchorBottom = 0.23f,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        screen.AddChild(coinArea);
        _button = CoinUi.AddFlipButton(coinArea, scene);
        screen.Completed += coinArea.Hide;
        if (_result != CoinResult.Pending)
            CoinUi.ShowResult(_button, _result);
        _button.Pressed += () =>
        {
            if (_waiting || NOverlayStack.Instance?.Peek() != screen || Rows(screen).Count == 0 ||
                container.FindChildren("*", "", true, false).OfType<NRewardButton>().Any(row => !row.IsEnabled))
                return;

            _waiting = true;
            _button.Disabled = true;
            CoinNetwork.Request(Key);
        };
    }

    [HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward))]
    private static class WaitForFlip
    {
        private static bool Prefix(Reward reward, ref Task<bool> __result)
        {
            var waiting = Batches.Any(pair => pair.Value._waiting && pair.Value._result == CoinResult.Pending &&
                (pair.Key.Rewards.Contains(reward) || reward.ParentRewardSet != null && pair.Key.Rewards.Contains(reward.ParentRewardSet)));
            if (!waiting)
                return true;

            __result = Task.FromResult(false);
            return false;
        }
    }

    private static void AddRow(NRewardsScreen screen, Reward reward)
    {
        Control row;
        if (reward is LinkedRewardSet linked)
        {
            var linkedRow = NLinkedRewardSet.Create(linked, screen);
            linkedRow.RewardClaimed += screen.RewardCollectedFrom;
            row = linkedRow;
        }
        else
        {
            var rewardRow = NRewardButton.Create(reward, screen);
            rewardRow.RewardClaimed += screen.RewardCollectedFrom;
            rewardRow.RewardSkipped += screen.RewardSkippedFrom;
            row = rewardRow;
        }

        reward.MarkContentAsSeen();
        Rows(screen).Add(row);
        screen.GetNode<Control>("%RewardsContainer").AddChild(row);
    }

    [HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.BeginRewardsSet))]
    private static class TrackBatch
    {
        private static void Postfix(RewardsSet set)
        {
            if (set.Rewards.Count == 0)
                return;

            var scene = Batches.GetValue(set, static value => new RewardScene(value));
            CoinNetwork.Register(scene.Key, scene);
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen._Ready))]
    private static class AttachButton
    {
        private static void Postfix(NRewardsScreen __instance, RewardsSet ____rewardsSet)
        {
            if (Batches.TryGetValue(____rewardsSet, out var scene))
                scene.Attach(__instance);
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "UpdateScreenState")]
    private static class RefreshButton
    {
        private static void Postfix(NRewardsScreen __instance, RewardsSet ____rewardsSet, List<Control> ____rewardButtons)
        {
            if (!Batches.TryGetValue(____rewardsSet, out var scene) || scene._button is not { } button)
                return;

            button.Visible = ____rewardButtons.Count > 0;
            if (button.Visible && !button.Disabled)
            {
                var first = ____rewardButtons[0];
                button.FocusNeighborBottom = first.GetPath();
                first.FocusNeighborTop = button.GetPath();
            }
        }
    }
}
