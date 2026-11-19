using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace CrookedPenny;

internal static class RewardCopies
{
    private static readonly AccessTools.FieldRef<SpecialCardReward, CardModel> SpecialCard =
        AccessTools.FieldRefAccess<SpecialCardReward, CardModel>("_card");
    private static readonly Func<CardReward, CardCreationOptions> GetRerollOptions =
        AccessTools.MethodDelegate<Func<CardReward, CardCreationOptions>>(
            AccessTools.PropertyGetter(typeof(CardReward), "RerollOptions"));

    // Keep linked choices linked. A group containing unsupported rewards can't be copied as a whole.
    internal static bool CanCopy(Reward reward) => reward.IsPopulated && reward switch
    {
        GoldReward or PotionReward or RelicReward or CardReward or SpecialCardReward => true,
        LinkedRewardSet linked => linked.Rewards.Count > 0 && linked.Rewards.All(CanCopy),
        _ => false
    };

    internal static Reward Copy(Reward reward)
    {
        var player = reward.Player;
        switch (reward)
        {
            case GoldReward gold:
                return new GoldReward(gold.Amount, player, gold.ToSerializable().WasGoldStolenBack);
            case PotionReward { Potion: { } potion }:
                return new PotionReward((PotionModel)potion.ClonePreservingMutability(), player);
            case RelicReward { Relic: { } relic }:
                return new RelicReward((RelicModel)relic.ClonePreservingMutability(), player);
            case CardReward card:
                var options = GetRerollOptions(card);
                return new CardReward(card.Cards.Select(player.RunState.CloneCard), options.Source, player, options)
                {
                    CanReroll = card.CanReroll,
                    CanSkip = card.CanSkip
                };
            case SpecialCardReward special:
                var specialCopy = new SpecialCardReward(player.RunState.CloneCard(SpecialCard(special)), player);
                var descriptionSource = special.ToSerializable().CustomDescriptionEncounterSourceId;
                if (descriptionSource != ModelId.none)
                    specialCopy.SetCustomDescriptionEncounterSource(descriptionSource);
                return specialCopy;
            case LinkedRewardSet linked:
                return new LinkedRewardSet(linked.Rewards.Select(Copy).ToList(), player);
            default:
                throw new ArgumentException($"Cannot copy reward {reward.GetType().Name}.", nameof(reward));
        }
    }
}
