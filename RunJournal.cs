using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace ChainedBeasts.Hybrids;

// A mod-created campaign receipt survives a native checkpoint without importing
// pre-install victories. No native save format is modified.
internal sealed class RunJournal
{
    internal sealed class State
    {
        public int Schema { get; set; }
        public string Run { get; set; }
        public string Receipt { get; set; }
        public int Difficulty { get; set; }
    }
    private readonly string _path;
    private readonly object _gate;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private bool _writable = true;
    private State _state;
    internal bool CanWrite { get { lock (_gate) return _writable; } }
    internal RunJournal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A campaign journal path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _gate = Gates.GetOrAdd(_path, _ => new object());
        lock (_gate) Reload();
    }
    private bool Reload()
    {
        try
        {
            if (Directory.Exists(_path)) throw new InvalidDataException("Run journal path is a directory.");
            _state = File.Exists(_path) ? Read(_path) : null;
            return true;
        }
        catch (Exception e)
        {
            _writable = false; _state = null;
            Warn("Run journal retained read-only: " + e.Message);
            return false;
        }
    }
    private static State Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4096) throw new InvalidDataException("Run journal too large.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Run journal must be an object.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!fields.Add(property.Name) || property.Name is not ("Schema" or "Run" or "Receipt" or "Difficulty"))
                throw new InvalidDataException("Unknown or duplicate run journal field.");
        if (fields.Count != 4 || !root.GetProperty("Schema").TryGetInt32(out var schema) || schema != 1)
            throw new InvalidDataException("Unsupported run journal schema.");
        var run = root.GetProperty("Run").GetString();
        var receipt = root.GetProperty("Receipt").GetString();
        if (string.IsNullOrWhiteSpace(run) || run.Length > 256 || !Guid.TryParseExact(receipt, "N", out _) ||
            !root.GetProperty("Difficulty").TryGetInt32(out var difficulty) || difficulty is < 1 or > 10)
            throw new InvalidDataException("Invalid run journal values.");
        return new State { Schema = 1, Run = run, Receipt = receipt, Difficulty = difficulty };
    }
    internal bool Begin(string run, int difficulty)
    {
        if (string.IsNullOrWhiteSpace(run) || run.Length > 256 || difficulty is < 1 or > 10) return false;
        lock (_gate)
        {
            if (!_writable || !Reload()) return false;
            if (_state?.Run == run)
            {
                // A send/receive replay must retain its original receipt. A conflicting
                // difficulty is not a second fresh campaign with the same identity.
                return _state.Difficulty == difficulty;
            }
            var next = new State { Schema = 1, Run = run, Difficulty = difficulty, Receipt = Guid.NewGuid().ToString("N") };
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
                if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak");
                else File.Move(temp, _path);
                _state = next; return true;
            }
            catch (Exception e)
            {
                _writable = false;
                Warn("Could not save campaign receipt: " + e.Message);
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception e) { Warn("Could not remove incomplete campaign journal: " + e.Message); }
            }
        }
    }
    internal string Receipt(string run, int difficulty)
    {
        lock (_gate)
        {
            if (!_writable || !Reload()) return null;
            return _state?.Run == run && _state.Difficulty == difficulty ? _state.Receipt : null;
        }
    }
    private static void Warn(string message)
    {
        try { HybridRuntime.Log?.LogWarning("[Hybrids] " + message); }
        catch { /* Logging cannot change journal behavior. */ }
    }
}
