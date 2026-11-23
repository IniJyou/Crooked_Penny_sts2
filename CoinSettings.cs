using System.Runtime.CompilerServices;
using BaseLib.Abstracts;
using BaseLib.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace CrookedPenny;

internal readonly record struct CoinRules(int SuccessPercent, int Multiplier, bool RandomMultiplier)
{
    internal const int MinimumRandomMultiplier = 2;
    internal const int MaximumMultiplier = 10;

    internal static CoinRules From(int successPercent, int multiplier, bool randomMultiplier) =>
        new(Math.Clamp(successPercent, 0, 100), Math.Clamp(multiplier, 1, MaximumMultiplier), randomMultiplier);
}

public sealed class CoinSettings : SimpleModConfig
{
    private static int _successPercent = 50;
    private static int _multiplier = 2;

    [ConfigSlider(0, 100, Format = "{0}%")]
    public static int SuccessPercent
    {
        get => _successPercent;
        set => _successPercent = Math.Clamp(value, 0, 100);
    }

    public static bool RandomMultiplier { get; set; }

    [ConfigSlider(1, CoinRules.MaximumMultiplier, Format = "×{0}")]
    public static int RewardMultiplier
    {
        get => _multiplier;
        set => _multiplier = Math.Clamp(value, 1, CoinRules.MaximumMultiplier);
    }

    internal static CoinRules Current => new(SuccessPercent, RewardMultiplier, RandomMultiplier);

    internal static void Register()
    {
        var config = new CoinSettings();
        config.ConfigChanged += (_, _) => CoinRulesSync.Synchronize();
        config.OnConfigReloaded += CoinRulesSync.Synchronize;
        ModConfigRegistry.Register("CrookedPenny", config);
    }
}

internal static class CoinRulesSync
{
    private sealed class SessionRules { internal CoinRules? Rules; }
    private static readonly ConditionalWeakTable<NetClientGameService, SessionRules> HostSettings = new();

    internal static CoinRules? ForDisplay => RunManager.Instance.NetService is NetClientGameService client
        ? HostSettings.GetOrCreateValue(client).Rules : CoinSettings.Current;

    internal static void Synchronize()
    {
        if (RunManager.Instance.NetService is NetHostGameService { IsConnected: true })
            CustomMessageWrapper.Send(new CoinRulesMessage(CoinSettings.Current));
    }

    internal static void Receive(CoinRulesMessage message, ulong sender)
    {
        switch (RunManager.Instance.NetService)
        {
            case NetHostGameService host when message.Request:
                host.SendMessage(new CustomMessageWrapper { Message = new CoinRulesMessage(CoinSettings.Current) }, sender);
                break;
            case NetClientGameService client when !message.Request && client.HostNetId == sender:
                HostSettings.GetOrCreateValue(client).Rules = message.Rules;
                break;
        }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
    private static class ShareRulesOnLaunch
    {
        private static void Postfix(RunManager __instance)
        {
            if (__instance.NetService is NetClientGameService)
                CustomMessageWrapper.Send(new CoinRulesMessage());
            else
                Synchronize();
        }
    }
}

public sealed class CoinRulesMessage : ICustomMessage
{
    private const int RandomMultiplierBit = 1 << 4;
    internal bool Request { get; private set; } = true;
    internal CoinRules Rules { get; private set; }
    public bool ShouldBroadcast => false;

    public CoinRulesMessage() { }

    internal CoinRulesMessage(CoinRules rules)
    {
        Request = false;
        Rules = rules;
    }

    public void HandleMessage(ulong senderId) => CoinRulesSync.Receive(this, senderId);

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(Request);
        if (Request)
            return;

        writer.WriteInt(Rules.SuccessPercent, 8);
        // Use the spare bit in the multiplier field to keep the rules packet the same size.
        writer.WriteInt(Rules.Multiplier | (Rules.RandomMultiplier ? RandomMultiplierBit : 0), 5);
    }

    public void Deserialize(PacketReader reader)
    {
        Request = reader.ReadBool();
        if (Request)
            return;

        var chance = reader.ReadInt(8);
        var multiplier = reader.ReadInt(5);
        Rules = CoinRules.From(chance, multiplier & (RandomMultiplierBit - 1), (multiplier & RandomMultiplierBit) != 0);
    }
}
