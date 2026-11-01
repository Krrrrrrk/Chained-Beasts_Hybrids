using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChainedBeasts.Hybrids.Core;

/// <summary>
/// Profile-local fresh-clear unlocks. The caller supplies a durable run identity and an observed
/// current victory. This store never opens native save files or imports historical results.
/// </summary>
public sealed class UnlockStore
{
    private const int LegacySchemaVersion = 1;
    private const int OverrideSchemaVersion = 2;
    private const int MaximumFileBytes = 131072;
    private const int MaximumConsumedReceipts = 512;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Action<string> _warn;
    private readonly object _gate;
    private State _state = State.Empty();
    private bool _canWrite = true;

    public UnlockStore(string path, Action<string> warn)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An unlock file path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _warn = warn;
        _gate = Gates.GetOrAdd(_path, _ => new object());
        lock (_gate) Reload();
    }

    public bool CanWrite { get { lock (_gate) return _canWrite; } }

    public bool IsUnlocked(int variant)
    {
        HybridCatalog.ValidateVariant(variant);
        lock (_gate) return _canWrite && Effective(_state, variant);
    }

    /// <summary>Persist an explicit developer choice without inventing or removing earned victories.</summary>
    public bool SetDeveloperUnlocked(int variant, bool unlocked)
    {
        HybridCatalog.ValidateVariant(variant);
        return EditDeveloper(state => state.Overrides[variant] = unlocked);
    }

    /// <summary>Set all three developer overrides in one atomic write. Bit 0 is IV, bit 1 V, bit 2 VI.</summary>
    public bool SetDeveloperMask(int mask)
    {
        if (mask < 0 || mask > 7) throw new ArgumentOutOfRangeException(nameof(mask), "Expected a three-tier mask from 0 through 7.");
        return EditDeveloper(state =>
        {
            for (var variant = 0; variant < 3; variant++) state.Overrides[variant] = (mask & (1 << variant)) != 0;
        });
    }

    private bool EditDeveloper(Action<State> edit)
    {
        lock (_gate)
        {
            if (!_canWrite || !Reload()) return false;
            var next = _state.Copy();
            next.EnableOverrides();
            edit(next);
            if (_state.SchemaVersion == OverrideSchemaVersion && next.Overrides.SequenceEqual(_state.Overrides)) return true;
            return Save(next, "Developer hybrid choices could not be saved; the previous choices were retained. ");
        }
    }

    private static bool Effective(State state, int variant) => state.Overrides?[variant] ?? state.Unlocked[variant];

    /// <summary>
    /// Returns true only after a new tier unlock is durably published. Only exact difficulties
    /// 4, 5, and 6 qualify. A fresh victory clears that tier's developer override. A previously
    /// consumed receipt cannot clear an override. Already visible unlocks and write failures return false.
    /// </summary>
    public bool RecordVictory(int difficulty, string durableRunKey)
    {
        if (difficulty < 4 || difficulty > 6) return false;
        if (string.IsNullOrWhiteSpace(durableRunKey) || durableRunKey.Length > 512)
        {
            Warn("Hybrid victory was not recorded: a bounded, durable run identity is required.");
            return false;
        }
        lock (_gate)
        {
            if (!_canWrite) return false;
            // Merge another live store's writes and recheck the actual file before replacing it.
            // Never overwrite a file that became corrupt or unsupported after construction.
            if (!Reload()) return false;
            var receipt = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(durableRunKey)));
            var variant = difficulty - 4;
            if (_state.Receipts.ContainsKey(receipt) || _state.ConsumedReceipts?.Contains(receipt) == true) return false;
            if (_state.Unlocked[variant] && _state.Overrides?[variant] == null) return false;
            var wasUnlocked = Effective(_state, variant);
            var next = _state.Copy();
            if (next.SchemaVersion == OverrideSchemaVersion)
            {
                if (next.ConsumedReceipts.Count >= MaximumConsumedReceipts)
                {
                    Warn("Hybrid victory was not recorded because the developer replay history is full. Existing unlocks, overrides, and receipts were preserved.");
                    return false;
                }
                next.ConsumedReceipts.Add(receipt);
                next.Overrides[variant] = null;
                foreach (var prior in next.Receipts.Where(item => item.Value == difficulty).Select(item => item.Key).ToArray()) next.Receipts.Remove(prior);
            }
            next.Unlocked[variant] = true;
            next.Receipts.Add(receipt, difficulty);
            return Save(next, "Hybrid unlock could not be saved; no new unlock was applied. ") && !wasUnlocked;
        }
    }

    private bool Save(State next, string warning)
    {
        try { Publish(next); _state = next; return true; }
        catch (Exception error) when (IsStorageFailure(error)) { Warn(warning + error.Message); return false; }
    }

    private bool Reload()
    {
        try
        {
            if (Directory.Exists(_path)) throw new InvalidDataException("The unlock file path is a directory.");
            _state = File.Exists(_path) ? Read(_path) : State.Empty();
            return true;
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            _state = State.Empty();
            _canWrite = false;
            Warn("Hybrid unlock file is unreadable or unsupported. All hybrid tiers stay locked and the file is preserved. " + error.Message);
            return false;
        }
    }

    private static State Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Unlock file exceeds its size limit.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unlock state must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate unlock-state field.");
        if (!root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) ||
            version is not (LegacySchemaVersion or OverrideSchemaVersion))
            throw new InvalidDataException("Unsupported hybrid unlock schema.");
        var expected = version == LegacySchemaVersion
            ? new[] { "schemaVersion", "unlocked", "receipts" }
            : new[] { "schemaVersion", "unlocked", "receipts", "overrides", "consumedReceipts" };
        if (names.Count != expected.Length || expected.Any(name => !names.Contains(name)))
            throw new InvalidDataException("Unknown or missing unlock-state field.");
        var flags = root.GetProperty("unlocked");
        if (flags.ValueKind != JsonValueKind.Array || flags.GetArrayLength() != 3)
            throw new InvalidDataException("Expected exactly three unlock flags.");
        var state = State.Empty();
        state.SchemaVersion = version;
        var index = 0;
        foreach (var flag in flags.EnumerateArray())
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Unlock flags must be boolean values.");
            state.Unlocked[index++] = flag.GetBoolean();
        }
        var receipts = root.GetProperty("receipts");
        if (receipts.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid victory receipts.");
        var recordedDifficulties = new HashSet<int>();
        foreach (var receipt in receipts.EnumerateObject())
        {
            if (state.Receipts.Count >= 3 || !ValidReceipt(receipt.Name) ||
                !receipt.Value.TryGetInt32(out var difficulty) || difficulty < 4 || difficulty > 6 ||
                !recordedDifficulties.Add(difficulty) || !state.Receipts.TryAdd(receipt.Name, difficulty))
                throw new InvalidDataException("Invalid or duplicate victory receipt.");
        }
        for (var variant = 0; variant < 3; variant++)
            if (state.Unlocked[variant] != recordedDifficulties.Contains(variant + 4))
                throw new InvalidDataException("Unlock flags and victory receipts do not agree.");
        if (version == OverrideSchemaVersion)
        {
            var overrides = root.GetProperty("overrides");
            if (overrides.ValueKind != JsonValueKind.Array || overrides.GetArrayLength() != 3)
                throw new InvalidDataException("Expected exactly three nullable developer overrides.");
            state.Overrides = new bool?[3];
            index = 0;
            foreach (var item in overrides.EnumerateArray())
            {
                if (item.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Developer overrides must be null or boolean values.");
                state.Overrides[index++] = item.ValueKind == JsonValueKind.Null ? null : item.GetBoolean();
            }
            var consumed = root.GetProperty("consumedReceipts");
            if (consumed.ValueKind != JsonValueKind.Array || consumed.GetArrayLength() > MaximumConsumedReceipts)
                throw new InvalidDataException("Invalid developer victory history.");
            state.ConsumedReceipts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in consumed.EnumerateArray())
            {
                var value = item.GetString();
                if (!ValidReceipt(value) || !state.ConsumedReceipts.Add(value))
                    throw new InvalidDataException("Invalid or duplicate consumed victory receipt.");
            }
            if (state.Receipts.Keys.Any(receipt => !state.ConsumedReceipts.Contains(receipt)))
                throw new InvalidDataException("A current victory receipt is missing from replay history.");
        }
        return state;
    }

    private static bool ValidReceipt(string receipt) => receipt?.Length == 64 &&
        !receipt.Any(character => !(character >= '0' && character <= '9') && !(character >= 'A' && character <= 'F'));

    private void Publish(State next)
    {
        var directory = Path.GetDirectoryName(_path);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Path.GetFileName(_path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            // Replace and backup are one filesystem operation. There is deliberately no fallback
            // that deletes the original first or copies a partial JSON document over it.
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", true);
            else File.Move(temporary, _path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void Warn(string message)
    {
        try { _warn?.Invoke(message); }
        catch { /* A caller's logger must not change persistence behavior. */ }
    }

    private static bool IsStorageFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or InvalidOperationException or NotSupportedException or System.Security.SecurityException;

    private sealed class State
    {
        public int SchemaVersion { get; set; }
        public bool[] Unlocked { get; set; }
        public Dictionary<string, int> Receipts { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool?[] Overrides { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HashSet<string> ConsumedReceipts { get; set; }
        public static State Empty() => new()
        {
            SchemaVersion = LegacySchemaVersion,
            Unlocked = new bool[3],
            Receipts = new Dictionary<string, int>(StringComparer.Ordinal)
        };
        public State Copy() => new()
        {
            SchemaVersion = SchemaVersion,
            Unlocked = (bool[])Unlocked.Clone(),
            Receipts = new Dictionary<string, int>(Receipts, StringComparer.Ordinal),
            Overrides = Overrides == null ? null : (bool?[])Overrides.Clone(),
            ConsumedReceipts = ConsumedReceipts == null ? null : new HashSet<string>(ConsumedReceipts, StringComparer.Ordinal)
        };
        public void EnableOverrides()
        {
            if (SchemaVersion == OverrideSchemaVersion) return;
            SchemaVersion = OverrideSchemaVersion;
            Overrides = new bool?[3];
            ConsumedReceipts = new HashSet<string>(Receipts.Keys, StringComparer.Ordinal);
        }
    }
}
