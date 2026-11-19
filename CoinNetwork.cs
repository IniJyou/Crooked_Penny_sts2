using System.Security.Cryptography;
using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace CrookedPenny;

internal enum CoinScene { Rewards, Merchant, Ancient, Treasure, EventRewards }
internal enum CoinAction { Flip, Pass, Select, Claim, RemoveCard, Rejected }
internal enum CoinResult { Pending, Won, Lost }

internal readonly record struct CoinKey(CoinScene Scene, ulong Owner, int Batch = 0);
internal readonly record struct CoinCommand(ulong Player, CoinAction Action, int Option, int Round, bool Won = false, int Seed = 0)
{
    internal int Multiplier { get; init; } = 2;
}

internal interface ICoinScene
{
    bool CanApply(CoinCommand command);
    void Apply(CoinCommand command);
    bool IsBusy => false;
    void BeforeFlip() { }
    void Rejected() { }
}

internal static class CoinNetwork
{
    private static readonly Dictionary<CoinKey, ICoinScene> Scenes = [];
    private static readonly Dictionary<CoinKey, List<(CoinMessage Message, ulong Sender)>> Waiting = [];
    private static INetGameService? _service;
    private static RunLocation _location;

    internal static void Register(CoinKey key, ICoinScene scene)
    {
        Refresh();
        Scenes[key] = scene;
        Drain(key);
    }

    internal static void Drain(CoinKey key)
    {
        Refresh();
        if (Waiting.Remove(key, out var messages))
            foreach (var (message, sender) in messages)
                Receive(message, sender);
    }

    internal static void Request(CoinKey key, CoinAction action = CoinAction.Flip, int option = 0, int round = 0)
    {
        Refresh();
        var message = new CoinMessage(_location, key, new CoinCommand(_service!.NetId, action, option, round), false);
        if (_service.Type == NetGameType.Client)
            CustomTargetedMessageWrapper.Send(message);
        else
            Receive(message, _service.NetId);
    }

    internal static void Receive(CoinMessage message, ulong sender)
    {
        Refresh();
        if (!message.Location.Equals(_location) || _service == null)
            return;

        if (message.Confirmed ? _service is not NetClientGameService client || sender != client.HostNetId :
            _service.Type is not (NetGameType.Host or NetGameType.Singleplayer) || sender != message.Command.Player)
            return;

        if (!Scenes.TryGetValue(message.Key, out var scene) || scene.IsBusy)
        {
            if (!Waiting.TryGetValue(message.Key, out var messages))
                Waiting.Add(message.Key, messages = []);
            messages.Add((message, sender));
            return;
        }

        if (message.Confirmed && message.Command.Action == CoinAction.Rejected)
        {
            scene.Rejected();
            return;
        }

        if (!scene.CanApply(message.Command))
        {
            if (!message.Confirmed)
                Reject(message, scene);
            return;
        }

        if (message.Confirmed)
        {
            scene.Apply(message.Command);
            return;
        }

        if (message.Command.Action == CoinAction.Flip)
        {
            try
            {
                scene.BeforeFlip();
            }
            catch (Exception error)
            {
                Log.Error($"[CrookedPenny] Could not save the before-flip checkpoint: {error}");
                Reject(message, scene);
                return;
            }
        }

        var command = message.Command with
        {
            Won = message.Command.Action == CoinAction.Flip && FlipCoin(),
            Seed = RandomNumberGenerator.GetInt32(int.MaxValue),
            Multiplier = CoinSettings.Current.Multiplier
        };
        if (_service.Type == NetGameType.Host)
            CustomTargetedMessageWrapper.Send(new CoinMessage(_location, message.Key, command, true));

        scene.Apply(command);
    }

    private static bool FlipCoin() => RandomNumberGenerator.GetInt32(100) < CoinSettings.Current.SuccessPercent;

    private static void Reject(CoinMessage request, ICoinScene scene)
    {
        var target = request.Command.Player;
        if (target == _service!.NetId)
        {
            scene.Rejected();
            return;
        }

        var refusal = new CoinMessage(_location, request.Key, request.Command with { Action = CoinAction.Rejected }, true);
        _service.SendMessage(new CustomTargetedMessageWrapper { Message = refusal }, target);
    }

    private static void Refresh()
    {
        var run = RunManager.Instance;
        var location = run.DebugOnlyGetState()?.RunLocation ?? run.RunLocationTargetedBuffer?.CurrentLocation ?? default;
        if (ReferenceEquals(_service, run.NetService) && _location.Equals(location))
            return;

        _service = run.NetService;
        _location = location;
        Scenes.Clear();
        Waiting.Clear();
    }
}

public sealed class CoinMessage : ICustomTargetedMessage
{
    internal CoinKey Key { get; private set; }
    internal CoinCommand Command { get; private set; }
    internal bool Confirmed { get; private set; }
    public RunLocation Location { get; private set; }
    public bool IsRewardMessage => Key.Scene == CoinScene.Rewards;
    public bool ShouldBroadcast => false;

    public CoinMessage() { }

    internal CoinMessage(RunLocation location, CoinKey key, CoinCommand command, bool confirmed)
    {
        Location = location;
        Key = key;
        Command = command;
        Confirmed = confirmed;
    }

    public void HandleMessage(ulong senderId) => CoinNetwork.Receive(this, senderId);

    public void Serialize(PacketWriter writer)
    {
        writer.Write(Location);
        writer.WriteInt((int)Key.Scene);
        writer.WriteULong(Key.Owner);
        writer.WriteInt(Key.Batch);
        writer.WriteULong(Command.Player);
        writer.WriteInt((int)Command.Action);
        writer.WriteInt(Command.Option);
        writer.WriteInt(Command.Round);
        writer.WriteBool(Command.Won);
        writer.WriteInt(Command.Seed);
        writer.WriteInt(Command.Multiplier, 5);
        writer.WriteBool(Confirmed);
    }

    public void Deserialize(PacketReader reader)
    {
        Location = reader.Read<RunLocation>();
        Key = new CoinKey((CoinScene)reader.ReadInt(), reader.ReadULong(), reader.ReadInt());
        Command = new CoinCommand(reader.ReadULong(), (CoinAction)reader.ReadInt(), reader.ReadInt(), reader.ReadInt(), reader.ReadBool(), reader.ReadInt())
        {
            Multiplier = Math.Clamp(reader.ReadInt(5), 1, 10)
        };
        Confirmed = reader.ReadBool();
    }
}
