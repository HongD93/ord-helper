using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

static class LocalClearRecommendationStoreTests
{
    private const string Map = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Id = "auto-20261028T0000000000000Z-0000000000000000";
    private static readonly List<object> Results = [];

    public static async Task RunAsync()
    {
        Results.Clear();
        await Case("empty native history publishes a valid map-bound empty snapshot", Empty);
        await Case("confirmed clear backfills complete owned counts and native proof", Confirmed);
        await Case("repeated refreshes and restart do not duplicate a canonical run", Restart);
        await Case("active, inactive, interrupted and unconfirmed endings are excluded", Outcomes);
        await Case("nightmare, map, player, identity and fresh final inventory are required", Guards);
        await Case("hidden random support remains present despite native character flag false", Random);
        await Case("transcendence heroes and Nika consume one shared slot", Transcendence);
        await Case("malformed or partial source is retried after recovery", Malformed);
        await Case("locked source preserves current snapshot and retries", Locked);
        await Case("failed publication retains current snapshot and retries idempotently", WriteFailure);
        await Case("cancellation before publication preserves prior snapshot", Cancellation);
        await Case("an immutable snapshot and prior revision remain readable", Immutable);
        await Case("a changed already accepted clear source cannot overwrite its evidence", Changed);
        await Case("existing validated manual wrapper is reused without rewriting", ManualWrapper);
        await Case("malformed catalogue is retried and never publishes a foreign map", CatalogRetry);
        await Case("concurrent refreshes publish one canonical record", Concurrent);
        await Case("one damaged or changed source cannot block a later healthy clear", IsolatedSourceFailure);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { passed = Results.Count, total = Results.Count, tests = Results, runtimeDataRequired = false, gameInputSent = false }));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ord-local-clears-" + Guid.NewGuid().ToString("N"));
        public string Auto => Path.Combine(Root, "data", "local-runs", "auto");
        public string Web => Path.Combine(Root, "wwwroot");
        public string CatalogPath => Path.Combine(Web, "data", "map-catalog.json");
        public Fixture() { Directory.CreateDirectory(Auto); Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!); File.WriteAllText(CatalogPath, Catalog().ToJsonString()); }
        public string Save(JsonObject? raw = null, string id = Id)
        {
            var file = Path.Combine(Auto, id + ".json"); File.WriteAllText(file, (raw ?? Clear(id)).ToJsonString()); return file;
        }
        public LocalClearRecommendationStore Store(Func<string, string, CancellationToken, Task>? write = null) => new(Auto, Web, warning: (error, _) => Console.Error.WriteLine(error), writeAtomically: write);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static async Task Case(string name, Func<Fixture, Task> test)
    {
        using var fixture = new Fixture(); await test(fixture); Results.Add(new { name, passed = true });
    }
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException("Local clear recommendation regression: " + reason); }
    private static JsonObject Catalog() => new()
    {
        ["map"] = new JsonObject { ["sha256"] = Map, ["version"] = "2.323[C]" },
        ["units"] = new JsonArray(Unit("H001", "초월"), Unit("H002", "초월"), Unit("H0BK", "영원"), Unit("h001", "흔함"), Unit("h036", "전설"), Unit("h04E", "불멸"), Unit("h06X", "랜덤전용"), Unit("h08N", "특수"), Unit("h076", "초월"), Unit("e018", "기타"))
    };
    private static JsonObject Unit(string code, string tier) => new() { ["rawcode"] = code, ["displayName"] = code, ["name"] = code, ["tier"] = tier, ["playable"] = true };
    private static JsonObject Progress(bool ended = false) => new() { ["status"] = "confirmed", ["localPlayer"] = 0, ["round"] = 65, ["activePlayer"] = true, ["difficulty"] = "nightmare", ["difficultyValue"] = 6, ["gameEnded"] = ended, ["victory"] = ended };
    private static JsonObject Count(string code, int count = 1, bool character = true) => new() { ["rawcode"] = code, ["name"] = code, ["count"] = count, ["isCharacter"] = character };
    private static JsonObject Clear(string id = Id)
    {
        var counts = new JsonArray(Count("H001"), Count("h036", 2), Count("h06X", 1, false), Count("h08N", 1, false), Count("h001", 5), Count("e018", 3, false));
        return new JsonObject { ["schemaVersion"] = 1, ["recordKind"] = "automatic_run", ["id"] = id, ["mapSha256"] = Map, ["localPlayer"] = 0, ["collectionStatus"] = "ended",
            ["outcome"] = new JsonObject { ["result"] = "victory", ["endedRound"] = 65, ["endedObservedAtUtc"] = "2026-10-28T00:00:00.250Z", ["lastActiveObservedAtUtc"] = "2026-10-28T00:00:00.000Z" },
            ["lastActiveSnapshot"] = new JsonObject { ["timestamp"] = "2026-10-28T00:00:00.000Z", ["progression"] = Progress(), ["counts"] = counts, ["elder"] = new JsonObject { ["name"] = "test" } },
            ["terminalObservation"] = new JsonObject { ["timestamp"] = "2026-10-28T00:00:00.250Z", ["progression"] = Progress(true) },
            ["finalInventory"] = counts.DeepClone(), ["finalInventoryObservedAtUtc"] = "2026-10-28T00:00:00.000Z" };
    }
    private static JsonObject Manifest(LocalClearRecommendationStore store) => JsonNode.Parse(store.ManifestJson)!.AsObject();
    private static JsonObject Snapshot(LocalClearRecommendationStore store) => JsonNode.Parse(store.SnapshotJson(Manifest(store)["revision"]!.GetValue<string>())!)!.AsObject();
    private static JsonArray Records(LocalClearRecommendationStore store) => Snapshot(store)["records"]!.AsArray();

    private static async Task Empty(Fixture f)
    {
        var store = f.Store(); await store.RefreshAsync(default); var manifest = Manifest(store);
        Check(store.RecordCount == 0 && Records(store).Count == 0, "empty sources have zero records");
        Check(manifest["catalogMapHash"]!.GetValue<string>() == Map && manifest["dataUrl"]!.GetValue<string>().StartsWith("/api/local-clear-records/"), "empty manifest identity");
        Check(store.SnapshotJson("../../data") is null, "route rejects unsafe revision");
    }
    private static async Task Confirmed(Fixture f)
    {
        var file = f.Save(); var source = File.ReadAllBytes(file); var store = f.Store(); await store.RefreshAsync(default);
        Check(store.RecordCount == 1 && File.ReadAllBytes(file).SequenceEqual(source), "source preserved and one record admitted");
        var record = Records(store)[0]!.AsObject(); var proof = record["localNativeProof"]!.AsObject();
        Check(record["id"]!.GetValue<string>() == "local-" + Id && record["rosterComplete"]!.GetValue<bool>() && record["fullRosterCountsVerified"]!.GetValue<bool>(), "canonical full native source");
        Check(record["entries"]!.AsArray().Count == 6 && record["goals"]!.AsArray().Count == 4, "full inventory and selected combat units");
        Check(proof["automaticRunSha256"]!.GetValue<string>() == Convert.ToHexStringLower(SHA256.HashData(source)), "source hash bound");
        Check(record["entries"]!.AsArray().All(row => row!["quantityEvidence"]!["automaticRunSha256"]!.GetValue<string>() == proof["automaticRunSha256"]!.GetValue<string>()), "all quantities bound to same source");
        var rawSnapshot = store.SnapshotJson(Manifest(store)["revision"]!.GetValue<string>())!;
        Check(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawSnapshot))) == Manifest(store)["revision"]!.GetValue<string>(), "HTTP payload hash matches manifest");
    }
    private static async Task Restart(Fixture f)
    {
        f.Save(); var store = f.Store(); await store.RefreshAsync(default); var first = store.ManifestJson;
        await store.RefreshAsync(default); await store.RefreshAsync(default);
        var next = f.Store(); await next.RefreshAsync(default);
        Check(store.ManifestJson == first && next.ManifestJson == first && next.RecordCount == 1, "repeat and restart stable snapshot");
        Check(Directory.GetFiles(Path.GetDirectoryName(f.Auto)!, "clear-run-*.json").Length == 1, "one deterministic wrapper");
    }
    private static async Task Outcomes(Fixture f)
    {
        foreach (var outcome in new[] { "in_progress", "inactive_or_left", "interrupted", "unconfirmed_end" })
        {
            var raw = Clear(); raw["outcome"]!["result"] = outcome; f.Save(raw); var store = f.Store(); await store.RefreshAsync(default); Check(store.RecordCount == 0, outcome);
        }
    }
    private static async Task Guards(Fixture f)
    {
        Action<JsonObject>[] mutations = [
            r => r["mapSha256"] = new string('b', 64), r => r["id"] = "foreign", r => r["localPlayer"] = 1,
            r => r["terminalObservation"]!["progression"]!["activePlayer"] = false,
            r => r["terminalObservation"]!["progression"]!["status"] = "unknown",
            r => r["lastActiveSnapshot"]!["progression"]!["difficultyValue"] = 5,
            r => r["lastActiveSnapshot"]!["progression"]!["gameEnded"] = true,
            r => r["outcome"]!["endedRound"] = 64,
            r => r["finalInventory"]!.AsArray()[0]!["count"] = 2,
            r => r["finalInventory"]!.AsArray().Add(r["finalInventory"]![0]!.DeepClone()),
            r => { r["terminalObservation"]!["timestamp"] = "2026-10-28T00:00:03Z"; r["outcome"]!["endedObservedAtUtc"] = "2026-10-28T00:00:03Z"; },
            r => r["finalInventoryObservedAtUtc"] = "2026-10-28T00:00:00.100Z",
            r => { r["lastActiveSnapshot"]!["counts"]![0]!["rawcode"] = "zzzz"; r["finalInventory"]![0]!["rawcode"] = "zzzz"; },
            r => { r["lastActiveSnapshot"]!["counts"]![0]!["count"] = 0; r["finalInventory"]![0]!["count"] = 0; }
        ];
        foreach (var mutate in mutations) { var raw = Clear(); mutate(raw); f.Save(raw); var store = f.Store(); await store.RefreshAsync(default); Check(store.RecordCount == 0, "invalid native source rejected"); }
    }
    private static async Task Random(Fixture f)
    {
        f.Save(); var store = f.Store(); await store.RefreshAsync(default); var record = Records(store)[0]!;
        Check(record["goals"]!.AsArray().Any(row => row!["rawcode"]!.GetValue<string>() == "h06X"), "random unit kept");
        Check(record["observedInventory"]!.AsArray().Any(row => row!["rawcode"]!.GetValue<string>() == "h06X" && row["isCharacter"]!.GetValue<bool>() == false), "false character flag preserved");
    }
    private static async Task Transcendence(Fixture f)
    {
        foreach (var code in new[] { "H002", "H0BK" })
        {
            var raw = Clear(); raw["lastActiveSnapshot"]!["counts"]!.AsArray().Add(Count(code)); raw["finalInventory"]!.AsArray().Add(Count(code)); f.Save(raw);
            var store = f.Store(); await store.RefreshAsync(default); Check(store.RecordCount == 0, "two slots rejected for " + code);
        }
        var one = Clear(); one["lastActiveSnapshot"]!["counts"]![0]!["rawcode"] = "H0BK"; one["finalInventory"]![0]!["rawcode"] = "H0BK"; f.Save(one);
        var single = f.Store(); await single.RefreshAsync(default); Check(single.RecordCount == 1, "one Nika slot allowed");
    }
    private static async Task Malformed(Fixture f)
    {
        var file = f.Save(); File.WriteAllText(file, "{partial"); var store = f.Store(); await store.RefreshAsync(default); Check(store.RecordCount == 0, "partial excluded");
        File.WriteAllText(file, Clear().ToJsonString()); await store.RefreshAsync(default); Check(store.RecordCount == 1, "partial recovered");
    }
    private static async Task Locked(Fixture f)
    {
        var store = f.Store(); await store.RefreshAsync(default); var old = store.ManifestJson; var file = f.Save();
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { await store.RefreshAsync(default); Check(store.ManifestJson == old, "locked source retains old snapshot"); }
        await store.RefreshAsync(default); Check(store.RecordCount == 1, "source unlock retried");
    }
    private static async Task WriteFailure(Fixture f)
    {
        var fail = false; var store = f.Store(async (file, json, cancel) => { if (fail && Path.GetFileName(file) == "manifest.json") throw new IOException("test write failure"); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, json, cancel); });
        await store.RefreshAsync(default); var old = store.ManifestJson; f.Save(); fail = true; await store.RefreshAsync(default); Check(store.ManifestJson == old && store.RecordCount == 0, "failed manifest retains lastgood");
        fail = false; await store.RefreshAsync(default); Check(store.RecordCount == 1, "failed publication retries");
    }
    private static async Task Cancellation(Fixture f)
    {
        var store = f.Store(); await store.RefreshAsync(default); var old = store.ManifestJson; f.Save(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await store.RefreshAsync(cancel.Token); throw new Exception("cancel expected"); } catch (OperationCanceledException) { }
        Check(store.ManifestJson == old, "cancel keeps lastgood"); await store.RefreshAsync(default); Check(store.RecordCount == 1, "cancelled input retries");
    }
    private static async Task Immutable(Fixture f)
    {
        var store = f.Store(); await store.RefreshAsync(default); var old = Manifest(store)["revision"]!.GetValue<string>(); var original = store.SnapshotJson(old); f.Save(); await store.RefreshAsync(default);
        Check(original == store.SnapshotJson(old) && store.RecordCount == 1, "old payload remains immutable and available");
    }
    private static async Task Changed(Fixture f)
    {
        f.Save(); var store = f.Store(); await store.RefreshAsync(default); var old = store.ManifestJson; var changed = Clear(); changed["lastActiveSnapshot"]!["counts"]![1]!["count"] = 7; changed["finalInventory"]![1]!["count"] = 7; f.Save(changed);
        await store.RefreshAsync(default); Check(store.ManifestJson == old && store.RecordCount == 1, "sealed source cannot mutate evidence");
    }
    private static async Task ManualWrapper(Fixture f)
    {
        f.Save(); var first = f.Store(); await first.RefreshAsync(default); var generated = Directory.GetFiles(Path.GetDirectoryName(f.Auto)!, "clear-run-*.json").Single();
        var manual = Path.Combine(Path.GetDirectoryName(f.Auto)!, "clear-run-20261028-090000.json"); File.Move(generated, manual); var bytes = File.ReadAllBytes(manual);
        var second = f.Store(); await second.RefreshAsync(default); Check(second.RecordCount == 1 && File.ReadAllBytes(manual).SequenceEqual(bytes), "manual wrapper unchanged");
        Check(Directory.GetFiles(Path.GetDirectoryName(f.Auto)!, "clear-run-*.json").Length == 1, "no duplicate wrapper");
        Check(Records(second)[0]!["sourceRecordRelativePath"]!.GetValue<string>().EndsWith("clear-run-20261028-090000.json"), "manual evidence path retained");
    }
    private static async Task CatalogRetry(Fixture f)
    {
        f.Save(); File.WriteAllText(f.CatalogPath, "{broken"); var store = f.Store(); await store.RefreshAsync(default); Check(Manifest(store)["status"]!.GetValue<string>() == "unavailable", "invalid catalog unavailable");
        File.WriteAllText(f.CatalogPath, Catalog().ToJsonString()); await store.RefreshAsync(default); Check(store.RecordCount == 1, "catalog recovery retried");
    }
    private static async Task Concurrent(Fixture f)
    {
        f.Save(); var store = f.Store(); await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => store.RefreshAsync(default))); Check(store.RecordCount == 1, "concurrent work serialized");
    }
    private static async Task IsolatedSourceFailure(Fixture f)
    {
        f.Save(); var store = f.Store(); await store.RefreshAsync(default); var changed = Clear(); changed["finalInventory"]![0]!["count"] = 9; f.Save(changed);
        var second = "auto-20261028T0000010000000Z-1111111111111111"; f.Save(Clear(second), second);
        var broken = "auto-20261028T0000020000000Z-2222222222222222"; File.WriteAllText(Path.Combine(f.Auto, broken + ".json"), "{broken");
        await store.RefreshAsync(default); Check(store.RecordCount == 2, "changed old and malformed new sources isolated");
        var count = Records(store).Single(row => row!["id"]!.GetValue<string>() == "local-" + Id)!["goals"]!.AsArray().Single(row => row!["rawcode"]!.GetValue<string>() == "H001")!["count"]!.GetValue<int>();
        Check(count == 1, "accepted original record retained");
        File.WriteAllText(Path.Combine(f.Auto, broken + ".json"), Clear(broken).ToJsonString()); await store.RefreshAsync(default);
        Check(store.RecordCount == 3, "recovered malformed source retried without duplicating earlier clears");
    }
}
