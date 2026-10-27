using System.Text;
using System.Text.Json;

static class GameRandomStateTests
{
    internal static void Run()
    {
        (string Name, Action Test)[] tests = [
            ("named history and exact nine-way integer hashtable", Known),
            ("empty initialized LoadInteger entries are known zero", Empty),
            ("table ids come from the validated JASS reference and allow native key zero", TableIdentity),
            ("verified map constant can be inlined by the JASS compiler", InlineConstant),
            ("unsupported layout never publishes scalar entitlements", Unsupported),
            ("all session boundaries and local owners remain exact", Session),
            ("enemy array changes are irrelevant to history", EnemyChurn),
            ("draw counter transitions are not mixed", ChangingCounter),
            ("one changing category cannot be published", ChangingCategory),
            ("same-value hashtable identity changes are not cached", ChangingTable),
            ("a partial hashtable leaves only separately checked scalars", Partial),
            ("stale agent generations and wrong agent classes are partial", Agents),
            ("integer hash masks and collision chains are bounded", Hashes),
            ("missing scalar and wrong scalar type are unknown", Globals),
            ("scalar current-value tags follow native initialization and assignment", ScalarValueTags),
            ("observed global type diagnostics survive scalar and table failures", TypeDiagnostics),
            ("exact global case and duplicate names are checked", Names),
            ("history totals must equal the accumulated draw counter", Totals),
            ("negative huge and corrupt counts are never promoted", Bounds),
            ("short failed and exceptional reads remain unknown", ReadErrors),
            ("players and repeated captures have no shared state", NoCache),
            ("serialization separates unknown partial and known zero", Serialization)
        ];
        foreach (var (name, test) in tests)
            try { test(); } catch (Exception error) { throw new Exception("Random history test failed: " + name, error); }
        Console.WriteLine($"PASS: read-only adaptive random-wisp history {tests.Length}/{tests.Length} cases; no game input or writes.");
    }
    private static void Known()
    {
        var f = new Fixture(); var value = f.Capture();
        Check(value.Status == "confirmed" && value.LocalPlayer == 0 && value.DrawCount == 6 && value.CorrectionInterval == 2 &&
            value.CommonAcquiredCounts.Count == 9 && value.CommonAcquiredCounts["h003"] == 4 && value.CommonAcquiredCounts["h002"] == 2,
            "native cumulative results are separate from current unit counts");
        Check(value.Source.ReadOnly && value.Source.SampleCount == 2 && value.Source.MapSha256 == GameProgression.SupportedMapHash,
            "two matching reads retain exact map evidence");
    }
    private static void Empty()
    {
        foreach (var kind in new[] { 0, 1, 2 })
        {
            var f = new Fixture(); f.Draws(0);
            if (kind == 0) f.Write(f.ParentBucket + 8, 0);
            if (kind == 1) f.Write(Fixture.Table + 0x40, uint.MaxValue);
            if (kind == 2) f.Write(Fixture.Parent + 0x40, uint.MaxValue);
            var value = f.Capture(); Check(value.Status == "confirmed" && value.DrawCount == 0 && value.CommonAcquiredCounts.Values.All(n => n == 0),
                "LoadInteger defaults are zero only after the exact initialized table is resolved");
        }
    }
    private static void TableIdentity()
    {
        var f = new Fixture(); f.Write(Fixture.Agent + 0x24, 0xFFFFFFFF);
        Check(f.Capture().Status == "confirmed", "the generic native agent's unrelated +24 is not a hashtable id");
        f.Write(Fixture.Reference + 0x24, 0); f.Write(Fixture.TableBuckets + 5 * 12 + 8, 0);
        f.Write(Fixture.Table + 4, 0); f.Write(Fixture.Table + 0x18, 0); f.Write(Fixture.TableBuckets + 8, Fixture.Table);
        Check(f.Capture().Status == "confirmed", "an exact existing native table record may use key zero");
        f.Write(Fixture.TableBuckets + 8, 0); Check(f.Capture().Status == "partial", "zero does not fabricate an absent table");
    }
    private static void InlineConstant()
    {
        var f = new Fixture(); f.RemoveGlobal("sZ"); var value = f.Capture();
        Check(value.Status == "confirmed" && value.CorrectionInterval == 2 && value.Source.CorrectionIntervalSource == "verified_map_constant_sZ", "a missing inlined immutable map constant is not guessed from current counts");
        f = new Fixture(); f.Value("sZ", 3); Unknown(f.Capture());
    }
    private static void Unsupported()
    {
        var f = new Fixture(); byte[] Forbidden(uint _, int __) => throw new Exception("Unexpected read");
        Unknown(GameRandomState.CaptureForTests(Forbidden, () => f.Roots, f.Roots, Fixture.Base, "1.26", GameProgression.SupportedMapHash));
        Unknown(GameRandomState.CaptureForTests(Forbidden, () => f.Roots, f.Roots, Fixture.Base, GameProgression.SupportedVersion, "another-map"));
        Unknown(GameRandomState.CaptureForTests(Forbidden, () => f.Roots, f.Roots, 0, GameProgression.SupportedVersion, GameProgression.SupportedMapHash));
        f.Bytes[Fixture.Base + 0x240968] = 0; Unknown(f.Capture());
    }
    private static void Session()
    {
        foreach (var boundary in new[] { 1, 2, 3 }) foreach (var kind in new[] { 0, 1, 2, 3 })
        {
            var f = new Fixture(); var calls = 0; var changed = kind switch { 0 => f.Roots with { Ui = f.Roots.Ui + 4 }, 1 => f.Roots with { Players = f.Roots.Players + 4 }, 2 => f.Roots with { Table = f.Roots.Table + 4 }, _ => f.Roots with { LocalPlayer = 1 } };
            Unknown(f.Capture(roots: () => ++calls >= boundary ? changed : f.Roots));
        }
        var a = new Fixture(); a.Bytes[Fixture.Player + 0x30] = 1; Unknown(a.Capture());
    }
    private static void EnemyChurn() { var f = new Fixture(); Check(f.Capture(roots: () => f.Roots with { Count = 999, Array = 0x22220000 }).Status == "confirmed", "global enemy churn does not change the local history session"); }
    private static void ChangingCounter()
    {
        var f = new Fixture(); var count = 0; byte[] Read(uint address, int size) { if (address == Fixture.ArrayData && ++count == 2) f.Draws(7); return f.Read(address, size); }
        Unknown(f.Capture(Read));
    }
    private static void ChangingCategory()
    {
        var f = new Fixture(); var count = 0; var target = f.Children["h003"] + 0x18;
        byte[] Read(uint address, int size) { if (address == target && ++count == 2) f.Write(target, 5); return f.Read(address, size); }
        Unknown(f.Capture(Read));
    }
    private static void ChangingTable()
    {
        var f = new Fixture(); f.Copy(Fixture.HashManager, Fixture.HashManager + 0x1000, 0x30); var count = 0;
        byte[] Read(uint address, int size) { if (address == f.Roots.Players + 0x404 && ++count == 2) f.Write(address, Fixture.HashManager + 0x1000); return f.Read(address, size); }
        Unknown(f.Capture(Read));
    }
    private static void Partial()
    {
        var f = new Fixture(); f.Value("kM", 1); var value = f.Capture();
        Check(value.Status == "partial" && value.DrawCount == 6 && value.CorrectionInterval == 2 && value.CommonAcquiredCounts.Count == 0 && value.Source.SampleCount == 2,
            "history table failure does not fabricate category counts or destroy independently read kL");
        f.Write(f.Nodes["kM"] + 0x1C, 4); Check(f.Capture().Status == "partial", "hashtable global must have handle type");
    }
    private static void Agents()
    {
        foreach (var mutation in new Action<Fixture>[] {
            f => f.Write(Fixture.Agent + 0x18, 99), f => f.Write(Fixture.Agent + 0x20, 1),
            f => f.Write(Fixture.Agent + 0x0C, 0), f => f.Write(Fixture.AgentSlots + 8, 0),
            f => f.Write(Fixture.Reference + 0x24, 0), f => f.Value("kM", 0xFFFFFFF)
        }) { var f = new Fixture(); mutation(f); var state = f.Capture(); Check(state.Status == "partial" && state.CommonAcquiredCounts.Count == 0, "invalid or reused hashtable handles cannot expose history"); }
    }
    private static void Hashes()
    {
        var f = new Fixture(); f.Write(Fixture.Parent + 0x40, 2); Check(f.Capture().Status == "partial", "non power-of-two child mask fails closed");
        f = new Fixture(); var child = f.Children["h003"]; f.Write(child + 8, child); Check(f.Capture().Status == "partial", "cyclic integer hash chain is bounded");
        f = new Fixture(); f.Write(f.ParentBucket, 3); Check(f.Capture().Status == "partial", "unaligned integer-chain step is rejected");
    }
    private static void Globals()
    {
        var f = new Fixture(); f.Write(f.Nodes["kL"] + 0x1C, 4); Unknown(f.Capture());
        f = new Fixture(); f.RemoveGlobal("kL"); Unknown(f.Capture());
        f = new Fixture(); f.Write(Fixture.ArrayHeader + 8, 0); Unknown(f.Capture());
        f = new Fixture(); f.Write(Fixture.Globals + 0x24, 2); Unknown(f.Capture());
    }
    private static void TypeDiagnostics()
    {
        var f = new Fixture(); f.Write(f.Nodes["kL"] + 0x1C, 13); var value = f.Capture(); Unknown(value);
        Check(value.Source.TypeMismatch is { Name: "kL", ExpectedType: 9, ActualType: 13, ActualValueTypeTag: 9 } && value.Source.TypeMismatch.AllowedValueTypeTags.SequenceEqual(new uint[] { 9 }), "counter type errors keep the name and observed native metadata");
        f = new Fixture(); f.Write(f.Nodes["kM"] + 0x1C, 4); value = f.Capture();
        Check(value.Status == "partial" && value.DrawCount == 6 && value.CommonAcquiredCounts.Count == 0 &&
            value.Source.TypeMismatch is { Name: "kM", ExpectedType: 7, ActualType: 4, ActualValueTypeTag: 7 } && value.Source.TypeMismatch.AllowedValueTypeTags.SequenceEqual(new uint[] { 0, 7 }), "a table-only mismatch keeps separately checked scalars and the original diagnostic");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Check(json.RootElement.GetProperty("source").GetProperty("globalTypeMismatch").GetProperty("name").GetString() == "kM", "the partial diagnostic is serialized for later live investigation");
        Check(new Fixture().Capture().Source.TypeMismatch is null, "diagnostics are capture-local");
    }
    private static void ScalarValueTags()
    {
        var f = new Fixture(); f.Write(f.Nodes["sZ"] + 0x18, 4); f.Write(f.Nodes["kM"] + 0x18, 7);
        Check(f.Capture().Status == "confirmed", "live assigned integer and handle tags are native declared types");
        f.Write(f.Nodes["sZ"] + 0x18, 0); Unknown(f.Capture());
        f = new Fixture(); f.Write(f.Nodes["kM"] + 0x18, 8); var value = f.Capture();
        Check(value.Status == "partial" && value.Source.TypeMismatch?.Name == "kM", "another scalar type is never accepted as a hashtable handle");
        f = new Fixture(); f.Bytes[Fixture.Base + 0x8CB01A] = 0; Unknown(f.Capture());
    }
    private static void Names()
    {
        var f = new Fixture(); f.AddGlobal("Kl", 9, Fixture.ArrayHeader); Check(f.Capture().Status == "confirmed", "hash collision does not ignore exact name case");
        f = new Fixture(); f.AddGlobal("kL", 9, Fixture.ArrayHeader); Unknown(f.Capture());
    }
    private static void Totals() { var f = new Fixture(); f.Draws(7); Unknown(f.Capture()); }
    private static void Bounds()
    {
        var f = new Fixture(); f.Draws(uint.MaxValue); Unknown(f.Capture());
        f = new Fixture(); f.Write(f.Children["h003"] + 0x18, uint.MaxValue); Check(f.Capture().Status == "partial", "negative category counts are not numeric observations");
    }
    private static void ReadErrors()
    {
        var f = new Fixture(); Unknown(f.Capture((_, size) => new byte[Math.Max(0, size - 1)]));
        Unknown(f.Capture((_, _) => throw new IOException("fixture")));
        Unknown(f.Capture((_, _) => throw new System.ComponentModel.Win32Exception(5)));
    }
    private static void NoCache()
    {
        var a = new Fixture(); Check(a.Capture().DrawCount == 6, "first state");
        var b = new Fixture(1); b.Draws(0); b.Write(b.ParentBucket + 8, 0); var state = b.Capture();
        Check(state.Status == "confirmed" && state.LocalPlayer == 1 && state.DrawCount == 0, "second player has a separate parent-key history");
        a.Value("kM", 1); Check(a.Capture().Status == "partial", "valid prior counts are never cached over a failed observation");
    }
    private static void Serialization()
    {
        using var value = JsonDocument.Parse(JsonSerializer.Serialize(GameRandomState.Unknown("fixture")));
        Check(value.RootElement.GetProperty("drawCount").ValueKind == JsonValueKind.Null && !value.RootElement.GetProperty("commonAcquiredCounts").EnumerateObject().Any(), "unknown is not zero history");
        var zero = new Fixture(); zero.Draws(0); zero.Write(zero.ParentBucket + 8, 0);
        Check(zero.Capture().CommonAcquiredCounts.Count == 9, "known zero retains all nine observed keys");
    }
    private static void Unknown(GameRandomState.State value) => Check(value.Status == "unknown" && value.DrawCount is null && value.CommonAcquiredCounts.Count == 0, "unknown publishes no partial counters");
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); }

    private sealed class Fixture
    {
        internal const uint Base = 0x60000000, Environment = 0x20000000, Instance = 0x21000000, Vms = 0x22000000, Vm = 0x23000000,
            Globals = 0x24000000, Buckets = 0x25000000, Player = 0x26000000, ArrayHeader = 0x27000000, ArrayData = 0x28000000,
            Handles = 0x35000000, JassSlots = 0x36000000, Reference = 0x37000000, NativeHandles = 0x38000000,
            AgentSlots = 0x39000000, Agent = 0x3A000000, HashManager = 0x40000000, TableBuckets = 0x41000000,
            Table = 0x42000000, ParentBuckets = 0x43000000, Parent = 0x44000000, ChildBuckets = 0x45000000;
        internal readonly Dictionary<uint, byte> Bytes = new();
        internal readonly Dictionary<string, uint> Nodes = new(StringComparer.Ordinal), Children = new(StringComparer.Ordinal);
        internal readonly Roots Roots;
        private uint nextGlobal = 0x30000000, nextChild = 0x46000000;
        internal uint ParentBucket => ParentBuckets + ((uint)Roots.LocalPlayer & 15) * 12;
        internal Fixture(int player = 0)
        {
            Roots = new(0x31000000, 0x32000000, 0x33000000, 0x34000000, 100, player);
            foreach (var (rva, data) in GameProgression.NativeSignatures(Base).Concat(GameRandomState.NativeSignatures(Base))) Put(Base + rva, data);
            Write(Base + 0xD0DEF8, Environment); Write(Environment + 0x14, Instance); Write(Instance + 0x90, Vms); Write(Vms + 4, Vm); Write(Vm + 0x285C, Globals);
            Fill(Globals, 0x28); Write(Globals + 0x1C, Buckets); Write(Globals + 0x24, 63); InitBuckets(Buckets, 64);
            Write(Roots.Players + 0x58 + (uint)player * 4, Player); Bytes[Player + 0x30] = (byte)player;
            Fill(ArrayHeader, 16); Write(ArrayHeader + 8, 4); Write(ArrayHeader + 12, ArrayData); for (uint i = 0; i < 4; i++) Write(ArrayData + i * 4, 0);
            Draws(6); AddGlobal("kL", 9, ArrayHeader); AddGlobal("sZ", 4, 2); AddGlobal("kM", 7, 0x10000A);
            Write(Base + 0xD305E0, Roots.Players); Write(Roots.Players + 0x1C, Handles); Write(Handles + 0x19C, JassSlots);
            Write(JassSlots + 10 * 12 + 4, Reference); Write(Reference + 0x0C, 1); Write(Reference + 0x10, 17);
            Write(Reference + 0x24, 5);
            Write(Base + 0xD30448, NativeHandles); Write(NativeHandles + 0x1C, 2); Write(NativeHandles + 0x0C, AgentSlots);
            Write(AgentSlots + 8, 0xFFFFFFFE); Write(AgentSlots + 12, Agent); Write(Agent + 0x18, 17); Write(Agent + 0x20, 0); Write(Agent + 0x0C, 0x2B61676C); Write(Agent + 0x24, 5);
            Write(Roots.Players + 0x404, HashManager); Fill(HashManager, 0x30); Write(HashManager + 0x28, 15); Write(HashManager + 0x20, TableBuckets); InitBuckets(TableBuckets, 16);
            Fill(Table, 0x48); Write(Table + 4, 5); Write(Table + 0x18, 5); Write(TableBuckets + 5 * 12 + 8, Table);
            Write(Table + 0x40, 15); Write(Table + 0x38, ParentBuckets); InitBuckets(ParentBuckets, 16);
            Fill(Parent, 0x48); Write(Parent + 4, (uint)player); Write(Parent + 0x18, (uint)player); Write(ParentBucket + 8, Parent);
            Write(Parent + 0x40, 15); Write(Parent + 0x38, ChildBuckets); InitBuckets(ChildBuckets, 16);
            foreach (var rawcode in GameRandomState.CommonRawcodes) AddChild(rawcode, rawcode == "h003" ? 4u : rawcode == "h002" ? 2u : 0);
        }
        internal void InitBuckets(uint address, int count) { Fill(address, count * 12); for (uint i = 0; i < count; i++) Write(address + i * 12, 4); }
        internal uint GlobalBucket(string name) => Buckets + (GameProgression.NameHash(name) & 63) * 12;
        internal void AddGlobal(string name, uint type, uint value)
        {
            var node = nextGlobal; nextGlobal += 0x100; Fill(node, 0x90); var key = node + 0x50; Put(key, Encoding.ASCII.GetBytes(name + "\0"));
            var bucket = GlobalBucket(name); Write(node, GameProgression.NameHash(name)); Write(node + 8, U32(bucket + 8)); Write(node + 0x14, key);
            Write(node + 0x18, type); Write(node + 0x1C, type); Write(node + 0x20, value); Write(bucket + 8, node); Nodes[name] = node;
        }
        internal void RemoveGlobal(string name)
        {
            // 테스트에서 제거하는 kL/sZ는 원래 bucket의 유일한 항목이다.
            Write(GlobalBucket(name) + 8, 0);
        }
        internal void AddChild(string name, uint value)
        {
            var code = ((uint)name[0] << 24) | ((uint)name[1] << 16) | ((uint)name[2] << 8) | name[3];
            var node = nextChild; nextChild += 0x100; Fill(node, 0x20); var bucket = ChildBuckets + (code & 15) * 12;
            Write(node, code); Write(node + 0x14, code); Write(node + 0x18, value); Write(node + 8, U32(bucket + 8)); Write(bucket + 8, node); Children[name] = node;
        }
        internal void Value(string name, uint value) => Write(Nodes[name] + 0x20, value);
        internal void Draws(uint value) => Write(ArrayData + (uint)Roots.LocalPlayer * 4, value);
        internal void Fill(uint address, int size) { for (var index = 0; index < size; index++) Bytes[address + (uint)index] = 0; }
        internal void Put(uint address, byte[] data) { for (var index = 0; index < data.Length; index++) Bytes[address + (uint)index] = data[index]; }
        internal void Write(uint address, uint value) => Put(address, BitConverter.GetBytes(value));
        internal uint U32(uint address) => BitConverter.ToUInt32(Read(address, 4));
        internal byte[] Read(uint address, int size) { var data = new byte[size]; for (var index = 0; index < size; index++) if (!Bytes.TryGetValue(address + (uint)index, out data[index])) throw new IOException("Unexpected random fixture address"); return data; }
        internal void Copy(uint source, uint destination, int size) => Put(destination, Read(source, size));
        internal GameRandomState.State Capture(Func<uint, int, byte[]>? reader = null, Func<Roots?>? roots = null) =>
            GameRandomState.CaptureForTests(reader ?? Read, roots ?? (() => Roots), Roots, Base, GameProgression.SupportedVersion, GameProgression.SupportedMapHash);
    }
}
