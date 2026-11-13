using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// 공개 수집본과 분리된 로컬 승리 기록이다. 원본을 검증한 뒤 불변 스냅샷만 제공한다.
sealed class LocalClearRecommendationStore
{
    private sealed record Published(string Revision, string Json, string Manifest, int Count);
    private sealed record SourceState(long Length, long Ticks, string? AcceptedHash);
    private sealed record Imported(string Id, string Hash, JsonObject Record, string WrapperPath, string WrapperJson);
    private readonly string autoDirectory, webRoot, derivedDirectory, clearDirectory;
    private readonly Action<Exception, string>? warning;
    private readonly Func<string, string, CancellationToken, Task> write;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, SourceState> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> records = new(StringComparer.Ordinal);
    private JsonObject? catalog;
    private Dictionary<string, JsonObject> units = new(StringComparer.Ordinal);
    private string? mapHash, version, variant;
    private Published? published;
    private DateTimeOffset nextWarning;
    private static readonly Regex RunId = new(@"^auto-\d{8}T\d{13}Z-[a-f0-9]{16}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hash = new(@"^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Code = new(@"^[A-Za-z0-9]{4}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> FinalTiers = ["전설", "히든", "초월", "불멸", "영원", "제한", "신비", "왜곡", "변화", "특수", "세라핌", "랜덤전용"];
    private static readonly HashSet<string> Temporary = ["h076", "h04T", "h04Y", "h05D", "h0CA"];
    private const int MaximumBytes = 32 * 1024 * 1024;

    public LocalClearRecommendationStore(string autoDirectory, string webRoot,
        Action<Exception, string>? warning = null,
        Func<string, string, CancellationToken, Task>? writeAtomically = null)
    {
        this.autoDirectory = Path.GetFullPath(autoDirectory);
        this.webRoot = Path.GetFullPath(webRoot);
        clearDirectory = Path.GetDirectoryName(this.autoDirectory) ?? throw new ArgumentException("Missing local run directory.");
        derivedDirectory = Path.Combine(clearDirectory, "recommendations");
        this.warning = warning;
        write = writeAtomically ?? WriteAtomically;
    }

    public string ManifestJson => Volatile.Read(ref published)?.Manifest ?? "{\"status\":\"unavailable\"}";
    public int RecordCount => Volatile.Read(ref published)?.Count ?? 0;

    public string? SnapshotJson(string revision)
    {
        if (!Hash.IsMatch(revision)) return null;
        var current = Volatile.Read(ref published);
        if (current?.Revision == revision) return current.Json;
        try
        {
            var file = Path.Combine(derivedDirectory, "local-clear-records-" + revision + ".json");
            var bytes = ReadSafe(file, derivedDirectory);
            return Sha(bytes) == revision ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task RefreshAsync(CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            cancel.ThrowIfCancellationRequested();
            LoadCatalog();
            var stagedSources = new Dictionary<string, SourceState>(sources, StringComparer.Ordinal);
            var stagedRecords = new Dictionary<string, JsonObject>(records, StringComparer.Ordinal);
            var wrappers = new List<Imported>();
            if (Directory.Exists(autoDirectory))
            {
                EnsureOrdinaryDirectory(autoDirectory);
                foreach (var file in Directory.EnumerateFiles(autoDirectory, "auto-*.json").Order(StringComparer.Ordinal))
                {
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        var fingerprint = new SourceState(info.Length, info.LastWriteTimeUtc.Ticks, null);
                        if (sources.TryGetValue(info.Name, out var prior) && prior.Length == fingerprint.Length && prior.Ticks == fingerprint.Ticks) continue;
                        var bytes = ReadSafe(file, autoDirectory);
                        // 한 번 확정한 원본의 변경은 해당 판만 격리하고 이후 정상 승리는 계속 처리한다.
                        if (prior?.AcceptedHash is { } accepted && Sha(bytes) != accepted) throw new IOException("A confirmed local clear source changed.");
                        var raw = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("Invalid automatic run object.");
                        var imported = Import(file, bytes, raw);
                        stagedSources[info.Name] = fingerprint with { AcceptedHash = imported?.Hash };
                        if (imported is null) continue;
                        stagedRecords[imported.Id] = imported.Record;
                        wrappers.Add(imported);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
                    {
                        // 입력 한 건의 잠금·손상·변경으로 다른 판의 등록을 막지 않는다. fingerprint도 보류해 다음 갱신 때 재시도한다.
                        Warn(error);
                    }
                }
            }
            foreach (var imported in wrappers)
            {
                cancel.ThrowIfCancellationRequested();
                await WriteImmutable(imported.WrapperPath, imported.WrapperJson, cancel);
            }
            var payload = new JsonObject
            {
                ["schemaVersion"] = 1, ["catalogMapHash"] = mapHash,
                ["records"] = new JsonArray(stagedRecords.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (JsonNode)pair.Value.DeepClone()).ToArray())
            }.ToJsonString();
            var revision = Sha(Encoding.UTF8.GetBytes(payload));
            if (published?.Revision != revision)
            {
                var manifest = new JsonObject
                {
                    ["schemaVersion"] = 1, ["revision"] = revision, ["catalogMapHash"] = mapHash,
                    ["recordCount"] = stagedRecords.Count, ["dataUrl"] = "/api/local-clear-records/" + revision + ".json"
                }.ToJsonString();
                await WriteImmutable(Path.Combine(derivedDirectory, "local-clear-records-" + revision + ".json"), payload, cancel);
                await write(Path.Combine(derivedDirectory, "manifest.json"), manifest, cancel);
                cancel.ThrowIfCancellationRequested();
                Volatile.Write(ref published, new Published(revision, payload, manifest, stagedRecords.Count));
            }
            sources.Clear(); foreach (var pair in stagedSources) sources.Add(pair.Key, pair.Value);
            records.Clear(); foreach (var pair in stagedRecords) records.Add(pair.Key, pair.Value);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            Warn(error);
        }
        finally { gate.Release(); }
    }

    private void LoadCatalog()
    {
        if (catalog is not null) return;
        var parsed = JsonNode.Parse(ReadSafe(Path.Combine(webRoot, "data", "map-catalog.json"), Path.Combine(webRoot, "data"))) as JsonObject
            ?? throw new JsonException("Invalid map catalog.");
        var match = Regex.Match(Text(parsed["map"] as JsonObject, "version") ?? "", @"^(\d+\.\d{3})\[([A-Za-z0-9]+)\]$", RegexOptions.CultureInvariant);
        var hash = Text(parsed["map"] as JsonObject, "sha256");
        if (!match.Success || hash is null || !Hash.IsMatch(hash) || parsed["units"] is not JsonArray rows) throw new JsonException("Unverified map catalog identity.");
        var indexed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var unit in rows.OfType<JsonObject>())
        {
            var code = Text(unit, "rawcode");
            if (code is null || !Code.IsMatch(code) || !indexed.TryAdd(code, unit)) throw new JsonException("Invalid catalog unit identity.");
        }
        catalog = parsed; units = indexed; mapHash = hash; version = match.Groups[1].Value; variant = match.Groups[2].Value;
    }

    private Imported? Import(string file, byte[] bytes, JsonObject raw)
    {
        var id = Text(raw, "id");
        if (id is null || !RunId.IsMatch(id) || Path.GetFileName(file) != id + ".json" ||
            Integer(raw, "schemaVersion") != 1 || Text(raw, "recordKind") != "automatic_run" || Text(raw, "mapSha256") != mapHash ||
            Integer(raw, "localPlayer") is not { } player || player is < 0 or > 11 || Text(raw, "collectionStatus") != "ended") return null;
        var outcome = raw["outcome"] as JsonObject; var terminal = raw["terminalObservation"] as JsonObject; var active = raw["lastActiveSnapshot"] as JsonObject;
        if (Text(outcome, "result") != "victory" || Integer(outcome, "endedRound") != 65 || active is null || terminal is null ||
            !Progression(terminal["progression"] as JsonObject, player, true) || !Progression(active["progression"] as JsonObject, player, false)) return null;
        var end = Timestamp(terminal, "timestamp"); var last = Timestamp(active, "timestamp");
        if (end is null || last is null || end < last || (end - last).Value.TotalSeconds > 2 ||
            end != Timestamp(outcome, "endedObservedAtUtc") || last != Timestamp(outcome, "lastActiveObservedAtUtc") ||
            last != Timestamp(raw, "finalInventoryObservedAtUtc")) return null;
        var inventory = Inventory(active["counts"]); var final = Inventory(raw["finalInventory"]);
        if (inventory is null || final is null || inventory.ToJsonString() != final.ToJsonString()) return null;
        if (inventory.Any(node => !units.ContainsKey(Text(node as JsonObject, "rawcode")!))) return null;
        var selected = inventory.OfType<JsonObject>().Where(row =>
        {
            var code = Text(row, "rawcode")!; var unit = units[code];
            return Flag(unit, "playable") == true && Flag(unit, "isSummon") != true && FinalTiers.Contains(Text(unit, "tier") ?? "") && !Temporary.Contains(code);
        }).ToArray();
        if (selected.Length == 0) return null;
        // 전략 엔진과 같은 영웅·니카 슬롯 분류이며 로컬 분석 파일은 실행에 필요하지 않다.
        var slots = selected.Where(row =>
        {
            var code = Text(row, "rawcode")!; var unit = units[code];
            return code == "H0BK" || Text(unit, "tier") == "초월" && code[0] == 'H';
        }).Sum(row => Integer(row, "count")!.Value);
        if (slots > 1) return null;
        var sourceHash = Sha(bytes); var relative = "data/local-runs/auto/" + id + ".json";
        var wrapper = new JsonObject
        {
            ["schemaVersion"] = 1, ["recordKind"] = "clear_run", ["canonicalRunId"] = id,
            ["map"] = new JsonObject { ["version"] = version, ["variant"] = variant, ["sha256"] = mapHash },
            ["mode"] = new JsonObject { ["difficulty"] = "nightmare", ["difficultyValue"] = 6, ["soloConfirmed"] = false },
            ["outcome"] = new JsonObject { ["result"] = "clear", ["round"] = 65, ["nativeOutcome"] = "victory", ["nativeVictoryConfirmed"] = true, ["nativeRound"] = 65, ["nativeObservedAtUtc"] = Text(terminal, "timestamp") },
            ["evidence"] = new JsonObject { ["automaticRunRelativePath"] = relative, ["automaticRunSha256"] = sourceHash },
            ["finalInventory"] = raw["finalInventory"]!.DeepClone(), ["nativeTerminalProgression"] = terminal["progression"]!.DeepClone()
        };
        var wrapperPath = Path.Combine(clearDirectory, "clear-run-" + id + ".json"); var wrapperJson = wrapper.ToJsonString();
        // 이미 수동 등록한 동일 판은 기존 증거 파일과 해시를 그대로 사용한다.
        foreach (var existing in Directory.Exists(clearDirectory) ? Directory.EnumerateFiles(clearDirectory, "clear-run-*.json").Order(StringComparer.Ordinal) : Enumerable.Empty<string>())
        {
            byte[] priorBytes; JsonObject? prior;
            try { priorBytes = ReadSafe(existing, clearDirectory); prior = JsonNode.Parse(priorBytes) as JsonObject; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (!EquivalentWrapper(prior, id, sourceHash, terminal, final)) continue;
            wrapperPath = existing; wrapperJson = Encoding.UTF8.GetString(priorBytes); break;
        }
        var inventoryHash = Sha(Encoding.UTF8.GetBytes(inventory.ToJsonString()));
        var proof = new JsonObject
        {
            ["schemaVersion"] = 1, ["kind"] = "verified_native_clear", ["canonicalRunId"] = id,
            ["automaticRunSha256"] = sourceHash, ["clearRecordSha256"] = Sha(Encoding.UTF8.GetBytes(wrapperJson)), ["finalInventorySha256"] = inventoryHash,
            ["catalogMapHash"] = mapHash, ["versionNumber"] = version, ["variant"] = variant, ["nativeVictoryConfirmed"] = true,
            ["terminalRound"] = 65, ["terminalObservedAt"] = Text(terminal, "timestamp"), ["lastActiveObservedAt"] = Text(active, "timestamp"),
            ["roster"] = new JsonArray(selected.Select(row => (JsonNode)row.DeepClone()).ToArray())
        };
        var targetCodes = selected.Select(row => Text(row, "rawcode")!).ToHashSet(StringComparer.Ordinal);
        var entries = new JsonArray(inventory.OfType<JsonObject>().Select(row =>
        {
            var code = Text(row, "rawcode")!; var count = Integer(row, "count")!.Value; var unit = units[code]; var contextOnly = !targetCodes.Contains(code);
            return (JsonNode)new JsonObject
            {
                ["sourceName"] = Text(unit, "displayName") ?? Text(unit, "name") ?? code, ["sourceTier"] = Text(unit, "tier"), ["sourceRawcode"] = code, ["rawcode"] = code,
                ["reportedCount"] = count, ["count"] = count, ["countMeaning"] = "verified_native_quantity", ["countVerified"] = true, ["quantityKnown"] = true,
                ["status"] = contextOnly ? "context_only" : "exact_native_identity", ["contextOnly"] = contextOnly, ["mappingStatus"] = "native_map_and_last_active_quantity_verified",
                ["quantityEvidence"] = new JsonObject { ["kind"] = "verified_native_last_active_quantity", ["sourceProvider"] = "local-native", ["sourceCountVerified"] = true,
                    ["canonicalRunId"] = id, ["automaticRunSha256"] = sourceHash, ["finalInventorySha256"] = inventoryHash, ["catalogMapHash"] = mapHash, ["rawcode"] = code, ["count"] = count }
            };
        }).ToArray());
        var recordId = "local-" + id;
        var record = new JsonObject
        {
            ["id"] = recordId, ["displayId"] = recordId, ["sourceProvider"] = "local-native", ["sourceUrl"] = null, ["sourceUrls"] = new JsonArray(), ["sourceRecordIds"] = new JsonArray(recordId),
            ["createdAt"] = Text(terminal, "timestamp"), ["version"] = new JsonObject { ["number"] = version, ["variant"] = variant, ["numericVerified"] = true,
                ["sourceNumericVersionVerified"] = true, ["variantVerified"] = true, ["source"] = "verified_native_loaded_map", ["assumedByUser"] = false, ["datePolicyApplied"] = false,
                ["minimumPossibleVersion"] = version, ["possibleSourceVersions"] = new JsonArray(version) },
            ["versionEvidence"] = new JsonObject { ["method"] = "verified_native_loaded_map", ["sourceNumericVersionVerified"] = true, ["catalogMapHash"] = mapHash, ["automaticRunSha256"] = sourceHash },
            ["cohort"] = "nightmare-regular", ["difficulty"] = "악몽", ["solo"] = null, ["mode"] = null, ["otherworldMode"] = null,
            ["cohortEvidence"] = new JsonObject { ["difficultyRaw"] = "nightmare", ["classificationRule"] = "confirmed_same_map_native_nightmare_victory", ["soloConfirmed"] = false },
            ["environment"] = new JsonObject { ["elder"] = Text(active["elder"] as JsonObject, "name"), ["navigation"] = null, ["navigationNode"] = null, ["enhancements"] = null, ["items"] = active["items"]?.DeepClone() },
            ["reportedRemainingEnemyCount"] = null, ["imageObservedRemainingEnemyCount"] = null, ["remainingEnemyCountUsedForRanking"] = false,
            ["rosterComplete"] = true, ["rosterCompleteness"] = "verified_last_active_owned_snapshot_catalog_projection", ["entries"] = entries,
            ["goals"] = new JsonArray(selected.Select(row => (JsonNode)row.DeepClone()).ToArray()), ["mappingComplete"] = true, ["sourceMappingComplete"] = true,
            ["unresolved"] = new JsonArray(), ["sourceOutcome"] = "local_native_victory_confirmed", ["exactListedCountsVerified"] = true, ["fullRosterCountsVerified"] = true,
            ["sourceUnitCount"] = inventory.Count, ["localNativeProof"] = proof,
            ["observedInventory"] = active["counts"]!.DeepClone(), ["sourceRecordRelativePath"] = "data/local-runs/" + Path.GetFileName(wrapperPath)
        };
        return new Imported(recordId, sourceHash, record, wrapperPath, wrapperJson);
    }

    private bool EquivalentWrapper(JsonObject? value, string id, string hash, JsonObject terminal, JsonArray inventory) =>
        value is not null && Integer(value, "schemaVersion") == 1 && Text(value, "recordKind") == "clear_run" && Text(value, "canonicalRunId") == id &&
        Text(value["map"] as JsonObject, "sha256") == mapHash && Text(value["map"] as JsonObject, "version") == version && Text(value["map"] as JsonObject, "variant") == variant &&
        Text(value["mode"] as JsonObject, "difficulty") == "nightmare" && Integer(value["mode"] as JsonObject, "difficultyValue") == 6 &&
        Text(value["outcome"] as JsonObject, "result") == "clear" && Flag(value["outcome"] as JsonObject, "nativeVictoryConfirmed") == true &&
        Text(value["outcome"] as JsonObject, "nativeOutcome") == "victory" && Integer(value["outcome"] as JsonObject, "nativeRound") == 65 && Integer(value["outcome"] as JsonObject, "round") == 65 &&
        Timestamp(value["outcome"] as JsonObject, "nativeObservedAtUtc") == Timestamp(terminal, "timestamp") &&
        Text(value["evidence"] as JsonObject, "automaticRunSha256") == hash && Text(value["evidence"] as JsonObject, "automaticRunRelativePath") == "data/local-runs/auto/" + id + ".json" &&
        Inventory(value["finalInventory"])?.ToJsonString() == inventory.ToJsonString() &&
        Progression(value["nativeTerminalProgression"] as JsonObject, Integer(terminal["progression"] as JsonObject, "localPlayer")!.Value, true);

    private static bool Progression(JsonObject? value, int player, bool ended) => value is not null && Text(value, "status") == "confirmed" &&
        Integer(value, "localPlayer") == player && Integer(value, "round") == 65 && Flag(value, "activePlayer") == true &&
        Text(value, "difficulty") == "nightmare" && Integer(value, "difficultyValue") == 6 && Flag(value, "gameEnded") == ended && Flag(value, "victory") == ended;

    private static JsonArray? Inventory(JsonNode? value)
    {
        if (value is not JsonArray array || array.Count == 0) return null;
        var rows = new SortedDictionary<string, int>(StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), false));
        foreach (var node in array)
        {
            if (node is not JsonObject row || Text(row, "rawcode") is not { } code || !Code.IsMatch(code) ||
                Integer(row, "count") is not { } count || count is <= 0 or > 1_000_000 || !rows.TryAdd(code, count)) return null;
        }
        return new JsonArray(rows.Select(pair => (JsonNode)new JsonObject { ["rawcode"] = pair.Key, ["count"] = pair.Value }).ToArray());
    }

    private async Task WriteImmutable(string file, string json, CancellationToken cancel)
    {
        if (File.Exists(file))
        {
            if (Sha(ReadSafe(file, Path.GetDirectoryName(file)!)) != Sha(Encoding.UTF8.GetBytes(json))) throw new IOException("Immutable local clear data conflicts.");
            return;
        }
        await write(file, json, cancel);
    }

    private static async Task WriteAtomically(string file, string json, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        EnsureOrdinaryDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancel);
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] ReadSafe(string file, string parent)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(parent), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Local clear path escapes its directory.");
        EnsureOrdinaryDirectory(parent);
        var info = new FileInfo(file);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaximumBytes) throw new IOException("Unsupported local clear source.");
        return File.ReadAllBytes(file);
    }
    private static void EnsureOrdinaryDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (info.Exists && (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)) throw new IOException("Linked local clear directories are unsupported.");
    }
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private void Warn(Exception error)
    {
        if (DateTimeOffset.UtcNow < nextWarning) return;
        nextWarning = DateTimeOffset.UtcNow.AddSeconds(30); warning?.Invoke(error, "local-clear-recommendations");
    }
    private static string? Text(JsonObject? value, string property) => value?[property] is JsonValue node && node.TryGetValue<string>(out var result) ? result : null;
    private static int? Integer(JsonObject? value, string property) => value?[property] is JsonValue node && node.TryGetValue<int>(out var result) ? result : null;
    private static bool? Flag(JsonObject? value, string property) => value?[property] is JsonValue node && node.TryGetValue<bool>(out var result) ? result : null;
    private static DateTimeOffset? Timestamp(JsonObject? value, string property) => DateTimeOffset.TryParse(Text(value, property), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result) ? result : null;
}
