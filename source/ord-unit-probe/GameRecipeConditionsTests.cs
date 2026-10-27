using System.Text.Json;

static class GameRecipeConditionsTests
{
    public static void Run()
    {
        (string Name, Action Run)[] tests = [
            ("normal map permits the KING predicate and an unmarked exact P7 target", Normal),
            ("native A908 marks only the exact UBAN target", Banned),
            ("hidden display pages still provide ban evidence", HiddenDisplay),
            ("an absent display is unknown rather than allowed", MissingDisplay),
            ("navigation level two blocks top crafting", MartialLaw),
            ("navigation level one without a known historical counter stays unknown", NavigationUnknown),
            ("navigation and an active owned top marker provide a blocking sufficient condition", OwnedTop),
            ("dead and explicitly excluded top units are not a historical counter", ExcludedTop),
            ("ambiguous navigation never grants permission", MultipleNavigation),
            ("unrelated P7 objects and non-navigation dead owned objects need no ability reads", UnrelatedObjects),
            ("enemy-only array and count churn preserves matching relevant markers", EnemyChurn),
            ("a new relevant display is not silently accepted between scans", RelevantChurn),
            ("a changing ban level or object generation stays unknown", ChangingMarker),
            ("a per-target invalid ability preserves other verified predicates", PartialAbility),
            ("stale native handles cannot become marker absence", StaleHandle),
            ("partial world reads never prove marker absence", PartialWorld),
            ("each session boundary is independently checked", SessionChanges),
            ("invalid world headers and changing handle roots fail closed", InvalidHeaders),
            ("unsupported map version and local player never read native memory", Unsupported),
            ("serialized known false differs from an unknown missing key", Serialization)
        ];
        foreach (var test in tests)
        {
            try { test.Run(); }
            catch (Exception error) { throw new Exception("Native recipe-condition test failed: " + test.Name, error); }
        }
        Console.WriteLine($"PASS: read-only UBAN/KING native markers {tests.Length}/{tests.Length} cases; no game input or writes.");
    }

    private static void Normal()
    {
        var f = new Fixture();f.Add("h07I", 7);
        var result = f.Capture();
        Check(result.Status == "confirmed" && result.LocalPlayer == 0 && result.UnitBans["h07I"] == false && result.King.Status == "allowed", "a complete two-scan normal map confirms both predicates");
        Check(result.Source.ReadOnly && result.Source.SampleCount == 2 && result.Source.ReadErrors == 0 && !result.Source.Q7DirectlyRead, "source does not claim direct JASS-counter access");
    }
    private static void Banned()
    {
        var f = new Fixture();f.Add("h07I", 7, "A908");f.Add("h04E", 7);
        var result = f.Capture();Check(result.UnitBans["h07I"] && !result.UnitBans["h04E"] && !result.UnitBans.ContainsKey("H07I"), "rawcodes remain case-sensitive and bans do not spread");
    }
    private static void HiddenDisplay()
    {
        var f = new Fixture();var display = f.Add("h07I", 7, "A908");f.Write(display + 0x20, 1);f.Write(display + 0x5C, 0x100);
        Check(f.Capture().UnitBans["h07I"], "the static hidden P7 page is not filtered as a dead combat unit");
    }
    private static void MissingDisplay()
    {
        var f = new Fixture();var result = f.Capture();Check(result.King.Status == "allowed" && !result.UnitBans.ContainsKey("h07I"), "complete nav absence does not manufacture a UBAN display");
    }
    private static void MartialLaw()
    {
        var f = new Fixture();var nav = f.Add("h0C8", 0, "A0II", 2);f.Write(nav + 0x5C, 0x100);
        var result = f.Capture();Check(result.King.Status == "blocked" && result.Source.KingMarkerCount == 1, "a hidden permanent navigation marker still blocks");
    }
    private static void NavigationUnknown()
    {
        var f = new Fixture();f.Add("h0C8", 0, "A0II");
        var result = f.Capture();Check(result.Status == "partial" && result.King.Status == "unknown" && result.King.Reason == "historical_top_counter_not_directly_read", "current top count zero does not establish Q7 zero");
    }
    private static void OwnedTop()
    {
        foreach (var marker in new[] { "A905", "A906", "A907", "A909" })
        {
            var f = new Fixture();f.Add("h0C8", 0, "A0II");f.Add("h07I", 0, marker);
            Check(f.Capture().King.Status == "blocked", "each direct Bhl top marker provides only a blocking sufficient condition");
        }
        var other = new Fixture();other.Add("h0C8", 0, "A0II");other.Add("h07I", 1, "A907");
        Check(other.Capture().King.Status == "unknown", "another player's top unit is not this player's historical counter");
    }
    private static void ExcludedTop()
    {
        var f = new Fixture();f.Add("h0C8", 0, "A0II");f.Add("H0BK", 0, "A907");f.Add("h057", 0, "A907");
        var dead = f.Add("h07I", 0, "A907");f.Write(dead + 0x20, 1);
        Check(f.Capture().King.Status == "unknown", "JASS exclusions and dead objects are not used as a false top-count proof");
    }
    private static void MultipleNavigation()
    {
        var f = new Fixture();f.Add("h0C8", 0, "A0II");f.Add("h0C8", 0, "A0II", 2);
        Check(f.Capture().King.Status == "unknown", "ambiguous navigation mirrors cannot be interpreted as permission");
    }
    private static void UnrelatedObjects()
    {
        var f = new Fixture();f.Add("h07I", 7);var decor = f.Add("h000", 7);var dead = f.Add("h001", 0);
        f.Remove(decor, 0x30);f.Remove(dead, 0x30); // owner/rawcode remain readable, irrelevant full headers do not.
        f.Remove(decor + 0x1DC, 8);f.Remove(dead + 0x1DC, 8);
        var result = f.Capture();Check(result.UnitBans["h07I"] == false && result.King.Status == "allowed" && result.Source.ReadErrors == 0, "unrelated headers and ability chains cannot poison native gates");
    }
    private static void EnemyChurn()
    {
        var f = new Fixture();f.Add("h07I", 7);var original = f.Roots;Roots? ReadRoots()
        {
            if (++f.RootReads == 2) { f.Add("o017", 6);f.SetArray(0x23010000); }
            return f.Roots;
        }
        var result = f.Capture(roots: ReadRoots, initial: original);
        Check(result.Status == "confirmed" && !result.UnitBans["h07I"] && result.King.Status == "allowed" && result.Source.WorldReadErrors == 0,
            "a new array and enemy count with the same session uses middle roots and keeps relevant gates known");
        var g = new Fixture();g.Add("h07I", 7);var arrayReads = 0;
        byte[] Read(uint address, int size)
        {
            var data = g.Read(address, size);
            if (address == g.Roots.Array && ++arrayReads == 1) g.Write(g.Roots.Array, 0); // first copied array remains valid; unrelated removal follows.
            return data;
        }
        var second = g.Capture(Read);Check(second.King.Status == "allowed" && !second.UnitBans["h07I"], "a changed unrelated array slot is not mistaken for a session boundary");
    }
    private static void RelevantChurn()
    {
        var f = new Fixture();f.Add("h07I", 7);Roots? Roots()
        {
            if (++f.RootReads == 2) f.Add("h07I", 7, "A908");
            return f.Roots;
        }
        Check(!f.Capture(roots: Roots).UnitBans.ContainsKey("h07I"), "a newly added relevant marker makes this target unknown until stable");
    }
    private static void ChangingMarker()
    {
        foreach (var generationChange in new[] { false, true })
        {
            var f = new Fixture();var display = f.Add("h07I", 7, "A908");Roots? Roots()
            {
                if (++f.RootReads == 2)
                {
                    if (generationChange) f.Write(display + 0x10, 99);
                    else f.Write(f.Abilities[display] + 0x50, 1);
                }
                return f.Roots;
            }
            Check(!f.Capture(roots: Roots).UnitBans.ContainsKey("h07I"), "the same address is not sufficient after marker or generation changes");
        }
    }
    private static void PartialAbility()
    {
        var f = new Fixture();var broken = f.Add("h07I", 7, "A908");f.Add("h04E", 7);f.Remove(f.Abilities[broken], 0x54);
        var result = f.Capture();Check(result.Status == "partial" && !result.UnitBans.ContainsKey("h07I") && !result.UnitBans["h04E"] && result.King.Status == "allowed" && result.Source.AbilityReadErrors > 0, "only the unreadable predicate remains unknown");
    }
    private static void StaleHandle()
    {
        var f = new Fixture();var display = f.Add("h07I", 7, "A908");f.Write(f.Records[display] + 0x18, 999);
        Check(!f.Capture().UnitBans.ContainsKey("h07I"), "a stale serial cannot be interpreted as A908 absent");
    }
    private static void PartialWorld()
    {
        var f = new Fixture();f.Add("h07I", 7);f.Remove(f.Enemy + 0x58, 4);Unknown(f.Capture());
        var g = new Fixture();g.Add("h07I", 7);byte[] Partial(uint address, int size) => address == g.Roots.Array ? new byte[size - 1] : g.Read(address, size);
        Unknown(g.Capture(Partial));
    }
    private static void SessionChanges()
    {
        for (var at = 1; at <= 3; at++)
        {
            var f = new Fixture();f.Add("h07I", 7);var boundary = at;
            Roots? Roots() => ++f.RootReads == boundary ? f.Roots with { Players = f.Roots.Players + 4 } : f.Roots;
            Unknown(f.Capture(roots: Roots));
        }
    }
    private static void InvalidHeaders()
    {
        var f = new Fixture();f.Write(f.Roots.Ui + 0x3BC, f.Roots.Table + 4);Unknown(f.Capture());
        var g = new Fixture();g.Write(g.Roots.Table + 0x604, 100001);Unknown(g.Capture());
        var h = new Fixture();var calls = 0;byte[] Read(uint address, int size)
        {
            if (address == Fixture.Base + 0xD30448 && ++calls == 2) h.Write(address, Fixture.Handles + 4);
            return h.Read(address, size);
        }
        Unknown(h.Capture(Read));
    }
    private static void Unsupported()
    {
        var f = new Fixture();var reads = 0;byte[] Read(uint _, int __) { reads++;throw new Exception("Unexpected native read."); }
        Unknown(f.Capture(Read, version: "1.27"));Unknown(f.Capture(Read, hash: "invalid"));
        Unknown(f.Capture(Read, initial: f.Roots with { LocalPlayer = 12 }));
        Check(reads == 0, "unsupported profiles fail before native reads");
    }
    private static void Serialization()
    {
        var f = new Fixture();f.Add("h07I", 7);using var json = JsonDocument.Parse(JsonSerializer.Serialize(f.Capture()));
        var root = json.RootElement;Check(root.GetProperty("unitBans").GetProperty("h07I").ValueKind == JsonValueKind.False && !root.GetProperty("unitBans").TryGetProperty("h04E", out _), "false means verified allowed; absence means unverified");
    }
    private static void Unknown(GameRecipeConditions.State state) => Check(state.Status == "unknown" && state.LocalPlayer is null && state.UnitBans.Count == 0 && state.King.Status == "unknown", "a failed complete read exposes no permission");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture
    {
        public const uint Base = 0x10000000, Handles = 0x24000000;
        private const uint Slots = 0x25000000;
        private readonly Dictionary<uint, byte> bytes = new();
        private readonly List<uint> units = new();
        public readonly Dictionary<uint, uint> Abilities = new(), Records = new();
        private int abilityCount;
        public int RootReads;
        public Roots Roots { get; private set; } = new(0x20000000, 0x21000000, 0x22000000, 0x23000000, 0, 0);
        public uint Enemy { get; }
        public Fixture()
        {
            Write(Base + 0xD30448, Handles);Write(Handles + 0x0C, Slots);Write(Handles + 0x1C, 0);
            Write(Roots.Ui + 0x3BC, Roots.Table);Enemy = Add("o017", 6);
        }
        public uint Add(string rawcode, uint owner, string? ability = null, int level = 1)
        {
            var address = checked(0x30000000u + (uint)units.Count * 0x1000);Fill(address, 0x200);
            Write(address, Base + 0x10000);Write(address + 0x0C, (uint)units.Count + 100);Write(address + 0x10, (uint)units.Count + 200);
            Write(address + 0x30, Code(rawcode));Write(address + 0x58, owner);Write(address + 0x1DC, uint.MaxValue);Write(address + 0x1E0, uint.MaxValue);
            if (ability is not null)
            {
                var index = (uint)abilityCount++;var serial = 1000 + index;
                var record = 0x26000000 + index * 0x100;var node = 0x27000000 + index * 0x100;
                Fill(record, 0x58);Fill(node, 0x54);Fill(Slots + index * 8, 8);
                Write(Slots + index * 8, 0xFFFFFFFE);Write(Slots + index * 8 + 4, record);Write(record + 0x18, serial);Write(record + 0x54, node);
                Write(node, Base + 0x20000);Write(node + 0x34, Code(ability));Write(node + 0x50, checked((uint)level - 1));
                Write(node + 0x24, uint.MaxValue);Write(node + 0x28, uint.MaxValue);
                Write(address + 0x1DC, index);Write(address + 0x1E0, serial);Write(Handles + 0x1C, (uint)abilityCount);
                Abilities[address] = node;Records[address] = record;
            }
            units.Add(address);SetArray(Roots.Array);return address;
        }
        public void SetArray(uint array)
        {
            Roots = Roots with { Array = array, Count = units.Count };
            for (var i = 0; i < units.Count; i++) Write(array + (uint)i * 4, units[i]);
            Write(Roots.Table + 0x604, (uint)units.Count);Write(Roots.Table + 0x608, array);
        }
        public byte[] Read(uint address, int size)
        {
            var result = new byte[size];for (var i = 0; i < size; i++)
                if (!bytes.TryGetValue(address + (uint)i, out result[i])) throw new IOException("Fixture read failed.");
            return result;
        }
        public void Write(uint address, uint value)
        { var data = BitConverter.GetBytes(value);for (var i = 0; i < data.Length; i++) bytes[address + (uint)i] = data[i]; }
        public void Remove(uint address, int size) { for (var i = 0; i < size; i++) bytes.Remove(address + (uint)i); }
        private void Fill(uint address, int size) { for (var i = 0; i < size; i++) bytes[address + (uint)i] = 0; }
        private static uint Code(string code) => ((uint)code[0] << 24) | ((uint)code[1] << 16) | ((uint)code[2] << 8) | code[3];
        public GameRecipeConditions.State Capture(Func<uint, int, byte[]>? read = null, Func<Roots?>? roots = null, Roots? initial = null,
            string? version = GameRecipeConditions.Version, string? hash = GameRecipeConditions.MapHash) =>
            GameRecipeConditions.CaptureForTests(read ?? Read, roots ?? (() => Roots), initial ?? Roots, Base, version, hash);
    }
}
