using System.Text.Json;
using System.Text.Json.Nodes;

static class FilePublicationTests
{
    public static void Run() => VerifyAsync().GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        await WithFiles(VerifyLockedSnapshot);
        await WithFiles(VerifyLockedTemporary);
        await WithFiles(VerifyLockedJournal);
        await WithFiles(VerifyProgressionJournal);
        await WithFiles(VerifyEvolutionJournal);
        await WithFiles(VerifyRunDirectory);
        var memoryOnly = Sink(null);
        await Publish(memoryOnly, 1, 4);
        await Publish(memoryOnly, 2, 5);
        Check(memoryOnly.Current.Sequence == 2 && Count(memoryOnly.Current.Json) == 5,
            "memory-only publication remains current without output files");
        Console.WriteLine("PASS: file publication 7/7 cases; round/outcome/evolution changes are journaled without per-kill churn, and locked files recover.");
    }

    private static async Task VerifyLockedSnapshot(string path)
    {
        File.WriteAllText(path, "unchanged");
        var warnings = new List<string>();
        var sink = Sink(path, (_, destination) => warnings.Add(destination));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Publish(sink, 1, 1);
            await Publish(sink, 2, 2);
            Check(File.ReadAllText(path) == "unchanged", "a read lock prevents the atomic snapshot replacement");
            Check(sink.Current.Sequence == 2 && Count(sink.Current.Json) == 2,
                "two failed file replacements still advance the production live buffer");
            Check(File.ReadAllLines(path + ".jsonl").Length == 2,
                "snapshot replacement failure does not suppress independent journal recording");
            Check(warnings.SequenceEqual(new[] { "snapshot" }), "repeated file failures have bounded warning output");
        }
        await Publish(sink, 3, 3);
        Check(Count(File.ReadAllText(path)) == 3 && sink.Current.Sequence == 3,
            "the next sample publishes after the reader releases its lock");
    }

    private static async Task VerifyLockedTemporary(string path)
    {
        File.WriteAllText(path, "previous");
        File.WriteAllText(path + ".tmp", "busy");
        var sink = Sink(path);
        using (var locked = new FileStream(path + ".tmp", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Publish(sink, 1, 4);
            Check(sink.Current.Sequence == 1 && Count(sink.Current.Json) == 4 && File.ReadAllText(path) == "previous",
                "temporary-file write failure preserves the prior file and current live sample");
            Check(File.ReadAllLines(path + ".jsonl").Length == 1,
                "temporary-file write failure does not prevent journal recording");
        }
        await Publish(sink, 2, 4);
        Check(Count(File.ReadAllText(path)) == 4, "temporary-file publication retries even when semantic counts are unchanged");
    }

    private static async Task VerifyLockedJournal(string path)
    {
        File.WriteAllText(path + ".jsonl", "");
        var sink = Sink(path);
        using (var locked = new FileStream(path + ".jsonl", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Publish(sink, 1, 7);
            await Publish(sink, 2, 7);
            Check(sink.Current.Sequence == 2 && Count(File.ReadAllText(path)) == 7,
                "journal failure does not stop atomic snapshot or live publication");
            Check(File.ReadAllText(path + ".jsonl") == "", "the locked journal was not appended");
        }
        await Publish(sink, 3, 7);
        var lines = File.ReadAllLines(path + ".jsonl");
        Check(lines.Length == 1 && Sequence(lines[0]) == 3,
            "failed append does not mark the semantic state recorded and retries with the latest sample");
        await Publish(sink, 4, 7);
        Check(File.ReadAllLines(path + ".jsonl").Length == 1, "a successful semantic state is deduplicated");
        await Publish(sink, 5, 8);
        Check(File.ReadAllLines(path + ".jsonl").Length == 2 && sink.Current.Sequence == 5,
            "a later semantic change continues to append normally");
    }

    private static async Task VerifyProgressionJournal(string path)
    {
        var sink = Sink(path);
        async Task Frame(long sequence, int round, bool active = true, bool victory = false, string session = "same-session", int draws = 1)
        {
            var payload = JsonSerializer.SerializeToNode(new
            {
                status = "snapshot_read", loadedMapHash = "fixture", localPlayer = 0, processId = 7,
                sessionIdentity = session, counts = new[] { new { rawcode = "h001", count = 1 } }, sequence,
                timestamp = DateTimeOffset.UtcNow,
                progression = new { status = "confirmed", localPlayer = 0, round, activePlayer = active,
                    gameEnded = victory || !active, victory, randomWisp = new { status = "confirmed", drawCount = draws } }
            })!.AsObject();
            await sink.PublishAsync(new(sequence, payload.ToJsonString()), payload, CancellationToken.None);
        }
        await Frame(1, 10);
        await Frame(2, 11);
        await Frame(3, 11);
        Check(File.ReadAllLines(path + ".jsonl").Length == 2, "a round-only change is retained while timestamp/sequence churn is deduplicated");
        await Frame(4, 11, active: false);
        Check(File.ReadAllLines(path + ".jsonl").Length == 3, "local inactivity is journaled even before unit cleanup changes counts");
        await Frame(5, 11, victory: true);
        Check(File.ReadAllLines(path + ".jsonl").Length == 4, "a victory-only change remains observable with the same inventory");
        await Frame(6, 11, victory: true, session: "new-session");
        Check(File.ReadAllLines(path + ".jsonl").Length == 5, "a new stable session cannot reuse an identical inventory journal key");
        await Frame(7, 11, victory: true, session: "new-session", draws: 2);
        Check(File.ReadAllLines(path + ".jsonl").Length == 6, "a cumulative random draw change survives equal net inventory counts");
    }

    private static Task VerifyRunDirectory(string path)
    {
        var root = Path.GetDirectoryName(path)!;
        Check(LiveCounterServer.RunDataDirectory(path) == Path.Combine(root, "runs"), "a custom output keeps run records beside its own logs");
        var manifest = Path.Combine(root, "portable-layout.json");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            File.WriteAllText(manifest, "{}");
            Check(LiveCounterServer.RunDataDirectory(Path.Combine(logs, "live-counter.json")) == Path.Combine(root, "data", "local-runs", "auto"), "portable capture uses the package data folder");
        }
        finally { File.Delete(manifest); Directory.Delete(logs); }
        return Task.CompletedTask;
    }

    private static async Task VerifyEvolutionJournal(string path)
    {
        var sink = Sink(path);
        async Task Frame(long sequence, int kills, string phase, string? result, int pending)
        {
            var payload = JsonSerializer.SerializeToNode(new
            {
                status = "snapshot_read", loadedMapHash = "fixture", localPlayer = 0,
                counts = new[] { new { rawcode = "h001", count = 1 } }, sequence,
                startingEvolution = new { status = "confirmed", localPlayer = 0, resultRawcode = result, killCount = kills, pendingCount = pending, phase }
            })!.AsObject();
            await sink.PublishAsync(new(sequence, payload.ToJsonString()), payload, CancellationToken.None);
        }
        await Frame(1, 1, "charging", "h00B", 1);
        await Frame(2, 100, "charging", "h00B", 1);
        Check(File.ReadAllLines(path + ".jsonl").Length == 1, "unchanged future identity is not journaled on every kill");
        await Frame(3, 152, "activating", "h00B", 1);
        Check(File.ReadAllLines(path + ".jsonl").Length == 2, "the activation boundary is retained");
        await Frame(4, 152, "completed", null, 0);
        Check(File.ReadAllLines(path + ".jsonl").Length == 3, "removing a delivered future unit remains observable without net inventory change");
    }

    private static LiveCounterServer.PublicationSink Sink(string? path, Action<Exception, string>? warning = null) =>
        new(path, new JsonSerializerOptions { WriteIndented = true }, new JsonSerializerOptions(), warning);

    private static async Task Publish(LiveCounterServer.PublicationSink sink, long sequence, int count)
    {
        var payload = JsonSerializer.SerializeToNode(new
        {
            status = "snapshot_read", loadedMapHash = "fixture", localPlayer = 0,
            counts = new[] { new { rawcode = "h001", count } }, sequence
        })!.AsObject();
        // 파일 공유 실패를 같은 tick에서 반복하지 않고 반환하는지 확인한다.
        await sink.PublishAsync(new(sequence, payload.ToJsonString()), payload, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task WithFiles(Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ord-file-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "snapshot.json");
        try { await test(path); }
        finally
        {
            foreach (var owned in new[] { path, path + ".tmp", path + ".jsonl" })
                if (File.Exists(owned)) File.Delete(owned);
            Directory.Delete(directory);
        }
    }

    private static int Count(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("counts")[0].GetProperty("count").GetInt32();
    }

    private static long Sequence(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("sequence").GetInt64();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("File publication test failed: " + message);
    }
}
