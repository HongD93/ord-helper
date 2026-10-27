using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// 같은 맵의 증가형 목재·변화 횟수·특성 포인트·라운드를 읽기만 한다.
static class GameSpecialRecipeConditions
{
    internal sealed record State(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("localPlayer")] int? LocalPlayer,
        [property: JsonPropertyName("distortionWoodSurcharge")] int? DistortionWoodSurcharge,
        [property: JsonPropertyName("distortionWoodIncrement")] int? DistortionWoodIncrement,
        [property: JsonPropertyName("transformationCount")] int? TransformationCount,
        [property: JsonPropertyName("skillPoints")] int? SkillPoints,
        [property: JsonPropertyName("round")] int? Round,
        [property: JsonPropertyName("source")] Evidence Source)
    {
        [JsonPropertyName("nikaEnhancement")]
        public NikaState NikaEnhancement { get; init; } = UnknownNika(null, "not_captured");
    }
    internal sealed record NikaState(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("localPlayer")] int? LocalPlayer,
        [property: JsonPropertyName("stage")] int? Stage,
        [property: JsonPropertyName("source")] Evidence Source);
    private static NikaState UnknownNika(int? owner, string reason) => new("unknown", owner, null,
        new(true, GameProgression.SupportedMapHash, 0, "two_matching_named_NG_integer_array_reads", reason));
    internal sealed record Evidence(
        [property: JsonPropertyName("readOnly")] bool ReadOnly,
        [property: JsonPropertyName("mapSha256")] string MapSha256,
        [property: JsonPropertyName("sampleCount")] int SampleCount,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("reason")] string Reason);
    private sealed record Entry(string Name, uint Node, uint Header, uint Data, uint Value);
    private sealed record Sample(uint Environment, uint Instance, uint Vm, uint Globals, uint Buckets, uint Mask, uint Player, Dictionary<string, Entry?> Values);
    internal static State Unknown(string reason) => Result(null, null, null, null, null, null, reason, 0);
    private static State Result(int? owner, int? surcharge, int? increment, int? changes, int? skills, int? round, string reason, int samples) =>
        new(owner is null ? "unknown" : new[] { surcharge, increment, changes, skills, round }.All(v => v is not null) ? "confirmed" : "partial",
            owner, surcharge, increment, changes, skills, round, new(true, GameProgression.SupportedMapHash, samples,
                "two_matching_named_globals_and_native_player_state5", reason));
    internal static State Capture(MemoryReader memory, Roots roots, uint moduleBase, string? version, string? mapHash) =>
        CaptureForTests(memory.Read, () => memory.ReadRoots(checked(moduleBase + 0xD326F0), checked(moduleBase + 0xD305E0)), roots, moduleBase, version, mapHash);
    internal static State CaptureForTests(Func<uint, int, byte[]> read, Func<Roots?> readRoots, Roots roots, uint moduleBase, string? version, string? mapHash)
    {
        if (version != GameProgression.SupportedVersion || mapHash != GameProgression.SupportedMapHash) return Unknown("unsupported_map_or_version");
        if (roots.LocalPlayer is < 0 or > 3 || !Pointer(moduleBase)) return Unknown("unsupported_player_or_module");
        try
        {
            Session();
            foreach (var (rva, bytes) in GameProgression.NativeSignatures(moduleBase))
                if (!Read(checked(moduleBase + rva), bytes.Length).AsSpan().SequenceEqual(bytes)) return Unknown("unsupported_native_layout");
            var nikaLayout = true;
            try { foreach (var (rva, bytes) in GameStartingEvolution.NativeSignatures().Where(signature => signature.Rva is 0x8D17B0 or 0x8CB9D0))
                nikaLayout &= Read(checked(moduleBase + rva), bytes.Length).AsSpan().SequenceEqual(bytes); }
            catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { nikaLayout = false; }
            var first = Take(); Session(); var second = Take(); Session();
            if (first.Environment != second.Environment || first.Instance != second.Instance || first.Vm != second.Vm || first.Globals != second.Globals ||
                first.Buckets != second.Buckets || first.Mask != second.Mask || first.Player != second.Player) return Unknown("global_table_changing");
            int? Value(string name, int maximum)
            {
                var a = first.Values[name]; var b = second.Values[name];
                return a is not null && a == b && a.Value <= maximum ? checked((int)a.Value) : null;
            }
            var increment = Value("LW", 1000); if (increment != 5) increment = null; // 초기화 후 이 맵에서 LW의 유일 대입은5다.
            var surcharge = increment is null ? null : Value("hW", GameResources.MaximumSupportedAmount);
            var changes = Value("LO", GameResources.MaximumSupportedAmount); var round = Value("X3", 65);
            var skills = GameResources.CaptureSkillPointsForTests(read, readRoots, roots, moduleBase, version);
            Session();
            var nika = nikaLayout ? Value("NG", 2) : null;
            return Result(roots.LocalPlayer, surcharge, increment, changes, skills, round, "matching_fields_only_unknown_is_not_zero", 2) with
            { NikaEnhancement = nika is { } stage ? new("confirmed", roots.LocalPlayer, stage,
                new(true, GameProgression.SupportedMapHash, 2, "two_matching_named_NG_integer_array_reads", "NG_0_unenhanced_1_enhanced_2_nika_completed")) : UnknownNika(roots.LocalPlayer, "NG_not_confirmed") };

            Sample Take()
            {
                var environment = Ptr(U32(checked(moduleBase + 0xD0DEF8)));
                var instance = Ptr(U32(checked(environment + 0x14))); var vms = Ptr(U32(checked(instance + 0x90)));
                var vm = Ptr(U32(checked(vms + 4))); var globals = Ptr(U32(checked(vm + 0x285C)));
                var buckets = Ptr(U32(checked(globals + 0x1C))); var mask = U32(checked(globals + 0x24));
                if (mask > 0xFFFF || (mask & (mask + 1)) != 0) throw new IOException("Invalid special global table.");
                var player = Ptr(U32(checked(roots.Players + 0x58 + (uint)roots.LocalPlayer * 4)));
                if (Read(checked(player + 0x30), 1)[0] != roots.LocalPlayer) throw new IOException("Player identity changed.");
                var values = new Dictionary<string, Entry?>(StringComparer.Ordinal);
                foreach (var name in new[] { "hW", "LW", "LO", "X3", "NG" })
                {
                    if(name=="NG"&&!nikaLayout){values[name]=null;continue;}
                    try { values[name] = Named(name); }
                    catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { values[name] = null; }
                }
                return new(environment, instance, vm, globals, buckets, mask, player, values);
                Entry Named(string name)
                {
                    var hash = GameProgression.NameHash(name); var bucket = checked(buckets + (hash & mask) * 12);
                    var step = unchecked((int)U32(bucket)); if ((step & 3) != 0 || step is < -0x1000 or > 0x1000) throw new IOException("Bad global chain.");
                    var node = U32(checked(bucket + 8)); var seen = new HashSet<uint>(); Entry? found = null;
                    while (unchecked((int)node) > 0)
                    {
                        Ptr(node); if (!seen.Add(node) || seen.Count > 128) throw new IOException("Global cycle.");
                        var bytes = Read(node, 0x24);
                        if (BitConverter.ToUInt32(bytes) == hash)
                        {
                            var key = Ptr(BitConverter.ToUInt32(bytes, 0x14));
                            if (Read(key, 3).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(name + "\0")))
                            {
                                if (found is not null) throw new IOException("Duplicate special global.");
                                var type = BitConverter.ToUInt32(bytes, 0x1C); var array = BitConverter.ToUInt32(bytes, 0x18); var value = BitConverter.ToUInt32(bytes, 0x20);
                                uint header = 0, data = 0;
                                if (name != "X3")
                                {
                                    if (type != 9 || array != 9) throw new IOException("Unexpected integer array.");
                                    // NG는 첫 강화 전 lazy0일 수 있다. getter8D17B0/8CB9D0의 기본0 경로를 별도 검증했다.
                                    if (name == "NG" && value == 0) value = 0;
                                    else
                                    {
                                        header = Ptr(value); var size = U32(checked(header + 8));
                                        if (size > 8192 || name != "NG" && size <= roots.LocalPlayer) throw new IOException("Invalid integer array.");
                                        if (name == "NG" && size <= roots.LocalPlayer) value = 0;
                                        else { data = Ptr(U32(checked(header + 12))); value = U32(checked(data + (uint)roots.LocalPlayer * 4)); }
                                    }
                                }
                                else if (type != 4 || !GameProgression.GlobalValueTagMatches(4, array, value)) throw new IOException("Unexpected round scalar.");
                                found = new(name, node, header, data, value);
                            }
                        }
                        var next = (long)node + step + 4; if (next < 0x10000 || next > 0x7FFF0000) throw new IOException("Invalid global next pointer.");
                        node = U32((uint)next);
                    }
                    return found ?? throw new IOException("Missing special global.");
                }
            }
            void Session() { if (!SnapshotStability.SameSession(readRoots(), roots)) throw new IOException("Session changed."); }
            byte[] Read(uint address, int count) { Ptr(address); var bytes = read(address, count); if (bytes.Length != count) throw new IOException("Short special read."); return bytes; }
            uint U32(uint address) => BitConverter.ToUInt32(Read(address, 4));
        }
        catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { return Unknown("special_condition_read_failed"); }
    }
    internal static State? Read(JsonElement value, int owner)
    {
        if (owner is < 0 or > 3 || value.ValueKind != JsonValueKind.Object || Text(value, "status") is not ("confirmed" or "partial") || Int(value, "localPlayer") != owner ||
            !value.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("readOnly", out var readOnly) || readOnly.ValueKind != JsonValueKind.True ||
            Text(source, "mapSha256") != GameProgression.SupportedMapHash || Int(source, "sampleCount") is not >= 2) return null;
        int? Known(string name, int maximum) { var n = Int(value, name); return n is >= 0 && n <= maximum ? n : null; }
        var increment = Known("distortionWoodIncrement", 1000); if (increment != 5) increment = null;
        var result = Result(owner, increment is null ? null : Known("distortionWoodSurcharge", GameResources.MaximumSupportedAmount), increment,
            Known("transformationCount", GameResources.MaximumSupportedAmount), Known("skillPoints", GameResources.MaximumSupportedAmount), Known("round", 65), "validated_snapshot_special_facts", 2);
        if (value.TryGetProperty("nikaEnhancement", out var nika) && nika.ValueKind == JsonValueKind.Object && Text(nika, "status") == "confirmed" &&
            Int(nika, "localPlayer") == owner && Int(nika, "stage") is >= 0 and <= 2 && nika.TryGetProperty("source", out var evidence) &&
            evidence.ValueKind == JsonValueKind.Object && evidence.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True &&
            Text(evidence, "mapSha256") == GameProgression.SupportedMapHash && Int(evidence, "sampleCount") is >= 2)
            result = result with { NikaEnhancement = new("confirmed", owner, Int(nika, "stage"), new(true, GameProgression.SupportedMapHash, 2, "validated_snapshot_NG", "matching_confirmed_NG")) };
        return result;
    }
    internal static bool ConsumptionMatches(JsonElement before, JsonElement after, IEnumerable<CraftRecipeCondition> requirements)
    {
        var required = requirements.Where(c => c.Type is "IFCT" or "CHCT" or "SKPT" or "NIKA").ToArray(); if (required.Length == 0) return true;
        var a = CraftConditions.Read(before); var b = CraftConditions.Read(after);
        if (a?.Special is null || b?.Special is null || a.Special.LocalPlayer != b.Special.LocalPlayer ||
            !CraftConditions.TryProject(a, required, out var projected, out _, out _, out _) || projected?.Special is not { } expected) return false;
        return (!required.Any(c => c.Type == "IFCT") || expected.DistortionWoodSurcharge == b.Special.DistortionWoodSurcharge && expected.DistortionWoodIncrement == b.Special.DistortionWoodIncrement) &&
            (!required.Any(c => c.Type == "CHCT") || expected.TransformationCount == b.Special.TransformationCount) &&
            (!required.Any(c => c.Type == "SKPT") || expected.SkillPoints == b.Special.SkillPoints) &&
            (!required.Any(c => c.Type == "NIKA") || a.Special.NikaEnhancement is { Status: "confirmed", Stage: 1 } && b.Special.NikaEnhancement is { Status: "confirmed", Stage: 2 });
    }
    private static int? Int(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var n) ? n : null;
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool Pointer(uint p) => p is >= 0x10000 and <= 0x7FFF0000;
    private static uint Ptr(uint p) => Pointer(p) ? p : throw new IOException("Invalid special pointer.");
}
