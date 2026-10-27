using System.ComponentModel;
using System.Text.Json.Serialization;

// 맵의 Qr 아이템 bool과 grant/consume에서 함께 갱신되는 player ability availability를 읽는다.
// 유닛 인벤토리나 더미 수량을 아이템으로 해석하지 않으며 게임 함수를 호출하지 않는다.
static class GameItems
{
    internal const string SupportedVersion = "1.28.5.7680";
    internal const string SupportedMapHash = "0794996c9bc73eaa3020f20ebc45fb37cfa64a6e98ffe8aa0dfa3b356e77bc5c";
    private const string ScriptHash = "96201e80edec1b2764f85818760306c6b2f0becd390ba4d63f41c86f3dfaff6d";
    private static readonly string[] Codes = ["AI00", "AI01", "AI02"];
    // 해당 Game.dll의 RVA A822D0. Native integer hash RVA1CACC0이 이 16개 상수를 사용한다.
    private static readonly uint[] HashWords = [
        0x98AA3D0C, 0xF67BCA9E, 0xC46CA84C, 0x2AC9D845, 0x9A1CF1DD, 0x6450148E, 0x8516213D, 0xC0882BBF,
        0xF10C2A9C, 0x9D7CF013, 0xCD845F5E, 0x1D4BD837, 0x1055F69A, 0xA6A87DCD, 0x312D8D9E, 0x645A1CEC
    ];

    internal sealed record State(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("localPlayer")] int? LocalPlayer,
        [property: JsonPropertyName("counts")] Dictionary<string, int> Counts,
        [property: JsonPropertyName("source")] Evidence Source);

    internal sealed record Evidence(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("readOnly")] bool ReadOnly,
        [property: JsonPropertyName("sampleCount")] int SampleCount,
        [property: JsonPropertyName("mapSha256")] string MapSha256,
        [property: JsonPropertyName("scriptSha256")] string ScriptSha256,
        [property: JsonPropertyName("scriptLines")] int[] ScriptLines,
        [property: JsonPropertyName("nativeRvas")] string[] NativeRvas,
        [property: JsonPropertyName("availabilityFlags")] Dictionary<string, uint> AvailabilityFlags,
        [property: JsonPropertyName("ownershipBasis")] string OwnershipBasis);

    private sealed record Entry(uint Address, uint Hash, uint Rawcode, uint Flags, uint Bucket, uint Head, int LinkOffset);
    private sealed record Sample(uint Player, uint AbilityData, uint Buckets, uint Mask, Entry[] Entries);
    private sealed class ItemReadException(string reason) : IOException(reason)
    {
        internal string Reason { get; } = reason;
    }

    public static State Unknown(string reason) => new("unknown", null, new(StringComparer.Ordinal), Source(reason, 0, []));

    public static State Capture(MemoryReader memory, Roots roots, uint moduleBase, string? gameVersion, string? loadedMapHash) =>
        CaptureForTests(memory.Read,
            () => memory.ReadRoots(checked(moduleBase + 0xD326F0), checked(moduleBase + 0xD305E0)),
            roots, moduleBase, gameVersion, loadedMapHash);

    internal static State CaptureForTests(Func<uint, int, byte[]> read, Func<Roots?> readRoots,
        Roots roots, uint moduleBase, string? gameVersion, string? loadedMapHash)
    {
        if (!string.Equals(gameVersion, SupportedVersion, StringComparison.Ordinal)) return Unknown("unsupported_version");
        if (!string.Equals(loadedMapHash, SupportedMapHash, StringComparison.OrdinalIgnoreCase)) return Unknown("unsupported_map");
        if (roots.LocalPlayer is < 0 or > 11) return Unknown("unsupported_local_player");
        if (!Pointer(moduleBase) || !Pointer(roots.Ui) || !Pointer(roots.Players) || !Pointer(roots.Table))
            return Unknown("invalid_session_pointer");
        try
        {
            if (!SnapshotStability.SameSession(readRoots(), roots)) return Unknown("session_changed");
            var first = ReadSample();
            if (!SnapshotStability.SameSession(readRoots(), roots)) return Unknown("session_changed");
            var second = ReadSample();
            if (!SnapshotStability.SameSession(readRoots(), roots)) return Unknown("session_changed");
            if (first.Player != second.Player || first.AbilityData != second.AbilityData || first.Buckets != second.Buckets ||
                first.Mask != second.Mask || !first.Entries.SequenceEqual(second.Entries)) return Unknown("items_changing");
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < Codes.Length; index++) counts.Add(Codes[index], (second.Entries[index].Flags & 1) != 0 ? 1 : 0);
            return new("confirmed", roots.LocalPlayer, counts, Source("two_matching_native_availability_reads", 2, second.Entries));

            Sample ReadSample()
            {
                var player = CheckedPointer(U32(checked(roots.Players + 0x58 + (uint)roots.LocalPlayer * 4)));
                if (ReadExact(checked(player + 0x30), 1)[0] != roots.LocalPlayer)
                    throw new ItemReadException("local_player_identity_mismatch");
                // Native setter246310 ->29CAD0: CPlayer+2D0의 per-player ability data.
                var abilityData = CheckedPointer(U32(checked(player + 0x2D0)));
                var buckets = CheckedPointer(U32(checked(abilityData + 0x48)));
                var mask = U32(checked(abilityData + 0x50));
                if (mask > 0xFFFF || (mask & (mask + 1)) != 0) throw new ItemReadException("invalid_ability_hash_table");
                var entries = new Entry[Codes.Length];
                for (var index = 0; index < Codes.Length; index++)
                {
                    var raw = Rawcode(Codes[index]);
                    var hash = Hash(raw);
                    var bucket = checked(buckets + (hash & mask) * 12);
                    var link = unchecked((int)U32(bucket));
                    if ((link & 3) != 0 || link is < -0x1000 or > 0x1000)
                        throw new ItemReadException("invalid_ability_chain_layout");
                    var head = U32(checked(bucket + 8));
                    var node = head;
                    var seen = new HashSet<uint>();
                    Entry? found = null;
                    while (unchecked((int)node) > 0)
                    {
                        CheckedPointer(node);
                        if (seen.Count >= 128 || !seen.Add(node)) throw new ItemReadException("invalid_ability_collision_chain");
                        var data = ReadExact(node, 0x1C);
                        var nodeHash = BitConverter.ToUInt32(data, 0);
                        var nodeRawcode = BitConverter.ToUInt32(data, 0x14);
                        if (nodeHash == hash && nodeRawcode == raw)
                        {
                            found = new(node, nodeHash, nodeRawcode, BitConverter.ToUInt32(data, 0x18), bucket, head, link);
                            break;
                        }
                        var nextAddress = (long)node + link + 4;
                        if (nextAddress < 0x10000 || nextAddress > 0x7FFF0000) throw new ItemReadException("invalid_ability_next_pointer");
                        node = U32((uint)nextAddress);
                    }
                    // 맵 BhW가 AIxx를 명시적으로 false 등록한다. 노드 부재는 '없음'이 아니라 초기화/조회 미확인이다.
                    entries[index] = found ?? throw new ItemReadException("item_entry_missing");
                }
                return new(player, abilityData, buckets, mask, entries);
            }
            uint U32(uint address) => BitConverter.ToUInt32(ReadExact(address, 4));
            byte[] ReadExact(uint address, int size)
            {
                CheckedPointer(address);
                var bytes = read(address, size);
                if (bytes.Length != size) throw new IOException("Incomplete native item read.");
                return bytes;
            }
        }
        catch (ItemReadException error) { return Unknown(error.Reason); }
        catch (Exception error) when (error is IOException or Win32Exception or OverflowException) { return Unknown("item_read_failed"); }
    }

    internal static uint Rawcode(string code)
    {
        if (code.Length != 4 || code.Any(character => character is < ' ' or > '~')) throw new ArgumentException("A four-byte rawcode is required.", nameof(code));
        return ((uint)code[0] << 24) | ((uint)code[1] << 16) | ((uint)code[2] << 8) | code[3];
    }

    internal static uint Hash(uint rawcode)
    {
        unchecked
        {
            uint first = 0x7FED7FED, second = 0xEEEEEEEE;
            while (rawcode != 0)
            {
                var value = rawcode & 255; rawcode >>= 8;
                first = (first + second) ^ (HashWords[value >> 4] - HashWords[value & 15]);
                second = first + 3 + value + second + (second << 5);
            }
            return first;
        }
    }

    private static bool Pointer(uint value) => value >= 0x10000 && value <= 0x7FFF0000;
    private static uint CheckedPointer(uint value) => Pointer(value) ? value : throw new IOException("Invalid native item pointer.");
    private static Evidence Source(string reason, int sampleCount, Entry[] entries) => new(
        "read_only_1285_player_ability_availability", reason, true, sampleCount, SupportedMapHash, ScriptHash,
        [10566, 10567, 10568, 10570, 10571, 10572, 10704, 10718, 10756, 10758, 10776, 10780],
        ["0x246310", "0x29CAD0", "0x2799F0", "0x2619A0", "0x1CACC0", "0xA822D0"],
        entries.Select((entry, index) => (entry, index)).ToDictionary(pair => Codes[pair.index], pair => pair.entry.Flags, StringComparer.Ordinal),
        "Map Qr boolean is mirrored by initialization/grant/consume into per-player AIxx availability bit0. Values are booleans (0 or 1), not stack counts; Qr and unit item slots are not directly read.");
}
