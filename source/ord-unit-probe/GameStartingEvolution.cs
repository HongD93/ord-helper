using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

// 시작 진화체의 확정된 중립 유닛과152킬 진행을 읽는다. 보유 재고나 조합 가능 수량은 바꾸지 않는다.
static class GameStartingEvolution
{
    internal static readonly string[] SpecialPool = ["h01K", "h00Q", "h00B", "h00P", "h00S", "h00R", "h00W", "h00T", "h00U", "h017", "h016", "h015", "h014", "h013", "h01F", "h012", "h011", "h00Z", "h00Y", "h00X", "h018", "h019", "h01C", "h00V", "h01A", "h01B", "h01D", "h01E", "h01H", "h01G", "h01J", "h01I"];
    internal sealed record State(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("localPlayer")] int? LocalPlayer,
        [property: JsonPropertyName("resultRawcode")] string? ResultRawcode,
        [property: JsonPropertyName("killCount")] int? KillCount,
        [property: JsonPropertyName("targetKills")] int TargetKills,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("pendingCount")] int PendingCount,
        [property: JsonPropertyName("source")] Evidence Source);
    internal sealed record Evidence(
        [property: JsonPropertyName("readOnly")] bool ReadOnly,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("mapSha256")] string MapSha256,
        [property: JsonPropertyName("scriptSha256")] string ScriptSha256,
        [property: JsonPropertyName("sampleCount")] int SampleCount,
        [property: JsonPropertyName("globalNames")] string[] GlobalNames,
        [property: JsonPropertyName("scriptLines")] int[] ScriptLines,
        [property: JsonPropertyName("nativeRvas")] string[] NativeRvas,
        [property: JsonPropertyName("expectedNeutralOwner")] int ExpectedNeutralOwner,
        [property: JsonPropertyName("worldMembershipVerified")] bool WorldMembershipVerified)
    {
        [JsonPropertyName("readFailure")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PointerFailure? Failure { get; init; }
    }
    internal sealed record PointerFailure(
        [property: JsonPropertyName("stage")] string Stage,
        [property: JsonPropertyName("observedValue")] uint ObservedValue);
    private sealed record Sample(int Kills, bool Activating, uint Handle, string? Rawcode, string Proof);
    private sealed class ReadFailure(string reason) : IOException(reason)
    {
        internal string Reason { get; } = reason;
        internal PointerFailure? Failure { get; init; }
    }
    internal static State Unknown(string reason) => Result("unknown", null, null, null, "unknown", 0, reason, 0, false);
    internal static State Capture(MemoryReader memory, Roots roots, uint moduleBase, string? version, string? mapHash) =>
        CaptureForTests(memory.Read, () => memory.ReadRoots(checked(moduleBase + 0xD326F0), checked(moduleBase + 0xD305E0)), roots, moduleBase, version, mapHash);

    internal static State CaptureForTests(Func<uint, int, byte[]> reader, Func<Roots?> readRoots, Roots roots, uint moduleBase, string? version, string? mapHash)
    {
        if (version != GameProgression.SupportedVersion || mapHash != GameProgression.SupportedMapHash) return Unknown("unsupported_map_or_version");
        if (roots.LocalPlayer is < 0 or > 3 || !Pointer(moduleBase) || !Pointer(roots.Ui) || !Pointer(roots.Players) || !Pointer(roots.Table)) return Unknown("unsupported_player_or_roots");
        try
        {
            Session();
            foreach (var (rva, bytes) in GameProgression.NativeSignatures(moduleBase).Concat(NativeSignatures()))
                if (!Read(checked(moduleBase + rva), bytes.Length).AsSpan().SequenceEqual(bytes)) return Unknown("unsupported_native_layout");
            var first = Take(); Session(); var second = Take(); Session();
            if (first != second) return Unknown("starting_evolution_changing");
            if (second.Handle == 0)
            {
                if (second.Kills == 0 && !second.Activating) return Result("confirmed", roots.LocalPlayer, null, 0, "not_started", 0, "two_matching_empty_local_evolution_slots", 2, false);
                if (second.Kills == 152 && second.Activating) return Result("confirmed", roots.LocalPlayer, null, 152, "completed", 0, "two_matching_completed_local_evolution_slots", 2, false);
                return Unknown("empty_evolution_slot_not_a_completed_reward");
            }
            return Result("confirmed", roots.LocalPlayer, second.Rawcode, second.Kills, second.Activating ? "activating" : "charging", 1,
                "two_matching_local_globals_and_neutral_unit_identity", 2, true);

            Sample Take()
            {
                var proof = new StringBuilder();
                uint Remember(uint address) { var value = U32(address); proof.Append(address).Append(':').Append(value).Append(';'); return value; }
                var environment = Ptr(Remember(checked(moduleBase + 0xD0DEF8)));
                var instance = Ptr(Remember(checked(environment + 0x14)));
                var vms = Ptr(Remember(checked(instance + 0x90))); var vm = Ptr(Remember(checked(vms + 4)));
                var globals = Ptr(Remember(checked(vm + 0x285C))); var buckets = Ptr(Remember(checked(globals + 0x1C))); var mask = Remember(checked(globals + 0x24));
                if (mask > 0xFFFF || (mask & (mask + 1)) != 0) throw new ReadFailure("invalid_global_table");
                if (Remember(checked(moduleBase + 0xD305E0)) != roots.Players) throw new ReadFailure("game_state_identity_mismatch");
                var player = Ptr(Remember(checked(roots.Players + 0x58 + (uint)roots.LocalPlayer * 4)));
                if (Read(checked(player + 0x30), 1)[0] != roots.LocalPlayer) throw new ReadFailure("local_player_identity_mismatch");
                var handle = NamedArray("sh", 12); var kills = NamedArray("si", 9); var activating = NamedArray("sl", 13);
                if (kills > 152 || activating > 1) throw new ReadFailure("evolution_progress_out_of_range");
                if (handle == 0) return new((int)kills, activating == 1, 0, null, proof.ToString());
                if (activating == 0 && kills == 152 || activating == 1 && kills != 152) throw new ReadFailure("evolution_phase_inconsistent");
                if (handle < 0x100000 || handle > 0x1FFFFF) throw new ReadFailure("invalid_evolution_unit_handle");
                var handles = Ptr(Remember(checked(roots.Players + 0x1C))); var slots = Ptr(Remember(checked(handles + 0x19C)));
                var reference = Ptr(Remember(checked(slots + (handle - 0x100000) * 12 + 4)));
                var index = Remember(checked(reference + 0x0C)); var generation = Remember(checked(reference + 0x10));
                var nativeHandles = Ptr(Remember(checked(moduleBase + 0xD30448))); var offset = (index & 0x80000000) != 0 ? 0x20u : 0u; index &= 0x7FFFFFFF;
                var length = Remember(checked(nativeHandles + 0x1C + offset));
                if (length > 2_000_000 || index >= length) throw new ReadFailure("invalid_evolution_agent_index");
                var agents = Ptr(Remember(checked(nativeHandles + 0x0C + offset))); var slot = checked(agents + index * 8);
                if (Remember(slot) != 0xFFFFFFFE) throw new ReadFailure("stale_evolution_agent_slot");
                var agent = Ptr(Remember(checked(slot + 4)));
                if (Remember(checked(agent + 0x18)) != generation || Remember(checked(agent + 0x20)) != 0 || Remember(checked(agent + 0x0C)) != 0x2B61676C)
                    throw new ReadFailure("stale_or_wrong_evolution_agent");
                // GetUnitTypeId의 resolver는 native agent가 아닌 검증된 JASS reference를 돌려준다.
                var code = Remember(checked(reference + 0x30)); var rawcode = MemoryReader.DecodeRawcode(BitConverter.GetBytes(code));
                var owner = Remember(checked(reference + 0x58));
                if (owner != 15) throw new ReadFailure("evolution_unit_not_neutral_pending");
                if (rawcode is null || !SpecialPool.Contains(rawcode, StringComparer.Ordinal)) throw new ReadFailure("evolution_result_not_in_verified_special_pool");
                var currentRoots = readRoots();
                if (!SnapshotStability.SameSession(currentRoots, roots)) throw new ReadFailure("session_changed");
                if (currentRoots!.Count is < 1 or > 100_000 || !Pointer(currentRoots.Array)) throw new ReadFailure("evolution_unit_world_membership_unknown");
                var world = Read(currentRoots.Array, checked(currentRoots.Count * 4)); var present = false;
                for (var indexInWorld = 0; indexInWorld < currentRoots.Count; indexInWorld++) if (BitConverter.ToUInt32(world, indexInWorld * 4) == reference) { present = true; break; }
                if (!present) throw new ReadFailure("evolution_unit_not_in_current_world");
                return new((int)kills, activating == 1, handle, rawcode, proof.ToString());

                uint NamedArray(string name, uint expectedType)
                {
                    var hash = GameProgression.NameHash(name); var bucket = checked(buckets + (hash & mask) * 12);
                    var step = unchecked((int)Remember(bucket));
                    if ((step & 3) != 0 || step is < -0x1000 or > 0x1000) throw new ReadFailure("invalid_global_chain_layout");
                    var node = Remember(checked(bucket + 8)); var seen = new HashSet<uint>(); uint? found = null;
                    while (unchecked((int)node) > 0)
                    {
                        Ptr(node); if (!seen.Add(node) || seen.Count > 128) throw new ReadFailure("invalid_global_collision_chain");
                        var bytes = Read(node, 0x24);
                        if (BitConverter.ToUInt32(bytes) == hash)
                        {
                            var key = Ptr(BitConverter.ToUInt32(bytes, 0x14));
                            if (Read(key, 3).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(name + "\0")))
                            {
                                if (found is not null) throw new ReadFailure("duplicate_global_name");
                                var declared = BitConverter.ToUInt32(bytes, 0x1C); var tag = BitConverter.ToUInt32(bytes, 0x18); var value = BitConverter.ToUInt32(bytes, 0x20);
                                if (declared != expectedType || !GameProgression.GlobalValueTagMatches(expectedType, tag, value)) throw new ReadFailure("unexpected_evolution_global_type");
                                proof.Append(node).Append(':').Append(Convert.ToHexString(bytes)).Append(';');
                                // Native8D17B0 returns0 for a not-yet-allocated array;8CB9D0 also
                                // returns0 for an index beyond its current allocation. sl stays lazy
                                // until its first true assignment at152 kills.
                                if (value == 0) found = 0;
                                else
                                {
                                    var header = Ptr(value, "global_array_header:" + name); var size = Remember(checked(header + 8));
                                    if (size > 8192) throw new ReadFailure("invalid_evolution_global_array");
                                    if (size <= roots.LocalPlayer) found = 0;
                                    else
                                    {
                                        var data = Ptr(Remember(checked(header + 12)), "global_array_data:" + name);
                                        found = Remember(checked(data + (uint)roots.LocalPlayer * 4));
                                    }
                                }
                            }
                        }
                        var next = (long)node + step + 4;
                        if (next < 0x10000 || next > 0x7FFF0000) throw new ReadFailure("invalid_global_next_pointer");
                        node = Remember((uint)next);
                    }
                    return found ?? throw new ReadFailure("evolution_global_missing");
                }
            }
            void Session() { if (!SnapshotStability.SameSession(readRoots(), roots)) throw new ReadFailure("session_changed"); }
            byte[] Read(uint address, int size) { Ptr(address); var bytes = reader(address, size); if (bytes.Length != size) throw new ReadFailure("short_evolution_read"); return bytes; }
            uint U32(uint address) => BitConverter.ToUInt32(Read(address, 4));
        }
        catch (ReadFailure error) { var unknown = Unknown(error.Reason); return unknown with { Source = unknown.Source with { Failure = error.Failure } }; }
        catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { return Unknown("evolution_read_failed"); }
    }

    private static State Result(string status, int? player, string? code, int? kills, string phase, int count, string reason, int samples, bool membership) =>
        new(status, player, code, kills, 152, phase, count, new(true, "read_only_named_local_evolution_and_native_unit_reference", reason,
            GameProgression.SupportedMapHash, GameProgression.ScriptHash, samples, ["sh", "si", "sl"], [1716, 1717, 1719, 56115, 56147, 56149, 56163, 56179, 56195, 56207, 56211, 57069, 57081],
            ["2368A0", "2217F4", "221813", "2B8490", "2B84A5", "07E090", "8D17B0", "8CB9D0"], 15, membership));
    private static bool Pointer(uint value) => value is >= 0x10000 and <= 0x7FFF0000;
    private static uint Ptr(uint value, [CallerArgumentExpression(nameof(value))] string stage = "") => Pointer(value) ? value : throw new ReadFailure("invalid_evolution_pointer") { Failure = new(stage, value) };
    internal static (uint Rva, byte[] Bytes)[] NativeSignatures() => [
        (0x2368A0, [0x55, 0x8B, 0xEC, 0x8B, 0x4D, 8, 0xE8, 0xF5, 0xAE, 0xFE, 0xFF, 0x85, 0xC0, 0x75, 2, 0x5D, 0xC3, 0x8B, 0x40, 0x30, 0x5D, 0xC3]),
        (0x2217F4, [0x56, 0x8B, 0xC8, 0xE8, 0x94, 0x6C, 9, 0, 0x50, 0x8D, 0x4D, 0xF0, 0xE8, 0x3B, 0x84, 0xFE, 0xFF]),
        (0x221813, [0x8B, 0x57, 0x10, 0x8B, 0x4F, 0x0C, 0xE8, 0x72, 0xC8, 0xE5, 0xFF, 0x85, 0xC0, 0x74, 0x36, 0x33, 0xC9, 0x81, 0x78, 0x0C, 0x6C, 0x67, 0x61, 0x2B]),
        (0x2B8490, [0x55, 0x8B, 0xEC, 0x8B, 0x45, 8, 0x8B, 0xD1, 0x3D, 0, 0, 0x10, 0]),
        (0x2B84A5, [0x8D, 0x0C, 0x40, 0x8B, 0x82, 0x9C, 1, 0, 0, 0x8B, 0x84, 0x88, 4, 0, 0x40, 0xFF]),
        (0x8D17B0, [0x55, 0x8B, 0xEC, 0x8B, 0x49, 0x20, 0x85, 0xC9, 0x75, 6, 0x33, 0xC0, 0x5D, 0xC2, 4, 0, 0x5D, 0xE9, 0x0A, 0xA2, 0xFF, 0xFF]),
        (0x8CB9D0, [0x55, 0x8B, 0xEC, 0x8B, 0x55, 8, 0x3B, 0x51, 8, 0x72, 6, 0x33, 0xC0, 0x5D, 0xC2, 4, 0])
    ];
}
