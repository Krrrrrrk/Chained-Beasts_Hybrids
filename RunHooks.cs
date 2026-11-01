using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace ChainedBeasts.Hybrids;

// These hooks only observe new runs and add the selected partner while the
// native primary grant is returning. They never replace beast identity or HP.
internal static class RunHooks
{
    private static bool _active;
    private static bool _faulted;
    private static string _lastStarted;
    private static Il2CppSystem.Action _campaignCompleted;
    private static Action _managedCampaignCompleted;
    [ThreadStatic] private static List<BeastAssignment> _assignments;
    [ThreadStatic] private static int _restoreDepth;

    internal static void Install(Harmony harmony)
    {
        if (_active) return;
        _faulted = false;
        _lastStarted = null;

        NativeHooks.Patch(typeof(RunHooks), typeof(RunDataManager), nameof(RunDataManager.StartNewRunRpc),
            prefix: nameof(StartBefore), postfix: nameof(StartAfter),
            args: new[] { typeof(int), typeof(int), typeof(AscensionLevel), typeof(DifficultyConfig), typeof(int) });
        NativeHooks.Patch(typeof(RunHooks), typeof(PlayerData), nameof(PlayerData.SetBeastType),
            prefix: nameof(BeastBefore), finalizer: nameof(BeastFinally),
            args: new[] { typeof(BeastType), typeof(int) });
        NativeHooks.Patch(typeof(RunHooks), typeof(PlayerData), nameof(PlayerData.AddSkill),
            postfix: nameof(SkillAfter), args: new[] { typeof(SkillType), typeof(int) });
        NativeHooks.Patch(typeof(RunHooks), typeof(RunDataManager), nameof(RunDataManager.RestoreCheckpoint),
            prefix: nameof(RestoreBefore), finalizer: nameof(RestoreFinally),
            args: new[] { typeof(RunDataManager.Checkpoint) });
        NativeHooks.Patch(typeof(RunHooks), typeof(GameServerLobby), nameof(GameServerLobby.HandleGameStart),
            finalizer: nameof(GameStartFinally), args: Type.EmptyTypes);

        // Keep both delegate objects alive, use the event accessors, and remove
        // only our own handler on shutdown. Never replace the native event field.
        _managedCampaignCompleted = OnCampaignCompleted;
        _campaignCompleted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(_managedCampaignCompleted);
        GameLevel.add_OnCampaignCompleted(_campaignCompleted);
        _active = true;
    }

    internal static void Shutdown()
    {
        _active = false;
        try
        {
            if (_campaignCompleted != null) GameLevel.remove_OnCampaignCompleted(_campaignCompleted);
        }
        catch (Exception e) { Warn("removing campaign handler", e); }
        finally
        {
            _campaignCompleted = null;
            _managedCampaignCompleted = null;
            _assignments?.Clear();
            _restoreDepth = 0;
            _lastStarted = null;
            EndArming();
        }
    }

    private sealed class StartSnapshot
    {
        internal IntPtr FirstPlayer;
    }

    private sealed class BeastAssignment
    {
        internal IntPtr Player;
        internal BeastType Beast;
        internal int Variant;
        internal SkillType Primary;
        internal bool Hybrid;
        internal bool Attempted;
    }

    private static IntPtr FirstPlayer(RunDataManager manager)
    {
        var list = manager?.playerDatas;
        return list != null && list.Count > 0 && list[0] != null ? list[0].Pointer : IntPtr.Zero;
    }

    private static RunDataManager CurrentManager() =>
        NetworkSession.InstanceExists ? NetworkSession.Instance?.runDataManager : null;

    private static void StartBefore(RunDataManager __instance, out StartSnapshot __state)
    {
        __state = null;
        if (!_active || _faulted) return;
        try { __state = new StartSnapshot { FirstPlayer = FirstPlayer(__instance) }; }
        catch (Exception e) { FailMod("inspecting new run", e); }
    }

    private static void StartAfter(RunDataManager __instance, int seed, int runStartTime,
        AscensionLevel difficulty, int forceStartingDay, StartSnapshot __state)
    {
        if (!_active || _faulted || __state == null || _restoreDepth > 0) return;
        try
        {
            // An RPC wrapper is called for sending as well as executing. The
            // native body replaces PlayerDatas, then sets every beast in later
            // SetPlayerBeastIndexRpc calls. A send-only return does neither.
            var current = CurrentManager();
            var first = FirstPlayer(__instance);
            if (current == null || current.Pointer != __instance.Pointer ||
                first == IntPtr.Zero || first == __state.FirstPlayer ||
                __instance.RunSeed != seed || __instance.RunStartTime != runStartTime ||
                __instance.AscensionLevel != difficulty) return;
            if (forceStartingDay != 1 || __instance.CompletedLevels != 0 || __instance.runStartCompletedLevels != 0)
            { EndArming(); return; }

            var key = __instance.Pointer + ":" + seed + ":" + runStartTime + ":" + first;
            // Host receive can be nested in the send call. Both postfixes see
            // the replaced roster; publish just one observed start.
            if (string.Equals(_lastStarted, key, StringComparison.Ordinal)) return;
            _lastStarted = key;
            HybridRuntime.BeginNewRun(__instance);
        }
        catch (Exception e) { FailMod("observing new run", e); }
    }

    private static void BeastBefore(PlayerData __instance, BeastType beastType, int variantIndex,
        out BeastAssignment __state)
    {
        __state = null;
        if (!_active || _faulted || _restoreDepth > 0 || __instance == null) return;
        try
        {
            if ((int)beastType < 0 || (int)beastType >= 8 || variantIndex < 0 || variantIndex >= 3) return;
            var frame = new BeastAssignment
            {
                Player = __instance.Pointer, Beast = beastType, Variant = variantIndex,
                Primary = GladiatorConsts.GetBeastSkillForVariant(beastType, variantIndex)
            };
            frame.Hybrid = HybridRuntime.TryGetSelection(__instance.playerID, out var beast, out var variant, out var hybrid) &&
                hybrid && beast == (int)beastType && variant == variantIndex;
            (_assignments ??= new List<BeastAssignment>()).Add(frame);
            __state = frame;
        }
        catch (Exception e) { FailMod("inspecting beast assignment", e); }
    }

    private static void SkillAfter(PlayerData __instance, SkillType skillType, int numberOfLevels)
    {
        if (!_active || _faulted || _restoreDepth > 0 || _assignments == null || _assignments.Count == 0) return;
        var frame = _assignments[_assignments.Count - 1];
        if (!frame.Hybrid || frame.Attempted || frame.Player != __instance.Pointer || skillType != frame.Primary) return;
        // Latch before calling any other native method or recursively AddSkill.
        frame.Attempted = true;
        try
        {
            if (numberOfLevels != 1 || __instance.beastType != frame.Beast ||
                __instance.beastVariantIndex != frame.Variant || __instance.HasSkill(frame.Primary) != 1 ||
                !HybridRuntime.CanApply(__instance)) return;

            var partner = HybridRuntime.Partner((int)frame.Beast, frame.Variant);
            if ((int)partner < 0 || (int)partner > 23 || partner == frame.Primary) return;
            var existing = __instance.HasSkill(partner);
            if (existing != 0) return; // Restored or repeated assignment: never add a rank.
            for (var id = 0; id < 24; id++)
                if ((SkillType)id != frame.Primary && __instance.HasSkill((SkillType)id) != 0) return;
            if (!GameLibrary.InstanceExists || !GameLibrary.Instance.SkillDict.ContainsKey(partner) ||
                GameLibrary.Instance.SkillDict[partner].OfferType != OfferType.BeastSkill) return;

            // Native SetBeastType has assigned appearance and added the primary.
            // Its next operation is get_MaxHealth, followed by the starting-HP
            // rules. Add the partner now so those rules see both native skills.
            __instance.AddSkill(partner, 1);
        }
        catch (Exception e) { FailMod("adding hybrid partner", e); }
    }

    private static Exception BeastFinally(Exception __exception, BeastAssignment __state)
    {
        if (__state != null && _assignments != null)
        {
            // Scope identity is a managed bookkeeping object, never player
            // wrapper identity. The player match above uses its native pointer.
            var index = _assignments.LastIndexOf(__state);
            if (index >= 0) _assignments.RemoveRange(index, _assignments.Count - index);
        }
        return __exception;
    }

    private static void RestoreBefore(out bool __state)
    {
        __state = false;
        if (!_active) return;
        _restoreDepth++;
        __state = true;
        EndArming();
    }

    private static Exception RestoreFinally(Exception __exception, bool __state)
    {
        if (__state && _restoreDepth > 0) _restoreDepth--;
        return __exception;
    }

    private static Exception GameStartFinally(Exception __exception)
    {
        // Remote clients end this window from HybridRuntime.Tick when their
        // scene becomes the preparation lobby or a battle. Host setup is done
        // here, after all selected-beast RPC calls and before ordinary play.
        EndArming();
        return __exception;
    }

    private static void OnCampaignCompleted()
    {
        if (!_active || _faulted) return;
        try
        {
            var manager = CurrentManager();
            var level = NetworkSession.InstanceExists ? NetworkSession.Instance?.sceneRoot?.TryCast<GameLevel>() : null;
            if (manager == null || level == null || !level.LevelComplete ||
                level.ActiveGameState != GameLevel.eGameState.LEVEL_COMPLETE ||
                level.resultStep != GameLevel.eResultStep.GAME_WIN) return;
            // This event is raised by the genuine GameWinCoroutine on peers.
            // Runtime validates its persisted observed-start receipt and awards
            // only the exact A4/A5/A6 flag for the current local profile.
            HybridRuntime.RecordVictory(manager);
        }
        catch (Exception e) { FailMod("recording campaign victory", e); }
    }

    private static void EndArming()
    {
        try { HybridRuntime.EndNewRun(); }
        catch (Exception e) { Warn("ending new-run scope", e); }
    }

    private static void FailMod(string action, Exception exception)
    {
        _faulted = true;
        EndArming();
        Warn(action + "; run integration disabled for this launch", exception);
    }

    private static void Warn(string action, Exception exception)
    {
        try { UnityEngine.Debug.LogWarning("[Hybrids] " + action + ": " + exception); }
        catch { /* Reporting must not interfere with the original native call. */ }
    }
}
