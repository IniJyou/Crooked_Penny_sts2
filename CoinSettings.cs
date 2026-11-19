using System.Runtime.CompilerServices;
using BaseLib.Abstracts;
using BaseLib.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace CrookedPenny;

internal readonly record struct CoinRules(int SuccessPercent, int Multiplier)
{
    internal static CoinRules From(int successPercent, int multiplier) =>
        new(Math.Clamp(successPercent, 0, 100), Math.Clamp(multiplier, 1, 10));
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

    [ConfigSlider(1, 10, Format = "×{0}")]
    public static int RewardMultiplier
    {
        get => _multiplier;
        set => _multiplier = Math.Clamp(value, 1, 10);
    }

    internal static CoinRules Current => new(SuccessPercent, RewardMultiplier);

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
        writer.WriteInt(Rules.Multiplier, 5);
    }

    public void Deserialize(PacketReader reader)
    {
        Request = reader.ReadBool();
        if (!Request)
            Rules = CoinRules.From(reader.ReadInt(8), reader.ReadInt(5));
    }
}
