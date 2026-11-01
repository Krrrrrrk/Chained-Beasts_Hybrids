using ChainedBeasts.ModFoundation.DevTools;
using UnityEngine;

namespace ChainedBeasts.Hybrids;

internal static class DeveloperUnlockMenu
{
    private static IDisposable _registration;
    private static readonly string[] Tiers = { "IV", "V", "VI" };
    internal static void Install()
    {
        _registration?.Dispose();
        _registration = DeveloperMenuExtensions.Register(Plugin.Id + ":unlocks", "Hybrid beast levels", Draw);
    }
    internal static void Shutdown() { _registration?.Dispose(); _registration = null; }
    private static void Draw(Action<string> status)
    {
        if (!HybridRuntime.CanEditProgression(out var reason)) { GUILayout.Label(reason); return; }
        GUILayout.Label("Checked = unlocked across all beasts on this profile.");
        for (var variant = 0; variant < 3; variant++)
        {
            var unlocked = (HybridRuntime.LocalMask & (1 << variant)) != 0;
            var next = GUILayout.Toggle(unlocked, "Level " + Tiers[variant] + (unlocked ? " - Unlocked" : " - Locked"));
            if (next == unlocked) continue;
            if (HybridRuntime.SetDeveloperUnlocked(variant, next, out var result)) status(result);
            else status(result ?? "Could not save hybrid progression.");
        }
        GUILayout.Space(6f);
        GUILayout.BeginHorizontal();
        try
        {
            if (GUILayout.Button("Unlock all IV / V / VI"))
            {
                HybridRuntime.SetDeveloperMask(7, out var result);
                status(result);
            }
            if (GUILayout.Button("Lock all IV / V / VI"))
            {
                HybridRuntime.SetDeveloperMask(0, out var result);
                status(result);
            }
        }
        finally { GUILayout.EndHorizontal(); }
        GUILayout.Space(6f);
        GUILayout.Label("Normal beast variants are still required.");
        GUILayout.Label("Saved for future selections. Current-run perks stay as they are.");
    }
}
