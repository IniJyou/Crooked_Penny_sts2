using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CrookedPenny;

internal static class EventRewardCheckpoint
{
    private sealed record Entry(string FinishText, string? Rewards);
    private static readonly Action<EventModel, LocString> Finish = AccessTools.MethodDelegate<Action<EventModel, LocString>>(
        AccessTools.Method(typeof(EventModel), "SetEventFinished"));

    internal static void Save(EventRoom room, RewardsSet set, IEnumerable<RewardsSet> knownBatches)
    {
        if (room.CanonicalEvent is AncientEventModel)
            return;

        var manager = RunManager.Instance;
        var entries = new Dictionary<ulong, string>();
        foreach (var model in manager.EventSynchronizer.Events)
        {
            var owner = model.Owner!;
            var batch = owner == set.Player ? set : knownBatches.Where(candidate => candidate.Player == owner &&
                candidate.Room == null && !manager.RewardsSetSynchronizer.IsRewardsSetCompleted(candidate)).MaxBy(candidate => candidate.Id);
            var finish = model.IsFinished ? model.Description?.LocEntryKey : batch == null ? null : FinishText(model.Id.Entry, batch);
            if (finish == null)
                continue;

            entries[owner.NetId] = JsonSerializer.Serialize(new Entry(finish,
                batch == null ? null : RewardCheckpoint.Capture(batch.Rewards)));
        }

        if (entries.ContainsKey(set.Player.NetId))
            CoinCheckpoint.Save(CoinScene.EventRewards, manager.ToSave(room), entries, set.Player.NetId);
    }

    // Events with further gameplay after the reward screen require their own continuation.
    private static string? FinishText(string entry, RewardsSet batch) => entry switch
    {
        "BRAIN_LEECH" => "BRAIN_LEECH.pages.RIP.description",
        "COLORFUL_PHILOSOPHERS" => "COLORFUL_PHILOSOPHERS.pages.DONE.description",
        "DROWNING_BEACON" => "DROWNING_BEACON.pages.BOTTLE.description",
        "POTION_COURIER" => batch.Rewards.Count == 1
            ? "POTION_COURIER.pages.RANSACK.description" : "POTION_COURIER.pages.GRAB_POTIONS.description",
        "PUNCH_OFF" => "PUNCH_OFF.pages.NAB.description",
        "THE_FUTURE_OF_POTIONS" => "THE_FUTURE_OF_POTIONS.pages.DONE.description",
        "THE_LEGENDS_WERE_TRUE" => "THE_LEGENDS_WERE_TRUE.pages.SLOWLY_FIND_AN_EXIT.description",
        "WELLSPRING" => "WELLSPRING.pages.BOTTLE.description",
        "WHISPERING_HOLLOW" => "WHISPERING_HOLLOW.pages.GOLD.description",
        "WUPPY_IN_TROUBLE" => "WUPPY_IN_TROUBLE.pages.IGNORE.description",
        _ => null
    };

    private static readonly Dictionary<EventModel, Entry> Resuming = new(ReferenceEqualityComparer.Instance);

    [HarmonyPatch(typeof(EventModel), "SetInitialEventState")]
    private static class RestoreEventPage
    {
        private static bool Prefix(EventModel __instance)
        {
            var owner = __instance.Owner!;
            if (CoinCheckpoint.Take(CoinScene.EventRewards, owner.NetId, owner.RunState) is not { } data)
                return true;

            var entry = JsonSerializer.Deserialize<Entry>(data)!;
            Resuming[__instance] = entry;
            Finish(__instance, new LocString(__instance.LocTable, entry.FinishText));
            return false;
        }
    }

    [HarmonyPatch(typeof(EventRoom), nameof(EventRoom.EnterInternal))]
    private static class ReopenRewardScreen
    {
        private static void Postfix(ref Task __result, IRunState? runState)
        {
            if (CoinCheckpoint.IsRestoring(CoinScene.EventRewards, runState))
                __result = ResumeAfterEntry(__result);
        }

        private static async Task ResumeAfterEntry(Task original)
        {
            await original;
            foreach (var model in RunManager.Instance.EventSynchronizer.Events)
            {
                if (!Resuming.Remove(model, out var entry) || entry.Rewards == null)
                    continue;

                _ = TaskHelper.RunSafely(RewardCheckpoint.ForEvent(entry.Rewards, model.Owner!).Offer());
            }
        }
    }
}
