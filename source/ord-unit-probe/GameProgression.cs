using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;

// 메인 스레드 instance 배열의 native mirror와 JASS global table을 읽기만 한다.
// 실행 중인 함수 호출·주입·메모리 쓰기와 화면 문자열 추정은 사용하지 않는다.
static class GameProgression
{
    internal const string SupportedVersion = "1.28.5.7680";
    internal const string SupportedMapHash = "0794996c9bc73eaa3020f20ebc45fb37cfa64a6e98ffe8aa0dfa3b356e77bc5c";
    internal const string ScriptHash = "96201e80edec1b2764f85818760306c6b2f0becd390ba4d63f41c86f3dfaff6d";
    private static readonly string[] Names = ["X3", "on", "NE", "sJ", "Ji", "YU"];

    internal sealed record State(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("localPlayer")] int? LocalPlayer,
        [property: JsonPropertyName("round")] int? Round,
        [property: JsonPropertyName("storyIndex")] int? StoryIndex,
        [property: JsonPropertyName("activePlayer")] bool? ActivePlayer,
        [property: JsonPropertyName("difficulty")] string Difficulty,
        [property: JsonPropertyName("difficultyValue")] int? DifficultyValue,
        [property: JsonPropertyName("gameEnded")] bool? GameEnded,
        [property: JsonPropertyName("roundTransitionDepth")] int? RoundTransitionDepth,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("rewardBoundary")] string RewardBoundary,
        [property: JsonPropertyName("claimedTechs")] Dictionary<string, bool> ClaimedTechs,
        [property: JsonPropertyName("source")] Evidence Source)
    {
        [JsonPropertyName("randomWisp")]
        public GameRandomState.State RandomWisp { get; init; } = GameRandomState.Unknown("not_captured");

        // YU는 맵 전체 승리 표식이다. 내 플레이어의 승패/퇴장 판정은 별도로 대조해야 한다.
        [JsonPropertyName("victory")]
        public bool? Victory { get; init; }

        [JsonPropertyName("playerSlotState")]
        public int? PlayerSlotState { get; init; }
    }

    internal sealed record Evidence(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("readOnly")] bool ReadOnly,
        [property: JsonPropertyName("sampleCount")] int SampleCount,
        [property: JsonPropertyName("mapSha256")] string MapSha256,
        [property: JsonPropertyName("scriptSha256")] string ScriptSha256,
        [property: JsonPropertyName("scriptLines")] int[] ScriptLines,
        [property: JsonPropertyName("nativeRvas")] string[] NativeRvas,
        [property: JsonPropertyName("globalNames")] string[] GlobalNames,
        [property: JsonPropertyName("claimFlagsRead")] bool ClaimFlagsRead,
        [property: JsonPropertyName("phaseRead")] bool PhaseRead)
    {
        [JsonPropertyName("globalTypeMismatch")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GlobalTypeMismatch? TypeMismatch { get; init; }

        [JsonPropertyName("playerSlotState")]
        public PlayerSlotEvidence? SlotState { get; init; }
    }

    internal sealed record PlayerSlotEvidence(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("readOnly")] bool ReadOnly,
        [property: JsonPropertyName("sampleCount")] int SampleCount,
        [property: JsonPropertyName("nativeRvas")] string[] NativeRvas,
        [property: JsonPropertyName("fieldOffset")] string FieldOffset);

    internal sealed record GlobalTypeMismatch(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("expectedType")] uint ExpectedType,
        [property: JsonPropertyName("actualType")] uint ActualType,
        [property: JsonPropertyName("allowedValueTypeTags")] uint[] AllowedValueTypeTags,
        [property: JsonPropertyName("actualValueTypeTag")] uint ActualValueTypeTag);

    private sealed record Entry(string Name, uint Node, uint Hash, uint Key, uint Type, uint Value,
        uint Bucket, int Step, uint ArrayHeader, uint ArraySize, uint ArrayData);
    private sealed record Sample(uint Environment, uint Instance, uint Vms, uint Vm, uint Globals,
        uint Buckets, uint Mask, uint Player, Entry[] Entries, int? PlayerSlotState, string SlotStateReason);
    private sealed class ProgressionReadException(string reason) : IOException(reason)
    {
        internal string Reason { get; } = reason;
        internal GlobalTypeMismatch? TypeMismatch { get; init; }
    }

    public static State Unknown(string reason) => new("unknown", null, null, null, null, "unknown", null,
        null, null, "unknown", "exclude_current_round", new(StringComparer.Ordinal), Source(reason, 0));

    public static State Capture(MemoryReader memory, Roots roots, uint moduleBase, string? version, string? mapHash) =>
        CaptureForTests(memory.Read, () => memory.ReadRoots(checked(moduleBase + 0xD326F0), checked(moduleBase + 0xD305E0)),
            roots, moduleBase, version, mapHash) with { RandomWisp = GameRandomState.Capture(memory, roots, moduleBase, version, mapHash) };

    internal static State CaptureForTests(Func<uint, int, byte[]> read, Func<Roots?> readRoots,
        Roots roots, uint moduleBase, string? version, string? mapHash)
    {
        if (version != SupportedVersion) return Unknown("unsupported_version");
        if (!string.Equals(mapHash, SupportedMapHash, StringComparison.OrdinalIgnoreCase)) return Unknown("unsupported_map");
        if (roots.LocalPlayer is < 0 or > 3) return Unknown("unsupported_local_player");
        if (!Pointer(moduleBase) || !Pointer(roots.Ui) || !Pointer(roots.Players) || !Pointer(roots.Table))
            return Unknown("invalid_session_pointer");
        try
        {
            if (!SnapshotStability.SameSession(readRoots(), roots)) return Unknown("session_changed");
            foreach (var (rva, bytes) in NativeSignatures(moduleBase))
                if (!Read(checked(moduleBase + rva), bytes.Length).AsSpan().SequenceEqual(bytes))
                    return Unknown("unsupported_native_layout");
            var first = ReadSample();
            if (!SnapshotStability.SameSession(readRoots(), roots)) return Unknown("session_changed");
            var second = ReadSample();
            if (!SnapshotStability.SameSession(readRoots(), roots)) return Unknown("session_changed");
            if (first.Environment != second.Environment || first.Instance != second.Instance || first.Vms != second.Vms ||
                first.Vm != second.Vm || first.Globals != second.Globals || first.Buckets != second.Buckets ||
                first.Mask != second.Mask || first.Player != second.Player || !first.Entries.SequenceEqual(second.Entries))
                return Unknown("progression_changing");
            var values = second.Entries.ToDictionary(entry => entry.Name, entry => unchecked((int)entry.Value), StringComparer.Ordinal);
            if (values["X3"] is < 0 or > 65 || values["on"] is < 0 or > 14 || values["NE"] is < 0 or > 6 ||
                values["sJ"] is < 0 or > 10000 || values["Ji"] is < 0 or > 1 || values["YU"] is < 0 or > 1)
                return Unknown("progression_out_of_supported_range");
            var slotKnown = first.PlayerSlotState is not null && first.PlayerSlotState == second.PlayerSlotState;
            var slotReason = slotKnown ? "two_matching_native_player_slot_reads" : first.PlayerSlotState != second.PlayerSlotState ? "player_slot_state_changing" : second.SlotStateReason;
            return new("confirmed", roots.LocalPlayer, values["X3"], values["on"], values["Ji"] == 1,
                values["NE"] == 6 ? "nightmare" : values["NE"] == 0 ? "unknown" : "other", values["NE"],
                values["YU"] == 1 || values["Ji"] == 0, values["sJ"], "unknown", "exclude_current_round", new(StringComparer.Ordinal),
                Source("two_matching_named_jass_global_reads", 2) with
                {
                    SlotState = new(slotKnown ? "confirmed" : "unknown", slotReason, true, slotKnown ? 2 : 0, ["234210", "233F50"], "0x270")
                }) { Victory = values["YU"] == 1, PlayerSlotState = slotKnown ? second.PlayerSlotState : null };

            Sample ReadSample()
            {
                var environment = Ptr(U32(checked(moduleBase + 0xD0DEF8)));
                var instance = Ptr(U32(checked(environment + 0x14)));
                var vms = Ptr(U32(checked(instance + 0x90)));
                var vm = Ptr(U32(checked(vms + 4)));
                var globals = Ptr(U32(checked(vm + 0x285C)));
                var buckets = Ptr(U32(checked(globals + 0x1C)));
                var mask = U32(checked(globals + 0x24));
                if (mask > 0xFFFF || (mask & (mask + 1)) != 0) throw new ProgressionReadException("invalid_global_table");
                var player = Ptr(U32(checked(roots.Players + 0x58 + (uint)roots.LocalPlayer * 4)));
                if (Read(checked(player + 0x30), 1)[0] != roots.LocalPlayer)
                    throw new ProgressionReadException("local_player_identity_mismatch");
                var entries = new Entry[Names.Length];
                for (var index = 0; index < Names.Length; index++)
                {
                    var name = Names[index]; var hash = NameHash(name);
                    var bucket = checked(buckets + (hash & mask) * 12);
                    var step = unchecked((int)U32(bucket));
                    if ((step & 3) != 0 || step is < -0x1000 or > 0x1000)
                        throw new ProgressionReadException("invalid_global_chain_layout");
                    var node = U32(checked(bucket + 8)); var seen = new HashSet<uint>(); Entry? found = null;
                    while (unchecked((int)node) > 0)
                    {
                        Ptr(node);
                        if (seen.Count >= 128 || !seen.Add(node)) throw new ProgressionReadException("invalid_global_collision_chain");
                        var data = Read(node, 0x24);
                        if (BitConverter.ToUInt32(data, 0) == hash)
                        {
                            var key = Ptr(BitConverter.ToUInt32(data, 0x14));
                            if (Read(key, 3).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(name + "\0")))
                            {
                                if (found is not null) throw new ProgressionReadException("duplicate_global_name");
                                var type = BitConverter.ToUInt32(data, 0x1C); var array = BitConverter.ToUInt32(data, 0x18);
                                var value = BitConverter.ToUInt32(data, 0x20); uint header = 0, size = 0, arrayData = 0;
                                var expectedType = name == "Ji" ? 13u : name == "YU" ? 8u : 4u;
                                if (type != expectedType || !GlobalValueTagMatches(expectedType, array, value))
                                    throw new ProgressionReadException("unexpected_global_type")
                                    {
                                        TypeMismatch = new(name, expectedType, type, AllowedGlobalValueTags(expectedType), array)
                                    };
                                if (name == "Ji")
                                {
                                    header = Ptr(value); size = U32(checked(header + 8)); arrayData = Ptr(U32(checked(header + 12)));
                                    if (size > 8192 || size <= roots.LocalPlayer) throw new ProgressionReadException("invalid_global_array");
                                    value = U32(checked(arrayData + (uint)roots.LocalPlayer * 4));
                                }
                                found = new(name, node, hash, key, type, value, bucket, step, header, size, arrayData);
                            }
                        }
                        var next = (long)node + step + 4;
                        if (next < 0x10000 || next > 0x7FFF0000) throw new ProgressionReadException("invalid_global_next_pointer");
                        node = U32((uint)next);
                    }
                    entries[index] = found ?? throw new ProgressionReadException("global_entry_missing");
                }
                var slot = ReadPlayerSlot(player);
                return new(environment, instance, vms, vm, globals, buckets, mask, player, entries, slot.Value, slot.Reason);
            }
            (int? Value, string Reason) ReadPlayerSlot(uint player)
            {
                try
                {
                    foreach (var (rva, bytes) in PlayerSlotSignatures())
                        if (!Read(checked(moduleBase + rva), bytes.Length).AsSpan().SequenceEqual(bytes)) return (null, "unsupported_native_slot_layout");
                    var value = U32(checked(player + 0x270));
                    return value <= 2 ? ((int)value, "native_slot_sample") : (null, "player_slot_state_out_of_range");
                }
                catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { return (null, "player_slot_state_read_failed"); }
            }
            byte[] Read(uint address, int size)
            {
                var bytes = read(address, size);
                if (bytes.Length != size) throw new ProgressionReadException("short_progression_read");
                return bytes;
            }
            uint U32(uint address) => BitConverter.ToUInt32(Read(address, 4));
        }
        catch (ProgressionReadException error) { return Unknown(error.Reason) with { Source = Source(error.Reason, 0) with { TypeMismatch = error.TypeMismatch } }; }
        catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { return Unknown("progression_read_failed"); }
    }

    internal static (uint Rva, byte[] Bytes)[] NativeSignatures(uint moduleBase) => [
        (0x0D6405, [0xA3, .. BitConverter.GetBytes(checked(moduleBase + 0xD0DEF8))]),
        (0x8BFA83, [0x8B, 0x89, 0x90, 0, 0, 0, 0x8B, 0x45, 8, 0x8B, 4, 0x81]),
        (0x8C1666, [0x8B, 0x89, 0x5C, 0x28, 0, 0]),
        (0x8C418A, [0x83, 0x7E, 0x24, 0xFF]),
        (0x8C41AE, [0x8B, 0x4E, 0x1C, 0x8B, 0x74, 0x91, 8]),
        (0x8C41EC, [0x8B, 0x74, 0x30, 4]),
        (0x8BE62D, [0x89, 0x58, 0x14, 0x89, 0x50, 0x1C, 0xC7, 0x40, 0x20, 0, 0, 0, 0]),
        (0x8CB01A, [0x8B, 0x46, 0x1C, 0x89, 0x46, 0x18])
    ];
    internal static (uint Rva, byte[] Bytes)[] PlayerSlotSignatures() => [
        (0x234210, [0x55, 0x8B, 0xEC, 0x8B, 0x4D, 8, 0xE8, 5, 0xC4, 0xFE, 0xFF, 0x85, 0xC0, 0x75, 2, 0x5D, 0xC3, 0x8B, 0x80, 0x70, 2, 0, 0, 0x5D, 0xC3]),
        (0x233F50, [0x55, 0x8B, 0xEC, 0x8B, 0x4D, 8, 0xE8, 0xC5, 0xC6, 0xFE, 0xFF, 0x85, 0xC0, 0x75, 2, 0x5D, 0xC3, 0x0F, 0xB6, 0x40, 0x30, 0x5D, 0xC3])
    ];

    // +1C는 선언형, +18은 현재 값의 형식이다. scalar 생성 직후는 0/값0이며,
    // native 할당기8CB01A는 선언형을 +18에 복사한다. 배열은 생성부터 배열형을 유지한다.
    internal static bool GlobalValueTagMatches(uint declaredType, uint valueTypeTag, uint value) =>
        valueTypeTag == declaredType || declaredType < 9 && valueTypeTag == 0 && value == 0;
    internal static uint[] AllowedGlobalValueTags(uint declaredType) => declaredType >= 9 ? [declaredType] : [0, declaredType];

    // 지원 맵에서 읽는 JASS 이름은 모두 두 ASCII 문자다. Storm ordinal590의 대문자 정규화·Jenkins mix와 같다.
    internal static uint NameHash(string name)
    {
        if (name.Length != 2 || name.Any(ch => ch is < (char)0x21 or > (char)0x7E)) throw new ArgumentException("Expected two ASCII global-name characters.");
        unchecked
        {
            var first = (uint)char.ToUpperInvariant(name[0]); var second = (uint)char.ToUpperInvariant(name[1]);
            uint a = 0x9E3779B9 + first + (second << 8), b = 0x9E3779B9, c = 2;
            a = (c >> 13) ^ (a - b - c); b = (a << 8) ^ (b - c - a); c = (b >> 13) ^ (c - a - b);
            a = (c >> 12) ^ (a - b - c); b = (a << 16) ^ (b - c - a); c = (b >> 5) ^ (c - a - b);
            a = (c >> 3) ^ (a - b - c); b = (a << 10) ^ (b - c - a); c = (b >> 15) ^ (c - a - b);
            return c;
        }
    }
    private static uint Ptr(uint value) => Pointer(value) ? value : throw new ProgressionReadException("invalid_progression_pointer");
    private static bool Pointer(uint value) => value is >= 0x10000 and <= 0x7FFF0000;
    private static Evidence Source(string reason, int samples) => new("read_only_1285_named_jass_globals", reason, true, samples,
        SupportedMapHash, ScriptHash, [137, 138, 232, 233, 243, 249, 253, 257, 315, 3861, 3862, 4739, 8514, 8520, 59116, 59290, 59306, 59323, 59332, 59334, 59472, 59474, 59757],
        ["0D63FA", "0D6405", "095DF0", "8BFA83", "8C1666", "8C4180", "8BE62D", "8CB01A", "Storm:ordinal590"], Names, false, false);
}
