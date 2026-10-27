using System.ComponentModel;
using System.Text.Json.Serialization;

// 2.323[C] UBAN/KING의 지속 전시·항법 표식을 읽는다. JASS 변수나 게임 메모리를 쓰지 않는다.
static class GameRecipeConditions
{
    internal const string MapHash = "0794996c9bc73eaa3020f20ebc45fb37cfa64a6e98ffe8aa0dfa3b356e77bc5c";
    internal const string Version = "1.28.5.7680";
    private static readonly HashSet<string> Wanted = new(StringComparer.Ordinal)
        { "A908", "A0II", "A905", "A906", "A907", "A909" };
    // 같은 SHA의 CHi 조합 등록 중 UBAN 대상 60종. P7의 무관 전시·스토리 개체 능력은 읽지 않는다.
    private static readonly HashSet<string> BanTargets = new(StringComparer.Ordinal)
    {
        "h040", "h049", "h04A", "h04B", "h04C", "h04D", "h04E", "h04F", "h04G", "h04J", "h04Q", "h058",
        "h059", "h05A", "h05B", "h05C", "h05E", "h05F", "h05G", "h05I", "H05N", "h06A", "h07I", "h07M",
        "h081", "h084", "h08O", "h08Q", "h08R", "H08U", "H08V", "H08W", "H08X", "H08Y", "H08Z", "H090",
        "H091", "H092", "H094", "H095", "H096", "H097", "H098", "H099", "H09A", "H09B", "H09E", "H09F",
        "H09H", "H09I", "H0AA", "h0AI", "H0B2", "H0B4", "H0B5", "H0BD", "H0BL", "H0BX", "h0CJ", "H0CO"
    };

    internal sealed record State(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("localPlayer")] int? LocalPlayer,
        [property: JsonPropertyName("unitBans")] Dictionary<string, bool> UnitBans,
        [property: JsonPropertyName("king")] Gate King,
        [property: JsonPropertyName("source")] Evidence Source)
    {
        [JsonPropertyName("special")]
        public GameSpecialRecipeConditions.State Special { get; init; } = GameSpecialRecipeConditions.Unknown("not_captured");
    }
    internal sealed record Gate([property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("reason")] string Reason);
    internal sealed record Evidence(
        [property: JsonPropertyName("readOnly")] bool ReadOnly,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("mapSha256")] string MapSha256,
        [property: JsonPropertyName("sampleCount")] int SampleCount,
        [property: JsonPropertyName("readErrors")] int ReadErrors,
        [property: JsonPropertyName("worldReadErrors")] int WorldReadErrors,
        [property: JsonPropertyName("abilityReadErrors")] int AbilityReadErrors,
        [property: JsonPropertyName("displayUnitCount")] int DisplayUnitCount,
        [property: JsonPropertyName("kingMarkerCount")] int KingMarkerCount,
        [property: JsonPropertyName("topMarkerCount")] int TopMarkerCount,
        [property: JsonPropertyName("scriptLines")] int[] ScriptLines,
        [property: JsonPropertyName("q7DirectlyRead")] bool Q7DirectlyRead);
    internal readonly record struct Identity(uint Address, uint Vtable, uint Index, uint Serial, string Rawcode, uint Owner);
    internal sealed record Candidate(Identity Identity, int Level, bool Valid);
    internal sealed record Scan(Candidate[] Displays, Candidate[] Navigation, Identity[] TopUnits,
        int WorldErrors, int AbilityErrors, uint Handles);

    public static State Unknown(string reason) => new("unknown", null, new(StringComparer.Ordinal), new("unknown", reason),
        EvidenceFor(reason, 0, 0, 0, 0, 0, 0));

    public static State Capture(MemoryReader memory, Roots roots, uint moduleBase, string? gameVersion, string? loadedMapHash) =>
        CaptureForTests(memory.Read, () => memory.ReadRoots(checked(moduleBase + 0xD326F0), checked(moduleBase + 0xD305E0)),
            roots, moduleBase, gameVersion, loadedMapHash) with
        { Special = GameSpecialRecipeConditions.Capture(memory, roots, moduleBase, gameVersion, loadedMapHash) };

    internal static State CaptureForTests(Func<uint, int, byte[]> read, Func<Roots?> readRoots, Roots roots,
        uint moduleBase, string? gameVersion, string? loadedMapHash)
    {
        if (gameVersion != Version) return Unknown("unsupported_version");
        if (!string.Equals(loadedMapHash, MapHash, StringComparison.OrdinalIgnoreCase)) return Unknown("unsupported_map");
        if (roots.LocalPlayer is < 0 or > 11 || !Pointer(moduleBase)) return Unknown("invalid_local_player_or_module");
        try
        {
            if (!SnapshotStability.SameSession(readRoots(), roots)) return Unknown("session_changed");
            var first = ReadScan(roots);
            var middle = readRoots();
            if (!SnapshotStability.SameSession(middle, roots)) return Unknown("session_changed");
            var second = ReadScan(middle!);
            if (!SnapshotStability.SameSession(readRoots(), roots)) return Unknown("session_changed");
            return Resolve(first, second, roots.LocalPlayer);

            Scan ReadScan(Roots current)
            {
                if (current.Count is < 0 or > 100000 || current.Count == 0) throw new IOException("Uninitialized world list.");
                var pointers = Read(current.Array, checked(current.Count * 4));
                var seen = new HashSet<uint>();var displays = new List<Candidate>();var nav = new List<Candidate>();
                var owned = new List<(uint Address, string Rawcode)>();var top = new List<Identity>();
                var worldErrors = 0;var abilityErrors = 0;
                var handles = U32(checked(moduleBase + 0xD30448));
                CheckedPointer(handles);
                for (var index = 0; index < current.Count; index++)
                {
                    var address = BitConverter.ToUInt32(pointers, index * 4);
                    if (address == 0 || !seen.Add(address)) continue;
                    try
                    {
                        CheckedPointer(address);
                        var owner = U32(checked(address + 0x58));
                        if (owner != current.LocalPlayer && owner != 7) continue;
                        var code = MemoryReader.DecodeRawcode(Read(checked(address + 0x30), 4));
                        if (code is null) throw new IOException("Invalid relevant unit rawcode.");
                        if (owner == 7 && !BanTargets.Contains(code)) continue;
                        if (owner == current.LocalPlayer && code != "h0C8")
                        { owned.Add((address, code));continue; }
                        var data = Read(address, 0x60);var identity = UnitIdentity(address, data);
                        if (identity.Owner != owner || identity.Rawcode != code) throw new IOException("Unit identity changed.");
                        if (owner == 7)
                            displays.Add(ReadMarker(identity, "A908")); // 숨겨진 페이지의 전시 유닛도 포함한다.
                        else
                        {
                            owned.Add((address, code));
                            if (identity.Rawcode == "h0C8") nav.Add(ReadMarker(identity, "A0II"));
                        }
                    }
                    catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { worldErrors++; }
                }
                if (nav.Count == 1 && nav[0].Valid && nav[0].Level == 1)
                {
                    foreach (var unit in owned.Where(unit => unit.Rawcode is not ("H0BK" or "h057")))
                    {
                        try
                        {
                            var data = Read(unit.Address, 0x60);
                            if ((BitConverter.ToUInt32(data, 0x20) & 1) != 0 || (BitConverter.ToUInt32(data, 0x5C) & 0x100) != 0) continue;
                            var identity = UnitIdentity(unit.Address, data);
                            if (identity.Owner != current.LocalPlayer || identity.Rawcode != unit.Rawcode) throw new IOException("Owned top marker changed.");
                            var abilities = ReadAbilities(identity);
                            // 이 네 표식은 Bhl의 OR 절과 직접 일치한다. A07O+Hero 경로는 판별 근거가 없어 추정하지 않는다.
                            if (new[] { "A905", "A906", "A907", "A909" }.Any(code => abilities.GetValueOrDefault(code) > 0)) top.Add(identity);
                        }
                        catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { abilityErrors++; }
                    }
                }
                // 적 생성·제거로 전역 Array/Count/내용은 바뀔 수 있다. 관련 표식은 두 scan에서 따로 대조한다.
                if (U32(checked(current.Ui + 0x3BC)) != current.Table) worldErrors++;
                var header = Read(checked(current.Table + 0x604), 8);
                if (BitConverter.ToInt32(header) is < 1 or > 100000 || !Pointer(BitConverter.ToUInt32(header, 4))) worldErrors++;
                if (U32(checked(moduleBase + 0xD30448)) != handles) worldErrors++;
                return new(displays.OrderBy(unit => unit.Identity.Address).ToArray(), nav.OrderBy(unit => unit.Identity.Address).ToArray(),
                    top.OrderBy(unit => unit.Address).ToArray(), worldErrors, abilityErrors, handles);

                Candidate ReadMarker(Identity unit, string marker)
                {
                    try { return new(unit, ReadAbilities(unit).GetValueOrDefault(marker), true); }
                    catch (Exception error) when (error is IOException or Win32Exception or OverflowException)
                    { abilityErrors++;return new(unit, 0, false); }
                }
                Dictionary<string, int> ReadAbilities(Identity unit)
                {
                    var head = Read(checked(unit.Address + 0x1DC), 8);
                    var address = ResolveAbility(head);var chain = new HashSet<uint>();
                    var abilities = new Dictionary<string, int>(StringComparer.Ordinal);
                    while (address != 0)
                    {
                        if (!chain.Add(address) || chain.Count > 128) throw new IOException("Invalid ability chain.");
                        var data = Read(address, 0x54);var vtable = BitConverter.ToUInt32(data, 0);
                        if (vtable < moduleBase || (ulong)vtable >= (ulong)moduleBase + 0xE00000 || (vtable & 3) != 0)
                            throw new IOException("Unknown ability class.");
                        var code = MemoryReader.DecodeRawcode(data.AsSpan(0x34, 4));var level = BitConverter.ToUInt32(data, 0x50);
                        if (code is null || level > 255) throw new IOException("Invalid ability data.");
                        if (Wanted.Contains(code) && !abilities.TryAdd(code, checked((int)level + 1))) throw new IOException("Duplicate marker.");
                        address = ResolveAbility(data.AsSpan(0x24, 8));
                    }
                    if (!head.AsSpan().SequenceEqual(Read(checked(unit.Address + 0x1DC), 8)) ||
                        UnitIdentity(unit.Address, Read(unit.Address, 0x60)) != unit || U32(checked(moduleBase + 0xD30448)) != handles)
                        throw new IOException("Unit marker changed.");
                    return abilities;
                }
                uint ResolveAbility(ReadOnlySpan<byte> reference)
                {
                    var index = BitConverter.ToUInt32(reference[..4]);var serial = BitConverter.ToUInt32(reference[4..]);
                    if ((index & serial) == uint.MaxValue) return 0;
                    var offset = (index & 0x80000000) != 0 ? 0x20u : 0u;index &= 0x7FFFFFFF;
                    var count = U32(checked(handles + 0x1C + offset));
                    if (count > 2_000_000 || index >= count) throw new IOException("Invalid ability handle index.");
                    var array = CheckedPointer(U32(checked(handles + 0x0C + offset)));
                    var slotAddress = checked(array + index * 8);var slot = Read(slotAddress, 8);
                    var record = BitConverter.ToUInt32(slot, 4);
                    if (BitConverter.ToUInt32(slot, 0) != 0xFFFFFFFE || !Pointer(record) || U32(checked(record + 0x18)) != serial || U32(checked(record + 0x20)) != 0)
                        throw new IOException("Stale ability handle.");
                    var ability = CheckedPointer(U32(checked(record + 0x54)));
                    if (!slot.AsSpan().SequenceEqual(Read(slotAddress, 8)) || U32(checked(record + 0x18)) != serial || U32(checked(record + 0x20)) != 0)
                        throw new IOException("Ability handle changed.");
                    return ability;
                }
            }
            Identity UnitIdentity(uint address, byte[] bytes)
            {
                var code = MemoryReader.DecodeRawcode(bytes.AsSpan(0x30, 4));var vtable = BitConverter.ToUInt32(bytes);
                if (code is null || vtable < moduleBase || (ulong)vtable >= (ulong)moduleBase + 0xE00000 || (vtable & 3) != 0)
                    throw new IOException("Invalid unit identity.");
                return new(address, vtable, BitConverter.ToUInt32(bytes, 0x0C), BitConverter.ToUInt32(bytes, 0x10), code, BitConverter.ToUInt32(bytes, 0x58));
            }
            uint U32(uint address) => BitConverter.ToUInt32(Read(address, 4));
            byte[] Read(uint address, int size)
            {
                CheckedPointer(address);if (size <= 0 || size > 400000) throw new IOException("Invalid condition read range.");
                var bytes = read(address, size);if (bytes.Length != size) throw new IOException("Incomplete condition read.");return bytes;
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { return Unknown("condition_read_failed"); }
    }

    internal static State Resolve(Scan first, Scan second, int localPlayer)
    {
        var worldErrors = first.WorldErrors + second.WorldErrors;var abilityErrors = first.AbilityErrors + second.AbilityErrors;
        if (worldErrors != 0 || first.Handles != second.Handles)
            return new("unknown", null, new(StringComparer.Ordinal), new("unknown", "incomplete_world_or_changed_handles"),
                EvidenceFor("incomplete_world_or_changed_handles", 2, worldErrors, abilityErrors, second.Displays.Length, second.Navigation.Length, second.TopUnits.Length));
        var bans = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var group in second.Displays.GroupBy(unit => unit.Identity.Rawcode, StringComparer.Ordinal))
        {
            var previous = first.Displays.Where(unit => unit.Identity.Rawcode == group.Key).ToArray();var current = group.ToArray();
            if (current.Any(unit => !unit.Valid) || !previous.SequenceEqual(current) || current.Select(unit => unit.Level > 0).Distinct().Count() != 1) continue;
            bans.Add(group.Key, current[0].Level > 0);
        }
        Gate king;
        if (!first.Navigation.SequenceEqual(second.Navigation)) king = new("unknown", "navigation_marker_changing");
        else if (second.Navigation.Length == 0) king = new("allowed", "complete_world_has_no_permanent_king_navigation_marker");
        else if (second.Navigation.Length != 1 || !second.Navigation[0].Valid) king = new("unknown", "ambiguous_or_unreadable_navigation_marker");
        else if (second.Navigation[0].Level >= 2) king = new("blocked", "martial_law_forbids_top_crafts");
        else if (second.Navigation[0].Level != 1) king = new("unknown", "navigation_ability_level_unverified");
        else if (first.TopUnits.Intersect(second.TopUnits).Any()) king = new("blocked", "king_navigation_and_owned_top_marker");
        else king = new("unknown", "historical_top_counter_not_directly_read");
        var partial = abilityErrors != 0 || king.Status == "unknown" || bans.Count != second.Displays.Select(unit => unit.Identity.Rawcode).Distinct().Count();
        return new(partial ? "partial" : "confirmed", localPlayer, bans, king,
            EvidenceFor(partial ? "some_gates_unverified" : "two_matching_persistent_native_markers", 2, 0, abilityErrors,
                second.Displays.Length, second.Navigation.Length, second.TopUnits.Length));
    }
    private static bool Pointer(uint value) => value >= 0x10000 && value <= 0x7FFF0000;
    private static uint CheckedPointer(uint value) => Pointer(value) ? value : throw new IOException("Invalid condition pointer.");
    private static Evidence EvidenceFor(string reason, int samples, int worldErrors, int abilityErrors, int displays, int navigation, int top) =>
        new(true, "read_only_1285_persistent_unit_markers", reason, MapHash, samples, worldErrors + abilityErrors, worldErrors, abilityErrors,
            displays, navigation, top, [3140, 3148, 48100, 48125, 50259, 50320, 52528, 52571, 58063, 58066, 11009, 11015], false);
}
