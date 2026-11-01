using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChainedBeasts.Hybrids.Core;

internal static class Program
{
    private static int _checks;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "chained-beasts-hybrid-core-tests-" + Guid.NewGuid().ToString("N"));

    private static int Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            CatalogMatchesApprovedRoster();
            MissingFileAndExactVictoryDifficulties();
            ReplayAndProfileIsolation();
            BackupContainsPreviousCompleteState();
            InvalidFilesStayLockedAndPreserved();
            RecheckBeforeWriteAndMergeOtherStores();
            WriteFailureNeverAppliesAnUnlock();
            DeveloperOverridesPreserveEarnedHistory();
            InvalidDeveloperStatesArePreserved();
            DeveloperEditsAndVictoryFailuresAreAtomic();
            RunJournalChecks.Run(Path.Combine(Root, "journals"), Check);
            Console.WriteLine("PASS: " + _checks + " pure catalog, eligibility, and persistence checks. No game assemblies loaded.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL after " + _checks + " checks: " + error);
            return 1;
        }
        finally
        {
            var absolute = Path.GetFullPath(Root);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(absolute).StartsWith("chained-beasts-hybrid-core-tests-", StringComparison.Ordinal))
                Directory.Delete(absolute, true);
        }
    }

    private static void CatalogMatchesApprovedRoster()
    {
        int[] bases = { 0, 3, 6, 9, 15, 18, 21, 12 };
        var approvedPartners = new Dictionary<int, int>();
        using var approved = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "approved-roster.json")));
        foreach (var recipe in approved.RootElement.GetProperty("pairs").EnumerateArray())
        {
            var ids = recipe.GetProperty("skills").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            Equal(2, ids.Length, "approved recipe has exactly two components");
            Equal(4 + ids[0] % 3, recipe.GetProperty("tier").GetInt32(), "approved recipe tier agrees with its normal row");
            approvedPartners.Add(ids[0], ids[1]);
            approvedPartners.Add(ids[1], ids[0]);
        }
        var components = new HashSet<int>();
        var recipes = new HashSet<string>(StringComparer.Ordinal);
        for (var beast = 0; beast < 8; beast++)
        for (var variant = 0; variant < 3; variant++)
        {
            var skill = HybridCatalog.Skill(beast, variant);
            var partner = HybridCatalog.Partner(beast, variant);
            Equal(bases[beast] + variant, skill, "native beast mapping");
            Equal(approvedPartners[skill], partner, "partner agrees with approved roster JSON");
            Equal(variant, partner % 3, "same normal row");
            Check(skill / 3 != partner / 3, "different native skill family");
            var partnerBeast = Array.FindIndex(bases, value => value <= partner && partner <= value + 2);
            Equal(skill, HybridCatalog.Partner(partnerBeast, variant), "mirrored recipe");
            Check(components.Add(skill), "one owner row for every native beast perk");
            recipes.Add(Math.Min(skill, partner) + ":" + Math.Max(skill, partner));
        }
        Equal(24, components.Count, "all native perk IDs represented");
        Equal(12, recipes.Count, "twelve distinct mirrored recipes");
        Equal(20, HybridCatalog.Partner(1, 2), "Eagle VI has Reckless Rhino");
        Equal(5, HybridCatalog.Partner(5, 2), "Rhino VI has Patient Eagle");
        for (var variant = 0; variant < 3; variant++)
        {
            Equal(4 + variant, HybridCatalog.Tier(variant), "hybrid tier label");
            foreach (var global in new[] { false, true })
            foreach (var native in new[] { false, true })
                Equal(global && native, HybridCatalog.IsEligible(variant, global, native), "both prerequisites are required");
        }
        foreach (var beast in new[] { -1, 8, int.MaxValue })
        {
            Throws<ArgumentOutOfRangeException>(() => HybridCatalog.Skill(beast, 0), "invalid beast rejected");
            Throws<ArgumentOutOfRangeException>(() => HybridCatalog.Partner(beast, 0), "invalid partner beast rejected");
        }
        foreach (var variant in new[] { -1, 3, int.MaxValue })
        {
            Throws<ArgumentOutOfRangeException>(() => HybridCatalog.Skill(0, variant), "invalid skill row rejected");
            Throws<ArgumentOutOfRangeException>(() => HybridCatalog.Partner(0, variant), "invalid partner row rejected");
            Throws<ArgumentOutOfRangeException>(() => HybridCatalog.Tier(variant), "invalid label row rejected");
            Throws<ArgumentOutOfRangeException>(() => HybridCatalog.IsEligible(variant, true, true), "invalid eligibility row rejected");
        }
    }

    private static void MissingFileAndExactVictoryDifficulties()
    {
        var path = FilePath("exact");
        var warnings = new List<string>();
        var store = new UnlockStore(path, warnings.Add);
        Flags(store, false, false, false);
        Check(store.CanWrite, "missing file can be initialized");
        Check(!File.Exists(path), "reading missing state does not create a save");
        foreach (var difficulty in new[] { int.MinValue, -1, 0, 1, 2, 3, 7, 8, int.MaxValue })
            Check(!store.RecordVictory(difficulty, "unqualified-" + difficulty), "only exact A4/A5/A6 qualifies");
        Check(!File.Exists(path), "unqualified victories do not write");
        Check(store.RecordVictory(6, "fresh-six"), "A6 grants VI");
        Flags(store, false, false, true);
        Flags(new UnlockStore(path, warnings.Add), false, false, true);
        Check(store.RecordVictory(4, "fresh-four"), "A4 grants IV");
        Flags(store, true, false, true);
        Check(store.RecordVictory(5, "fresh-five"), "A5 grants V");
        Flags(new UnlockStore(path, warnings.Add), true, true, true);
        Equal(0, warnings.Count, "ordinary saves produce no warnings");
        Throws<ArgumentOutOfRangeException>(() => store.IsUnlocked(3), "invalid unlock row rejected");

        var historicalProfile = Path.Combine(Root, "historical-native-profile");
        Directory.CreateDirectory(historicalProfile);
        var nativePath = Path.Combine(historicalProfile, "SaveData");
        const string nativeHistory = "{\"victories\":[4,5,6]}";
        File.WriteAllText(nativePath, nativeHistory);
        Flags(new UnlockStore(Path.Combine(historicalProfile, "hybrids.json"), null), false, false, false);
        Equal(nativeHistory, File.ReadAllText(nativePath), "historical native save is neither read for unlocks nor modified");
    }

    private static void ReplayAndProfileIsolation()
    {
        var path = FilePath("replay");
        var store = new UnlockStore(path, null);
        Check(store.RecordVictory(4, "same-run"), "first fresh receipt accepted");
        var initial = File.ReadAllBytes(path);
        Check(!store.RecordVictory(4, "same-run"), "same callback replay rejected");
        Check(!store.RecordVictory(5, "same-run"), "one run cannot masquerade as a second difficulty");
        Check(!new UnlockStore(path, null).RecordVictory(6, "same-run"), "receipt survives restart");
        Check(!store.RecordVictory(4, "another-four"), "already unlocked tier does not create another receipt");
        Check(initial.SequenceEqual(File.ReadAllBytes(path)), "replays never rewrite the file");
        Flags(new UnlockStore(path, null), true, false, false);
        Flags(new UnlockStore(FilePath("different-profile"), null), false, false, false);
        Check(store.RecordVictory(5, "different-run"), "another fresh run can unlock its exact tier");
        using var state = JsonDocument.Parse(File.ReadAllText(path));
        Equal(2, state.RootElement.GetProperty("receipts").EnumerateObject().Count(), "flags and receipts saved together");
        Check(!File.ReadAllText(path).Contains("same-run", StringComparison.Ordinal), "only bounded run hashes are persisted");
        var invalidIdentityPath = FilePath("identity");
        var warnings = new List<string>();
        var invalidIdentity = new UnlockStore(invalidIdentityPath, warnings.Add);
        foreach (var identity in new[] { null, "", "  ", new string('x', 513) })
            Check(!invalidIdentity.RecordVictory(4, identity), "missing or oversized run identity rejected");
        Check(!File.Exists(invalidIdentityPath), "invalid identity cannot initialize an unlock file");
        Equal(4, warnings.Count, "invalid qualifying identities produce a useful warning");
    }

    private static void BackupContainsPreviousCompleteState()
    {
        var path = FilePath("backup");
        var store = new UnlockStore(path, null);
        Check(store.RecordVictory(4, "backup-four"), "initial save");
        var first = File.ReadAllBytes(path);
        Check(store.RecordVictory(5, "backup-five"), "atomic replacement");
        Check(first.SequenceEqual(File.ReadAllBytes(path + ".bak")), "backup contains the previous exact bytes");
        Flags(new UnlockStore(path + ".bak", null), true, false, false);
        Flags(new UnlockStore(path, null), true, true, false);
        var second = File.ReadAllBytes(path);
        Check(store.RecordVictory(6, "backup-six"), "third exact clear");
        Check(second.SequenceEqual(File.ReadAllBytes(path + ".bak")), "backup advances by one complete state");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path), "*.tmp-*").Any(), "temporary writes cleaned up");
    }

    private static void InvalidFilesStayLockedAndPreserved()
    {
        var invalid = new List<string>
        {
            "{", "null", "[]", "{}",
            "{\"schemaVersion\":99,\"unlocked\":[true,true,true],\"receipts\":{}}",
            "{\"schemaVersion\":0,\"unlocked\":[false,false,false],\"receipts\":{}}",
            "{\"schemaVersion\":\"1\",\"unlocked\":[false,false,false],\"receipts\":{}}",
            "{\"schemaVersion\":1,\"unlocked\":[false,false],\"receipts\":{}}",
            "{\"schemaVersion\":1,\"unlocked\":[false,0,false],\"receipts\":{}}",
            "{\"schemaVersion\":1,\"unlocked\":[true,false,false],\"receipts\":{}}",
            "{\"schemaVersion\":1,\"unlocked\":[false,false,false],\"receipts\":{\"bad\":4}}",
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"unlocked\":[false,false,false],\"receipts\":{}}",
            "{\"schemaVersion\":1,\"unlocked\":[false,false,false],\"receipts\":{},\"futureField\":true}",
            new string('x', 32769)
        };
        var receiptA = new string('A', 64);
        var receiptB = new string('B', 64);
        invalid.Add("{\"schemaVersion\":1,\"unlocked\":[true,false,false],\"receipts\":{\"" + receiptA + "\":4,\"" + receiptB + "\":4}}");
        invalid.Add("{\"schemaVersion\":1,\"unlocked\":[true,false,false],\"receipts\":{\"" + receiptA + "\":4,\"" + receiptA + "\":4}}");
        invalid.Add("{\"schemaVersion\":1,\"unlocked\":[true,false,false],\"receipts\":{\"" + receiptA + "\":7}}");
        invalid.Add("{\"schemaVersion\":1,\"unlocked\":[true,false,false],\"receipts\":{\"" + receiptA + "\":\"4\"}}");
        invalid.Add("{\"schemaVersion\":1,\"unlocked\":[false,false,false],\"receipts\":{\"" + receiptA + "\":4}}");
        for (var index = 0; index < invalid.Count; index++)
        {
            var path = FilePath("invalid-" + index);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, invalid[index]);
            var original = File.ReadAllBytes(path);
            var warnings = new List<string>();
            var store = new UnlockStore(path, warnings.Add);
            Flags(store, false, false, false);
            Check(!store.CanWrite, "invalid or future state fails closed");
            Check(!store.RecordVictory(4, "must-not-overwrite"), "invalid original is never overwritten");
            Check(original.SequenceEqual(File.ReadAllBytes(path)), "invalid original bytes preserved");
            Check(!File.Exists(path + ".bak"), "invalid original does not replace a valid backup");
            Equal(1, warnings.Count, "one clear warning at load");
        }
        var loggerPath = FilePath("logger-failure");
        Directory.CreateDirectory(Path.GetDirectoryName(loggerPath));
        File.WriteAllText(loggerPath, "{}");
        var brokenLogger = new UnlockStore(loggerPath, _ => throw new InvalidOperationException("logger unavailable"));
        Check(!brokenLogger.RecordVictory(4, "logger-run"), "logger failure cannot bypass fail-closed state");
    }

    private static void RecheckBeforeWriteAndMergeOtherStores()
    {
        var path = FilePath("merge");
        var first = new UnlockStore(path, null);
        var second = new UnlockStore(path, null);
        Check(first.RecordVictory(4, "merge-four"), "first store writes");
        Check(second.RecordVictory(5, "merge-five"), "second store rereads and merges");
        Flags(new UnlockStore(path, null), true, true, false);
        const string future = "{\"schemaVersion\":2,\"unlocked\":[true,true,true],\"receipts\":{}}";
        var backup = File.ReadAllBytes(path + ".bak");
        File.WriteAllText(path, future);
        Check(!first.RecordVictory(6, "merge-six"), "external unsupported replacement is caught before writing");
        Check(!first.CanWrite, "store becomes read-only after detecting replacement");
        Flags(first, false, false, false);
        Equal(future, File.ReadAllText(path), "external unsupported file preserved");
        Check(backup.SequenceEqual(File.ReadAllBytes(path + ".bak")), "last valid backup preserved too");
    }

    private static void WriteFailureNeverAppliesAnUnlock()
    {
        var parentFile = Path.Combine(Root, "not-a-directory");
        File.WriteAllText(parentFile, "keep-me");
        var path = Path.Combine(parentFile, "unlock.json");
        var warnings = new List<string>();
        var store = new UnlockStore(path, warnings.Add);
        Check(!store.RecordVictory(4, "failed-write"), "save failure returns false");
        Flags(store, false, false, false);
        Equal("keep-me", File.ReadAllText(parentFile), "blocking file preserved");
        Check(warnings.Count > 0, "save failure is reported");

        var directoryPath = Path.Combine(Root, "directory-instead-of-unlock");
        Directory.CreateDirectory(directoryPath);
        var directoryStore = new UnlockStore(directoryPath, null);
        Check(!directoryStore.CanWrite, "directory cannot be mistaken for an absent save");
        Check(!directoryStore.RecordVictory(4, "directory-run"), "directory path stays untouched");
    }

    private static string FilePath(string name) => Path.Combine(Root, name, "unlocks.json");

    private static void DeveloperOverridesPreserveEarnedHistory()
    {
        var path = FilePath("developer-legacy");
        var store = new UnlockStore(path, null);
        Check(store.RecordVictory(4, "earned-four-before-cheats"), "legacy victory fixture");
        var legacy = File.ReadAllBytes(path);
        using (var json = JsonDocument.Parse(legacy))
        {
            Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32(), "normal earned save remains schema 1");
            Equal(3, json.RootElement.EnumerateObject().Count(), "schema 1 does not gain developer-only fields");
        }
        Check(store.SetDeveloperUnlocked(0, false), "developer can lock an earned tier");
        Flags(store, false, false, false);
        Check(legacy.SequenceEqual(File.ReadAllBytes(path + ".bak")), "schema upgrade backs up the previous legacy state");
        using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
        using (var previous = JsonDocument.Parse(legacy))
        {
            Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32(), "first developer edit upgrades to schema 2");
            Check(json.RootElement.GetProperty("unlocked")[0].GetBoolean(), "developer lock preserves earned status");
            Equal(previous.RootElement.GetProperty("receipts").GetRawText(), json.RootElement.GetProperty("receipts").GetRawText(), "developer edit preserves genuine victory receipts");
            Equal(1, json.RootElement.GetProperty("consumedReceipts").GetArrayLength(), "migration seeds replay history from earned receipts");
            Equal(JsonValueKind.False, json.RootElement.GetProperty("overrides")[0].ValueKind, "locked developer choice is explicit");
        }
        var locked = File.ReadAllBytes(path);
        Check(store.SetDeveloperUnlocked(0, false), "idempotent developer lock reports success");
        Check(locked.SequenceEqual(File.ReadAllBytes(path)), "idempotent developer lock does not rewrite state");
        Flags(new UnlockStore(path, null), false, false, false);
        Check(!store.RecordVictory(4, "earned-four-before-cheats"), "replayed pre-cheat victory cannot undo a developer lock");
        Check(locked.SequenceEqual(File.ReadAllBytes(path)), "old victory replay leaves developer state untouched");
        Check(store.RecordVictory(4, "fresh-four-after-lock"), "fresh exact victory restores normal progression");
        Flags(store, true, false, false);
        using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            Equal(JsonValueKind.Null, json.RootElement.GetProperty("overrides")[0].ValueKind, "fresh victory clears the exact tier override");
            Equal(1, json.RootElement.GetProperty("receipts").EnumerateObject().Count(), "one current victory receipt per earned tier");
            Equal(2, json.RootElement.GetProperty("consumedReceipts").GetArrayLength(), "older consumed receipt retained after replacement");
        }
        Check(store.SetDeveloperUnlocked(0, false), "earned tier can be locked again");
        Check(!store.RecordVictory(4, "earned-four-before-cheats"), "old replaced receipt remains replay protected");
        Check(!store.RecordVictory(4, "fresh-four-after-lock"), "latest receipt remains replay protected");
        Flags(store, false, false, false);
        Check(store.RecordVictory(4, "another-fresh-four"), "another genuinely new exact win can clear a second developer lock");

        Check(store.SetDeveloperUnlocked(1, true), "developer can unlock an unearned tier");
        Flags(store, true, true, false);
        using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            Check(!json.RootElement.GetProperty("unlocked")[1].GetBoolean(), "developer unlock does not fabricate earned status");
            Equal(1, json.RootElement.GetProperty("receipts").EnumerateObject().Count(), "developer unlock does not fabricate victory receipts");
        }
        Check(!store.RecordVictory(5, "real-five-after-cheat"), "earning a visibly developer-unlocked tier does not report a new visible unlock");
        using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            Check(json.RootElement.GetProperty("unlocked")[1].GetBoolean(), "actual subsequent win records earned status");
            Equal(JsonValueKind.Null, json.RootElement.GetProperty("overrides")[1].ValueKind, "actual win replaces developer unlock with earned state");
        }
        var earned = File.ReadAllBytes(path);
        Check(!store.RecordVictory(4, "ordinary-already-earned-win"), "ordinary earned victories do not grow developer replay history");
        Check(earned.SequenceEqual(File.ReadAllBytes(path)), "ordinary earned victory is a storage no-op");

        Check(store.SetDeveloperMask(7), "unlock all is one atomic operation");
        Flags(store, true, true, true);
        Check(store.SetDeveloperMask(0), "lock all is one atomic operation");
        Flags(new UnlockStore(path, null), false, false, false);
        using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
        using (var previous = JsonDocument.Parse(earned))
        {
            Equal(previous.RootElement.GetProperty("unlocked").GetRawText(), json.RootElement.GetProperty("unlocked").GetRawText(), "all-tier edits retain earned flags");
            Equal(previous.RootElement.GetProperty("receipts").GetRawText(), json.RootElement.GetProperty("receipts").GetRawText(), "all-tier edits retain current victory receipts");
            Equal(previous.RootElement.GetProperty("consumedReceipts").GetRawText(), json.RootElement.GetProperty("consumedReceipts").GetRawText(), "all-tier edits retain complete consumed history");
        }
        Check(!store.RecordVictory(5, "real-five-after-cheat"), "lock-all survives replay of a real earned win");
        Check(store.SetDeveloperMask(5), "three-bit mask supports mixed developer choices");
        Flags(new UnlockStore(path, null), true, false, true);
        foreach (var mask in new[] { -1, 8, int.MaxValue })
            Throws<ArgumentOutOfRangeException>(() => store.SetDeveloperMask(mask), "invalid developer mask rejected");
        foreach (var variant in new[] { -1, 3 })
            Throws<ArgumentOutOfRangeException>(() => store.SetDeveloperUnlocked(variant, true), "invalid developer variant rejected");

        var freshPath = FilePath("developer-no-earned-history");
        var fresh = new UnlockStore(freshPath, null);
        Check(fresh.SetDeveloperMask(7), "developer controls work before any qualifying victories");
        Flags(new UnlockStore(freshPath, null), true, true, true);
        using var freshJson = JsonDocument.Parse(File.ReadAllBytes(freshPath));
        Check(freshJson.RootElement.GetProperty("unlocked").EnumerateArray().All(flag => !flag.GetBoolean()), "fresh developer controls leave all earned flags false");
        Equal(0, freshJson.RootElement.GetProperty("receipts").EnumerateObject().Count(), "fresh developer controls create no receipts");
        Equal(0, freshJson.RootElement.GetProperty("consumedReceipts").GetArrayLength(), "fresh developer controls create no consumed victories");
    }

    private static Dictionary<string, object> DeveloperState() => new()
    {
        ["schemaVersion"] = 2,
        ["unlocked"] = new[] { true, false, false },
        ["receipts"] = new Dictionary<string, int> { [new string('A', 64)] = 4 },
        ["overrides"] = new bool?[] { false, null, null },
        ["consumedReceipts"] = new[] { new string('A', 64) }
    };

    private static void InvalidDeveloperStatesArePreserved()
    {
        var cases = new List<Dictionary<string, object>>();
        var missingOverride = DeveloperState(); missingOverride.Remove("overrides"); cases.Add(missingOverride);
        var missingHistory = DeveloperState(); missingHistory.Remove("consumedReceipts"); cases.Add(missingHistory);
        var shortOverride = DeveloperState(); shortOverride["overrides"] = new bool?[] { true, false }; cases.Add(shortOverride);
        var invalidOverride = DeveloperState(); invalidOverride["overrides"] = new object[] { true, "false", null }; cases.Add(invalidOverride);
        var missingCurrentReceipt = DeveloperState(); missingCurrentReceipt["consumedReceipts"] = Array.Empty<string>(); cases.Add(missingCurrentReceipt);
        var duplicateHistory = DeveloperState(); duplicateHistory["consumedReceipts"] = new[] { new string('A', 64), new string('A', 64) }; cases.Add(duplicateHistory);
        var invalidHistory = DeveloperState(); invalidHistory["consumedReceipts"] = new object[] { new string('A', 64), null }; cases.Add(invalidHistory);
        var wrongHistory = DeveloperState(); wrongHistory["consumedReceipts"] = "not-an-array"; cases.Add(wrongHistory);
        var tooMuchHistory = DeveloperState(); tooMuchHistory["consumedReceipts"] = Enumerable.Range(0, 513).Select(value => value.ToString("X64")).ToArray(); cases.Add(tooMuchHistory);
        var future = DeveloperState(); future["schemaVersion"] = 3; cases.Add(future);
        var extra = DeveloperState(); extra["unknown"] = true; cases.Add(extra);
        for (var index = 0; index < cases.Count; index++)
        {
            var path = FilePath("developer-invalid-" + index);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(cases[index]));
            var original = File.ReadAllBytes(path);
            var store = new UnlockStore(path, null);
            Check(!store.CanWrite, "invalid schema 2 or future state is read-only");
            Flags(store, false, false, false);
            Check(!store.SetDeveloperUnlocked(0, true), "developer single-tier action cannot overwrite invalid state");
            Check(!store.SetDeveloperMask(7), "developer all-tier action cannot overwrite invalid state");
            Check(!store.RecordVictory(4, "must-preserve-schema-two"), "victory cannot overwrite invalid schema 2 state");
            Check(original.SequenceEqual(File.ReadAllBytes(path)), "invalid schema 2 bytes preserved");
        }

        var fullPath = FilePath("developer-history-full");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        var fullState = DeveloperState();
        fullState["consumedReceipts"] = Enumerable.Range(0, 511).Select(value => value.ToString("X64")).Append(new string('A', 64)).ToArray();
        File.WriteAllText(fullPath, JsonSerializer.Serialize(fullState));
        var warnings = new List<string>();
        var full = new UnlockStore(fullPath, warnings.Add);
        Check(full.CanWrite, "bounded full replay history is a valid state");
        var fullBytes = File.ReadAllBytes(fullPath);
        Check(!full.RecordVictory(4, "would-exceed-history"), "full history refuses a new receipt rather than forgetting replay protection");
        Check(fullBytes.SequenceEqual(File.ReadAllBytes(fullPath)), "full history preserves all prior data");
        Flags(full, false, false, false);
        Equal(1, warnings.Count, "full history explains the rejected progression update");
        Check(full.SetDeveloperMask(7), "developer controls remain usable when replay history is full");
        Flags(new UnlockStore(fullPath, null), true, true, true);
    }

    private static void DeveloperEditsAndVictoryFailuresAreAtomic()
    {
        var path = FilePath("developer-atomic-failure");
        var warnings = new List<string>();
        var store = new UnlockStore(path, warnings.Add);
        Check(store.SetDeveloperMask(7), "atomic failure fixture starts unlocked");
        var original = File.ReadAllBytes(path);
        Directory.CreateDirectory(path + ".bak");
        Check(!store.SetDeveloperMask(0), "failed lock-all is reported");
        Flags(store, true, true, true);
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "failed lock-all cannot persist a partial mask");
        Check(warnings.Count > 0, "failed developer write is explained");
        Directory.Delete(path + ".bak");
        Check(store.SetDeveloperMask(0), "developer write can retry after a temporary filesystem error");
        var locked = File.ReadAllBytes(path);
        File.Delete(path + ".bak");
        Directory.CreateDirectory(path + ".bak");
        Check(!store.RecordVictory(4, "failed-unlock-receipt"), "failed real-victory write reports no new unlock");
        Flags(store, false, false, false);
        Check(locked.SequenceEqual(File.ReadAllBytes(path)), "failed victory retains its developer lock and prior earned history");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path), "*.tmp-*").Any(), "failed developer writes leave no temporary JSON files");

        var replacedPath = FilePath("developer-external-future");
        var replaced = new UnlockStore(replacedPath, null);
        Check(replaced.SetDeveloperMask(7), "external replacement fixture");
        const string future = "{\"schemaVersion\":8}";
        File.WriteAllText(replacedPath, future);
        Check(!replaced.SetDeveloperMask(0), "developer edit rechecks a future external replacement");
        Equal(future, File.ReadAllText(replacedPath), "developer edit preserves future external bytes");
        Flags(replaced, false, false, false);
    }
    private static void Flags(UnlockStore store, params bool[] expected)
    {
        for (var index = 0; index < 3; index++) Equal(expected[index], store.IsUnlocked(index), "exact unlocked flags");
    }
    private static void Equal<T>(T expected, T actual, string label) => Check(EqualityComparer<T>.Default.Equals(expected, actual), label + ": expected " + expected + ", got " + actual);
    private static void Check(bool condition, string label)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(label);
    }
    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, label); return; }
        throw new InvalidOperationException(label + ": expected " + typeof(T).Name);
    }
}
