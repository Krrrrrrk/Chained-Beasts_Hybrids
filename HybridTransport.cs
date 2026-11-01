using System.Text.Json;
using ChainedBeasts.ModFoundation.Networking;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Netcode;
using UnityEngine;
using Allocator = Unity.Collections.Allocator;

namespace ChainedBeasts.Hybrids;

// One host-owned loadout snapshot. Native character RPCs continue to carry only
// valid native variants; ALT is never inferred from another machine's keyboard.
internal static class HybridTransport
{
    private const string Channel = "local.chainedbeasts.hybrids/v1";
    private const int MaxBytes = 24000;
    private static readonly Dictionary<int, HybridChoice> Choices = new();
    private static readonly Dictionary<int, HybridChoice> Pending = new();
    private static readonly Dictionary<ulong, int> Masks = new();
    private static readonly Dictionary<ulong, long> Acks = new();
    private static readonly Dictionary<string, long> Requests = new(StringComparer.Ordinal);
    private static NetworkManager _manager;
    private static CustomMessagingManager.HandleNamedMessageDelegate _handler;
    private static string _epoch;
    private static long _revision, _request;
    private static double _sendAt;
    private static int _mask = -1;
    private static bool _received;
    private static string _lobbyEpoch;
    private static IntPtr _lobbyPointer;
    private static bool _inLobby;

    private sealed class Packet
    {
        public int Protocol { get; set; } = 1;
        public string Version { get; set; } = Plugin.Version;
        public string Kind { get; set; }
        public string Epoch { get; set; }
        public string Lobby { get; set; }
        public long Revision { get; set; }
        public int Mask { get; set; }
        public HybridChoice Choice { get; set; }
        public List<HybridChoice> Choices { get; set; }
        public Dictionary<ulong, int> Masks { get; set; }
    }
    private static bool FoundationReady => GameManager.InstanceExists && GameManager.Instance.CurrentGameMode == GameMode.Solo || MultiplayerServices.Ready;
    internal static bool PendingLocal => Pending.Count > 0;
    internal static bool Ready
    {
        get
        {
            if (_manager == null || !_manager.IsListening || !FoundationReady) return false;
            if (!_manager.IsServer) return _received && _epoch != null && Pending.Count == 0;
            foreach (var id in Peers())
                if (!Masks.ContainsKey(id) || !Acks.TryGetValue(id, out var ack) || ack != _revision) return false;
            return true;
        }
    }
    private static IEnumerable<ulong> Peers()
    {
        if (_manager == null) yield break;
        var list = _manager.ConnectedClientsIds;
        var count = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<ulong>>().Count;
        for (var i = 0; i < count; i++) if (list[i] != _manager.LocalClientId) yield return list[i];
    }
    internal static int MaskFor(ulong owner) => Masks.TryGetValue(owner, out var mask) ? mask : 0;
    internal static HybridChoice Get(int player) => Pending.TryGetValue(player, out var pending) ? pending : Choices.TryGetValue(player, out var choice) ? choice : null;
    internal static HybridChoice[] Snapshot() => Choices.Values.Select(x => x.Copy()).ToArray();
    internal static void Tick()
    {
        var manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening) { if (_manager != null) Shutdown(); return; }
        if (_manager?.Pointer != manager.Pointer)
        {
            Shutdown(); _manager = manager;
            _handler = (Action<ulong, FastBufferReader>)Receive;
            _manager.CustomMessagingManager.RegisterNamedMessageHandler(Channel, _handler);
            if (_manager.IsServer) { _epoch = Guid.NewGuid().ToString("N"); _revision = 1; _received = true; }
            _sendAt = 0;
        }
        var lobby = NetworkSession.InstanceExists ? NetworkSession.Instance.sceneRoot?.TryCast<GameServerLobby>() : null;
        if (lobby == null) _inLobby = false;
        else if (!_inLobby || _lobbyPointer != lobby.Pointer)
        {
            _inLobby = true; _lobbyPointer = lobby.Pointer;
            Choices.Clear(); Pending.Clear(); Requests.Clear(); Acks.Clear();
            if (_manager.IsServer) { _lobbyEpoch = Guid.NewGuid().ToString("N"); _revision++; }
            else _received = false;
            _sendAt = 0;
        }
        var localMask = HybridRuntime.LocalMask;
        if (_mask != localMask)
        {
            _mask = localMask;
            if (_manager.IsServer) { Masks[_manager.LocalClientId] = localMask; _revision++; }
            _sendAt = 0;
        }
        if (_manager.IsServer && NetworkSession.InstanceExists)
        {
            var connected = Peers().Append(_manager.LocalClientId).ToHashSet();
            foreach (var key in Masks.Keys.Where(x => !connected.Contains(x)).ToArray()) Masks.Remove(key);
            foreach (var key in Acks.Keys.Where(x => !connected.Contains(x)).ToArray()) Acks.Remove(key);
            foreach (var key in Requests.Keys.Where(x => !connected.Any(id => x.StartsWith(id + ":", StringComparison.Ordinal))).ToArray()) Requests.Remove(key);
            var removed = Choices.Where(x => !MatchesSeat(x.Value)).Select(x => x.Key).ToArray();
            foreach (var player in removed) Choices.Remove(player);
            if (removed.Length > 0) _revision++;
        }
        if (Time.unscaledTimeAsDouble < _sendAt) return;
        _sendAt = Time.unscaledTimeAsDouble + .5;
        if (_manager.IsServer) Broadcast();
        else
        {
            Send(NetworkManager.ServerClientId, new Packet { Kind = "hello", Mask = localMask });
            if (_epoch != null)
                foreach (var choice in Pending.Values.ToArray()) Send(NetworkManager.ServerClientId, new Packet { Kind = "choose", Choice = choice });
        }
    }
    internal static void Select(HybridChoice choice)
    {
        Tick();
        if (_manager == null || !MatchesSeat(choice) || !NetworkSession.Instance.IsLocalClient(Client(choice.Player))) return;
        choice.Request = ++_request;
        if (_manager.IsServer) Commit(choice, _manager.LocalClientId);
        else
        {
            Pending[choice.Player] = choice;
            if (_epoch != null) Send(NetworkManager.ServerClientId, new Packet { Kind = "choose", Choice = choice });
            _sendAt = 0;
        }
    }
    internal static void RefreshUnlocks() { _mask = -1; _sendAt = 0; }
    private static ClientIdentifier Client(int player) => !NetworkSession.InstanceExists || player < 0 || player > 3 ? null : NetworkSession.Instance.GetClientByPlayerIndex((ulong)player);
    private static bool MatchesSeat(HybridChoice choice)
    {
        if (choice == null || choice.Player < 0 || choice.Player > 3 || choice.Beast < 0 || choice.Beast >= 8 || choice.Variant < 0 || choice.Variant >= 3) return false;
        var client = Client(choice.Player);
        return client != null && client.NetworkID == choice.Network && client.LocalID == choice.Local && client.PlayerIndex == (ulong)choice.Player;
    }
    private static void Commit(HybridChoice choice, ulong sender)
    {
        if (!_manager.IsServer || choice.Network != sender || !MatchesSeat(choice)) return;
        var lobby = NetworkSession.Instance.sceneRoot?.TryCast<GameServerLobby>();
        if (lobby?.uiState == null || lobby.uiState.gameStarting || lobby.uiState.isRestoringCheckpoint) return;
        var client = Client(choice.Player);
        if (!HybridRuntime.BaseAvailable(client, choice.Beast, choice.Variant)) return;
        if (choice.Hybrid && (MaskFor(sender) & (1 << choice.Variant)) == 0) return;
        var stream = sender + ":" + choice.Local;
        if (Requests.TryGetValue(stream, out var last) && choice.Request <= last) { Broadcast(); return; }
        if (choice.Request <= 0) return;
        Requests[stream] = choice.Request;
        Choices[choice.Player] = choice.Copy();
        _revision++; Broadcast();
    }
    private static void Send(ulong target, Packet packet)
    {
        if (_manager == null || !_manager.IsListening) return;
        packet.Epoch = _epoch;
        packet.Lobby = _lobbyEpoch;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("Hybrid snapshot is too large.");
        var writer = new FastBufferWriter(bytes.Length, Allocator.Temp, bytes.Length);
        try
        {
            writer.WriteBytesSafe((Il2CppStructArray<byte>)bytes);
            _manager.CustomMessagingManager.SendNamedMessage(Channel, target, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }
        finally { writer.Dispose(); }
    }
    private static void Broadcast()
    {
        if (_manager?.IsServer != true) return;
        foreach (var peer in Peers())
            Send(peer, new Packet { Kind = "snapshot", Revision = _revision, Choices = Choices.Values.Select(x => x.Copy()).ToList(), Masks = new Dictionary<ulong, int>(Masks) });
    }
    private static void Receive(ulong sender, FastBufferReader reader)
    {
        try
        {
            if (_manager == null) return;
            var length = reader.Length - reader.Position;
            if (length < 1 || length > MaxBytes || !reader.TryBeginRead(length)) return;
            var bytes = new Il2CppStructArray<byte>(length); reader.ReadBytesSafe(ref bytes, length);
            var packet = JsonSerializer.Deserialize<Packet>(bytes.ToArray(), new JsonSerializerOptions { MaxDepth = 10 });
            if (packet == null || packet.Protocol != 1 || packet.Version != Plugin.Version || packet.Kind?.Length > 16 || packet.Epoch?.Length > 32 || packet.Lobby?.Length > 32 ||
                packet.Choices?.Count > 4 || packet.Masks?.Count > 8) return;
            if (_manager.IsServer)
            {
                if (!Peers().Contains(sender)) return;
                if (packet.Kind == "hello")
                {
                    if (packet.Mask < 0 || packet.Mask > 7) return;
                    if (!Masks.TryGetValue(sender, out var previous) || previous != packet.Mask) { Masks[sender] = packet.Mask; _revision++; }
                    Broadcast(); return;
                }
                if (packet.Epoch != _epoch || !Masks.ContainsKey(sender)) return;
                if (packet.Kind == "ack") { if (packet.Revision == _revision) Acks[sender] = packet.Revision; return; }
                if (packet.Kind == "choose" && packet.Lobby == _lobbyEpoch && packet.Choice != null) Commit(packet.Choice, sender);
            }
            else
            {
                if (sender != NetworkManager.ServerClientId || packet.Kind != "snapshot" || string.IsNullOrWhiteSpace(packet.Epoch) || packet.Revision < 1 ||
                    packet.Choices == null || packet.Masks == null || (_epoch != null && _epoch != packet.Epoch) || packet.Revision < _revision) return;
                var nextMasks = new Dictionary<ulong, int>();
                foreach (var item in packet.Masks)
                {
                    if (item.Value < 0 || item.Value > 7) return;
                    nextMasks.Add(item.Key, item.Value);
                }
                var next = new Dictionary<int, HybridChoice>();
                foreach (var choice in packet.Choices)
                {
                    if (!MatchesSeat(choice) || !next.TryAdd(choice.Player, choice)) return;
                    if (!HybridRuntime.BaseAvailable(Client(choice.Player), choice.Beast, choice.Variant)) return;
                    if (choice.Hybrid && (!nextMasks.TryGetValue(choice.Network, out var mask) || (mask & (1 << choice.Variant)) == 0)) return;
                }
                if (_lobbyEpoch != packet.Lobby) Pending.Clear();
                _lobbyEpoch = packet.Lobby;
                _epoch = packet.Epoch; _revision = packet.Revision;
                Choices.Clear(); foreach (var item in next) Choices[item.Key] = item.Value;
                Masks.Clear(); foreach (var item in nextMasks) Masks[item.Key] = item.Value;
                foreach (var pending in Pending.Values.ToArray())
                    if (Choices.TryGetValue(pending.Player, out var accepted) && accepted.Network == pending.Network && accepted.Local == pending.Local &&
                        accepted.Request == pending.Request && accepted.Beast == pending.Beast && accepted.Variant == pending.Variant && accepted.Hybrid == pending.Hybrid) Pending.Remove(pending.Player);
                _received = true;
                Send(NetworkManager.ServerClientId, new Packet { Kind = "ack", Revision = _revision });
            }
        }
        catch (Exception e) { HybridRuntime.Log?.LogWarning("[Hybrids] Rejected hybrid packet: " + e.Message); }
    }
    internal static bool NativeSelectionsMatch(GameServerLobby lobby)
    {
        if (lobby.uiState == null) return false;
        foreach (var choice in Choices.Values)
        {
            if (!MatchesSeat(choice) || choice.Player >= lobby.uiState.beastIndex.Length || choice.Player >= lobby.uiState.beastVariantIndex.Length) return false;
            if (lobby.uiState.beastIndex[choice.Player] != choice.Beast || lobby.uiState.beastVariantIndex[choice.Player] != choice.Variant) return false;
            if (choice.Hybrid && (!HybridRuntime.BaseAvailable(Client(choice.Player), choice.Beast, choice.Variant) ||
                (MaskFor(choice.Network) & (1 << choice.Variant)) == 0)) return false;
        }
        return true;
    }
    internal static void Shutdown()
    {
        try { _manager?.CustomMessagingManager?.UnregisterNamedMessageHandler(Channel); } catch { }
        _manager = null; _handler = null; _epoch = null; _revision = 0; _request = 0; _mask = -1; _received = false; _sendAt = 0;
        _lobbyEpoch = null; _lobbyPointer = IntPtr.Zero; _inLobby = false;
        Choices.Clear(); Pending.Clear(); Masks.Clear(); Acks.Clear(); Requests.Clear();
    }
}
