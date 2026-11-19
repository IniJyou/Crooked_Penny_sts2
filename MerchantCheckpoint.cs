using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CrookedPenny;

internal static class MerchantCheckpoint
{
    private sealed record Entry(string? Item, int Cost, bool OnSale = false, bool Used = false);
    private sealed record RestoreState(Entry[] Entries, SerializablePlayerRngSet PlayerRng,
        SerializableRelicGrabBag PlayerRelics, SerializableRelicGrabBag SharedRelics);

    private static readonly AccessTools.FieldRef<MerchantEntry, int> Cost =
        AccessTools.FieldRefAccess<MerchantEntry, int>("_cost");
    private static readonly Action<MerchantCardEntry, CardCreationResult?> SetCard =
        AccessTools.MethodDelegate<Action<MerchantCardEntry, CardCreationResult?>>(
            AccessTools.PropertySetter(typeof(MerchantCardEntry), nameof(MerchantCardEntry.CreationResult)));
    private static readonly Action<MerchantCardEntry, bool> SetSale =
        AccessTools.MethodDelegate<Action<MerchantCardEntry, bool>>(
            AccessTools.PropertySetter(typeof(MerchantCardEntry), nameof(MerchantCardEntry.IsOnSale)));
    private static readonly Action<MerchantRelicEntry, RelicModel?> SetRelic =
        AccessTools.MethodDelegate<Action<MerchantRelicEntry, RelicModel?>>(
            AccessTools.PropertySetter(typeof(MerchantRelicEntry), nameof(MerchantRelicEntry.Model)));
    private static readonly Action<MerchantPotionEntry, PotionModel?> SetPotion =
        AccessTools.MethodDelegate<Action<MerchantPotionEntry, PotionModel?>>(
            AccessTools.PropertySetter(typeof(MerchantPotionEntry), nameof(MerchantPotionEntry.Model)));

    internal static string Capture(MerchantInventory inventory) => JsonSerializer.Serialize(inventory.AllEntries.Select(entry => entry switch
    {
        MerchantCardEntry card => new Entry(card.CreationResult is { } result ? CoinCheckpoint.Serialize(result.Card.ToSerializable()) : null,
            Cost(card), OnSale: card.IsOnSale),
        MerchantRelicEntry relic => new Entry(relic.Model is { } model ? CoinCheckpoint.Serialize(model.ToSerializable()) : null, Cost(relic)),
        MerchantPotionEntry potion => new Entry(potion.Model is { } model ? CoinCheckpoint.Serialize(model.Id) : null, Cost(potion)),
        MerchantCardRemovalEntry removal => new Entry(null, Cost(removal), Used: removal.Used),
        _ => new Entry(null, Cost(entry))
    }).ToArray());

    [HarmonyPatch(typeof(MerchantInventory), nameof(MerchantInventory.CreateForNormalMerchant))]
    private static class RestoreMerchant
    {
        private static void Prefix(Player player, out RestoreState? __state)
        {
            __state = null;
            if (CoinCheckpoint.Take(CoinScene.Merchant, player.NetId, player.RunState) is not { } data)
                return;

            __state = new RestoreState(JsonSerializer.Deserialize<Entry[]>(data)!, player.PlayerRng.ToSerializable(),
                player.RelicGrabBag.ToSerializable(), player.RunState.SharedRelicGrabBag.ToSerializable());
        }

        private static void Postfix(MerchantInventory __result, Player player, RestoreState? __state)
        {
            if (__state == null)
                return;

            player.PlayerRng.LoadFromSerializable(__state.PlayerRng);
            player.RelicGrabBag.LoadFromSerializable(__state.PlayerRelics);
            player.RunState.SharedRelicGrabBag.LoadFromSerializable(__state.SharedRelics);
            foreach (var (entry, saved) in __result.AllEntries.Zip(__state.Entries))
            {
                Cost(entry) = saved.Cost;
                switch (entry)
                {
                    case MerchantCardEntry card:
                        SetCard(card, saved.Item == null ? null : new CardCreationResult(
                            player.RunState.LoadCard(CoinCheckpoint.Deserialize<SerializableCard>(saved.Item), player)));
                        SetSale(card, saved.OnSale);
                        break;
                    case MerchantRelicEntry relic:
                        SetRelic(relic, saved.Item == null ? null : RelicModel.FromSerializable(CoinCheckpoint.Deserialize<SerializableRelic>(saved.Item)));
                        break;
                    case MerchantPotionEntry potion:
                        SetPotion(potion, saved.Item == null ? null : ModelDb.GetById<PotionModel>(CoinCheckpoint.Deserialize<ModelId>(saved.Item)).ToMutable());
                        break;
                    case MerchantCardRemovalEntry removal when saved.Used:
                        removal.SetUsed();
                        break;
                }
            }
        }
    }
}
