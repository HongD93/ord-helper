using System.ComponentModel;

static class GameResourcesTests
{
    public static void Run()
    {
        (string Name, Action Run)[] tests =
        [
            ("known raw amounts and public path", Known),
            ("fractional resources cannot overstate availability", Fractions),
            ("supported player endpoints", Players),
            ("unsupported version and player do not read", Unsupported),
            ("session transition clears both amounts", SessionChanged),
            ("resource transition clears both amounts", Changing),
            ("read failure is isolated", ReadFailure),
            ("supported numeric boundaries", Boundaries),
            ("bad pointers are rejected", InvalidPointer)
        ];
        foreach (var test in tests)
        {
            try { test.Run(); }
            catch (Exception error) { throw new Exception("Resource test failed: " + test.Name, error); }
        }
        Console.WriteLine($"PASS: read-only player resources {tests.Length}/{tests.Length} cases; no game input or writes.");
    }

    private static void Known()
    {
        var f = new Fixture(); var state = f.Capture();
        Check(state.Status == "confirmed" && state.Gold == 128511 && state.Wood == 57 && state.LocalPlayer == 0,
            "same-version raw values must retain their exact /10 interpretation");
        Check(state.Source.ReadOnly && !state.Source.HudCrossCheckPerformed && state.Source.SampleCount == 2 &&
            state.Source.RawWood == 570, "stable memory reads do not claim a fresh HUD cross-check");
    }
    private static void Fractions()
    {
        var f = new Fixture(gold: 1009, wood: 579);var state = f.Capture();
        Check(state.Gold == 100 && state.Wood == 57, "57.9 wood cannot fund a 58-wood action");
    }
    private static void Players()
    {
        foreach (var local in new[] { 0, 1, 11 })
        {
            var f = new Fixture(local, gold: 1230, wood: 70);var state = f.Capture();
            Check(state.Status == "confirmed" && state.LocalPlayer == local && state.Wood == 7,
                "source table offsets must address the local player, including P12");
        }
    }
    private static void Unsupported()
    {
        byte[] Forbidden(uint a, int size) => throw new Exception("Unsupported profiles must never read memory.");
        var roots = new Fixture().Roots;
        Unknown(GameResources.CaptureForTests(Forbidden, () => roots, roots, Fixture.Base, "1.26.0.6401"));
        Unknown(GameResources.CaptureForTests(Forbidden, () => roots, roots with { LocalPlayer = 12 }, Fixture.Base, GameResources.SupportedVersion));
        Unknown(GameResources.CaptureForTests(Forbidden, () => roots, roots with { LocalPlayer = -1 }, Fixture.Base, GameResources.SupportedVersion));
    }
    private static void SessionChanged()
    {
        for (var transition = 1; transition <= 3; transition++)
        {
            var f = new Fixture();var calls = 0;var at = transition;
            var state = GameResources.CaptureForTests(f.Read, () => ++calls >= at ? f.Roots with { Players = f.Roots.Players + 4 } : f.Roots,
                f.Roots, Fixture.Base, GameResources.SupportedVersion);
            Unknown(state);Check(state.Source.Reason == "session_changed", "each session check is enforced");
        }
    }
    private static void Changing()
    {
        var f = new Fixture();var count = 0;
        byte[] Read(uint address, int size)
        {
            if (address == f.WoodAddress && ++count == 2) return BitConverter.GetBytes(560);
            return f.Read(address, size);
        }
        Unknown(GameResources.CaptureForTests(Read, () => f.Roots, f.Roots, Fixture.Base, GameResources.SupportedVersion));
    }
    private static void ReadFailure()
    {
        var f = new Fixture();
        Unknown(GameResources.CaptureForTests((a, size) => throw new Win32Exception(299), () => f.Roots,
            f.Roots, Fixture.Base, GameResources.SupportedVersion));
        Unknown(GameResources.CaptureForTests((a, size) => new byte[3], () => f.Roots,
            f.Roots, Fixture.Base, GameResources.SupportedVersion));
    }
    private static void Boundaries()
    {
        var max = GameResources.MaximumSupportedAmount * 10;
        Check(new Fixture(gold: 0, wood: max).Capture().Wood == GameResources.MaximumSupportedAmount, "supported endpoints");
        Unknown(new Fixture(gold: -1).Capture());Unknown(new Fixture(wood: max + 1).Capture());
    }
    private static void InvalidPointer()
    {
        var f = new Fixture();f.Values[Fixture.Base + 0xD30448] = 0;
        Unknown(f.Capture());
        f = new Fixture();f.Values[0x2400000C] = 0xFFFFFFFF;
        Unknown(f.Capture());
    }
    private static void Unknown(GameResources.State state) => Check(state.Status == "unknown" && state.Gold is null &&
        state.Wood is null && state.LocalPlayer is null, "uncertain resources must fail closed independently");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture
    {
        internal const uint Base = 0x10000000;
        internal Roots Roots { get; }
        internal Dictionary<uint, uint> Values { get; } = new();
        internal uint WoodAddress { get; }
        internal Fixture(int local = 0, int gold = 1285110, int wood = 570)
        {
            uint[] offsets = [0x190, 0x1410, 0x26A0, 0x3920, 0x4BB0, 0x5E30, 0x70C0, 0x8350, 0x95D0, 0xA860, 0xBAE0, 0xCD70];
            Roots = new(0x20000000, 0x21000000, 0x22000000, 0x23000000, 1, local);
            Values[Base + 0xD30448] = 0x24000000;Values[0x2400000C] = 0x25000000;
            Values[0x2500000C] = 0x26000098;
            var goldAddress = 0x26000000 + offsets[local];WoodAddress = goldAddress + 0x80;
            Values[goldAddress] = unchecked((uint)gold);Values[WoodAddress] = unchecked((uint)wood);
        }
        internal byte[] Read(uint address, int size)
        {
            if (size != 4 || !Values.TryGetValue(address, out var value)) throw new IOException("Unexpected resource read.");
            return BitConverter.GetBytes(value);
        }
        internal GameResources.State Capture() => GameResources.CaptureForTests(Read, () => Roots, Roots, Base, GameResources.SupportedVersion);
    }
}
