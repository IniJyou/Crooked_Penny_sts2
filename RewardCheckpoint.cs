using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CrookedPenny;

internal static class RewardCheckpoint
{
    private sealed record Item(string Kind, string? Data = null, string[]? Cards = null,
        Item[]? Linked = null, bool CanReroll = false, bool CanSkip = true);

    private static readonly Action<RewardsSet, AbstractRoom> AssignRoom =
        AccessTools.MethodDelegate<Action<RewardsSet, AbstractRoom>>(
            AccessTools.PropertySetter(typeof(RewardsSet), nameof(RewardsSet.Room)));
    private static readonly AccessTools.FieldRef<RewardsSet, bool> Generated =
        AccessTools.FieldRefAccess<RewardsSet, bool>("_isGenerated");

    internal static string Capture(IEnumerable<Reward> rewards) => JsonSerializer.Serialize(
        rewards.Where(reward => !reward.SuccessfullySelected).Select(CaptureItem).ToArray());

    internal static List<Reward> Restore(string data, Player player, RoomType roomType) =>
        JsonSerializer.Deserialize<Item[]>(data)!.Select(item => RestoreItem(item, player, roomType)).ToList();

    internal static RewardsSet ForEvent(string data, Player player)
    {
        var set = new RewardsSet(player).WithCustomRewards(Restore(data, player, RoomType.Event));
        Generated(set) = true;
        return set;
    }

    private static Item CaptureItem(Reward reward) => reward switch
    {
        CardReward card => new Item("cards", Cards: card.Cards.Select(model => CoinCheckpoint.Serialize(model.ToSerializable())).ToArray(),
            CanReroll: card.CanReroll, CanSkip: card.CanSkip),
        RelicReward { Relic: { } relic } => new Item("relic", CoinCheckpoint.Serialize(relic.ToSerializable())),
        PotionReward { Potion: { } potion } => new Item("potion", CoinCheckpoint.Serialize(potion.Id)),
        LinkedRewardSet linked => new Item("linked", Linked: linked.Rewards.Select(CaptureItem).ToArray()),
        _ => new Item("vanilla", CoinCheckpoint.Serialize(reward.ToSerializable()))
    };

    private static Reward RestoreItem(Item item, Player player, RoomType roomType)
    {
        return item.Kind switch
        {
            "cards" => RestoreCards(item, player, roomType),
            "relic" => new RelicReward(RelicModel.FromSerializable(CoinCheckpoint.Deserialize<SerializableRelic>(item.Data!)), player),
            "potion" => new PotionReward(ModelDb.GetById<PotionModel>(CoinCheckpoint.Deserialize<ModelId>(item.Data!)).ToMutable(), player),
            "linked" => new LinkedRewardSet(item.Linked!.Select(child => RestoreItem(child, player, roomType)).ToList(), player),
            _ => Reward.FromSerializable(CoinCheckpoint.Deserialize<SerializableReward>(item.Data!), player)
        };
    }

    private static CardReward RestoreCards(Item item, Player player, RoomType roomType)
    {
        var cards = item.Cards!.Select(data => player.RunState.LoadCard(CoinCheckpoint.Deserialize<SerializableCard>(data), player));
        var options = roomType == RoomType.Event
            ? CardCreationOptions.ForNonCombatWithDefaultOdds([player.Character.CardPool])
            : CardCreationOptions.ForRoom(player, roomType).WithFlags(CardCreationFlags.IsFromCombat);
        return new CardReward(cards, options.Source, player, options)
        {
            CanReroll = item.CanReroll,
            CanSkip = item.CanSkip
        };
    }

    [HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.WithRewardsFromRoom))]
    private static class RestoreCombatRewards
    {
        private static bool Prefix(RewardsSet __instance, AbstractRoom room, ref RewardsSet __result)
        {
            if (room is not CombatRoom { IsPreFinished: true } ||
                CoinCheckpoint.Take(CoinScene.Rewards, __instance.Player.NetId, __instance.Player.RunState) is not { } data)
                return true;

            AssignRoom(__instance, room);
            __instance.Rewards.AddRange(Restore(data, __instance.Player, room.RoomType));
            Generated(__instance) = true;
            __result = __instance;
            return false;
        }
    }
}
