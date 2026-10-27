using System.ComponentModel;
using System.Text.Json.Serialization;

// 2.323[C]의 NB(선정 원로)와 NK(전역 효과 캐스터)를 읽기 전용으로 구분한다.
// 게임 함수를 호출하거나 프로세스 메모리를 쓰지 않는다. 세션 간 캐시는 두지 않는다.
static class ElderDetector
{
    internal const string MapHash = "0794996c9bc73eaa3020f20ebc45fb37cfa64a6e98ffe8aa0dfa3b356e77bc5c";
    private const uint HandleTableRva = 0xD30448;
    private static readonly Dictionary<string, (string Id, string Name)> Types = new(StringComparer.Ordinal)
    {
        ["o02E"] = ("warcury", "워큐리"),
        ["o031"] = ("saturn", "새턴"),
        ["o032"] = ("nasujuro", "나스쥬로")
    };
    private static readonly HashSet<string> RelevantAbilities = new(StringComparer.Ordinal)
        { "A0Z1", "A0Z2", "A0Z3", "A13D", "A13E", "A13F", "A13G" };

    internal sealed record State(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("effectStatus")] string EffectStatus,
        [property: JsonPropertyName("source")] Evidence Source);
    internal sealed record Evidence(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("candidateCount")] int CandidateCount,
        [property: JsonPropertyName("effectCasterCount")] int EffectCasterCount,
        [property: JsonPropertyName("readErrors")] int ReadErrors,
        [property: JsonPropertyName("selectedRawcode")] string? SelectedRawcode,
        [property: JsonPropertyName("effectAbilities")] IReadOnlyDictionary<string, int> EffectAbilities,
        [property: JsonPropertyName("scriptLines")] int[] ScriptLines);
    internal sealed record Candidate(uint Address, string Rawcode);
    internal sealed record Scan(Candidate[] Selected, int CasterCount, Dictionary<string, int> Abilities,
        int WorldErrors, int AbilityErrors, uint HandleTable);

    public static State Unknown(string reason) => new(null, null, "unknown", "unknown",
        new("read_only_world_and_effect_abilities", reason, 0, 0, 0, null,
            new Dictionary<string, int>(), [8941, 8956, 58971, 58979]));

    public static State Capture(MemoryReader memory, Roots roots, uint moduleBase, string? loadedMapHash)
    {
        if (!string.Equals(loadedMapHash, MapHash, StringComparison.Ordinal)) return Unknown("unsupported_map");
        return CaptureStable(current => ReadScan(memory, current, moduleBase),
            () => memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0), roots);
    }

    internal static State CaptureStable(Func<Roots, Scan> readScan, Func<Roots?> readRoots, Roots expectedRoots)
    {
        try
        {
            var initial = readRoots();
            if (!SnapshotStability.SameSession(initial, expectedRoots))
                return Unknown("session_changed");
            var first = readScan(initial!);
            var middle = readRoots();
            if (!SnapshotStability.SameSession(middle, expectedRoots))
                return Unknown("session_changed");
            var second = readScan(middle!);
            if (!SnapshotStability.SameSession(readRoots(), expectedRoots))
                return Unknown("session_changed");
            if (!SameScan(first, second)) return Unknown("elder_state_changing");
            return Resolve(second);
        }
        catch (Exception error) when (error is IOException or Win32Exception or OverflowException)
        {
            return Unknown("elder_read_failed");
        }
    }

    internal static State Resolve(Scan scan)
    {
        State Result(string? rawcode, string status, string effects, string reason)
        {
            var match = rawcode is not null && Types.TryGetValue(rawcode, out var found) ? found : default;
            return new(match.Id, match.Name, status, effects,
                new("read_only_world_and_effect_abilities", reason, scan.Selected.Length, scan.CasterCount,
                    scan.WorldErrors + scan.AbilityErrors, scan.Selected.Length == 1 ? scan.Selected[0].Rawcode : null,
                    scan.Abilities, [8941, 8956, 58971, 58979, 8874, 8933, 8748, 8791]));
        }
        if (scan.WorldErrors != 0) return Result(null, "unknown", "unknown", "incomplete_world_read");
        var usableCaster = scan.CasterCount == 1 && scan.AbilityErrors == 0;
        var highDifficulty = usableCaster && (Level("A0Z2") > 0 || Level("A0Z3") > 0);
        var active = new List<string>();
        if (usableCaster)
        {
            if (Level("A13D") > 0) active.Add("o02E");
            if (Level("A13G") > 0) active.Add("o031");
            if (Level("A13E") > 0 && Level("A13F") > 0) active.Add("o032");
            if ((Level("A13E") > 0) != (Level("A13F") > 0))
                return Result(null, "unknown", "unknown", "partial_nasujuro_effect_transition");
        }
        if (active.Count > 1) return Result(null, "ambiguous", "unknown", "multiple_elder_effects");
        if (active.Count == 1)
        {
            if (!highDifficulty) return Result(null, "unknown", "unknown", "effect_without_difficulty_aura");
            if (scan.Selected.Any(candidate => candidate.Rawcode != active[0]))
                return Result(null, "ambiguous", "unknown", "selected_and_active_elder_disagree");
            return Result(active[0], "active", "active", "effect_ability_pattern_confirmed");
        }
        if (scan.Selected.Length > 1) return Result(null, "ambiguous", "unknown", "multiple_selected_elder_objects");
        if (scan.Selected.Length == 1)
            return Result(scan.Selected[0].Rawcode, "selected", usableCaster ? "inactive" : "unknown",
                usableCaster ? "selected_object_before_effect_activation" : "selected_object_effect_state_unverified");
        return Result(null, "unknown", usableCaster ? "inactive" : "unknown",
            usableCaster && !highDifficulty ? "no_elder_object_or_high_difficulty_aura" : "no_selected_elder_object");

        int Level(string code) => scan.Abilities.GetValueOrDefault(code);
    }

    internal static bool SameScan(Scan first, Scan second) =>
        first.HandleTable == second.HandleTable && first.CasterCount == second.CasterCount &&
        first.WorldErrors == second.WorldErrors && first.AbilityErrors == second.AbilityErrors &&
        first.Selected.SequenceEqual(second.Selected) && first.Abilities.Count == second.Abilities.Count &&
        first.Abilities.All(pair => second.Abilities.TryGetValue(pair.Key, out var level) && level == pair.Value);

    private static Scan ReadScan(MemoryReader memory, Roots roots, uint moduleBase)
    {
        var selected = new List<Candidate>();
        var casters = new List<uint>();
        var seen = new HashSet<uint>();
        var errors = 0;
        var pointers = roots.Count == 0 ? [] : memory.Read(roots.Array, checked(roots.Count * 4));
        for (var index = 0; index < roots.Count; index++)
        {
            var address = BitConverter.ToUInt32(pointers, index * 4);
            if (address < 0x10000 || !seen.Add(address)) continue;
            try
            {
                // 전체 맵에서 필요한 네 rawcode만 식별하고 다른 적/플레이어 정보는 반환하지 않는다.
                var raw = memory.U32(address + 0x30);
                if (raw is not (0x6F303245 or 0x6F303331 or 0x6F303332 or 0x65303033)) continue;
                var info = memory.Read(address + 0x20, 0x40);
                var code = MemoryReader.DecodeRawcode(info.AsSpan(0x10, 4));
                var owner = BitConverter.ToUInt32(info, 0x38);
                if (memory.U32(address + 0x30) != raw || memory.U32(address + 0x58) != owner)
                { errors++; continue; }
                if ((BitConverter.ToUInt32(info, 0) & 1) != 0 || (BitConverter.ToUInt32(info, 0x3C) & 0x100) != 0) continue;
                if (code is not null && Types.ContainsKey(code) && owner == 7)
                    selected.Add(new(address, code));
                if (code == "e003" && owner == 6)
                {
                    var sprite = memory.U32(address + 0x28);
                    if (sprite < 0x10000) { errors++; continue; }
                    var position = memory.Read(sprite + 0xC0, 8);
                    var x = BitConverter.ToSingle(position, 0);
                    var y = BitConverter.ToSingle(position, 4);
                    if (float.IsFinite(x) && float.IsFinite(y) && Math.Abs(x + 1700) < 1 && Math.Abs(y - 1600) < 1)
                        casters.Add(address);
                }
            }
            catch (Exception error) when (error is IOException or Win32Exception) { errors++; }
        }
        var abilities = new Dictionary<string, int>(StringComparer.Ordinal);
        var abilityErrors = 0;
        uint handles = 0;
        if (casters.Count == 1)
        {
            try
            {
                handles = memory.U32(moduleBase + HandleTableRva);
                abilities = ReadEffectAbilities(memory, casters[0], moduleBase, handles);
            }
            catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { abilityErrors++; }
        }
        return new(selected.OrderBy(candidate => candidate.Address).ToArray(), casters.Count, abilities,
            errors, abilityErrors, handles);
    }

    // 1.28.5 원본 근거: GetUnitAbilityLevel RVA236120 -> 4C0C90,
    // unit+1DC/1E0 약참조 -> 08AE90 -> 07E090 -> record+54 CAbility.
    // CAbility+24/+28 다음 참조, +34 custom rawcode, +50은 0부터 시작하는 레벨.
    private static Dictionary<string, int> ReadEffectAbilities(MemoryReader memory, uint caster, uint moduleBase, uint handles)
    {
        if (handles < 0x10000) throw new IOException("Missing handle table.");
        var head = memory.Read(caster + 0x1DC, 8);
        var current = ResolveAbility(memory, handles, head);
        var seen = new HashSet<uint>();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        while (current != 0)
        {
            if (!seen.Add(current) || seen.Count > 128) throw new IOException("Invalid ability chain.");
            var data = memory.Read(current, 0x54);
            var vtable = BitConverter.ToUInt32(data, 0);
            if (vtable < moduleBase || (ulong)vtable >= (ulong)moduleBase + 0xE00000 || (vtable & 3) != 0)
                throw new IOException("Unknown ability class.");
            var rawcode = MemoryReader.DecodeRawcode(data.AsSpan(0x34, 4));
            var level = BitConverter.ToUInt32(data, 0x50);
            if (rawcode is null || level > 255) throw new IOException("Invalid ability body.");
            if (RelevantAbilities.Contains(rawcode) && !result.TryAdd(rawcode, checked((int)level + 1)))
                throw new IOException("Duplicate effect ability.");
            current = ResolveAbility(memory, handles, data.AsSpan(0x24, 8));
        }
        if (!head.AsSpan().SequenceEqual(memory.Read(caster + 0x1DC, 8)) ||
            memory.U32(caster + 0x30) != 0x65303033 || memory.U32(caster + 0x58) != 6 ||
            (memory.U32(caster + 0x20) & 1) != 0 || (memory.U32(caster + 0x5C) & 0x100) != 0 ||
            memory.U32(moduleBase + HandleTableRva) != handles)
            throw new IOException("Effect caster changed.");
        return result;
    }

    private static uint ResolveAbility(MemoryReader memory, uint handles, ReadOnlySpan<byte> reference)
    {
        var index = BitConverter.ToUInt32(reference[..4]);
        var serial = BitConverter.ToUInt32(reference[4..]);
        if ((index & serial) == uint.MaxValue) return 0;
        var high = (index & 0x80000000) != 0;
        index &= 0x7FFFFFFF;
        var offset = high ? 0x20u : 0u;
        var count = memory.U32(handles + 0x1C + offset);
        if (count > 2_000_000 || index >= count) throw new IOException("Invalid ability handle index.");
        var array = memory.U32(handles + 0x0C + offset);
        var slotAddress = checked(array + index * 8);
        var slot = memory.Read(slotAddress, 8);
        var record = BitConverter.ToUInt32(slot, 4);
        if (BitConverter.ToUInt32(slot, 0) != 0xFFFFFFFE || record < 0x10000 ||
            memory.U32(record + 0x18) != serial || memory.U32(record + 0x20) != 0)
            throw new IOException("Stale ability handle.");
        var ability = memory.U32(record + 0x54);
        if (ability < 0x10000 || !slot.AsSpan().SequenceEqual(memory.Read(slotAddress, 8)) ||
            memory.U32(record + 0x18) != serial || memory.U32(record + 0x20) != 0)
            throw new IOException("Ability handle changed.");
        return ability;
    }

    internal static void RunSelfTests()
    {
        Scan Fixture(Candidate[] selected, params (string Code, int Level)[] abilities) =>
            new(selected, 1, abilities.ToDictionary(pair => pair.Code, pair => pair.Level, StringComparer.Ordinal), 0, 0, 0x10000);
        void Check(bool value, string label) { if (!value) throw new Exception("Elder test failed: " + label); }
        var selected = Fixture([new(0x20000, "o031")], ("A0Z3", 1));
        Check(Resolve(selected) is { Id: "saturn", Name: "새턴", Status: "selected", EffectStatus: "inactive" }, "initial selection precedes effect activation");
        Check(Resolve(Fixture([], ("A0Z3", 3), ("A13G", 2))) is { Id: "saturn", Status: "active" }, "effect caster identifies active elder without a visible model");
        Check(Resolve(Fixture([new(1, "o02E"), new(2, "o031"), new(3, "o032")])) is { Id: null, Status: "ambiguous" }, "three placed elder objects cannot imply one selected elder");
        Check(Resolve(Fixture([], ("A0Z3", 2), ("A13D", 2), ("A13G", 2))) is { Id: null, Status: "ambiguous" }, "multiple effects are rejected");
        Check(Resolve(Fixture([], ("A0Z3", 2), ("A13E", 2))) is { Id: null, Status: "unknown" }, "partial two-ability transition is rejected");
        Check(Resolve(Fixture([], ("A0Z3", 2), ("A13E", 2), ("A13F", 2))) is { Id: "nasujuro", Status: "active" }, "both nasujuro effects identify activation");
        Check(Resolve(Fixture([new(1, "o02E")], ("A0Z3", 3), ("A13G", 2))) is { Id: null, Status: "ambiguous" }, "selected and effect identities must agree");
        Check(Resolve(selected with { WorldErrors = 1 }) is { Id: null }, "partial scans do not guess an elder");
        Check(!SameScan(selected, selected with { Selected = [new(0x30000, "o02E")] }), "changed object invalidates confirmation");
        Check(Resolve(Fixture([])) is { Id: null, Status: "unknown" }, "a new session never inherits the old selected elder");
        Check(Unknown("unsupported_map").Id is null, "unsupported map cannot reuse a known identity");

        var expectedRoots = new Roots(0x10000, 0x20000, 0x30000, 0x40000, 50, 0);
        var rootsReads = 0;
        var scannedRoots = new List<Roots>();
        var grown = CaptureStable(current => { scannedRoots.Add(current);return selected; },
            () => ++rootsReads == 1 ? expectedRoots with { Array = 0x50000, Count = 60 } : expectedRoots with { Array = 0x60000, Count = 70 }, expectedRoots);
        Check(grown is { Id: "saturn", Status: "selected" } && scannedRoots.Count == 2 &&
            scannedRoots[0].Array == 0x50000 && scannedRoots[0].Count == 60 &&
            scannedRoots[1].Array == 0x60000 && scannedRoots[1].Count == 70,
            "both scans use their latest world array while enemy-only growth preserves the elder");
        foreach (var changed in new[] { expectedRoots with { Ui = 0x70000 }, expectedRoots with { Players = 0x70000 },
            expectedRoots with { Table = 0x70000 }, expectedRoots with { LocalPlayer = 1 } })
        {
            for (var boundary = 1; boundary <= 3; boundary++)
            {
                var at = boundary;var reads = 0;
                Check(CaptureStable(_ => selected, () => ++reads == at ? changed : expectedRoots, expectedRoots)
                    is { Id: null, Status: "unknown", Source.Reason: "session_changed" }, "every real session boundary is still rejected");
            }
        }
        Check(CaptureStable(_ => selected, () => null, expectedRoots)
            is { Id: null, Status: "unknown", Source.Reason: "session_changed" }, "missing roots never inherit a selected elder");
        var scans = 0;
        Check(CaptureStable(_ => ++scans == 1 ? selected : selected with { Selected = [new(0x30000, "o02E")] },
            () => expectedRoots, expectedRoots) is { Id: null, Source.Reason: "elder_state_changing" }, "object changes still fail the two-scan check");
        scans = 0;
        Check(CaptureStable(_ => ++scans == 1 ? selected : Fixture([new(0x20000, "o031")], ("A0Z3", 1), ("A13G", 2)),
            () => expectedRoots, expectedRoots) is { Id: null, Source.Reason: "elder_state_changing" }, "effect activation is not hidden by the session fix");
        Check(CaptureStable(_ => selected with { WorldErrors = 1 }, () => expectedRoots, expectedRoots)
            is { Id: null, Source.Reason: "incomplete_world_read", Source.ReadErrors: 1 }, "matching partial reads never confirm an elder");
        Check(CaptureStable(_ => throw new IOException("Fixture partial read."), () => expectedRoots, expectedRoots)
            is { Id: null, Source.Reason: "elder_read_failed" }, "read failures preserve their existing unknown outcome");
        Console.WriteLine("PASS: elder initial selection, effect activation, ambiguous/missing objects, transitions, disagreement, unstable reads and session reset.");
        Console.WriteLine("PASS: elder latest-array capture, enemy churn, session boundaries, object/effect transitions and read errors (7 pure cases; no game input).");
    }
}
