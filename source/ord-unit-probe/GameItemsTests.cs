using System.ComponentModel;
using System.Text.Json;

static class GameItemsTests
{
    public static void Run()
    {
        (string Name, Action Run)[] tests = [
            ("native hash vectors and collision lookup", Known),
            ("availability is a boolean bit rather than a stack count", BooleanFlags),
            ("known false differs from absent initialization", MissingEntry),
            ("hash collisions still require the exact rawcode", ExactRawcode),
            ("changing item values fail closed", Changing),
            ("each session boundary is checked", SessionChanges),
            ("enemy-array churn does not change the player session", WorldChurn),
            ("unsupported profiles do not read", Unsupported),
            ("local player identity is independently checked", PlayerIdentity),
            ("invalid pointers masks cycles and chain limits fail closed", InvalidStructures),
            ("partial and failed reads never expose partial item counts", ReadFailures),
            ("separate players and captures do not reuse old item states", NoCache),
            ("snapshot serialization keeps explicit known zero values", Serialization)
        ];
        foreach (var test in tests)
        {
            try { test.Run(); }
            catch (Exception error) { throw new Exception("Native item test failed: " + test.Name, error); }
        }
        Console.WriteLine($"PASS: read-only per-player native item states {tests.Length}/{tests.Length} cases; no game input or writes.");
    }

    private static void Known()
    {
        Check(GameItems.Hash(GameItems.Rawcode("AI00")) == 0x5C1BC339 &&
            GameItems.Hash(GameItems.Rawcode("AI01")) == 0x3F3FAC85 &&
            GameItems.Hash(GameItems.Rawcode("AI02")) == 0xCCB6C881, "hashes match local native disassembly and the live read-only probe");
        var f = new Fixture(); var state = f.Capture();
        Check(state.Status == "confirmed" && state.LocalPlayer == 0 && state.Counts.Count == 3 &&
            state.Counts["AI00"] == 1 && state.Counts["AI01"] == 0 && state.Counts["AI02"] == 0,
            "the two-collision live trace keeps exact per-player availability values");
        Check(state.Source.ReadOnly && state.Source.SampleCount == 2 && state.Source.MapSha256 == GameItems.SupportedMapHash &&
            state.Source.AvailabilityFlags["AI00"] == 1, "evidence distinguishes a map-synchronized native mirror from unit item slots");
    }
    private static void BooleanFlags()
    {
        var f = new Fixture();f.Write(f.Nodes["AI00"] + 0x18, 0xC3);f.Write(f.Nodes["AI01"] + 0x18, 2);
        var state = f.Capture();
        Check(state.Status == "confirmed" && state.Counts["AI00"] == 1 && state.Counts["AI01"] == 0 &&
            state.Source.AvailabilityFlags["AI00"] == 0xC3, "unrelated flag bits never become owned item counts");
    }
    private static void MissingEntry()
    {
        var f = new Fixture(0, [0, 0, 0]);
        Check(f.Capture().Counts.Values.All(count => count == 0), "three explicit false entries are known absence");
        f.Write(f.Bucket("AI01") + 8, 0);
        var state = f.Capture();Unknown(state);
        Check(state.Source.Reason == "item_entry_missing", "a missing initializer or node cannot be inferred as known false");
    }
    private static void ExactRawcode()
    {
        var f = new Fixture();const uint impostor = 0x27001000;
        f.AddNode(impostor, "xxxx", 1, f.Bucket("AI00"), GameItems.Hash(GameItems.Rawcode("AI00")));
        Check(f.Capture().Counts["AI00"] == 1, "an equal hash with a different key is skipped to the correct node");
        f.Write(impostor + 8, 0);Unknown(f.Capture());
    }
    private static void Changing()
    {
        var f = new Fixture();var calls = 0;
        byte[] Read(uint address, int size)
        {
            if (address == f.Nodes["AI00"] && ++calls == 2) f.Write(address + 0x18, 0);
            return f.Read(address, size);
        }
        var state = f.Capture(Read);Unknown(state);
        Check(state.Source.Reason == "items_changing", "a transition during either native sample does not publish a mixed inventory");
    }
    private static void SessionChanges()
    {
        for (var at = 1; at <= 3; at++)
        {
            var f = new Fixture();var calls = 0;var boundary = at;
            var state = f.Capture(readRoots: () => ++calls >= boundary ? f.Roots with { Players = f.Roots.Players + 4 } : f.Roots);
            Unknown(state);Check(state.Source.Reason == "session_changed", "initial, middle and final session boundaries are all required");
        }
    }
    private static void WorldChurn()
    {
        var f = new Fixture();var state = f.Capture(readRoots: () => f.Roots with { Array = f.Roots.Array + 0x100, Count = 900 });
        Check(state.Status == "confirmed" && state.LocalPlayer == 0, "unrelated enemy spawns do not invalidate the unchanged player item table");
    }
    private static void Unsupported()
    {
        var f = new Fixture();
        byte[] Forbidden(uint address, int size) => throw new Exception("Unsupported items must not touch memory.");
        Unknown(GameItems.CaptureForTests(Forbidden, () => f.Roots, f.Roots, Fixture.Base, "1.26.0.6401", GameItems.SupportedMapHash));
        foreach (var map in new string?[] { null, "", "other-map" })
            Unknown(GameItems.CaptureForTests(Forbidden, () => f.Roots, f.Roots, Fixture.Base, GameItems.SupportedVersion, map));
        foreach (var local in new[] { -1, 12 })
            Unknown(GameItems.CaptureForTests(Forbidden, () => f.Roots, f.Roots with { LocalPlayer = local }, Fixture.Base,
                GameItems.SupportedVersion, GameItems.SupportedMapHash));
    }
    private static void PlayerIdentity()
    {
        var f = new Fixture();f.Bytes[Fixture.Player + 0x30] = 1;
        var state = f.Capture();Unknown(state);Check(state.Source.Reason == "local_player_identity_mismatch", "a reused player pointer cannot expose another player's items");
    }
    private static void InvalidStructures()
    {
        var f = new Fixture();f.Write(Fixture.Player + 0x2D0, 0);Unknown(f.Capture());
        f = new Fixture();f.Write(Fixture.Ability + 0x48, 0xFFFFFFFF);Unknown(f.Capture());
        foreach (var mask in new uint[] { 6, 0xFFFFFFFF, 0x1FFFF })
        { f = new Fixture();f.Write(Fixture.Ability + 0x50, mask);Unknown(f.Capture()); }
        f = new Fixture();f.Write(f.Nodes["AI02"] + 8, f.Nodes["AI02"]);Unknown(f.Capture());
        f = new Fixture();f.Write(f.Bucket("AI00"), 3);Unknown(f.Capture());
        f = new Fixture();
        for (var index = 0; index < 129; index++)
            f.AddNode(0x28000000 + (uint)index * 0x100, "xxxx", 0, f.Bucket("AI00"));
        Unknown(f.Capture());
    }
    private static void ReadFailures()
    {
        var f = new Fixture();
        Unknown(f.Capture((address, size) => throw new Win32Exception(299)));
        Unknown(f.Capture((address, size) => new byte[Math.Max(0, size - 1)]));
        Unknown(f.Capture((address, size) => address == f.Nodes["AI01"] ? throw new IOException("Partial item table.") : f.Read(address, size)));
    }
    private static void NoCache()
    {
        var first = new Fixture().Capture();
        var second = new Fixture(11, [0, 1, 1]).Capture();
        Check(first.Counts["AI00"] == 1 && second.Status == "confirmed" && second.LocalPlayer == 11 &&
            second.Counts["AI00"] == 0 && second.Counts["AI01"] == 1 && second.Counts["AI02"] == 1,
            "a separate capture and player cannot inherit the previous owned-state result");
    }
    private static void Serialization()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Fixture().Capture()));
        var value = document.RootElement;
        Check(value.GetProperty("status").GetString() == "confirmed" && value.GetProperty("localPlayer").GetInt32() == 0 &&
            value.GetProperty("counts").GetProperty("AI01").GetInt32() == 0 && value.GetProperty("source").GetProperty("readOnly").GetBoolean(),
            "snapshot field names and known zero values remain explicit");
    }
    private static void Unknown(GameItems.State state) => Check(state.Status == "unknown" && state.LocalPlayer is null &&
        state.Counts.Count == 0, "unknown item state must not expose false-known or partial per-item counts");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture
    {
        internal const uint Base = 0x10000000, Player = 0x24000000, Ability = 0x25000000, Buckets = 0x26000000;
        internal Roots Roots { get; }
        internal Dictionary<uint, byte> Bytes { get; } = [];
        internal Dictionary<string, uint> Nodes { get; } = new(StringComparer.Ordinal);
        internal Fixture(int local = 0, int[]? counts = null)
        {
            Roots = new(0x20000000, 0x21000000, 0x22000000, 0x23000000, 5, local);
            Write(Roots.Players + 0x58 + (uint)local * 4, Player);Bytes[Player + 0x30] = (byte)local;
            Write(Player + 0x2D0, Ability);Write(Ability + 0x48, Buckets);Write(Ability + 0x50, 7);
            for (var index = 0; index < 8; index++) { Write(Buckets + (uint)index * 12, 4);Write(Buckets + (uint)index * 12 + 8, 0); }
            string[] codes = ["AI00", "AI01", "AI02"];counts ??= [1, 0, 0];
            for (var index = 0; index < codes.Length; index++)
            {
                var node = 0x27000000 + (uint)index * 0x100;Nodes.Add(codes[index], node);
                AddNode(node, codes[index], (uint)counts[index], Bucket(codes[index]));
            }
            AddNode(0x27000300, "A0G7", 0, Bucket("AI01"));
        }
        internal uint Bucket(string code) => Buckets + (GameItems.Hash(GameItems.Rawcode(code)) & 7) * 12;
        internal void AddNode(uint address, string code, uint flags, uint bucket, uint? hash = null)
        {
            var head = BitConverter.ToUInt32(Read(bucket + 8, 4));
            for (var offset = 0; offset < 0x1C; offset++) Bytes[address + (uint)offset] = 0;
            Write(address, hash ?? GameItems.Hash(GameItems.Rawcode(code)));Write(address + 8, head);
            Write(address + 0x14, GameItems.Rawcode(code));Write(address + 0x18, flags);Write(bucket + 8, address);
        }
        internal void Write(uint address, uint value)
        { var bytes = BitConverter.GetBytes(value);for (var index = 0; index < 4; index++) Bytes[address + (uint)index] = bytes[index]; }
        internal byte[] Read(uint address, int size)
        {
            var result = new byte[size];
            for (var index = 0; index < size; index++)
                if (!Bytes.TryGetValue(address + (uint)index, out result[index])) throw new IOException("Unexpected native-item fixture read.");
            return result;
        }
        internal GameItems.State Capture(Func<uint, int, byte[]>? read = null, Func<Roots?>? readRoots = null) =>
            GameItems.CaptureForTests(read ?? Read, readRoots ?? (() => Roots), Roots, Base, GameItems.SupportedVersion, GameItems.SupportedMapHash);
    }
}
