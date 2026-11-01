using BepInEx;
using BepInEx.Unity.IL2CPP;
using ChainedBeasts.ModFoundation.Networking;
using HarmonyLib;
using UnityEngine;

namespace ChainedBeasts.Hybrids;

[BepInPlugin(Id, "Chained Beasts Hybrids", Version)]
[BepInDependency("local.chainedbeasts.modfoundation", "1.1.1")]
public sealed class Plugin : BasePlugin
{
    public const string Id = "local.chainedbeasts.hybrids";
    public const string Version = "0.1.1";
    private Harmony _harmony;
    private HybridController _controller;
    public override void Load()
    {
        var enabled = Config.Bind("General", "Enabled", true, "Enable the fixed IV/V/VI hybrid loadouts on the normal character selector.").Value;
        MultiplayerServices.RegisterCompatibility(Id, Version, "enabled=" + enabled + ";exact-new-A4-A5-A6;same-row-roster-v1");
        MultiplayerServices.RegisterBinary(typeof(Plugin).Assembly);
        if (!enabled) { Log.LogInfo("Hybrids disabled."); return; }
        HybridRuntime.Log = Log;
        try
        {
            _harmony = new Harmony(Id);
            NativeHooks.Initialize(_harmony);
            HybridRuntime.Initialize();
            RunHooks.Install(_harmony);
            SelectionUi.Install(_harmony);
            _controller = AddComponent<HybridController>();
            HybridRuntime.Active = true;
            DeveloperUnlockMenu.Install();
            Log.LogInfo("Hybrids 0.1.1 ready. Hold ALT on character selection to preview IV/V/VI. F1 > Progression includes profile unlock controls for Solo testing.");
        }
        catch (Exception e)
        {
            HybridRuntime.Active = false;
            try { RunHooks.Shutdown(); } catch { }
            try { HybridRuntime.Shutdown(); } catch { }
            _harmony?.UnpatchSelf();
            Log.LogError("Hybrids could not start; native selection retained. " + e);
        }
    }
    public override bool Unload()
    {
        HybridRuntime.Active = false;
        HybridRuntime.Shutdown();
        RunHooks.Shutdown();
        _harmony?.UnpatchSelf();
        if (_controller != null) UnityEngine.Object.Destroy(_controller);
        return true;
    }
}

public sealed class HybridController : MonoBehaviour
{
    public HybridController(IntPtr pointer) : base(pointer) { }
    public void Update()
    {
        try { HybridRuntime.Tick(); }
        catch (Exception e) { HybridRuntime.Fault(e); }
    }
}
