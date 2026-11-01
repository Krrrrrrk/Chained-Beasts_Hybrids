using System;
using System.Collections.Generic;
using System.Linq;

namespace ChainedBeasts.ModFoundation.DevTools;

/// <summary>Optional F1 progression panels supplied by other installed mods.</summary>
public static class DeveloperMenuExtensions
{
    private static readonly Dictionary<string, Panel> Panels = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>
    /// Register a small progression panel. Foundation calls it only from its Solo
    /// developer menu. Providers must recheck authority before changing saved data.
    /// Dispose the registration when unloading the provider.
    /// </summary>
    public static IDisposable Register(string key, string title, Action<Action<string>> draw)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title) || draw == null)
            throw new ArgumentException("A developer panel requires a key, title, and draw callback.");
        var panel = new Panel(key, title, draw);
        lock (Gate)
        {
            if (Panels.ContainsKey(key)) throw new InvalidOperationException("Developer panel is already registered: " + key);
            Panels.Add(key, panel);
        }
        return panel;
    }
    public static bool HasPanels { get { lock (Gate) return Panels.Count != 0; } }
    internal static void Draw(Action<string> heading, Action<string> status)
    {
        Panel[] snapshot;
        lock (Gate) snapshot = Panels.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        foreach (var panel in snapshot)
        {
            if (panel.Disposed) continue;
            try { heading(panel.Title); panel.Callback(status); }
            catch (Exception error) { status?.Invoke(panel.Title + " unavailable: " + error.GetType().Name); }
        }
    }
    private sealed class Panel : IDisposable
    {
        internal readonly string Key;
        internal readonly string Title;
        internal readonly Action<Action<string>> Callback;
        internal bool Disposed;
        internal Panel(string key, string title, Action<Action<string>> draw) { Key = key; Title = title; Callback = draw; }
        public void Dispose()
        {
            lock (Gate)
            {
                if (Disposed) return;
                Disposed = true;
                if (Panels.TryGetValue(Key, out var current) && ReferenceEquals(current, this)) Panels.Remove(Key);
            }
        }
    }
}
