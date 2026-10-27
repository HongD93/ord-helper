using System.Text.Json;
using System.Text.Json.Nodes;

static class GameRunRecorderTests
{
    private static int checks;
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-27T00:00:00Z");
    public static void Run() => VerifyAsync().GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        checks = 0;
        await WithDirectory(VerifyStartGuards);
        await WithDirectory(VerifySemanticRecording);
        await WithDirectory(VerifyInactivePreservesFinalState);
        await WithDirectory(VerifyVictory);
        await WithDirectory(VerifyGapsAndResume);
        await WithDirectory(VerifyRestart);
        await WithDirectory(VerifyTerminalRestartRecovery);
        await WithDirectory(VerifyTerminalRestartBaselineGuards);
        await WithDirectory(VerifyTerminalRoundRegression);
        await WithDirectory(VerifySessionChange);
        await WithDirectory(VerifyAddressReuse);
        await WithDirectory(VerifyRoundRegression);
        await WithDirectory(VerifyWriteRetry);
        await WithDirectory(VerifyPendingTerminalBeforeNewRun);
        await WithDirectory(VerifyLockedRecord);
        await WithDirectory(VerifyOutOfOrder);
        await WithDirectory(VerifyObservationRoundRange);
        await WithDirectory(VerifyUnstableTerminal);
        await WithDirectory(VerifyConcurrentCalls);
        Console.WriteLine($"PASS: automatic run recording {checks} checks; native termination ambiguity, final active inventory, round changes, gaps, restart, atomic retry and session isolation.");
    }

    private static async Task VerifyStartGuards(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(new JsonObject { ["status"] = "game_not_running", ["timestamp"] = Time(1) }, default);
        Check(!Directory.Exists(directory), "a missing game creates no folder or fabricated run");
        var unknown = Sample(2); unknown["progression"]!["status"] = "unknown";
        await recorder.ProcessAsync(unknown, default);
        var deadFirst = Sample(3, active: false, ended: true);
        await recorder.ProcessAsync(deadFirst, default);
        var foreign = Sample(4); foreign["progression"]!["localPlayer"] = 1;
        await recorder.ProcessAsync(foreign, default);
        var wrongMap = Sample(5); wrongMap["loadedMapHash"] = new string('b', 64);
        await recorder.ProcessAsync(wrongMap, default);
        var unstable = Sample(6); unstable["stableUnits"] = false;
        await recorder.ProcessAsync(unstable, default);
        var unverifiedArchive = Sample(7); unverifiedArchive["catalogMatchesLoadedArchive"] = false;
        await recorder.ProcessAsync(unverifiedArchive, default);
        Check(recorder.CurrentForTests is null && !Directory.Exists(directory), "unknown, terminal-first, foreign, wrong-map and unstable samples cannot begin a run");
    }

    private static async Task VerifySemanticRecording(string directory)
    {
        var writes = 0;
        var recorder = new GameRunRecorder(directory, writeAtomically: async (file, json, cancel) =>
        {
            writes++; Directory.CreateDirectory(directory); await File.WriteAllTextAsync(file, json, cancel);
        });
        await recorder.ProcessAsync(Sample(1), default);
        Check(writes == 1 && Events(Read(recorder)).Count == 1, "one confirmed active sample begins one run and baseline event");
        var churn = Sample(2); churn["totalWorldEntries"] = 999; churn["worldArray"] = "0xDEADBEEF";
        churn["counts"]!.AsArray().Add(new JsonObject { ["rawcode"] = "h06O", ["name"] = "장난감", ["count"] = 20, ["isCharacter"] = true });
        churn["counts"]!.AsArray()[0]!["flagVariants"] = new JsonArray(new JsonObject { ["flags20"] = "changed" });
        await recorder.ProcessAsync(churn, default);
        await recorder.ProcessAsync(Sample(3), default);
        Check(writes == 1, "gold, sequence, time, enemy-array churn and noningredient toys do not write each tick");
        await recorder.ProcessAsync(Sample(4, round: 21), default);
        var doc = Read(recorder);
        Check(writes == 2 && Number(doc, "lastKnownRound") == 21, "a round-only change is durably recorded");
        Check(Events(doc).Last()!["type"]!.GetValue<string>() == "round_changed", "round-only recording has a distinct event");
        await recorder.ProcessAsync(Sample(5, round: 21, count: 3), default);
        doc = Read(recorder);
        Check(writes == 3 && Events(doc).Last()!["detail"]!["changes"]!.AsArray().Any(change => change!["rawcode"]!.GetValue<string>() == "h003"), "owned inventory changes record net deltas");
        var text = File.ReadAllText(recorder.CurrentPathForTests!);
        Check(!text.Contains("AA:BB:CC") && !text.Contains("worldArray") && !text.Contains("flagVariants"), "memory addresses, raw session identity and native pointer diagnostics are not persisted");
    }

    private static async Task VerifyInactivePreservesFinalState(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        var active = Sample(1, round: 59, count: 7);
        active["resources"]!["wood"] = 57; active["resources"]!["gold"] = 12345;
        await recorder.ProcessAsync(active, default);
        var terminal = Sample(2, round: 59, active: false, ended: true);
        terminal["counts"] = new JsonArray(); terminal["resources"]!["wood"] = 0; terminal["resources"]!["gold"] = 0;
        await recorder.ProcessAsync(terminal, default);
        var doc = Read(recorder); var outcome = doc["outcome"]!.AsObject();
        Check(Text(outcome, "result") == "inactive_or_left", "Ji false is not mislabeled as a proven defeat");
        Check(Number(outcome, "endedRound") == 59 && outcome["defeatRound"] is null && outcome["defeatConfirmed"]!.GetValue<bool>() == false, "observed termination round and unconfirmed defeat are distinct");
        Check(Number(doc["finalResources"]!.AsObject(), "wood") == 57 && Number(doc["finalResources"]!.AsObject(), "gold") == 12345, "zeroed terminal resources do not replace final active resources");
        Check(doc["finalRoster"]!.AsArray().Any(unit => unit!["rawcode"]!.GetValue<string>() == "h003" && unit["count"]!.GetValue<int>() == 7), "terminal removal preserves the last active roster");
        Check(doc["terminalObservation"]!["counts"]!.AsArray().Count == 0, "actual terminal cleanup is retained separately");
        Check(Number(outcome, "exactEndRound") == 59, "same-round short-gap termination keeps an exact observed end round");
        var sealedText = File.ReadAllText(recorder.CurrentPathForTests!);
        await recorder.ProcessAsync(new JsonObject { ["status"] = "game_not_running", ["timestamp"] = Time(3) }, default);
        await recorder.ProcessAsync(Sample(4, round: 65, active: false, ended: true, victory: true), default);
        Check(File.ReadAllText(recorder.CurrentPathForTests!) == sealedText, "process exit and other-player global victory cannot reclassify or advance an ended local run");
    }

    private static async Task VerifyVictory(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(1, round: 65), default);
        await recorder.ProcessAsync(Sample(2, round: 65, ended: true, victory: true), default);
        var doc = Read(recorder);
        Check(Text(doc["outcome"]!.AsObject(), "result") == "victory", "global victory with locally active player is recorded as victory");
        Check(doc["outcome"]!["defeatRound"] is null, "victory never sets a defeat round");
    }

    private static async Task VerifyGapsAndResume(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(1), default);
        var unknown = Sample(2); unknown["progression"]!["status"] = "unknown";
        await recorder.ProcessAsync(unknown, default);
        unknown["timestamp"] = Time(3); await recorder.ProcessAsync(unknown, default);
        var doc = Read(recorder);
        Check(Events(doc).Count == 2 && Text(doc, "collectionStatus") == "suspended", "repeated unknown progression creates one gap rather than a false defeat");
        await recorder.ProcessAsync(Sample(4), default);
        doc = Read(recorder);
        Check(Text(doc, "collectionStatus") == "active" && Events(doc).Count == 3, "a fresh native sample resumes the same run with one resume event");
        await recorder.ProcessAsync(new JsonObject { ["status"] = "game_not_running", ["timestamp"] = Time(5) }, default);
        doc = Read(recorder);
        Check(Text(doc["outcome"]!.AsObject(), "result") == "interrupted" && doc["outcome"]!["endedRound"] is null, "game closure only marks collection interruption");
        await recorder.ProcessAsync(Sample(6), default);
        Check(Files(directory).Length == 1 && Text(Read(recorder)["outcome"]!.AsObject(), "result") == "in_progress", "an interruption can resume without inventing another run");
    }

    private static async Task VerifyRestart(string directory)
    {
        var first = new GameRunRecorder(directory);
        await first.ProcessAsync(Sample(1), default);
        var id = Text(Read(first), "id");
        await first.StopAsync(default);
        var second = new GameRunRecorder(directory);
        await second.ProcessAsync(Sample(10, round: 21), default);
        Check(Files(directory).Length == 1 && Text(Read(second), "id") == id, "collector restart restores the same session file");
        Check(Text(Read(second)["outcome"]!.AsObject(), "result") == "in_progress", "confirmed activity resumes an interrupted run");
        await second.ProcessAsync(Sample(11, round: 21, active: false, ended: true), default);
        var sealedPath = second.CurrentPathForTests!; var sealedText = File.ReadAllText(sealedPath);
        var third = new GameRunRecorder(directory);
        await third.ProcessAsync(Sample(12, round: 22, active: false, ended: true), default);
        Check(File.ReadAllText(sealedPath) == sealedText && Files(directory).Length == 1, "restart on a passive terminal screen does not duplicate an already recorded game");
    }

    private static async Task VerifyTerminalRestartRecovery(string directory)
    {
        var first = new GameRunRecorder(directory);
        var active = Sample(1, round: 59, count: 7);
        active["resources"]!["wood"] = 57;
        await first.ProcessAsync(active, default);
        var id = Text(Read(first), "id");
        var second = new GameRunRecorder(directory);
        var terminal = Sample(10, round: 59, active: false, ended: true);
        terminal["counts"] = new JsonArray(); terminal["resources"]!["wood"] = 0;
        await second.ProcessAsync(terminal, default);
        var restored = Read(second); var outcome = restored["outcome"]!.AsObject();
        Check(Files(directory).Length == 1 && Text(restored, "id") == id && Text(outcome, "result") == "inactive_or_left", "terminal-first restart finishes the saved active game instead of losing its ending or creating another run");
        Check(restored["finalRoster"]!.AsArray().Any(unit => unit!["rawcode"]!.GetValue<string>() == "h003" && unit["count"]!.GetValue<int>() == 7) && Number(restored["finalResources"]!.AsObject(), "wood") == 57, "terminal-first recovery keeps the saved active roster and resources rather than terminal cleanup");
        Check(Number(outcome, "endedRound") == 59 && outcome["exactEndRound"] is null && outcome["defeatRound"] is null, "a restart observation gap does not invent an exact death round");
        await second.ProcessAsync(Sample(11, round: 65), default);
        var thirdPath = second.CurrentPathForTests!; var thirdId = Text(Read(second), "id");
        await second.ProcessAsync(new JsonObject { ["status"] = "game_not_running", ["timestamp"] = Time(12) }, default);
        Check(Text(ReadFile(thirdPath)["outcome"]!.AsObject(), "result") == "interrupted", "the second saved game has an interrupted baseline before restart");
        var third = new GameRunRecorder(directory);
        await third.ProcessAsync(Sample(20, round: 65, ended: true, victory: true), default);
        Check(Files(directory).Length == 2 && Text(Read(third), "id") == thirdId && Text(Read(third)["outcome"]!.AsObject(), "result") == "victory", "terminal-first restart also completes the same saved interrupted game as a native local victory");
    }

    private static async Task VerifyTerminalRestartBaselineGuards(string directory)
    {
        var first = new GameRunRecorder(directory);
        await first.ProcessAsync(Sample(10, round: 40), default);
        var file = first.CurrentPathForTests!; var original = ReadFile(file);
        var mutations = new Action<JsonObject>[]
        {
            document => document["lastActiveSnapshot"] = null,
            document => document["lastActiveSnapshot"]!["progression"]!["localPlayer"] = 1,
            document => document["localPlayer"] = 1,
            document => document["mapSha256"] = new string('b', 64),
            document => document["lastActiveSnapshot"]!["progression"]!["status"] = "unknown",
            document => document["lastActiveSnapshot"]!["progression"]!["activePlayer"] = false,
            document => document["lastActiveSnapshot"]!["counts"] = new JsonObject(),
            document => document["lastKnownRound"] = 39,
            document => document["lastObservedAtUtc"] = Time(30),
            document => document["lastActiveSnapshot"]!["timestamp"] = Time(25)
        };
        foreach (var mutate in mutations)
        {
            var invalid = original.DeepClone().AsObject(); mutate(invalid);
            var text = invalid.ToJsonString(); File.WriteAllText(file, text);
            var recorder = new GameRunRecorder(directory);
            await recorder.ProcessAsync(Sample(20, round: 40, active: false, ended: true), default);
            Check(recorder.CurrentForTests is null && File.ReadAllText(file) == text && Files(directory).Length == 1, "a terminal-first restart cannot use a missing, foreign, uncertain, mismatched or future active baseline");
        }
        File.WriteAllText(file, original.ToJsonString());
        var older = new GameRunRecorder(directory);
        await older.ProcessAsync(Sample(9, round: 40, active: false, ended: true), default);
        Check(older.CurrentForTests is null && Text(ReadFile(file)["outcome"]!.AsObject(), "result") == "in_progress", "a terminal-first observation older than the saved game cannot rewrite it");
    }

    private static async Task VerifyTerminalRoundRegression(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(1, round: 40, count: 7), default);
        var id = Text(Read(recorder), "id");
        await recorder.ProcessAsync(Sample(2, round: 39, active: false, ended: true), default);
        var document = Read(recorder); var outcome = document["outcome"]!.AsObject();
        Check(Text(document, "id") == id && Number(document, "lastKnownRound") == 40 && Text(document, "collectionStatus") == "suspended" && Text(outcome, "result") == "in_progress", "a newer terminal with a lower round is an observation mismatch rather than the saved game's ending");
        Check(document["terminalObservation"] is null && outcome["endedRound"] is null && outcome["possibleEndRoundRange"] is null, "a regressed terminal creates no invalid or inverted ending range");
        var restarted = new GameRunRecorder(directory);
        await restarted.ProcessAsync(Sample(3, round: 38, active: false, ended: true), default);
        Check(Files(directory).Length == 1 && Text(Read(restarted), "id") == id && Number(Read(restarted), "lastKnownRound") == 40 && Text(Read(restarted)["outcome"]!.AsObject(), "result") == "in_progress", "terminal-first restore also rejects a newer but regressed ending round");
        await restarted.ProcessAsync(Sample(4, round: 40, active: false, ended: true), default);
        document = Read(restarted); outcome = document["outcome"]!.AsObject();
        Check(Text(outcome, "result") == "inactive_or_left" && Number(outcome, "endedRound") == 40 && Number(outcome["possibleEndRoundRange"]!.AsObject(), "minimum") == 40 && Number(outcome["possibleEndRoundRange"]!.AsObject(), "maximum") == 40, "a later consistent native termination can still finish the same saved game");
    }

    private static async Task VerifySessionChange(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(1), default);
        var old = recorder.CurrentPathForTests!;
        await recorder.ProcessAsync(Sample(2, identity: "DD:EE:FF"), default);
        Check(Files(directory).Length == 2 && Text(ReadFile(old)["outcome"]!.AsObject(), "result") == "interrupted", "new native session preserves the old unknown result and starts another file");
        var second = recorder.CurrentPathForTests!;
        await recorder.ProcessAsync(Sample(3, identity: "DD:EE:FF", process: 4321), default);
        Check(Files(directory).Length == 3 && recorder.CurrentPathForTests != second, "a different game process cannot merge with the old session");
    }

    private static async Task VerifyAddressReuse(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(1, round: 59), default);
        await recorder.ProcessAsync(Sample(2, round: 59, active: false, ended: true), default);
        var old = recorder.CurrentPathForTests!; var oldText = File.ReadAllText(old);
        await recorder.ProcessAsync(Sample(3, round: 0), default);
        Check(Files(directory).Length == 2 && recorder.CurrentPathForTests != old, "fresh activity after a terminal state starts a new game even when native addresses are reused");
        Check(File.ReadAllText(old) == oldText, "address reuse does not overwrite the finished record");
    }

    private static async Task VerifyRoundRegression(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(1, round: 40), default);
        var id = Text(Read(recorder), "id");
        await recorder.ProcessAsync(Sample(2, round: 39), default);
        Check(Files(directory).Length == 1 && Number(Read(recorder), "lastKnownRound") == 40, "a single decreasing round is a gap, not a new game");
        await recorder.ProcessAsync(Sample(3, round: 40), default);
        Check(Text(Read(recorder), "id") == id, "a transient round regression recovers in the same game");
        await recorder.ProcessAsync(Sample(4, round: 0), default);
        Check(Files(directory).Length == 1, "one initial-round regression is not sufficient to split a run");
        await recorder.ProcessAsync(Sample(5, round: 1), default);
        Check(Files(directory).Length == 2 && Text(Read(recorder), "id") != id, "two fresh initial-round observations can identify a new game after a missed boundary");
    }

    private static async Task VerifyWriteRetry(string directory)
    {
        var calls = 0; var warnings = 0;
        var recorder = new GameRunRecorder(directory, (_, _) => warnings++, async (file, json, cancel) =>
        {
            if (++calls <= 2) throw new IOException("test sharing failure");
            Directory.CreateDirectory(directory); await File.WriteAllTextAsync(file, json, cancel);
        });
        await recorder.ProcessAsync(Sample(1), default);
        await recorder.ProcessAsync(Sample(2), default);
        Check(calls == 2 && warnings == 1 && recorder.CurrentForTests is not null, "write failures do not stop collection or flood warnings");
        await recorder.ProcessAsync(Sample(3), default);
        Check(calls == 3 && Events(Read(recorder)).Count == 1, "an unchanged semantic state retries a dirty write without duplicated events");
        await recorder.ProcessAsync(Sample(4, round: 21), default);
        Check(Events(Read(recorder)).Count == 2, "collection continues after a successful retry");
        var temporary = recorder.CurrentPathForTests! + ".tmp";
        Check(!File.Exists(temporary), "successful publication leaves no temporary file");
    }

    private static async Task VerifyOutOfOrder(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(10, round: 30), default);
        await recorder.ProcessAsync(Sample(9, round: 29, active: false, ended: true), default);
        Check(Number(Read(recorder), "lastKnownRound") == 30 && Text(Read(recorder)["outcome"]!.AsObject(), "result") == "in_progress", "an older terminal sample cannot replace newer activity");
        var text = File.ReadAllText(recorder.CurrentPathForTests!);
        await recorder.ProcessAsync(Sample(10, round: 30), default);
        Check(File.ReadAllText(recorder.CurrentPathForTests!) == text, "identical timestamp retries do not append duplicate events");
        var unknown = Sample(12, round: 31); unknown["progression"]!["status"] = "unknown";
        await recorder.ProcessAsync(unknown, default);
        await recorder.ProcessAsync(Sample(11, round: 31), default);
        Check(Text(Read(recorder), "collectionStatus") == "suspended" && Number(Read(recorder), "lastKnownRound") == 30, "a late active sample cannot leap back across a newer unknown observation");
    }

    private static async Task VerifyPendingTerminalBeforeNewRun(string directory)
    {
        var terminalFailures = 0;
        var recorder = new GameRunRecorder(directory, writeAtomically: async (file, json, cancel) =>
        {
            if (Text(JsonNode.Parse(json)!["outcome"]!.AsObject(), "result") == "inactive_or_left" && ++terminalFailures <= 2) throw new IOException("terminal sharing failure");
            Directory.CreateDirectory(directory); await File.WriteAllTextAsync(file, json, cancel);
        });
        await recorder.ProcessAsync(Sample(1, round: 59), default);
        var previous = recorder.CurrentPathForTests!;
        await recorder.ProcessAsync(Sample(2, round: 59, active: false, ended: true), default);
        await recorder.ProcessAsync(Sample(3, round: 0), default);
        Check(recorder.CurrentPathForTests == previous && Files(directory).Length == 1, "a new run waits while the prior terminal record remains dirty");
        await recorder.ProcessAsync(Sample(4, round: 0), default);
        Check(Files(directory).Length == 2 && recorder.CurrentPathForTests != previous, "the pending terminal record is written before starting a new same-address game");
        Check(Text(ReadFile(previous)["outcome"]!.AsObject(), "result") == "inactive_or_left", "retry does not lose the prior termination");
    }

    private static async Task VerifyLockedRecord(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(1, round: 59), default);
        var file = recorder.CurrentPathForTests!;
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await recorder.ProcessAsync(Sample(2, round: 59, active: false, ended: true), default).WaitAsync(TimeSpan.FromSeconds(3));
            Check(Text(ReadFile(file)["outcome"]!.AsObject(), "result") == "in_progress" && Text(recorder.CurrentForTests!["outcome"]!.AsObject(), "result") == "inactive_or_left", "locked atomic publication preserves the previous file and the newer in-memory termination");
        }
        await recorder.ProcessAsync(Sample(3, round: 60, active: false, ended: true), default);
        Check(Number(ReadFile(file)["outcome"]!.AsObject(), "endedRound") == 59 && !File.Exists(file + ".tmp"), "unlocked retry publishes the first terminal observation and cleans its temporary file");
    }

    private static async Task VerifyObservationRoundRange(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(1, round: 58), default);
        var unknown = Sample(2, round: 59); unknown["progression"]!["status"] = "unknown";
        await recorder.ProcessAsync(unknown, default);
        await recorder.ProcessAsync(Sample(20, round: 62, active: false, ended: true), default);
        var outcome = Read(recorder)["outcome"]!.AsObject();
        Check(Number(outcome, "endedRound") == 62 && Number(outcome, "lastActiveRound") == 58, "first observed terminal round is separate from the last active round");
        Check(Number(outcome["possibleEndRoundRange"]!.AsObject(), "minimum") == 58 && Number(outcome["possibleEndRoundRange"]!.AsObject(), "maximum") == 62 && outcome["exactEndRound"] is null && outcome["defeatRound"] is null, "a delayed observation keeps a range rather than inventing an exact death round");
    }

    private static async Task VerifyUnstableTerminal(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await recorder.ProcessAsync(Sample(1, round: 59, count: 9), default);
        var terminal = Sample(2, round: 59, active: false, ended: true); terminal["stableUnits"] = false; terminal["counts"] = new JsonArray();
        await recorder.ProcessAsync(terminal, default);
        Check(Text(Read(recorder)["outcome"]!.AsObject(), "result") == "inactive_or_left" && Read(recorder)["finalRoster"]!.AsArray().Any(unit => unit!["count"]!.GetValue<int>() == 9), "independently confirmed termination preserves inventory even while cleanup makes unit collection unstable");
    }

    private static async Task VerifyConcurrentCalls(string directory)
    {
        var recorder = new GameRunRecorder(directory);
        await Task.WhenAll(recorder.ProcessAsync(Sample(1), default), recorder.ProcessAsync(Sample(2, round: 21), default));
        Check(Files(directory).Length == 1 && Number(Read(recorder), "lastKnownRound") == 21 && Events(Read(recorder)).Count == 2, "concurrent publications are serialized into one consistent run");
    }

    private static JsonObject Sample(int sequence, int round = 20, bool active = true, bool ended = false, bool victory = false, int count = 2,
        string identity = "AA:BB:CC", int process = 1234) => new()
    {
        ["status"] = "snapshot_read", ["timestamp"] = Time(sequence), ["sequence"] = sequence,
        ["processId"] = process, ["sessionIdentity"] = identity, ["localPlayer"] = 0,
        ["gameVersion"] = GameProgression.SupportedVersion, ["loadedMapHash"] = GameProgression.SupportedMapHash,
        ["catalogMatchesLoadedArchive"] = true, ["stableRoots"] = true, ["stableUnits"] = true, ["readErrors"] = 0,
        ["counts"] = new JsonArray(
            new JsonObject { ["rawcode"] = "h003", ["name"] = "루피 - 흔함", ["count"] = count, ["isCharacter"] = true },
            new JsonObject { ["rawcode"] = "h04Q", ["name"] = "빅맘 - 불멸", ["count"] = 1, ["isCharacter"] = true }),
        ["progression"] = new JsonObject { ["status"] = "confirmed", ["localPlayer"] = 0, ["round"] = round, ["storyIndex"] = 5,
            ["activePlayer"] = active, ["gameEnded"] = ended, ["victory"] = victory, ["difficulty"] = "nightmare", ["difficultyValue"] = 6 },
        ["resources"] = new JsonObject { ["status"] = "confirmed", ["localPlayer"] = 0, ["wood"] = 50, ["gold"] = 1000 + sequence },
        ["items"] = new JsonObject { ["status"] = "confirmed", ["localPlayer"] = 0, ["counts"] = new JsonObject { ["AI00"] = 0 } },
        ["elder"] = new JsonObject { ["status"] = "active", ["id"] = "saturn", ["name"] = "새턴", ["effectStatus"] = "active" }
    };

    private static string Time(int sequence) => Origin.AddSeconds(sequence).ToString("O");
    private static JsonObject Read(GameRunRecorder recorder) => ReadFile(recorder.CurrentPathForTests ?? throw new Exception("No run path."));
    private static JsonObject ReadFile(string file) => JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    private static JsonArray Events(JsonObject document) => document["events"]!.AsArray();
    private static int? Number(JsonObject document, string key) => document[key]?.GetValue<int>();
    private static string? Text(JsonObject document, string key) => document[key]?.GetValue<string>();
    private static string[] Files(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory, "auto-*.json") : [];

    private static async Task WithDirectory(Func<string, Task> test)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ord-run-recording-" + Guid.NewGuid().ToString("N")));
        var directory = Path.Combine(parent, "records");
        try { await test(directory); }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory))
                {
                    if (!Path.GetFullPath(file).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Test cleanup escaped its directory.");
                    File.Delete(file);
                }
                Directory.Delete(directory);
            }
            if (Directory.Exists(parent)) Directory.Delete(parent);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Automatic run recording test failed: " + message);
        checks++;
    }
}
