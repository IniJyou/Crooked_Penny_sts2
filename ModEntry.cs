using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace CrookedPenny;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public static void Initialize()
    {
        CoinSettings.Register();
        new Harmony("CrookedPenny").PatchAll(typeof(ModEntry).Assembly);
    }
}
