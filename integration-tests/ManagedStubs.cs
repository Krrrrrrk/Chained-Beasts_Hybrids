using System.Reflection;

// Only this test assembly contains these types. It links the real RunHooks.cs,
// while modeled native call sequences never load Unity, IL2CPP or the game.
namespace HarmonyLib { internal sealed class Harmony { } }
namespace Il2CppSystem
{
    internal sealed class Action
    {
        private readonly System.Action _callback;
        internal Action(System.Action callback) { _callback = callback; }
        internal void Invoke() => _callback();
    }
}
namespace Il2CppInterop.Runtime
{
    internal static class DelegateSupport
    {
        internal static T ConvertDelegate<T>(Delegate callback) =>
            (T)(object)new Il2CppSystem.Action(() => callback.DynamicInvoke());
    }
}
namespace UnityEngine
{
    internal static class Debug
    {
        internal static readonly List<string> Warnings = new();
        public static void LogWarning(string value) => Warnings.Add(value);
    }
}

internal enum AscensionLevel { Tutorial, A1, A2, A3, A4, A5, A6 }
internal enum BeastType { Lion, Eagle, Bear, Panther, Stag, Rhino, Monkey, Wolf }
internal enum SkillType { None = -1, Beast_Lion_Lv1 = 0, Beast_Eagle_Lv1 = 3 }
internal enum OfferType { Other, BeastSkill }
internal sealed class SkillDef { internal OfferType OfferType = OfferType.BeastSkill; }
internal static class GladiatorConsts
{
    private static readonly int[] Bases = { 0, 3, 6, 9, 15, 18, 21, 12 };
    internal static SkillType GetBeastSkillForVariant(BeastType beast, int variant) => (SkillType)(Bases[(int)beast] + variant);
}
internal sealed class GameLibrary
{
    internal static GameLibrary Instance = new();
    internal static bool InstanceExists => Instance != null;
    internal readonly Dictionary<SkillType, SkillDef> SkillDict = Enumerable.Range(0, 24).ToDictionary(x => (SkillType)x, _ => new SkillDef());
}
internal class SceneRoot
{
    internal T TryCast<T>() where T : class => this as T;
}
internal sealed class NetworkSession
{
    internal static NetworkSession Instance;
    internal static bool InstanceExists => Instance != null;
    internal RunDataManager runDataManager;
    internal SceneRoot sceneRoot;
}
internal static class FakePointers
{
    private static int _next = 100;
    internal static IntPtr New() => new(++_next);
}
internal sealed class PlayerData
{
    internal readonly IntPtr Pointer = FakePointers.New();
    internal int playerID;
    internal BeastType beastType;
    internal int beastVariantIndex;
    internal readonly Dictionary<SkillType, int> Skills = new();
    internal readonly List<string> Order = new();
    internal int SkillsSeenByStartingHp;
    internal bool ThrowBeforePrimary;
    internal int HasSkill(SkillType skill) => Skills.TryGetValue(skill, out var rank) ? rank : 0;
    public void AddSkill(SkillType skillType, int numberOfLevels)
    {
        ChainedBeasts.Hybrids.NativeHooks.Invoke(this, nameof(AddSkill), new() { [nameof(skillType)] = skillType, [nameof(numberOfLevels)] = numberOfLevels }, () =>
        {
            Skills[skillType] = HasSkill(skillType) + numberOfLevels;
            Order.Add("skill:" + (int)skillType);
        });
    }
    public void SetBeastType(BeastType beastType, int variantIndex)
    {
        ChainedBeasts.Hybrids.NativeHooks.Invoke(this, nameof(SetBeastType), new() { [nameof(beastType)] = beastType, [nameof(variantIndex)] = variantIndex }, () =>
        {
            if (ThrowBeforePrimary) throw new InvalidOperationException("modeled native assignment failure");
            // Order is taken from the inspected native SetBeastType body.
            this.beastType = beastType; beastVariantIndex = variantIndex;
            AddSkill(GladiatorConsts.GetBeastSkillForVariant(beastType, variantIndex), 1);
            Order.Add("starting-hp");
            SkillsSeenByStartingHp = Skills.Count(x => x.Value > 0);
        });
    }
}
internal sealed class RunDataManager
{
    internal sealed class Checkpoint { }
    internal readonly IntPtr Pointer = FakePointers.New();
    internal readonly List<PlayerData> playerDatas = new() { new PlayerData { playerID = 0 } };
    internal int RunSeed, RunStartTime, CompletedLevels, runStartCompletedLevels;
    internal AscensionLevel AscensionLevel;
    internal bool NestedHostSend, SendOnly, ThrowDuringRestore;
    internal System.Action RestoreBody;
    public void StartNewRunRpc(int seed, int runStartTime, AscensionLevel difficulty, DifficultyConfig difficultyMultipliers, int forceStartingDay = 1)
    {
        ChainedBeasts.Hybrids.NativeHooks.Invoke(this, nameof(StartNewRunRpc), new()
        {
            [nameof(seed)] = seed, [nameof(runStartTime)] = runStartTime, [nameof(difficulty)] = difficulty,
            [nameof(difficultyMultipliers)] = difficultyMultipliers, [nameof(forceStartingDay)] = forceStartingDay
        }, () =>
        {
            if (SendOnly) return;
            if (NestedHostSend)
            {
                NestedHostSend = false;
                StartNewRunRpc(seed, runStartTime, difficulty, difficultyMultipliers, forceStartingDay);
                return;
            }
            RunSeed = seed; RunStartTime = runStartTime; AscensionLevel = difficulty;
            CompletedLevels = runStartCompletedLevels = Math.Max(0, forceStartingDay - 1);
            playerDatas.Clear(); playerDatas.Add(new PlayerData { playerID = 0 });
        });
    }
    public void RestoreCheckpoint(Checkpoint checkpoint)
    {
        ChainedBeasts.Hybrids.NativeHooks.Invoke(this, nameof(RestoreCheckpoint), new() { [nameof(checkpoint)] = checkpoint }, () =>
        {
            RestoreBody?.Invoke();
            if (ThrowDuringRestore) throw new InvalidOperationException("modeled native restore failure");
        });
    }
}
internal sealed class DifficultyConfig { }
internal sealed class GameServerLobby : SceneRoot
{
    internal System.Action StartBody;
    public void HandleGameStart() => ChainedBeasts.Hybrids.NativeHooks.Invoke(this, nameof(HandleGameStart), new(), () => StartBody?.Invoke());
}
internal sealed class GameLevel : SceneRoot
{
    internal enum eGameState { RUNNING, LEVEL_COMPLETE, LEVEL_FAILED }
    internal enum eResultStep { BANNER, GAME_WIN, DONE }
    internal bool LevelComplete;
    internal eGameState ActiveGameState;
    internal eResultStep resultStep;
    private static readonly List<Il2CppSystem.Action> Handlers = new();
    internal static int HandlerCount => Handlers.Count;
    public static void add_OnCampaignCompleted(Il2CppSystem.Action callback) => Handlers.Add(callback);
    public static void remove_OnCampaignCompleted(Il2CppSystem.Action callback) => Handlers.Remove(callback);
    internal static void RaiseCompletion() { foreach (var handler in Handlers.ToArray()) handler.Invoke(); }
}

namespace ChainedBeasts.Hybrids
{
    internal static class HybridRuntime
    {
        internal static bool Armed;
        internal static bool SelectionHybrid = true;
        internal static bool AllowApply = true;
        internal static int Begins, Ends, Victories;
        internal static bool TryGetSelection(int player, out int beast, out int variant, out bool hybrid)
        { beast = 0; variant = 0; hybrid = SelectionHybrid; return player == 0; }
        internal static SkillType Partner(int beast, int variant) => SkillType.Beast_Eagle_Lv1;
        internal static bool CanApply(PlayerData data) => Armed && AllowApply && NetworkSession.Instance.runDataManager.playerDatas.Any(x => x.Pointer == data.Pointer);
        internal static void BeginNewRun(RunDataManager manager) { Begins++; Armed = true; }
        internal static void EndNewRun() { Ends++; Armed = false; }
        internal static void RecordVictory(RunDataManager manager) => Victories++;
        internal static void Reset() { Armed = false; SelectionHybrid = true; AllowApply = true; Begins = Ends = Victories = 0; }
    }

    internal static class NativeHooks
    {
        private sealed record Hook(Type Owner, string Prefix, string Postfix, string Finalizer);
        private static readonly Dictionary<(Type Type, string Method), List<Hook>> Hooks = new();
        internal static void Reset() => Hooks.Clear();
        internal static void Patch(Type owner, Type target, string method, string prefix = null, string postfix = null, string finalizer = null, params Type[] args)
        {
            if (target.GetMethod(method, args) == null) throw new MissingMethodException(target.Name, method);
            if (!Hooks.TryGetValue((target, method), out var hooks)) Hooks[(target, method)] = hooks = new();
            hooks.Add(new Hook(owner, prefix, postfix, finalizer));
        }
        internal static void Invoke(object instance, string method, Dictionary<string, object> arguments, System.Action body)
        {
            var hooks = Hooks.TryGetValue((instance.GetType(), method), out var existing) ? existing.ToArray() : Array.Empty<Hook>();
            var states = new object[hooks.Length];
            Exception error = null;
            var run = true;
            try
            {
                for (var i = 0; i < hooks.Length; i++)
                {
                    var result = Call(hooks[i], hooks[i].Prefix, instance, arguments, ref states[i], error);
                    if (result is false) run = false;
                }
                if (run) body();
                for (var i = 0; i < hooks.Length; i++) Call(hooks[i], hooks[i].Postfix, instance, arguments, ref states[i], error);
            }
            catch (Exception exception) { error = exception; }
            for (var i = hooks.Length - 1; i >= 0; i--)
                if (hooks[i].Finalizer != null) error = (Exception)Call(hooks[i], hooks[i].Finalizer, instance, arguments, ref states[i], error);
            if (error != null) throw error;
        }
        private static object Call(Hook hook, string name, object instance, Dictionary<string, object> args, ref object state, Exception error)
        {
            if (name == null) return null;
            var method = hook.Owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException(hook.Owner.Name, name);
            var parameters = method.GetParameters();
            var values = new object[parameters.Length];
            var stateIndex = -1;
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                values[i] = parameter.Name switch
                {
                    "__instance" => instance,
                    "__state" => state,
                    "__exception" => error,
                    _ => args.TryGetValue(parameter.Name, out var value) ? value : throw new InvalidOperationException("Missing argument " + parameter.Name)
                };
                if (parameter.Name == "__state") stateIndex = i;
            }
            try { return method.Invoke(null, values); }
            catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
            finally { if (stateIndex >= 0) state = values[stateIndex]; }
        }
    }
}
