using System.Text.Json;
using ChainedBeasts.Hybrids;

var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    checks.Add(name);
}
RunDataManager Setup(bool hybrid = true)
{
    RunHooks.Shutdown(); NativeHooks.Reset(); HybridRuntime.Reset(); UnityEngine.Debug.Warnings.Clear();
    var manager = new RunDataManager();
    NetworkSession.Instance = new NetworkSession { runDataManager = manager, sceneRoot = new GameServerLobby() };
    HybridRuntime.SelectionHybrid = hybrid;
    RunHooks.Install(new HarmonyLib.Harmony());
    return manager;
}
void Start(RunDataManager manager)
{
    var lobby = (GameServerLobby)NetworkSession.Instance.sceneRoot;
    lobby.StartBody = () =>
    {
        manager.StartNewRunRpc(12345, 2000000000, AscensionLevel.A4, new DifficultyConfig());
        manager.playerDatas[0].SetBeastType(BeastType.Lion, 0);
    };
    lobby.HandleGameStart();
}

var manager = Setup();
Start(manager);
var player = manager.playerDatas[0];
Check(HybridRuntime.Begins == 1, "one observed new-run receipt");
Check(player.Order.SequenceEqual(new[] { "skill:0", "skill:3", "starting-hp" }), "partner follows primary and precedes native HP calculation");
Check(player.SkillsSeenByStartingHp == 2, "starting HP sees both skills");
Check(player.beastType == BeastType.Lion && player.beastVariantIndex == 0, "primary beast appearance and native variant stay intact");
Check(!HybridRuntime.Armed, "host setup finalizer ends grant authorization");

manager = Setup(false); Start(manager); player = manager.playerDatas[0];
Check(player.HasSkill((SkillType)0) == 1 && player.HasSkill((SkillType)3) == 0, "ordinary selection receives only native primary");

manager = Setup(); manager.NestedHostSend = true; Start(manager);
Check(HybridRuntime.Begins == 1, "nested host send and receive publish one start");
Check(manager.playerDatas[0].HasSkill((SkillType)3) == 1, "nested host start adds exactly one partner rank");

manager = Setup(); manager.SendOnly = true;
manager.StartNewRunRpc(12345, 2000000000, AscensionLevel.A4, new DifficultyConfig());
Check(HybridRuntime.Begins == 0, "send-only RPC return cannot authorize new-run grants");

manager = Setup();
manager.StartNewRunRpc(12345, 2000000000, AscensionLevel.A4, new DifficultyConfig(), 5);
Check(HybridRuntime.Begins == 0, "debug day skip cannot create a new campaign receipt");

manager = Setup();
var stale = new RunDataManager();
stale.StartNewRunRpc(12345, 2000000000, AscensionLevel.A4, new DifficultyConfig());
Check(HybridRuntime.Begins == 0, "stale manager cannot authorize grants");

manager = Setup();
manager.StartNewRunRpc(12345, 2000000000, AscensionLevel.A4, new DifficultyConfig());
player = manager.playerDatas[0];
player.AddSkill((SkillType)0, 1);
Check(player.HasSkill((SkillType)3) == 0, "ordinary AddSkill outside beast assignment cannot add a partner");

manager = Setup();
manager.StartNewRunRpc(12345, 2000000000, AscensionLevel.A4, new DifficultyConfig());
player = manager.playerDatas[0];
player.SetBeastType(BeastType.Lion, 0); player.SetBeastType(BeastType.Lion, 0);
Check(player.HasSkill((SkillType)3) == 1, "repeated native beast assignment cannot duplicate the partner");

manager = Setup();
manager.StartNewRunRpc(12345, 2000000000, AscensionLevel.A4, new DifficultyConfig());
player = manager.playerDatas[0];
manager.RestoreBody = () => player.SetBeastType(BeastType.Lion, 0);
manager.RestoreCheckpoint(new RunDataManager.Checkpoint());
Check(player.HasSkill((SkillType)3) == 0 && !HybridRuntime.Armed, "restore suppresses partner insertion and ends arming");

manager = Setup();
manager.StartNewRunRpc(12345, 2000000000, AscensionLevel.A4, new DifficultyConfig());
player = manager.playerDatas[0]; player.ThrowBeforePrimary = true;
var assignmentError = false;
try { player.SetBeastType(BeastType.Lion, 0); } catch (InvalidOperationException) { assignmentError = true; }
player.ThrowBeforePrimary = false; player.AddSkill((SkillType)0, 1);
Check(assignmentError && player.HasSkill((SkillType)3) == 0, "native assignment exception propagates and its scope is cleaned");

manager = Setup(); manager.ThrowDuringRestore = true;
var restoreError = false;
try { manager.RestoreCheckpoint(new RunDataManager.Checkpoint()); } catch (InvalidOperationException) { restoreError = true; }
manager.ThrowDuringRestore = false; Start(manager);
Check(restoreError && manager.playerDatas[0].HasSkill((SkillType)3) == 1, "restore exception propagates and does not poison later fresh starts");

manager = Setup(); HybridRuntime.AllowApply = false; Start(manager);
Check(manager.playerDatas[0].HasSkill((SkillType)3) == 0 && UnityEngine.Debug.Warnings.Count == 0, "runtime refusal preserves native base grant without exceptions");

manager = Setup();
var level = new GameLevel { ActiveGameState = GameLevel.eGameState.RUNNING, resultStep = GameLevel.eResultStep.GAME_WIN };
NetworkSession.Instance.sceneRoot = level;
GameLevel.RaiseCompletion();
Check(HybridRuntime.Victories == 0, "running scene cannot record victory");
level.LevelComplete = true; level.ActiveGameState = GameLevel.eGameState.LEVEL_COMPLETE; level.resultStep = GameLevel.eResultStep.DONE;
GameLevel.RaiseCompletion();
Check(HybridRuntime.Victories == 0, "ordinary completed level or stale final result cannot record victory");
level.resultStep = GameLevel.eResultStep.GAME_WIN; GameLevel.RaiseCompletion();
Check(HybridRuntime.Victories == 1, "genuine campaign event reaches receipt validation");
NetworkSession.Instance.sceneRoot = new GameServerLobby(); GameLevel.RaiseCompletion();
Check(HybridRuntime.Victories == 1, "completion event outside battle scene is ignored");
RunHooks.Shutdown();
Check(GameLevel.HandlerCount == 0, "shutdown removes only the rooted campaign handler");
GameLevel.RaiseCompletion();
Check(HybridRuntime.Victories == 1, "shutdown handler cannot award future victories");

Console.WriteLine(JsonSerializer.Serialize(new
{
    passed = checks.Count,
    scope = "Actual RunHooks.cs with minimal managed stubs and verified native call ordering; no Unity, IL2CPP, game launch or native handler execution.",
    checks
}, new JsonSerializerOptions { WriteIndented = true }));
