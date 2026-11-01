using ChainedBeasts.Hybrids;
using ChainedBeasts.Hybrids.Core;
using ChainedBeasts.ModFoundation.DevTools;
using UnityEngine;

var checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
var root = Path.Combine(Path.GetTempPath(), "hybrid-menu-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "unlocks.json");
HybridRuntime.Store = new UnlockStore(path, _ => { });
var statuses = new List<string>();
void Draw() { DeveloperMenuExtensions.Draw(_ => { }, statuses.Add); Check(GUILayout.HorizontalDepth == 0, "Balanced GUILayout group"); }

try
{
    Check(!DeveloperMenuExtensions.HasPanels, "Foundation starts without optional providers");
    DeveloperUnlockMenu.Install();
    Check(DeveloperMenuExtensions.HasPanels, "Hybrid panel registers through the actual Foundation API");
    Draw(); Check(HybridRuntime.LocalMask == 0 && !File.Exists(path), "Drawing does not unlock or write flags");

    GUILayout.ToggleNext = "Level IV - Locked";
    Draw(); Check(HybridRuntime.LocalMask == 1, "Actual IV toggle saves only IV");
    Check(new UnlockStore(path, _ => { }).IsUnlocked(0), "Toggle survives reopening the store");
    Check(statuses.Last().Contains("unlocked"), "Toggle reports result");
    GUILayout.ToggleNext = "Level V - Locked";
    Draw(); Check(HybridRuntime.LocalMask == 3, "Actual V toggle preserves IV");
    GUILayout.ToggleNext = "Level VI - Locked";
    Draw(); Check(HybridRuntime.LocalMask == 7, "Actual VI toggle completes all three");
    GUILayout.ToggleNext = "Level V - Unlocked";
    Draw(); Check(HybridRuntime.LocalMask == 5, "Actual V toggle locks only V");

    GUILayout.ButtonNext = "Unlock all IV / V / VI";
    Draw(); Check(HybridRuntime.LocalMask == 7, "Unlock all callback uses persisted mask");
    GUILayout.ButtonNext = "Lock all IV / V / VI";
    Draw(); Check(HybridRuntime.LocalMask == 0, "Lock all callback uses persisted mask");
    var bytes = File.ReadAllBytes(path);
    Draw(); Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "Repaint does not rewrite overrides");

    HybridRuntime.Allowed = false;
    GUILayout.ButtonNext = "Unlock all IV / V / VI";
    Draw(); Check(HybridRuntime.LocalMask == 0, "Unavailable session cannot render/edit controls");
    Check(GUILayout.Labels.Last().Contains("Solo"), "Unavailable state explains the Solo requirement");
    GUILayout.ButtonNext = null;
    HybridRuntime.Allowed = true;
    HybridRuntime.RevokeOnNextEdit = true;
    GUILayout.ToggleNext = "Level IV - Locked";
    Draw(); Check(HybridRuntime.LocalMask == 0, "Authority loss at callback recheck refuses the edit");
    Check(statuses.Last().Contains("unavailable"), "Authority refusal is visible");
    HybridRuntime.Allowed = true;

    // A real earned victory remains separate from a subsequent F1 lock.
    Check(HybridRuntime.Store.RecordVictory(5, "fixture-tribune-win"), "Earned victory saved");
    GUILayout.ToggleNext = "Level V - Unlocked";
    Draw(); Check(!HybridRuntime.Store.IsUnlocked(1), "Actual toggle can lock an earned tier");
    using (var data = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
        Check(data.RootElement.GetProperty("unlocked")[1].GetBoolean(), "F1 lock preserves earned flag");

    DeveloperUnlockMenu.Shutdown();
    Check(!DeveloperMenuExtensions.HasPanels, "Provider unregisters without leaving F1 buttons behind");
    DeveloperUnlockMenu.Shutdown();
    Check(!DeveloperMenuExtensions.HasPanels, "Provider shutdown is idempotent");
    DeveloperUnlockMenu.Install(); DeveloperUnlockMenu.Install();
    Check(DeveloperMenuExtensions.HasPanels, "Reinstall disposes its prior registration");
    DeveloperUnlockMenu.Shutdown();

    var healthyCalls = 0;
    using var bad = DeveloperMenuExtensions.Register("a-broken", "Broken", _ => throw new InvalidOperationException("fixture"));
    using var good = DeveloperMenuExtensions.Register("b-good", "Good", _ => healthyCalls++);
    Draw(); Check(healthyCalls == 1, "One provider failure does not suppress the next panel");
    Check(statuses.Any(s => s.Contains("Broken unavailable")), "Provider failure is reported");
    try { DeveloperMenuExtensions.Register("b-good", "Duplicate", _ => { }); throw new Exception("Duplicate accepted"); }
    catch (InvalidOperationException) { checks++; }
    good.Dispose(); good.Dispose();
    Draw(); Check(healthyCalls == 1, "Disposed callbacks no longer run");
    bad.Dispose();
    Check(!DeveloperMenuExtensions.HasPanels, "Disposing providers removes only their registrations");
    Console.WriteLine($"PASS: {checks} checks against the actual Foundation registry, hybrid F1 panel and unlock store; managed GUI/session stubs only.");
}
finally
{
    DeveloperUnlockMenu.Shutdown();
    // Only the unique fixture directory created above is removed.
    var full = Path.GetFullPath(root);
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("hybrid-menu-fixture-", StringComparison.Ordinal))
        throw new InvalidOperationException("Fixture cleanup escaped its unique temporary directory.");
    if (Directory.Exists(full)) Directory.Delete(full, true);
}

namespace ChainedBeasts.Hybrids
{
    internal static class Plugin { internal const string Id = "local.chainedbeasts.hybrids.fixture"; }
    // A session boundary adapter. Production provider and persistence run above;
    // no Unity or native game handlers are linked into this fixture.
    internal static class HybridRuntime
    {
        internal static UnlockStore Store;
        internal static bool Allowed = true;
        internal static bool RevokeOnNextEdit;
        internal static int LocalMask => Enumerable.Range(0, 3).Where(Store.IsUnlocked).Aggregate(0, (mask, i) => mask | (1 << i));
        internal static bool CanEditProgression(out string reason) { reason = Allowed ? null : "Solo profile unavailable"; return Allowed; }
        private static bool Recheck(out string result)
        {
            if (RevokeOnNextEdit) { RevokeOnNextEdit = false; Allowed = false; }
            return CanEditProgression(out result);
        }
        internal static bool SetDeveloperUnlocked(int variant, bool value, out string result)
        {
            if (!Recheck(out result)) return false;
            var okay = Store.SetDeveloperUnlocked(variant, value);
            result = okay ? (value ? "unlocked" : "locked") : "write failed";
            return okay;
        }
        internal static bool SetDeveloperMask(int mask, out string result)
        {
            if (!Recheck(out result)) return false;
            var okay = Store.SetDeveloperMask(mask);
            result = okay ? "saved all" : "write failed";
            return okay;
        }
    }
}

namespace UnityEngine
{
    internal static class GUILayout
    {
        internal static string ToggleNext, ButtonNext;
        internal static int HorizontalDepth;
        internal static readonly List<string> Labels = new();
        public static void Label(string text) => Labels.Add(text);
        public static bool Toggle(bool value, string label) { if (ToggleNext != label) return value; ToggleNext = null; return !value; }
        public static bool Button(string label) { if (ButtonNext != label) return false; ButtonNext = null; return true; }
        public static void BeginHorizontal() => HorizontalDepth++;
        public static void EndHorizontal() => HorizontalDepth--;
        public static void Space(float amount) { }
    }
}
