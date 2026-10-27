using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;

// kL/kM은 현재 보유량이 아니라 e0IX의 누적 흔함 획득 이력이다.
// LoadInteger의 native 자료구조를 읽으며 게임 함수를 호출하거나 쓰지 않는다.
static class GameRandomState
{
    internal static readonly string[] CommonRawcodes = ["h003", "h002", "h001", "h007", "h004", "h008", "h005", "h009", "h006"];
    internal sealed record State(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("localPlayer")] int? LocalPlayer,
        [property: JsonPropertyName("drawCount")] int? DrawCount,
        [property: JsonPropertyName("correctionInterval")] int? CorrectionInterval,
        [property: JsonPropertyName("commonAcquiredCounts")] IReadOnlyDictionary<string, int> CommonAcquiredCounts,
        [property: JsonPropertyName("source")] Evidence Source);
    internal sealed record Evidence(
        [property: JsonPropertyName("readOnly")] bool ReadOnly,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("mapSha256")] string MapSha256,
        [property: JsonPropertyName("sampleCount")] int SampleCount,
        [property: JsonPropertyName("correctionIntervalSource")] string CorrectionIntervalSource,
        [property: JsonPropertyName("scriptLines")] int[] ScriptLines)
    {
        [JsonPropertyName("globalTypeMismatch")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GameProgression.GlobalTypeMismatch? TypeMismatch { get; init; }
    }
    private sealed record Global(string Name, uint Node, uint Key, uint Type, uint Value, uint Header, uint Size, uint Data);
    private sealed record Sample(int DrawCount, int Interval, string IntervalSource, Dictionary<string, int> Counts, string Proof);
    private sealed class ReadFailure(string reason) : IOException(reason)
    {
        internal string Reason { get; } = reason;
        internal GameProgression.GlobalTypeMismatch? TypeMismatch { get; init; }
    }

    internal static State Unknown(string reason) => Result("unknown", null, null, null, new(StringComparer.Ordinal), reason, 0, "unknown");
    internal static State Capture(MemoryReader memory, Roots roots, uint moduleBase, string? version, string? mapHash) =>
        CaptureForTests(memory.Read, () => memory.ReadRoots(checked(moduleBase + 0xD326F0), checked(moduleBase + 0xD305E0)), roots, moduleBase, version, mapHash);

    internal static State CaptureForTests(Func<uint, int, byte[]> reader, Func<Roots?> readRoots, Roots roots,
        uint moduleBase, string? version, string? mapHash)
    {
        if (version != GameProgression.SupportedVersion) return Unknown("unsupported_version");
        if (!string.Equals(mapHash, GameProgression.SupportedMapHash, StringComparison.OrdinalIgnoreCase)) return Unknown("unsupported_map");
        if (roots.LocalPlayer is < 0 or > 3) return Unknown("unsupported_local_player");
        if (!Pointer(moduleBase) || !Pointer(roots.Ui) || !Pointer(roots.Players) || !Pointer(roots.Table)) return Unknown("invalid_session_pointer");
        try
        {
            Session();
            foreach (var (rva, bytes) in GameProgression.NativeSignatures(moduleBase).Concat(NativeSignatures(moduleBase)))
                if (!Read(checked(moduleBase + rva), bytes.Length).AsSpan().SequenceEqual(bytes)) return Unknown("unsupported_native_layout");
            try
            {
                var first = ReadSample(true); Session(); var second = ReadSample(true); Session();
                if (!Equal(first, second)) return Unknown("random_history_changing");
                if (second.Counts.Values.Sum(value => (long)value) != second.DrawCount) return Unknown("random_history_total_mismatch");
                return Result("confirmed", roots.LocalPlayer, second.DrawCount, second.Interval, second.Counts,
                    "two_matching_named_globals_and_integer_hashtable_reads", 2, second.IntervalSource);
            }
            catch (ReadFailure error) when (error.Reason != "session_changed")
            {
                // 해시테이블 이력 실패가 라운드나 별도로 두 번 확인한 kL까지 확정값으로 위조하지 않게 분리한다.
                var first = ReadSample(false); Session(); var second = ReadSample(false); Session();
                if (!Equal(first, second)) return Unknown("random_history_scalars_changing");
                return WithMismatch(Result("partial", roots.LocalPlayer, second.DrawCount, second.Interval, new(StringComparer.Ordinal),
                    error.Reason, 2, second.IntervalSource), error.TypeMismatch);
            }

            void Session()
            {
                if (!SnapshotStability.SameSession(readRoots(), roots)) throw new ReadFailure("session_changed");
            }
            Sample ReadSample(bool withCounts)
            {
                var proof = new StringBuilder();
                uint Remember(uint address)
                {
                    var value = U32(address); proof.Append(address).Append(':').Append(value).Append(';'); return value;
                }
                var environment = Ptr(Remember(checked(moduleBase + 0xD0DEF8)));
                var instance = Ptr(Remember(checked(environment + 0x14)));
                var vms = Ptr(Remember(checked(instance + 0x90))); var vm = Ptr(Remember(checked(vms + 4)));
                var globals = Ptr(Remember(checked(vm + 0x285C)));
                var buckets = Ptr(Remember(checked(globals + 0x1C))); var mask = Remember(checked(globals + 0x24));
                Mask(mask, false);
                var player = Ptr(Remember(checked(roots.Players + 0x58 + (uint)roots.LocalPlayer * 4)));
                if (Read(checked(player + 0x30), 1)[0] != roots.LocalPlayer) throw new ReadFailure("local_player_identity_mismatch");
                Global? Named(string name, uint type, bool optional = false)
                {
                    var hash = GameProgression.NameHash(name); var bucket = checked(buckets + (hash & mask) * 12);
                    var step = unchecked((int)Remember(bucket)); ChainStep(step);
                    var node = Remember(checked(bucket + 8)); var seen = new HashSet<uint>(); Global? found = null;
                    while (unchecked((int)node) > 0)
                    {
                        Ptr(node); if (seen.Count >= 128 || !seen.Add(node)) throw new ReadFailure("invalid_global_collision_chain");
                        var header = Read(node, 0x24);
                        if (BitConverter.ToUInt32(header, 0) == hash)
                        {
                            var key = Ptr(BitConverter.ToUInt32(header, 0x14));
                            if (Read(key, 3).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(name + "\0")))
                            {
                                if (found is not null) throw new ReadFailure("duplicate_global_name");
                                var actualType = BitConverter.ToUInt32(header, 0x1C); var array = BitConverter.ToUInt32(header, 0x18);
                                var value = BitConverter.ToUInt32(header, 0x20);
                                if (actualType != type || !GameProgression.GlobalValueTagMatches(type, array, value))
                                    throw new ReadFailure("unexpected_random_global_type")
                                    {
                                        TypeMismatch = new(name, type, actualType, GameProgression.AllowedGlobalValueTags(type), array)
                                    };
                                uint arrayHeader = 0, size = 0, data = 0;
                                if (type == 9)
                                {
                                    arrayHeader = Ptr(value); size = Remember(checked(arrayHeader + 8)); data = Ptr(Remember(checked(arrayHeader + 12)));
                                    if (size > 8192 || size <= roots.LocalPlayer) throw new ReadFailure("invalid_random_global_array");
                                    value = Remember(checked(data + (uint)roots.LocalPlayer * 4));
                                }
                                found = new(name, node, key, actualType, value, arrayHeader, size, data);
                                proof.Append(found).Append(';');
                            }
                        }
                        node = Remember(Next(node, step));
                    }
                    if (found is null && !optional) throw new ReadFailure("random_global_missing");
                    if (found is null) proof.Append("missing:").Append(name).Append(';');
                    return found;
                }
                var draws = Named("kL", 9)!; var interval = Named("sZ", 4, true);
                // sZ는 지원 맵에서 재할당이 없는 constant integer 2다. compiler의 상수 인라인 생략을 허용한다.
                var every = interval is null ? 2 : checked((int)interval.Value);
                if (every != 2) throw new ReadFailure("correction_interval_disagrees_with_supported_map");
                if (draws.Value > 1_000_000) throw new ReadFailure("random_history_out_of_range");
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                if (!withCounts) return new((int)draws.Value, every, interval is null ? "verified_map_constant_sZ" : "named_global_sZ", counts, proof.ToString());
                var tableGlobal = Named("kM", 7)!;
                // D305E0는 GameState/플레이어 관리자다. roots.Table은 UI+3BC의 유닛 목록이므로 서로 다르다.
                if (Remember(checked(moduleBase + 0xD305E0)) != roots.Players) throw new ReadFailure("game_state_identity_mismatch");
                var handle = tableGlobal.Value;
                if (handle < 0x100000 || handle > 0x1FFFFF) throw new ReadFailure("invalid_hashtable_handle");
                var handles = Ptr(Remember(checked(roots.Players + 0x1C)));
                var handleArray = Ptr(Remember(checked(handles + 0x19C)));
                var reference = Ptr(Remember(checked(handleArray + (handle - 0x100000) * 12 + 4)));
                var index = Remember(checked(reference + 0x0C)); var generation = Remember(checked(reference + 0x10));
                var nativeHandles = Ptr(Remember(checked(moduleBase + 0xD30448)));
                var high = (index & 0x80000000) != 0; var offset = high ? 0x20u : 0u; index &= 0x7FFFFFFF;
                var length = Remember(checked(nativeHandles + 0x1C + offset));
                if (length > 2_000_000 || index >= length) throw new ReadFailure("invalid_hashtable_agent_index");
                var nativeArray = Ptr(Remember(checked(nativeHandles + 0x0C + offset))); var slot = checked(nativeArray + index * 8);
                if (Remember(slot) != 0xFFFFFFFE) throw new ReadFailure("stale_hashtable_agent_slot");
                var agent = Ptr(Remember(checked(slot + 4)));
                if (Remember(checked(agent + 0x18)) != generation || Remember(checked(agent + 0x20)) != 0 || Remember(checked(agent + 0x0C)) != 0x2B61676C)
                    throw new ReadFailure("stale_or_wrong_hashtable_agent");
                // LoadInteger가 받는 객체는 검증된 JASS reference다. native agent는 생존/세대 검증에만 쓴다.
                // native는 table-id 0도 그대로 조회하므로 숫자0을 잘못된 포인터로 취급하지 않는다.
                var tableId = Remember(checked(reference + 0x24));
                var manager = Ptr(Remember(checked(roots.Players + 0x404)));
                var table = Lookup(checked(manager + 4), tableId, 0x24, 0x1C, 4, 0x18, true);
                if (table == 0) throw new ReadFailure("hashtable_record_missing");
                var parent = Lookup(table, (uint)roots.LocalPlayer, 0x40, 0x38, 4, 0x18, true);
                foreach (var rawcode in CommonRawcodes)
                {
                    var key = ((uint)rawcode[0] << 24) | ((uint)rawcode[1] << 16) | ((uint)rawcode[2] << 8) | rawcode[3];
                    var child = parent == 0 ? 0 : Lookup(parent, key, 0x40, 0x38, 0, 0x14, false);
                    var value = child == 0 ? 0 : Remember(checked(child + 0x18));
                    if (value > 1_000_000) throw new ReadFailure("random_history_out_of_range");
                    counts.Add(rawcode, (int)value);
                }
                return new((int)draws.Value, every, interval is null ? "verified_map_constant_sZ" : "named_global_sZ", counts, proof.ToString());

                uint Lookup(uint table, uint key, uint maskOffset, uint bucketOffset, uint firstKeyOffset, uint secondKeyOffset, bool emptyAllowed)
                {
                    var hashMask = Remember(checked(table + maskOffset));
                    if (hashMask == uint.MaxValue) return 0;
                    Mask(hashMask, emptyAllowed);
                    var hashBuckets = Ptr(Remember(checked(table + bucketOffset)));
                    var bucket = checked(hashBuckets + (key & hashMask) * 12); var step = unchecked((int)Remember(bucket)); ChainStep(step);
                    var node = Remember(checked(bucket + 8)); var seen = new HashSet<uint>(); uint found = 0;
                    while (unchecked((int)node) > 0)
                    {
                        Ptr(node); if (seen.Count >= 128 || !seen.Add(node)) throw new ReadFailure("invalid_integer_hashtable_chain");
                        if (Remember(checked(node + firstKeyOffset)) == key && Remember(checked(node + secondKeyOffset)) == key)
                        {
                            if (found != 0) throw new ReadFailure("duplicate_integer_hashtable_key");
                            found = node;
                        }
                        node = Remember(Next(node, step));
                    }
                    return found;
                }
            }
            byte[] Read(uint address, int size)
            {
                var bytes = reader(address, size); if (bytes.Length != size) throw new ReadFailure("short_random_history_read"); return bytes;
            }
            uint U32(uint address) => BitConverter.ToUInt32(Read(address, 4));
        }
        catch (ReadFailure error) { return WithMismatch(Unknown(error.Reason), error.TypeMismatch); }
        catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { return Unknown("random_history_read_failed"); }
    }
    private static State WithMismatch(State state, GameProgression.GlobalTypeMismatch? mismatch) =>
        state with { Source = state.Source with { TypeMismatch = mismatch } };
    private static bool Equal(Sample first, Sample second) => first.DrawCount == second.DrawCount && first.Interval == second.Interval && first.IntervalSource == second.IntervalSource &&
        first.Proof == second.Proof && first.Counts.Count == second.Counts.Count && first.Counts.All(pair => second.Counts.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static bool Pointer(uint value) => value is >= 0x10000 and <= 0x7FFF0000;
    private static uint Ptr(uint value) => Pointer(value) ? value : throw new ReadFailure("invalid_random_history_pointer");
    private static void Mask(uint value, bool _) { if (value > 0xFFFF || (value & (value + 1)) != 0) throw new ReadFailure("invalid_random_history_mask"); }
    private static void ChainStep(int step) { if ((step & 3) != 0 || step is < -0x1000 or > 0x1000) throw new ReadFailure("invalid_random_history_chain_step"); }
    private static uint Next(uint node, int step) { var address = (long)node + step + 4; if (address < 0x10000 || address > 0x7FFF0000) throw new ReadFailure("invalid_random_history_next"); return (uint)address; }
    private static State Result(string status, int? player, int? draws, int? interval, Dictionary<string, int> counts, string reason, int samples, string intervalSource) =>
        new(status, player, draws, interval, counts, new(true, "read_only_named_globals_and_native_LoadInteger_layout", reason,
            GameProgression.SupportedMapHash, samples, intervalSource, [14, 209, 3181, 3182, 3183, 4497, 4502, 4521, 4525, 49078]));
    internal static (uint Rva, byte[] Bytes)[] NativeSignatures(uint moduleBase) => [
        (0x240955, [0x8B, 0x0D, .. BitConverter.GetBytes(checked(moduleBase + 0xD305E0))]),
        (0x240968, [0xFF, 0x76, 0x24, 0xE8, 0x90, 0x51, 0x02, 0]),
        (0x2B8490, [0x55, 0x8B, 0xEC, 0x8B, 0x45, 8, 0x8B, 0xD1, 0x3D, 0, 0, 0x10, 0]),
        (0x2B84A5, [0x8D, 0x0C, 0x40, 0x8B, 0x82, 0x9C, 1, 0, 0, 0x8B, 0x84, 0x88, 4, 0, 0x40, 0xFF]),
        (0x21FC93, [0x8B, 0x57, 0x10, 0x8B, 0x4F, 0x0C, 0xE8, 0xF2, 0xE3, 0xE5, 0xFF]),
        (0x21FCA4, [0x81, 0x78, 0x0C, 0x6C, 0x67, 0x61, 0x2B]),
        (0x2135E4, [0x8B, 0x87, 4, 4, 0, 0]),
        (0x265B66, [0x8B, 0x46, 0x40, 0x83, 0xF8, 0xFF]),
        (0x2648AB, [0x8B, 0x47, 0x40, 0x83, 0xF8, 0xFF]),
        (0x265B87, [0x39, 0x50, 0x14]),
        (0x265B9D, [0x8B, 0x40, 0x18])
    ];
}
