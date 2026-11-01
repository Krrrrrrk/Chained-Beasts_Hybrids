using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BepInEx.Logging;
using ChainedBeasts.Hybrids.Core;
using ChainedBeasts.ModFoundation.Runtime;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChainedBeasts.Hybrids;

internal static class HybridRuntime
{
    internal static ManualLogSource Log;
    internal static bool Active;
    private static string _profile;
    private static string _profileRoot;
    private static UnlockStore _unlocks;
    private static RunJournal _journal;
    private static readonly Dictionary<string, RunJournal> Journals = new(StringComparer.Ordinal);
    private static string _armedRun;
    private static IntPtr _armedManager;
    private static readonly Dictionary<int, HybridChoice> Frozen = new();
    private static Dictionary<int, PerkText> _text;
    private static bool _faulted;
    private static double _auditAt;
    private static string _lastStartWarning;
    private static GameServerLobby _deferredStart;

    internal sealed class PerkText { public string Name { get; set; } public string Description { get; set; } }
    internal static bool AltHeld => Keyboard.current != null && (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed);
    internal static bool EnabledForSelection => Active && !_faulted && NetworkSession.InstanceExists;
    internal static bool IsSelectedHybrid(int player) => TryGetSelection(player, out _, out _, out var hybrid) && hybrid;

    internal static void Initialize()
    {
        using var input = typeof(HybridRuntime).Assembly.GetManifestResourceStream("ChainedBeasts.Hybrids.descriptions.json");
        _text = JsonSerializer.Deserialize<Dictionary<int, PerkText>>(input);
        NativeHooks.Patch(typeof(HybridRuntime), typeof(GameServerLobby), nameof(GameServerLobby.HandleGameStart), prefix: nameof(StartAllowed));
        NativeHooks.Patch(typeof(HybridRuntime), typeof(GameServerLobby), nameof(GameServerLobby.BroadcastGameStartingRpc), prefix: nameof(CountdownAllowed));
        NativeHooks.Patch(typeof(HybridRuntime), typeof(GameServerLobby), nameof(GameServerLobby.HandleNavigationCharacterConfirmRpc),
            prefix: nameof(ConfirmAllowed), args: new[] { typeof(ClientIdentifier) });
    }
    internal static void Tick()
    {
        if (!Active || _faulted) return;
        EnsureProfile();
        HybridTransport.Tick();
        if (_deferredStart != null)
        {
            var current = NetworkSession.InstanceExists ? NetworkSession.Instance.sceneRoot?.TryCast<GameServerLobby>() : null;
            if (current == null || current.Pointer != _deferredStart.Pointer || current.uiState == null || !current.uiState.gameStarting || !current.uiState.allReady)
                _deferredStart = null;
            else if (HybridTransport.Ready && HybridTransport.NativeSelectionsMatch(current))
            {
                _deferredStart = null;
                current.HandleGameStart();
            }
        }
        if (_armedRun != null && NetworkSession.InstanceExists &&
            (NetworkSession.Instance.sceneRoot?.TryCast<GameLevelLobby>() != null || NetworkSession.Instance.sceneRoot?.TryCast<GameLevel>() != null)) EndNewRun();
        if (Time.unscaledTimeAsDouble >= _auditAt) { _auditAt = Time.unscaledTimeAsDouble + 5; NativeHooks.Audit(); }
    }
    private static void EnsureProfile()
    {
        if (!NetworkSession.InstanceExists || PersistentData.Current == null) return;
        var client = NetworkSession.Instance.primaryIdentifier;
        if (client == null || !NetworkSession.Instance.IsLocalClient(client)) return;
        // Solo client identifiers have no PlayerID. Account identity must not
        // depend on a transient network/player slot or on the selected mode.
        string id;
        if (GameManager.Platform?.TryCast<Platforms.Steam>() != null)
        {
            var steam = Steamworks.SteamUser.GetSteamID().m_SteamID;
            if (steam == 0) return;
            id = "steam:" + steam;
        }
        else id = "native-save:" + Path.GetFullPath(Application.persistentDataPath) + ":" + PersistentData.SaveDataFileName;
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant().Substring(0, 24);
        if (profile == _profile) { SelectJournal(); return; }
        var root = Path.Combine(Application.persistentDataPath, "ChainedBeasts.Hybrids", profile);
        _unlocks = new UnlockStore(Path.Combine(root, "unlocks.json"), message => Log?.LogWarning("[Hybrids] " + message));
        _profile = profile; _profileRoot = root; Journals.Clear(); SelectJournal();
        EndNewRun(); Frozen.Clear();
        Log?.LogInfo("[Hybrids] Profile unlock store loaded. IV=" + _unlocks.IsUnlocked(0) + " V=" + _unlocks.IsUnlocked(1) + " VI=" + _unlocks.IsUnlocked(2));
    }
    private static void SelectJournal()
    {
        if (_profileRoot == null || !GameManager.InstanceExists) return;
        var mode = GameManager.Instance.CurrentGameMode.ToString();
        if (!Journals.TryGetValue(mode, out var journal))
        {
            journal = new RunJournal(Path.Combine(_profileRoot, "campaign-" + mode + ".json"));
            Journals[mode] = journal;
        }
        _journal = journal;
    }
    internal static int LocalMask
    {
        get
        {
            EnsureProfile();
            if (_unlocks == null || !_unlocks.CanWrite) return 0;
            var mask = 0;
            for (var i = 0; i < 3; i++) if (_unlocks.IsUnlocked(i)) mask |= 1 << i;
            return mask;
        }
    }
    internal static bool BaseAvailable(ClientIdentifier client, int beast, int variant)
    {
        if (client == null || beast < 0 || beast >= 8 || variant < 0 || variant >= 3 || !NetworkSession.InstanceExists) return false;
        var s = NetworkSession.Instance;
        return s.IsBeastUnlocked(client, (BeastType)beast) && s.IsBeastVariantUnlocked(client, (BeastType)beast, variant);
    }
    internal static bool CanSelect(ClientIdentifier client, int beast, int variant, out string reason)
    {
        reason = LockReason(client, beast, variant);
        return reason == null;
    }
    internal static string LockReason(ClientIdentifier client, int beast, int variant)
    {
        if (!EnabledForSelection || client == null || beast < 0 || beast >= 8 || variant < 0 || variant >= 3) return "Hybrid selection isn't ready yet.";
        if (!BaseAvailable(client, beast, variant)) return "Unlock this beast's normal " + Roman(variant + 1) + " variant first.";
        EnsureProfile();
        if (_unlocks == null) return "Waiting for your profile.";
        if (!_unlocks.CanWrite && NetworkSession.Instance.IsLocalClient(client)) return "Hybrid unlock data needs attention. See the game log.";
        var mask = NetworkSession.Instance.IsLocalClient(client) ? LocalMask : HybridTransport.MaskFor(client.NetworkID);
        if ((mask & (1 << variant)) == 0)
            return "Win a new " + new[] { "Primus (difficulty 4)", "Tribune (difficulty 5)", "Legate (difficulty 6)" }[variant] + " campaign to unlock " + Roman(variant + 4) + ".";
        if (!HybridTransport.Ready) return "Waiting for matching hybrid selections from the party.";
        return null;
    }
    internal static void SelectLocal(ClientIdentifier client, int beast, int variant, bool hybrid)
    {
        if (!EnabledForSelection || client == null || !NetworkSession.Instance.IsLocalClient(client) || !BaseAvailable(client, beast, variant)) return;
        if (hybrid && !CanSelect(client, beast, variant, out _)) return;
        var lobby = NetworkSession.Instance.sceneRoot?.TryCast<GameServerLobby>();
        if (lobby == null || lobby.uiState == null || lobby.uiState.gameStarting || lobby.uiState.isRestoringCheckpoint) return;
        var choice = new HybridChoice { Player = checked((int)client.PlayerIndex), Network = client.NetworkID, Local = client.LocalID,
            Beast = beast, Variant = variant, Hybrid = hybrid };
        HybridTransport.Select(choice);
        // This native RPC takes the actual BeastType enum, not the sorted grid index.
        lobby.HandleCharacterVariantSelectedRpc(client, beast, variant);
    }
    internal static bool TryGetSelection(int player, out int beast, out int variant, out bool hybrid)
    {
        var choice = _armedRun != null && Frozen.TryGetValue(player, out var frozen) ? frozen : HybridTransport.Get(player);
        beast = choice?.Beast ?? -1; variant = choice?.Variant ?? -1; hybrid = choice?.Hybrid ?? false;
        return choice != null;
    }
    internal static SkillType Partner(int beast, int variant) => (SkillType)HybridCatalog.Partner(beast, variant);
    internal static bool CanEditProgression(out string reason)
    {
        reason = null;
        if (!Active || _faulted || !SessionAccess.IsSoloMode || !NetworkSession.InstanceExists)
        { reason = "Hybrid progression controls are available in Solo only."; return false; }
        var session = NetworkSession.Instance;
        var network = NetworkManager.Singleton;
        var clients = session.clientList?.clients;
        if (network?.IsListening != true || !network.IsServer || clients?.Count != 1 || !session.IsLocalClient(clients[0]) || session.TransitionPending)
        { reason = "Wait for one local Solo host outside a scene transition."; return false; }
        var lobby = session.sceneRoot?.TryCast<GameServerLobby>();
        if (lobby != null)
        {
            if (lobby.uiState == null || lobby.uiState.gameStarting || lobby.uiState.isRestoringCheckpoint)
            { reason = "Wait until character selection is ready."; return false; }
        }
        else if (!SessionAccess.CanUseDeveloperTools)
        { reason = "Open a Solo character-selection lobby or run first."; return false; }
        EnsureProfile();
        if (_unlocks == null || !_unlocks.CanWrite)
        { reason = "Your hybrid profile is unavailable or needs attention. See the game log."; return false; }
        return true;
    }
    internal static bool SetDeveloperUnlocked(int variant, bool unlocked, out string result)
    {
        if (!CanEditProgression(out result)) return false;
        if (!_unlocks.SetDeveloperUnlocked(variant, unlocked)) { result = "Could not save the hybrid level change."; return false; }
        DeveloperUnlocksChanged();
        result = "Hybrid " + Roman(variant + 4) + (unlocked ? " unlocked" : " locked") + " for this profile.";
        return true;
    }
    internal static bool SetDeveloperMask(int mask, out string result)
    {
        if (!CanEditProgression(out result)) return false;
        if (!_unlocks.SetDeveloperMask(mask)) { result = "Could not save the hybrid level changes."; return false; }
        DeveloperUnlocksChanged();
        result = mask == 7 ? "Hybrid IV, V and VI unlocked for this profile." : "Hybrid IV, V and VI locked for this profile.";
        return true;
    }
    private static void DeveloperUnlocksChanged()
    {
        HybridTransport.RefreshUnlocks();
        // Locking progression must not leave a revoked hybrid queued to launch.
        // Change only the queued menu choice; never strip native run skills.
        if (NetworkSession.Instance.sceneRoot?.TryCast<GameServerLobby>() == null) return;
        var network = NetworkManager.Singleton;
        foreach (var choice in HybridTransport.Snapshot())
        {
            if (!choice.Hybrid || choice.Network != network.LocalClientId || _unlocks.IsUnlocked(choice.Variant)) continue;
            choice.Hybrid = false;
            HybridTransport.Select(choice);
        }
    }
    private static string SkillName(int id)
    {
        try { var name = StringManager.GetSkillName((SkillType)id); if (!string.IsNullOrWhiteSpace(name)) return name; } catch { }
        return _text.TryGetValue(id, out var text) ? text.Name : ((SkillType)id).ToString();
    }
    internal static string BuildName(int beast, int variant) => SkillName(HybridCatalog.Skill(beast, variant)) + " + " + SkillName(HybridCatalog.Partner(beast, variant));
    private static string Description(int id)
    {
        try
        {
            var value = I2.Loc.LocalizationManager.GetTranslation(StringManager.GetSkillDescTerm((SkillType)id));
            if (!string.IsNullOrWhiteSpace(value)) return value.Replace("\n", "\n");
        }
        catch { }
        return _text.TryGetValue(id, out var text) ? text.Description : "";
    }
    internal static string BuildDescription(int beast, int variant) =>
        Description(HybridCatalog.Skill(beast, variant)) + "\n\n" + Description(HybridCatalog.Partner(beast, variant));
    internal static string Roman(int level) => level switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", _ => "?" };
    internal static string RunKey(RunDataManager manager) =>
        GameManager.Instance.CurrentGameMode + ":" + manager.RunSeed + ":" + manager.RunStartTime;
    internal static void BeginNewRun(RunDataManager manager)
    {
        EnsureProfile(); EndNewRun(); Frozen.Clear();
        if (!Active || _faulted || manager == null || !HybridTransport.Ready) return;
        NativeHooks.Audit();
        var run = RunKey(manager);
        if (_journal == null || !_journal.Begin(run, (int)manager.AscensionLevel))
            Log?.LogWarning("[Hybrids] Campaign receipt could not be written. Existing hybrid loadouts still apply, but this campaign cannot grant a fresh tier unlock.");
        // HandleGameStart normalizes native player indices before starting the
        // campaign. Rebind the validated choice by its stable native seat.
        foreach (var choice in HybridTransport.Snapshot())
        {
            var clients = NetworkSession.Instance.clientList?.clients;
            if (clients == null) continue;
            for (var i = 0; i < clients.Count; i++)
            {
                var client = clients[i];
                if (client.NetworkID != choice.Network || client.LocalID != choice.Local) continue;
                var rebound = choice.Copy(); rebound.Player = checked((int)client.PlayerIndex);
                if (!Frozen.TryAdd(rebound.Player, rebound)) throw new InvalidOperationException("Duplicate normalized hybrid seat.");
                break;
            }
        }
        _armedManager = manager.Pointer; _armedRun = run;
        Log?.LogInfo("[Hybrids] New campaign observed; " + Frozen.Values.Count(x => x.Hybrid) + " hybrid selection(s) frozen before character setup.");
    }
    internal static void EndNewRun() { _armedRun = null; _armedManager = IntPtr.Zero; }
    internal static bool CanApply(PlayerData data)
    {
        if (!Active || _faulted || data == null || _armedRun == null || !NetworkSession.InstanceExists) return false;
        var manager = NetworkSession.Instance.runDataManager;
        if (manager == null || manager.Pointer != _armedManager || RunKey(manager) != _armedRun ||
            manager.CompletedLevels != manager.runStartCompletedLevels || !Frozen.TryGetValue(data.playerID, out var choice) || !choice.Hybrid) return false;
        var present = false;
        for (var i = 0; i < manager.playerDatas.Count; i++) if (manager.playerDatas[i]?.Pointer == data.Pointer) { present = true; break; }
        return present;
    }
    internal static void RecordVictory(RunDataManager manager)
    {
        EnsureProfile();
        if (!Active || _faulted || manager == null || _journal == null || _unlocks == null) return;
        var difficulty = (int)manager.AscensionLevel;
        var receipt = _journal.Receipt(RunKey(manager), difficulty);
        if (receipt == null) { Log?.LogWarning("[Hybrids] Victory has no post-install campaign receipt; no unlock awarded."); return; }
        if (_unlocks.RecordVictory(difficulty, receipt))
        {
            Log?.LogInfo("[Hybrids] New difficulty " + difficulty + " victory: " + Roman(difficulty) + " unlocked for every available beast.");
            HybridTransport.RefreshUnlocks();
        }
    }
    private static bool StartAllowed(GameServerLobby __instance)
    {
        if (!Active || _faulted) return true;
        if (__instance.uiState?.isRestoringCheckpoint == true) return true;
        if (!HybridTransport.Ready || !HybridTransport.NativeSelectionsMatch(__instance))
        {
            // A native starting coroutine only calls this once. If a peer
            // changes during the animation, retain one guarded retry.
            _deferredStart = __instance;
            var warning = "Waiting for the party's hybrid choices to finish syncing before starting.";
            if (_lastStartWarning != warning) { _lastStartWarning = warning; Log?.LogWarning("[Hybrids] " + warning); }
            return false;
        }
        _lastStartWarning = null; return true;
    }
    private static bool CountdownAllowed(GameServerLobby __instance)
    {
        if (!Active || _faulted || !__instance.IsServer || __instance.uiState?.isRestoringCheckpoint == true) return true;
        if (HybridTransport.Ready && HybridTransport.NativeSelectionsMatch(__instance)) return true;
        // Native Update sets this flag immediately before broadcasting. Return
        // it to false so the next Update can retry after choices are synced.
        if (__instance.uiState != null) __instance.uiState.gameStarting = false;
        return false;
    }
    private static bool ConfirmAllowed(ClientIdentifier clientID)
    {
        if (!Active || _faulted || !NetworkSession.InstanceExists || !NetworkSession.Instance.IsLocalClient(clientID)) return true;
        return !HybridTransport.PendingLocal;
    }
    internal static void Shutdown() { DeveloperUnlockMenu.Shutdown(); EndNewRun(); _deferredStart = null; Frozen.Clear(); HybridTransport.Shutdown(); SelectionUi.Reset(); }
    internal static void Fault(Exception e)
    {
        if (_faulted) return;
        _faulted = true; EndNewRun(); Log?.LogError("[Hybrids] Disabled after runtime integration error: " + e);
        HybridTransport.Shutdown();
    }
}

internal sealed class HybridChoice
{
    public int Player { get; set; }
    public ulong Network { get; set; }
    public ulong Local { get; set; }
    public int Beast { get; set; }
    public int Variant { get; set; }
    public bool Hybrid { get; set; }
    public long Request { get; set; }
    internal HybridChoice Copy() => (HybridChoice)MemberwiseClone();
}
