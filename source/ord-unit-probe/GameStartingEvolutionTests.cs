using System.ComponentModel;
using System.Text;
using System.Text.Json;

static class GameStartingEvolutionTests
{
    internal static void Run()
    {
        (string Name, Action Test)[] tests = [
            ("local neutral result is already fixed while charging", Known),
            ("all32 exact map pool results remain distinct", Pool),
            ("152 kills enter activation before actual delivery", Activating),
            ("cleared completed slots never duplicate actual inventory", Completed),
            ("unstarted and cleanup states are not future results", Empty),
            ("native lazy arrays and unwritten indices have verified zero defaults", LazyArrays),
            ("unsupported map version player and native layout fail closed", Unsupported),
            ("wrong global declared or current-value types are rejected", Types),
            ("array lengths values and handles are bounded", Bounds),
            ("only the exact local player's pending neutral unit is used", Ownership),
            ("stale references and native agents do not identify units", Stale),
            ("world membership uses the unit reference instead of generic agent", World),
            ("changing progress and result never form a mixed observation", Changing),
            ("all local session boundaries revoke pending forecasts", Sessions),
            ("enemy list churn does not change the local result", EnemyChurn),
            ("failed and short reads do not fabricate a result", ReadFailures),
            ("serialization preserves unknown and completed quantities", Serialization)
        ];
        foreach (var (name, test) in tests)
            try { test(); } catch (Exception error) { throw new Exception("Starting evolution test failed: " + name, error); }
        Console.WriteLine($"PASS: starting evolution {tests.Length}/{tests.Length} groups; exact map/local neutral handle,152-kill lifecycle and no game input.");
    }
    private static void Known()
    {
        var state = new Fixture().Capture();
        Check(state.Status == "confirmed" && state.LocalPlayer == 0 && state.ResultRawcode == "h00B" && state.KillCount == 88 && state.TargetKills == 152 && state.Phase == "charging" && state.PendingCount == 1, "the pending special result comes from sh, not from random probability or current stock");
        Check(state.Source.ReadOnly && state.Source.SampleCount == 2 && state.Source.WorldMembershipVerified && state.Source.ExpectedNeutralOwner == 15 && state.Source.MapSha256 == GameProgression.SupportedMapHash, "identity has exact map, local globals and world evidence");
    }
    private static void Pool()
    {
        Check(GameStartingEvolution.SpecialPool.Length == 32 && GameStartingEvolution.SpecialPool.Distinct(StringComparer.Ordinal).Count() == 32, "the equal-weight pool has32 exact identities");
        foreach (var code in GameStartingEvolution.SpecialPool) { var f = new Fixture(); f.Code(code); Check(f.Capture().ResultRawcode == code, "each native pool code retains exact case"); }
        var bad = new Fixture(); bad.Code("h001"); Unknown(bad.Capture());
    }
    private static void Activating()
    {
        var f = new Fixture(); f.Progress(152, true); var state = f.Capture();
        Check(state.Status == "confirmed" && state.Phase == "activating" && state.PendingCount == 1 && state.KillCount == 152, "the transition timer may still own the neutral pending object");
        f.Progress(151, true); Unknown(f.Capture()); f.Progress(152, false); Unknown(f.Capture());
    }
    private static void Completed()
    {
        var f = new Fixture(); f.Progress(152, true); f.Write(f.Data["sh"], 0); var state = f.Capture();
        Check(state.Status == "confirmed" && state.Phase == "completed" && state.ResultRawcode is null && state.PendingCount == 0, "completed state has no phantom additional owned unit");
    }
    private static void Empty()
    {
        var f = new Fixture(); f.Progress(0, false); f.Write(f.Data["sh"], 0);
        Check(f.Capture() is { Status: "confirmed", Phase: "not_started", ResultRawcode: null, PendingCount: 0 }, "unstarted slots have no result");
        f.Progress(0, true); Unknown(f.Capture()); f.Progress(100, false); Unknown(f.Capture());
    }
    private static void LazyArrays()
    {
        var f = new Fixture(); f.Write(f.Nodes["sl"] + 0x20, 0);
        Check(f.Capture() is { Status: "confirmed", Phase: "charging", KillCount: 88, ResultRawcode: "h00B", PendingCount: 1 }, "the real pre152 lazy boolean array is false, not a corrupt pointer");
        f.Write(f.Nodes["si"] + 0x20, 0); Check(f.Capture().KillCount == 0, "an unallocated integer array has the native zero default");
        f.Write(f.Nodes["sh"] + 0x20, 0); Check(f.Capture() is { Status: "confirmed", Phase: "not_started", PendingCount: 0 }, "no allocated result still promises no future unit");
        f = new Fixture(1); f.Write(f.Headers["sl"] + 8, 1); f.Write(f.Headers["sl"] + 12, 0);
        Check(f.Capture().Phase == "charging", "a not-yet-written local index uses the native bounds default without reading a missing data buffer");
        f = new Fixture(); f.Write(f.Nodes["sl"] + 0x20, 1); var bad = f.Capture(); Unknown(bad);
        Check(bad.Source.Failure is { Stage: "global_array_header:sl", ObservedValue: 1 }, "a nonzero invalid pointer remains rejected with a precise diagnostic");
        f = new Fixture(); f.Bytes[Fixture.Base + 0x8D17B0] = 0; Unknown(f.Capture());
    }
    private static void Unsupported()
    {
        var f = new Fixture(); byte[] Forbidden(uint _, int __) => throw new Exception("Unsupported state read memory");
        Unknown(GameStartingEvolution.CaptureForTests(Forbidden, () => f.Roots, f.Roots, Fixture.Base, "other", GameProgression.SupportedMapHash));
        Unknown(GameStartingEvolution.CaptureForTests(Forbidden, () => f.Roots, f.Roots, Fixture.Base, GameProgression.SupportedVersion, "other"));
        Unknown(GameStartingEvolution.CaptureForTests(Forbidden, () => f.Roots, f.Roots with { LocalPlayer = 4 }, Fixture.Base, GameProgression.SupportedVersion, GameProgression.SupportedMapHash));
        f.Bytes[Fixture.Base + 0x2368A0] = 0; Unknown(f.Capture());
    }
    private static void Types()
    {
        foreach (var name in new[] { "sh", "si", "sl" }) { var f = new Fixture(); f.Write(f.Nodes[name] + 0x1C, 4); Unknown(f.Capture()); f = new Fixture(); f.Write(f.Nodes[name] + 0x18, 0); Unknown(f.Capture()); }
        var exact = new Fixture(); exact.Add("SH", 12, 0); Check(exact.Capture().ResultRawcode == "h00B", "case-normalized hash is not an identity match");
        exact.Add("sh", 12, Fixture.Handle); Unknown(exact.Capture());
    }
    private static void Bounds()
    {
        foreach (var value in new uint[] { 153, uint.MaxValue }) { var f = new Fixture(); f.Write(f.Data["si"], value); Unknown(f.Capture()); }
        var a = new Fixture(); a.Write(a.Data["sl"], 2); Unknown(a.Capture());
        a = new Fixture(); a.Write(a.Nodes["sh"] + 0x20, 0); Unknown(a.Capture());
        a = new Fixture(); a.Write(a.Headers["sh"] + 8, 8193); Unknown(a.Capture());
        a = new Fixture(); a.Write(a.Data["sh"], 1); Unknown(a.Capture());
    }
    private static void Ownership()
    {
        foreach (var owner in new uint[] { 0, 1, 12 }) { var f = new Fixture(); f.Write(Fixture.Reference + 0x58, owner); Unknown(f.Capture()); }
        var other = new Fixture(1); Check(other.Capture().LocalPlayer == 1 && other.Capture().ResultRawcode == "h00B", "local-player array index is respected");
        other.Write(other.Data["sh"], 0); other.Progress(0, false); Check(other.Capture().PendingCount == 0, "another player's slot is not substituted");
    }
    private static void Stale()
    {
        foreach (var mutation in new Action<Fixture>[] { f => f.Write(Fixture.Agent + 0x18, 18), f => f.Write(Fixture.Agent + 0x20, 1), f => f.Write(Fixture.Agent + 0x0C, 0), f => f.Write(Fixture.AgentSlots + 8, 0), f => f.Write(Fixture.Reference + 0x0C, 999) }) { var f = new Fixture(); mutation(f); Unknown(f.Capture()); }
    }
    private static void World()
    {
        var f = new Fixture(); f.Write(f.Roots.Array, Fixture.Agent); Unknown(f.Capture());
        f = new Fixture(); f.Write(f.Roots.Array, 0); Unknown(f.Capture());
        Check(new Fixture().Capture().Status == "confirmed", "unit reference flags at+20 are not the generic agent's liveness field");
    }
    private static void Changing()
    {
        var f = new Fixture(); var reads = 0; byte[] Read(uint address, int size) { if (address == f.Data["si"] && ++reads == 2) f.Write(address, 89); return f.Read(address, size); }
        Unknown(f.Capture(Read));
        f = new Fixture(); reads = 0; byte[] CodeRead(uint address, int size) { if (address == Fixture.Reference + 0x30 && ++reads == 2) f.Code("h00Q"); return f.Read(address, size); }
        Unknown(f.Capture(CodeRead));
    }
    private static void Sessions()
    {
        foreach (var boundary in new[] { 1, 2, 3, 4, 5 }) { var f = new Fixture(); var calls = 0; Unknown(f.Capture(roots: () => ++calls >= boundary ? f.Roots with { Players = f.Roots.Players + 4 } : f.Roots)); }
    }
    private static void EnemyChurn()
    {
        var f = new Fixture(); f.Write(Fixture.OtherWorld, Fixture.Reference); f.Write(Fixture.OtherWorld + 4, 0);
        Check(f.Capture(roots: () => f.Roots with { Count = 2, Array = Fixture.OtherWorld }).Status == "confirmed", "current world membership may move to a new enemy-list allocation");
    }
    private static void ReadFailures()
    {
        var f = new Fixture(); Unknown(f.Capture((_, size) => new byte[Math.Max(0, size - 1)]));
        foreach (var error in new Exception[] { new IOException(), new Win32Exception(5), new OverflowException() }) Unknown(f.Capture((_, _) => throw error));
    }
    private static void Serialization()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(GameStartingEvolution.Unknown("test")));
        Check(doc.RootElement.GetProperty("resultRawcode").ValueKind == JsonValueKind.Null && doc.RootElement.GetProperty("killCount").ValueKind == JsonValueKind.Null && doc.RootElement.GetProperty("pendingCount").GetInt32() == 0, "unknown does not become a guaranteed future unit");
    }
    private static void Unknown(GameStartingEvolution.State state) => Check(state.Status == "unknown" && state.ResultRawcode is null && state.PendingCount == 0 && state.KillCount is null, "unknown cannot promise a future special result");
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private sealed class Fixture
    {
        internal const uint Base = 0x60000000, Environment = 0x20000000, Instance = 0x21000000, Vms = 0x22000000, Vm = 0x23000000, Globals = 0x24000000, Buckets = 0x25000000, Player = 0x26000000, Handles = 0x35000000, Slots = 0x36000000, Reference = 0x37000000, NativeHandles = 0x38000000, AgentSlots = 0x39000000, Agent = 0x3A000000, OtherWorld = 0x34001000, Handle = 0x10000A;
        internal Dictionary<uint, byte> Bytes = new();
        internal Dictionary<string, uint> Nodes = new(StringComparer.Ordinal), Data = new(StringComparer.Ordinal), Headers = new(StringComparer.Ordinal);
        internal Roots Roots;
        private uint next = 0x30000000, nextArray = 0x27000000;
        internal Fixture(int player = 0)
        {
            Roots = new(0x31000000, 0x32000000, 0x33000000, 0x34000000, 1, player);
            foreach (var (rva, bytes) in GameProgression.NativeSignatures(Base).Concat(GameStartingEvolution.NativeSignatures())) Put(Base + rva, bytes);
            Write(Base + 0xD0DEF8, Environment); Write(Environment + 0x14, Instance); Write(Instance + 0x90, Vms); Write(Vms + 4, Vm); Write(Vm + 0x285C, Globals);
            Write(Globals + 0x1C, Buckets); Write(Globals + 0x24, 63); Fill(Buckets, 64 * 12); for (uint i = 0; i < 64; i++) Write(Buckets + i * 12, 4);
            Write(Base + 0xD305E0, Roots.Players); Write(Roots.Players + 0x58 + (uint)player * 4, Player); Put(Player + 0x30, [(byte)player]);
            Add("sh", 12, Handle); Add("si", 9, 88); Add("sl", 13, 0);
            Write(Roots.Players + 0x1C, Handles); Write(Handles + 0x19C, Slots); Write(Slots + 10 * 12 + 4, Reference);
            Write(Reference + 0x0C, 1); Write(Reference + 0x10, 17); Write(Reference + 0x20, 0x64E); Code("h00B"); Write(Reference + 0x58, 15);
            Write(Base + 0xD30448, NativeHandles); Write(NativeHandles + 0x1C, 2); Write(NativeHandles + 0x0C, AgentSlots);
            Write(AgentSlots + 8, 0xFFFFFFFE); Write(AgentSlots + 12, Agent); Write(Agent + 0x18, 17); Write(Agent + 0x20, 0); Write(Agent + 0x0C, 0x2B61676C);
            Write(Roots.Array, Reference);
        }
        internal void Add(string name, uint type, uint value)
        {
            var node = next; next += 0x100; var header = nextArray; nextArray += 0x1000; Fill(node, 0x24); Put(node + 0x50, Encoding.ASCII.GetBytes(name + "\0"));
            var hash = GameProgression.NameHash(name); var bucket = Buckets + (hash & 63) * 12;
            Write(node, hash); Write(node + 8, U32(bucket + 8)); Write(node + 0x14, node + 0x50); Write(node + 0x18, type); Write(node + 0x1C, type); Write(node + 0x20, header); Write(bucket + 8, node);
            Write(header + 8, 4); Write(header + 12, header + 0x100); Fill(header + 0x100, 16); Write(header + 0x100 + (uint)Roots.LocalPlayer * 4, value);
            Nodes[name] = node; Headers[name] = header; Data[name] = header + 0x100 + (uint)Roots.LocalPlayer * 4;
        }
        internal void Progress(int kills, bool activating) { Write(Data["si"], (uint)kills); Write(Data["sl"], activating ? 1u : 0u); }
        internal void Code(string code) => Put(Reference + 0x30, Encoding.ASCII.GetBytes(code).Reverse().ToArray());
        internal void Fill(uint address, int size) { for (var i = 0; i < size; i++) Bytes[address + (uint)i] = 0; }
        internal void Put(uint address, byte[] bytes) { for (var i = 0; i < bytes.Length; i++) Bytes[address + (uint)i] = bytes[i]; }
        internal void Write(uint address, uint value) => Put(address, BitConverter.GetBytes(value));
        internal uint U32(uint address) => BitConverter.ToUInt32(Read(address, 4));
        internal byte[] Read(uint address, int size) { var bytes = new byte[size]; for (var i = 0; i < size; i++) if (!Bytes.TryGetValue(address + (uint)i, out bytes[i])) throw new IOException("Unexpected evolution fixture address"); return bytes; }
        internal GameStartingEvolution.State Capture(Func<uint, int, byte[]>? read = null, Func<Roots?>? roots = null) => GameStartingEvolution.CaptureForTests(read ?? Read, roots ?? (() => Roots), Roots, Base, GameProgression.SupportedVersion, GameProgression.SupportedMapHash);
    }
}
