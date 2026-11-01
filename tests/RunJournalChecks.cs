using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChainedBeasts.Hybrids;

internal static class RunJournalChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "campaign.json");
        var first = new RunJournal(path);
        check(first.Receipt("old-native-run", 4) == null, "missing journal cannot recognize a historical run");
        check(!File.Exists(path), "journal reading does not create a campaign");
        check(first.Begin("fresh-campaign-four", 4), "a fresh campaign creates a durable journal");
        var four = first.Receipt("fresh-campaign-four", 4);
        check(Guid.TryParseExact(four, "N", out _), "campaign receipt is a durable UUID");
        check(first.Receipt("fresh-campaign-four", 5) == null, "receipt is bound to observed difficulty");
        check(first.Receipt("different-run", 4) == null, "receipt is bound to observed run identity");
        var original = File.ReadAllBytes(path);
        check(first.Begin("fresh-campaign-four", 4), "nested native start observation is idempotent");
        check(original.SequenceEqual(File.ReadAllBytes(path)), "duplicate start does not rewrite the receipt");
        check(!first.Begin("fresh-campaign-four", 5), "one observed run cannot change its qualifying difficulty");
        check(original.SequenceEqual(File.ReadAllBytes(path)), "conflicting difficulty preserves the original receipt");
        var resumed = new RunJournal(path);
        check(resumed.Receipt("fresh-campaign-four", 4) == four, "resume recovers the post-install receipt");
        check(resumed.Begin("fresh-campaign-six", 6), "a new campaign can replace the journal");
        var six = resumed.Receipt("fresh-campaign-six", 6);
        check(six != null && six != four, "a different fresh campaign receives a distinct receipt");
        check(resumed.Receipt("fresh-campaign-four", 4) == null, "old campaign is not accidentally current");
        check(original.SequenceEqual(File.ReadAllBytes(path + ".bak")), "journal backup retains the prior complete campaign");

        const string future = "{\"Schema\":99,\"Run\":\"future\",\"Receipt\":\"0123456789abcdef0123456789abcdef\",\"Difficulty\":4}";
        var invalid = new List<string>
        {
            "{", "null", future,
            "{\"Run\":\"missing-schema\",\"Receipt\":\"0123456789abcdef0123456789abcdef\",\"Difficulty\":4}",
            "{\"Schema\":1,\"Receipt\":\"0123456789abcdef0123456789abcdef\",\"Difficulty\":4}",
            "{\"Schema\":1,\"Run\":\"\",\"Receipt\":\"0123456789abcdef0123456789abcdef\",\"Difficulty\":4}",
            "{\"Schema\":1,\"Run\":\"bad-receipt\",\"Receipt\":\"invalid\",\"Difficulty\":4}",
            "{\"Schema\":1,\"Schema\":1,\"Run\":\"duplicate-schema\",\"Receipt\":\"0123456789abcdef0123456789abcdef\",\"Difficulty\":4}",
            "{\"Schema\":1,\"Run\":\"future-field\",\"Receipt\":\"0123456789abcdef0123456789abcdef\",\"Difficulty\":4,\"Unknown\":true}",
            "{\"Schema\":1,\"Run\":\"wrong-type\",\"Receipt\":\"0123456789abcdef0123456789abcdef\",\"Difficulty\":\"4\"}",
            "{\"Schema\":1,\"Run\":\"  \",\"Receipt\":\"0123456789abcdef0123456789abcdef\",\"Difficulty\":4}",
            new string('x', 4097)
        };
        for (var index = 0; index < invalid.Count; index++)
        {
            var malformedPath = Path.Combine(root, "malformed-" + index + ".json");
            File.WriteAllText(malformedPath, invalid[index]);
            var saved = File.ReadAllBytes(malformedPath);
            var journal = new RunJournal(malformedPath);
            check(!journal.CanWrite, "malformed/future journal is explicitly read-only: case " + index);
            check(!journal.Begin("must-preserve-original", 4), "malformed/future journal cannot be overwritten: case " + index);
            check(saved.SequenceEqual(File.ReadAllBytes(malformedPath)), "malformed/future journal bytes preserved: case " + index);
        }
        var stalePath = Path.Combine(root, "changed-after-load.json");
        var stale = new RunJournal(stalePath);
        check(stale.Begin("first-campaign", 4), "initial campaign for disk-change test");
        File.WriteAllText(stalePath, future);
        check(!stale.Begin("second-campaign", 5), "future-schema replacement after load is detected before write");
        check(File.ReadAllText(stalePath) == future, "future-schema replacement remains untouched");

        var receiptPath = Path.Combine(root, "changed-before-victory.json");
        var receiptJournal = new RunJournal(receiptPath);
        check(receiptJournal.Begin("native-resume-run", 6), "receipt recheck fixture");
        File.WriteAllText(receiptPath, future);
        check(receiptJournal.Receipt("native-resume-run", 6) == null, "victory cannot use a cached receipt after unsupported replacement");
        check(!receiptJournal.CanWrite, "victory receipt recheck fails closed");
        check(File.ReadAllText(receiptPath) == future, "victory receipt recheck preserves future data");

        var modeA = new RunJournal(Path.Combine(root, "campaign-Solo.json"));
        var modeB = new RunJournal(Path.Combine(root, "campaign-Online.json"));
        check(modeA.Begin("solo-four", 4), "solo journal begins independently");
        check(modeB.Begin("online-five", 5), "online journal begins independently");
        check(modeA.Receipt("solo-four", 4) != null, "starting online does not erase solo receipt");
        check(modeB.Receipt("solo-four", 4) == null, "mode journals cannot borrow each other's receipts");

        var blockingFile = Path.Combine(root, "blocking-parent");
        File.WriteAllText(blockingFile, "keep-me");
        var failed = new RunJournal(Path.Combine(blockingFile, "campaign.json"));
        check(!failed.Begin("failed-campaign", 4), "failed write cannot establish a campaign receipt");
        check(!failed.CanWrite && failed.Receipt("failed-campaign", 4) == null, "failed journal stays read-only without a receipt");
        check(File.ReadAllText(blockingFile) == "keep-me", "failed journal write preserves blocking file");
        check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "journal writes leave no incomplete temporary files");
    }
}

// The linked journal only needs logging. This test assembly deliberately supplies
// a managed stub and references neither BepInEx nor native game assemblies.
namespace ChainedBeasts.Hybrids
{
    internal static class HybridRuntime
    {
        internal static TestLog Log = new();
    }
    internal sealed class TestLog
    {
        internal void LogWarning(string message) { }
        internal void LogError(string message) { }
    }
}
