using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// 종료 판정과 최종 전력은 분리한다. 유닛 숨김이나 프로세스 종료는 패배 증거가 아니다.
sealed class GameRunRecorder
{
    private readonly string directory;
    private readonly Action<Exception, string>? warning;
    private readonly Func<string, string, CancellationToken, Task> write;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private JsonObject? run;
    private string? sessionKey;
    private string semantic = "";
    private string? gap;
    private bool dirty;
    private DateTimeOffset? pendingResetAt;
    private DateTimeOffset nextWarning;
    private static readonly HashSet<string> Temporary = ["h06O", "h0C5", "h0CA"];
    private static readonly HashSet<string> Terminal = ["victory", "inactive_or_left", "unconfirmed_end"];

    public GameRunRecorder(string directory, Action<Exception, string>? warning = null,
        Func<string, string, CancellationToken, Task>? writeAtomically = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.warning = warning;
        write = writeAtomically ?? WriteAtomically;
    }

    internal JsonObject? CurrentForTests => run?.DeepClone().AsObject();
    internal string? CurrentPathForTests => run is null ? null : FilePath(run);

    public async Task ProcessAsync(JsonObject payload, CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            if (!TryIdentity(payload, out var key, out var timestamp, out var player))
            {
                await SuspendAsync(Text(payload, "status") ?? "invalid_snapshot", Timestamp(payload), cancel);
                return;
            }
            if (run is not null && timestamp < Timestamp(run, "lastObservedAtUtc"))
            {
                await FlushAsync(cancel);
                return;
            }
            var progression = payload["progression"] as JsonObject;
            if (progression is null || Text(progression, "status") != "confirmed" || Int(progression, "localPlayer") != player ||
                Int(progression, "round") is not { } round || round is < 0 or > 65 || Bool(progression, "activePlayer") is not { } active ||
                Bool(progression, "gameEnded") is not { } ended)
            {
                await SuspendAsync("progression_unconfirmed", timestamp, cancel);
                return;
            }
            var victory = Bool(progression, "victory");
            // Ji=false은 실제 패배와 자진 퇴장 모두에 사용된다. YU는 전역 승리 표식이다.
            var result = !active && ended ? "inactive_or_left" : active && victory == true && ended ? "victory" : ended ? "unconfirmed_end" : "in_progress";
            if (!active && !ended || victory == true && !ended)
            {
                await SuspendAsync("inconsistent_progression", timestamp, cancel);
                return;
            }
            if (run is null && result != "in_progress")
            {
                // 종료 화면에서 재시작했어도 앞서 저장한 같은 판의 활성 표본은 복구할 수 있다.
                // 그런 근거가 없는 종료 표본으로 새 판을 만들거나 봉인된 결과를 다시 쓰지는 않는다.
                var saved = LoadLatest(key);
                if (saved is null || IsTerminal(saved) || !CanRestoreTerminalBaseline(saved, payload, timestamp, player)) return;
                run = saved;
                sessionKey = key;
                semantic = Text(run, "lastSemanticState") ?? "";
                gap = "collector_restarted";
            }
            var stable = Bool(payload, "stableRoots") == true && Bool(payload, "stableUnits") == true && Int(payload, "readErrors") == 0;
            if (!stable)
            {
                // 독립적으로 확정된 종료값은 남기되, 불안정한 수량으로 최종 재고를 교체하지 않는다.
                if (run is null || key != sessionKey || result == "in_progress")
                {
                    await SuspendAsync("inventory_unstable", timestamp, cancel);
                    return;
                }
            }
            if (run is not null && key != sessionKey)
            {
                await InterruptAsync("session_changed", timestamp, cancel);
                if (dirty) return;
                Reset();
            }
            if (run is null)
            {
                // 시작을 보지 못한 종료 표본으로는 새 판을 만들지 않는다.
                if (result != "in_progress" || !active || !stable) return;
                run = LoadLatest(key);
                sessionKey = key;
                if (run is not null && IsTerminal(run)) Reset();
                if (run is null) Create(payload, key, timestamp, player, round);
                else
                {
                    semantic = Text(run, "lastSemanticState") ?? "";
                    gap = Text(run, "collectionStatus") == "active" ? null : "collector_restarted";
                }
            }
            if (run is null) return;
            var previousTime = Timestamp(run, "lastObservedAtUtc");
            if (timestamp < previousTime || timestamp == previousTime && Text(run, "lastSemanticState") is not null)
            {
                await FlushAsync(cancel);
                return;
            }
            if (IsTerminal(run))
            {
                // 나중에 다른 플레이어가 승리해도 앞선 로컬 비활성화를 승리로 바꾸지 않는다.
                await FlushAsync(cancel);
                if (dirty || result != "in_progress" || !active || !stable) return;
                // 새 판에서 같은 native 주소가 재사용돼도 봉인된 이전 파일을 덮어쓰지 않는다.
                Reset();
                Create(payload, key, timestamp, player, round);
            }
            if (result != "in_progress" && round < (Int(run, "lastKnownRound") ?? round))
            {
                // 전역 라운드는 같은 판에서 후퇴하지 않는다. 주소 재사용이나 불일치 표본을
                // 이전 판의 종료로 연결하거나 minimum > maximum인 범위로 저장하지 않는다.
                await SuspendAsync("terminal_round_regressed", timestamp, cancel);
                return;
            }
            if (result == "in_progress" && round < (Int(run, "lastKnownRound") ?? round))
            {
                if (round > 2 || pendingResetAt is null || timestamp <= pendingResetAt.Value)
                {
                    pendingResetAt = timestamp;
                    await SuspendAsync("round_regressed", timestamp, cancel);
                    return;
                }
                await InterruptAsync("initial_round_reset_confirmed_without_terminal", timestamp, cancel);
                if (dirty) return;
                Reset();
                Create(payload, key, timestamp, player, round);
            }
            pendingResetAt = null;
            var snapshot = Snapshot(payload, progression, timestamp);
            var nextSemantic = Semantic(snapshot, result);
            var resumed = gap is not null || Text(run!, "collectionStatus") != "active";
            if (resumed && result == "in_progress")
            {
                Event("collection_resumed", timestamp, round, new JsonObject { ["reason"] = gap ?? "collector_restarted" });
                run!["outcome"] = Outcome("in_progress");
                gap = null;
            }
            run!["lastObservedAtUtc"] = Format(timestamp);
            run["lastKnownRound"] = round;
            run["collectionStatus"] = "active";
            if (result == "in_progress")
            {
                var previous = run["lastActiveSnapshot"] as JsonObject;
                run["lastActiveSnapshot"] = snapshot.DeepClone();
                if (nextSemantic != semantic)
                {
                    var reason = previous is null ? "initial_inventory" : Int(previous["progression"] as JsonObject, "round") != round ? "round_changed" : "state_changed";
                    var detail = new JsonObject { ["changes"] = Changes(previous?["counts"] as JsonArray, snapshot["counts"]!.AsArray()), ["context"] = Context(snapshot) };
                    if (reason is "initial_inventory" or "round_changed") detail["counts"] = snapshot["counts"]!.DeepClone();
                    Event(reason, timestamp, round, detail);
                }
            }
            else
            {
                run["terminalObservation"] = snapshot.DeepClone();
                run["outcome"] = Outcome(result, timestamp, round);
                var last = run["lastActiveSnapshot"] as JsonObject;
                var lastActiveRound = Int(last?["progression"] as JsonObject, "round");
                var lastActiveAt = last is null ? DateTimeOffset.MinValue : Timestamp(last);
                var gapSeconds = lastActiveAt == DateTimeOffset.MinValue ? (double?)null : Math.Max(0, (timestamp - lastActiveAt).TotalSeconds);
                var outcome = run["outcome"]!.AsObject();
                outcome["lastActiveRound"] = lastActiveRound;
                outcome["lastActiveObservedAtUtc"] = last?["timestamp"]?.DeepClone();
                outcome["observationGapSeconds"] = gapSeconds;
                outcome["possibleEndRoundRange"] = new JsonObject { ["minimum"] = lastActiveRound, ["maximum"] = round };
                outcome["exactEndRound"] = lastActiveRound == round && gapSeconds is <= 2 ? round : null;
                run["finalRoster"] = last?["counts"] is JsonArray counts ? new JsonArray(counts.Where(node => node is JsonObject item && Bool(item, "isCharacter") == true).Select(node => node!.DeepClone()).ToArray()) : new JsonArray();
                run["finalInventory"] = last?["counts"]?.DeepClone() ?? new JsonArray();
                run["finalResources"] = last?["resources"]?.DeepClone();
                run["finalInventoryObservedAtUtc"] = last?["timestamp"]?.DeepClone();
                run["collectionStatus"] = "ended";
                Event("native_termination_observed", timestamp, round, new JsonObject { ["result"] = result, ["defeatConfirmed"] = false, ["context"] = Context(snapshot) });
            }
            semantic = nextSemantic;
            run["lastSemanticState"] = semantic;
            await FlushAsync(cancel);
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (run is not null && Timestamp(run, "lastObservedAtUtc") > now) now = Timestamp(run, "lastObservedAtUtc");
            await InterruptAsync("collector_stopped", now, cancel);
        }
        finally { gate.Release(); }
    }

    private void Create(JsonObject payload, string key, DateTimeOffset timestamp, int player, int round)
    {
        var id = "auto-" + timestamp.UtcDateTime.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + "-" + key[..16];
        run = new JsonObject
        {
            ["schemaVersion"] = 1, ["recordKind"] = "automatic_run", ["id"] = id, ["sessionKey"] = key,
            ["mapSha256"] = Text(payload, "loadedMapHash"), ["localPlayer"] = player,
            ["startedObservedAtUtc"] = Format(timestamp), ["lastObservedAtUtc"] = Format(timestamp),
            ["startedAtRound"] = round, ["lastKnownRound"] = round, ["collectionStatus"] = "active",
            ["outcome"] = Outcome("in_progress"), ["events"] = new JsonArray(),
            ["evidencePolicy"] = new JsonObject
            {
                ["capture"] = "read_only_native_snapshots", ["outcomeRequiresConfirmedNativeProgression"] = true,
                ["inactiveDoesNotDistinguishDefeatFromLeave"] = true, ["unknownIsNotDefeat"] = true,
                ["resourceHistory"] = "sampled_with_inventory_round_story_items_or_terminal_events",
                ["inventoryChangeCause"] = "unattributed_net_change", ["failureReason"] = "requires_independent_evidence_or_user_report"
            }
        };
        sessionKey = key;
        semantic = "";
        gap = null;
        dirty = true;
    }

    private static JsonObject Outcome(string result, DateTimeOffset? timestamp = null, int? round = null) => new()
    {
        ["result"] = result, ["endedObservedAtUtc"] = timestamp is { } time ? Format(time) : null,
        ["endedRound"] = round, ["endedRoundEvidence"] = round.HasValue ? "confirmed_native_progression" : null,
        ["endedRoundMeaning"] = round.HasValue ? "first_terminal_observation_global_round_not_necessarily_death_round" : null,
        ["defeatRound"] = null, ["defeatConfirmed"] = false, ["failureReason"] = null,
        ["userOutcome"] = null, ["userDefeatRound"] = null, ["userFailureReason"] = null
    };

    private async Task SuspendAsync(string reason, DateTimeOffset timestamp, CancellationToken cancel)
    {
        if (run is null || IsTerminal(run)) { await FlushAsync(cancel); return; }
        if (timestamp < Timestamp(run, "lastObservedAtUtc")) { await FlushAsync(cancel); return; }
        if (reason is "game_not_running" or "world_not_ready")
        {
            // 준비 안 됨은 일시 로딩일 수도 있으므로 종료 원인이나 라운드는 확정하지 않는다.
            await InterruptAsync(reason, timestamp, cancel);
            return;
        }
        if (gap != reason)
        {
            gap = reason;
            run["collectionStatus"] = "suspended";
            Event("observation_gap", timestamp, Int(run, "lastKnownRound"), new JsonObject { ["reason"] = reason });
        }
        run["lastObservedAtUtc"] = Format(timestamp);
        await FlushAsync(cancel);
    }

    private async Task InterruptAsync(string reason, DateTimeOffset timestamp, CancellationToken cancel)
    {
        if (run is not null && !IsTerminal(run) && (Text(run["outcome"] as JsonObject, "result") != "interrupted" || gap != reason))
        {
            gap = reason;
            run["collectionStatus"] = "interrupted";
            run["outcome"] = Outcome("interrupted");
            run["outcome"]!["interruptionObservedAtUtc"] = Format(timestamp);
            run["outcome"]!["interruptionReason"] = reason;
            Event("collection_interrupted", timestamp, Int(run, "lastKnownRound"), new JsonObject { ["reason"] = reason, ["nativeEndObserved"] = false });
        }
        if (run is not null && !IsTerminal(run) && timestamp >= Timestamp(run, "lastObservedAtUtc")) run["lastObservedAtUtc"] = Format(timestamp);
        await FlushAsync(cancel);
    }

    private void Event(string kind, DateTimeOffset timestamp, int? round, JsonObject detail)
    {
        if (run is null) return;
        run["events"]!.AsArray().Add(new JsonObject { ["type"] = kind, ["timestamp"] = Format(timestamp), ["round"] = round, ["detail"] = detail });
        dirty = true;
    }

    private async Task FlushAsync(CancellationToken cancel)
    {
        if (!dirty || run is null) return;
        try
        {
            await write(FilePath(run), run.ToJsonString(json), cancel);
            dirty = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Warn(error, "run_record");
        }
    }

    private async Task WriteAtomically(string file, string content, CancellationToken cancel)
    {
        Directory.CreateDirectory(directory);
        var temporary = file + ".tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancel);
        File.Move(temporary, file, true);
    }

    private JsonObject? LoadLatest(string key)
    {
        if (!Directory.Exists(directory)) return null;
        JsonObject? latest = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "auto-*.json"))
            {
                try
                {
                    var candidate = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                    if (candidate is null || Text(candidate, "recordKind") != "automatic_run" || Int(candidate, "schemaVersion") != 1 ||
                        Text(candidate, "sessionKey") != key || candidate["events"] is not JsonArray || !ValidId(Text(candidate, "id")) ||
                        !string.Equals(Path.GetFileName(file), Text(candidate, "id") + ".json", StringComparison.Ordinal)) continue;
                    if (latest is null || Timestamp(candidate, "lastObservedAtUtc") > Timestamp(latest, "lastObservedAtUtc")) latest = candidate;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { Warn(error, "run_resume"); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Warn(error, "run_resume"); }
        return latest;
    }

    private static bool CanRestoreTerminalBaseline(JsonObject document, JsonObject payload, DateTimeOffset timestamp, int player)
    {
        if (Text(document["outcome"] as JsonObject, "result") is not ("in_progress" or "interrupted") ||
            Int(document, "localPlayer") != player || Text(document, "mapSha256") != Text(payload, "loadedMapHash") ||
            document["lastActiveSnapshot"] is not JsonObject active || active["counts"] is not JsonArray ||
            active["progression"] is not JsonObject progression || Text(progression, "status") != "confirmed" ||
            Int(progression, "localPlayer") != player || Bool(progression, "activePlayer") != true ||
            Bool(progression, "gameEnded") != false || Bool(progression, "victory") == true ||
            Int(progression, "round") is not { } round || round is < 0 or > 65 || Int(document, "lastKnownRound") != round)
            return false;
        var activeAt = Timestamp(active);
        var lastObservedAt = Timestamp(document, "lastObservedAtUtc");
        return activeAt != DateTimeOffset.MinValue && lastObservedAt != DateTimeOffset.MinValue &&
            activeAt <= lastObservedAt && lastObservedAt <= timestamp;
    }

    private void Reset() { run = null; sessionKey = null; semantic = ""; gap = null; dirty = false; pendingResetAt = null; }
    private string FilePath(JsonObject document) => Path.Combine(directory, ValidId(Text(document, "id")) ? Text(document, "id") + ".json" : throw new IOException("Invalid automatic run identifier."));
    private static bool ValidId(string? id) => id is { Length: >= 10 and <= 100 } && id.StartsWith("auto-", StringComparison.Ordinal) && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');
    private static bool IsTerminal(JsonObject document) => Terminal.Contains(Text(document["outcome"] as JsonObject, "result") ?? "");
    private void Warn(Exception error, string destination)
    {
        if (DateTimeOffset.UtcNow < nextWarning) return;
        nextWarning = DateTimeOffset.UtcNow.AddSeconds(30);
        warning?.Invoke(error, destination);
    }

    private static bool TryIdentity(JsonObject payload, out string key, out DateTimeOffset timestamp, out int player)
    {
        key = ""; timestamp = Timestamp(payload); player = Int(payload, "localPlayer") ?? -1;
        var map = Text(payload, "loadedMapHash"); var identity = Text(payload, "sessionIdentity"); var process = Int(payload, "processId");
        if (Text(payload, "status") != "snapshot_read" || Text(payload, "gameVersion") != GameProgression.SupportedVersion ||
            map != GameProgression.SupportedMapHash || Bool(payload, "catalogMatchesLoadedArchive") != true ||
            player is < 0 or > 3 || process is null or <= 0 || identity is not { Length: > 0 and <= 256 } || timestamp == DateTimeOffset.MinValue) return false;
        key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{process}|{identity}|{map}|{player}")));
        return true;
    }

    private static JsonObject Snapshot(JsonObject payload, JsonObject progression, DateTimeOffset timestamp)
    {
        var counts = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in payload["counts"] as JsonArray ?? [])
        {
            if (item is not JsonObject unit || Text(unit, "rawcode") is not { Length: 4 } rawcode || Temporary.Contains(rawcode) ||
                !rawcode.All(char.IsAsciiLetterOrDigit) || Int(unit, "count") is not { } count || count is <= 0 or > 1_000_000) continue;
            counts[rawcode] = new JsonObject { ["rawcode"] = rawcode, ["name"] = Text(unit, "name"), ["count"] = count, ["isCharacter"] = Bool(unit, "isCharacter") };
        }
        var cleanProgression = Pick(progression, "status", "localPlayer", "round", "storyIndex", "activePlayer", "difficulty", "difficultyValue", "gameEnded", "victory", "playerSlotState", "roundTransitionDepth");
        cleanProgression["playerSlotStateEvidence"] = progression["source"]?["playerSlotState"] is JsonObject slot ? Pick(slot, "status", "reason", "readOnly", "sampleCount") : null;
        cleanProgression["randomWisp"] = progression["randomWisp"] is JsonObject random ? Pick(random, "status", "localPlayer", "drawCount", "correctionInterval", "commonAcquiredCounts") : null;
        return new JsonObject
        {
            ["timestamp"] = Format(timestamp), ["sequence"] = Int(payload, "sequence"),
            ["counts"] = new JsonArray(counts.Values.Select(value => (JsonNode)value).ToArray()),
            ["progression"] = cleanProgression,
            ["resources"] = payload["resources"] is JsonObject resources ? Pick(resources, "status", "localPlayer", "gold", "wood") : null,
            ["items"] = payload["items"] is JsonObject items ? Pick(items, "status", "localPlayer", "counts") : null,
            ["startingEvolution"] = payload["startingEvolution"] is JsonObject evolution ? Pick(evolution, "status", "localPlayer", "resultRawcode", "pendingCount", "phase", "killCount", "targetKills") : null,
            ["elder"] = payload["elder"] is JsonObject elder ? Pick(elder, "status", "id", "name", "effectStatus") : null
        };
    }

    private static JsonObject Pick(JsonObject source, params string[] keys)
    {
        var result = new JsonObject();
        foreach (var key in keys) if (source.TryGetPropertyValue(key, out var value)) result[key] = value?.DeepClone();
        return result;
    }
    private static JsonObject Context(JsonObject snapshot) => Pick(snapshot, "resources", "items", "elder", "progression", "startingEvolution");
    private static string Semantic(JsonObject snapshot, string result)
    {
        var progression = snapshot["progression"]!.AsObject();
        // 자원만 변한 매 tick은 기록하지 않는다. 순번·시각·전역 유닛 배열도 변경 키가 아니다.
        return new JsonObject
        {
            ["counts"] = snapshot["counts"]!.DeepClone(), ["progression"] = Pick(progression, "status", "round", "storyIndex", "activePlayer", "difficulty", "difficultyValue", "gameEnded", "victory"),
            ["randomWisp"] = progression["randomWisp"]?.DeepClone(),
            ["startingEvolution"] = snapshot["startingEvolution"] is JsonObject evolution ? Pick(evolution, "status", "localPlayer", "resultRawcode", "pendingCount", "phase") : null,
            ["items"] = snapshot["items"]?.DeepClone(), ["elder"] = snapshot["elder"]?.DeepClone(), ["result"] = result
        }.ToJsonString();
    }
    private static JsonArray Changes(JsonArray? previous, JsonArray current)
    {
        var before = (previous ?? []).OfType<JsonObject>().ToDictionary(unit => Text(unit, "rawcode")!, unit => Int(unit, "count") ?? 0, StringComparer.Ordinal);
        var after = current.OfType<JsonObject>().ToDictionary(unit => Text(unit, "rawcode")!, unit => Int(unit, "count") ?? 0, StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var code in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            if (before.GetValueOrDefault(code) != after.GetValueOrDefault(code)) result.Add(new JsonObject { ["rawcode"] = code, ["before"] = before.GetValueOrDefault(code), ["after"] = after.GetValueOrDefault(code) });
        return result;
    }
    private static string? Text(JsonObject? node, string property) => node?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int? Int(JsonObject? node, string property) => node?[property] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    private static bool? Bool(JsonObject? node, string property) => node?[property] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    private static DateTimeOffset Timestamp(JsonObject payload, string property = "timestamp") => DateTimeOffset.TryParse(Text(payload, property), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result) ? result : DateTimeOffset.MinValue;
    private static string Format(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
