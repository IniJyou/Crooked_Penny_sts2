using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CrookedPenny;

internal static class CoinCheckpoint
{
    private sealed record Checkpoint(int Format, CoinScene Scene, string Run, Dictionary<ulong, string> Entries);
    private sealed record NetworkCheckpoint(CoinScene Scene, Dictionary<ulong, string> Entries);

    private static Checkpoint? _restoring;
    private static SerializableRun? _restoredRun;
    private static RunState? _restoredState;
    private static RunState? _capturedState;
    private static Checkpoint? _captured;
    private static readonly HashSet<ulong> CapturedPlayers = [];

    internal static void Save(CoinScene scene, SerializableRun run, Dictionary<ulong, string> entries, ulong? owner = null)
    {
        var manager = RunManager.Instance;
        if (!manager.ShouldSave || manager.NetService.Type is not (NetGameType.Singleplayer or NetGameType.Host))
            return;

        var multiplayer = manager.NetService.Type == NetGameType.Host;
        var activeState = manager.DebugOnlyGetState();
        if (multiplayer && owner.HasValue && ReferenceEquals(_capturedState, activeState) && _captured?.Scene == scene)
        {
            var previous = Deserialize<SerializableRun>(_captured.Run);
            if (SamePlace(previous, run))
            {
                // Keep the original pre-flip state for players who have already flipped here.
                run = Deserialize<SerializableRun>(Serialize(run));
                for (var i = 0; i < run.Players.Count; i++)
                {
                    var player = run.Players[i].NetId;
                    if (!CapturedPlayers.Contains(player))
                        continue;

                    run.Players[i] = previous.Players.Single(saved => saved.NetId == player);
                    var currentPoint = run.MapPointHistory[run.CurrentActIndex].LastOrDefault();
                    var previousPoint = previous.MapPointHistory[previous.CurrentActIndex].LastOrDefault();
                    if (currentPoint != null && previousPoint != null)
                    {
                        var index = currentPoint.PlayerStats.FindIndex(stats => stats.PlayerId == player);
                        if (index >= 0)
                            currentPoint.PlayerStats[index] = previousPoint.GetEntry(player);
                    }
                    if (_captured.Entries.TryGetValue(player, out var oldEntry))
                        entries[player] = oldEntry;
                }
                run.SerializableSharedRelicGrabBag = previous.SerializableSharedRelicGrabBag;
            }
            else
                CapturedPlayers.Clear();
        }
        else
            CapturedPlayers.Clear();

        var fileName = GetFileName(multiplayer);
        var checkpoint = new Checkpoint(1, scene, Serialize(run), entries);
        Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
        var temporaryFile = fileName + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(checkpoint));
        File.Move(temporaryFile, fileName, overwrite: true);
        _captured = checkpoint;
        _capturedState = activeState;
        if (owner is { } playerId)
            CapturedPlayers.Add(playerId);
    }

    internal static string? Take(CoinScene scene, ulong owner, IRunState run)
    {
        return IsRestoring(scene, run) && _restoring!.Entries.Remove(owner, out var entry)
            ? entry : null;
    }

    internal static bool IsRestoring(CoinScene scene, IRunState? run) =>
        _restoredRun != null && _restoredState != null && ReferenceEquals(_restoredState, run) &&
        _restoring?.Scene == scene && run!.CurrentActIndex == _restoredRun.CurrentActIndex &&
        _restoredState.VisitedMapCoords.SequenceEqual(_restoredRun.VisitedMapCoords);

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonSerializationUtility.GetTypeInfo<T>());
    internal static T Deserialize<T>(string data) => JsonSerializer.Deserialize(data, JsonSerializationUtility.GetTypeInfo<T>())!;

    private static string GetFileName(bool multiplayer) => ProjectSettings.GlobalizePath(
        SaveManager.Instance.GetProfileScopedPath(multiplayer ? "crooked_penny_mp.json" : "crooked_penny_sp.json"));

    private static bool SamePlace(SerializableRun original, SerializableRun checkpoint) =>
        original.StartTime == checkpoint.StartTime && original.SerializableRng.Seed == checkpoint.SerializableRng.Seed &&
        original.CurrentActIndex == checkpoint.CurrentActIndex && original.VisitedMapCoords.SequenceEqual(checkpoint.VisitedMapCoords);

    private static void Restore(ref ReadSaveResult<SerializableRun> result, bool multiplayer)
    {
        if (result?.SaveData is not { } current || !result.Success)
            return;

        try
        {
            var fileName = GetFileName(multiplayer);
            if (!File.Exists(fileName))
                return;

            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(fileName));
            if (checkpoint is not { Format: 1 })
                return;

            var restored = Deserialize<SerializableRun>(checkpoint.Run);
            if (!SamePlace(current, restored) || restored.SchemaVersion != current.SchemaVersion)
                return;

            restored.NumReloads = current.NumReloads;
            _restoring = checkpoint;
            _restoredRun = restored;
            result = new ReadSaveResult<SerializableRun>(restored, ReadSaveStatus.Success);
            Log.Info($"[CrookedPenny] Restored a before-flip checkpoint for {checkpoint.Scene}.");
        }
        catch (Exception error)
        {
            Log.Warn($"[CrookedPenny] Could not restore a checkpoint: {error.Message}");
        }
    }

    [HarmonyPatch(typeof(SerializableRun), nameof(SerializableRun.Serialize))]
    private static class SendCheckpoint
    {
        private static void Postfix(SerializableRun __instance, PacketWriter writer)
        {
            var hasCheckpoint = _restoring != null && _restoredRun != null && SamePlace(__instance, _restoredRun);
            writer.WriteBool(hasCheckpoint);
            if (hasCheckpoint)
                writer.WriteString(JsonSerializer.Serialize(new NetworkCheckpoint(_restoring!.Scene, _restoring.Entries)));
        }
    }

    [HarmonyPatch(typeof(SerializableRun), nameof(SerializableRun.Deserialize))]
    private static class ReceiveCheckpoint
    {
        private static void Postfix(SerializableRun __instance, PacketReader reader)
        {
            if (!reader.ReadBool())
                return;

            var message = JsonSerializer.Deserialize<NetworkCheckpoint>(reader.ReadString())!;
            _restoredRun = __instance;
            _restoring = new Checkpoint(1, message.Scene, "", message.Entries);
        }
    }

    [HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.LoadRunSave))]
    private static class ReadSolo
    {
        private static void Postfix(ref ReadSaveResult<SerializableRun> __result) => Restore(ref __result, false);
    }

    [HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.LoadMultiplayerRunSave))]
    private static class ReadMultiplayer
    {
        private static void Postfix(ref ReadSaveResult<SerializableRun> __result) => Restore(ref __result, true);
    }

    [HarmonyPatch(typeof(RunState), nameof(RunState.FromSerializable))]
    private static class BindCheckpoint
    {
        private static void Postfix(SerializableRun save, RunState __result)
        {
            if (_restoredRun != null && SamePlace(save, _restoredRun))
                _restoredState = __result;
        }
    }

    [HarmonyPatch(typeof(AbstractRoom), nameof(AbstractRoom.FromSerializable))]
    private static class ResumeCustomRoom
    {
        private static bool Prefix(SerializableRoom? serializableRoom, IRunState? runState, ref AbstractRoom? __result)
        {
            if (serializableRoom?.RoomType == RoomType.Shop && IsRestoring(CoinScene.Merchant, runState))
            {
                __result = new MerchantRoom();
                return false;
            }

            if (serializableRoom?.RoomType == RoomType.Treasure && IsRestoring(CoinScene.Treasure, runState))
            {
                __result = new TreasureRoom(runState!.CurrentActIndex);
                return false;
            }

            return true;
        }
    }
}
