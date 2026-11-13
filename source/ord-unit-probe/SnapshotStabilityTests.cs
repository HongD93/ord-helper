using System.Text.Json;

static class SnapshotStabilityTests
{
    internal static void Run()
    {
        var passed = 0;
        void Check(bool success, string name) { if (!success) throw new Exception("Snapshot stability: " + name); passed++; }
        var roots = new Roots(0x10000, 0x20000, 0x30000, 0x40000, 100, 0);
        Check(SnapshotStability.SameSession(roots with { Array = 0x50000, Count = 200 }, roots), "enemy growth and array relocation are not a new session");
        Check(!SnapshotStability.SameSession(roots with { Table = 0x60000 }, roots), "a new game table is rejected");
        Check(!SnapshotStability.SameSession(roots with { LocalPlayer = 1 }, roots), "owner changes are rejected");
        Check(!SnapshotStability.SameSession(null, roots), "missing roots are rejected");
        var ids = new HashSet<string>(["h001", "h002", "e018", "e0IX", "e01A", "e017", "e016", "e019", "e015"], StringComparer.Ordinal);
        var unit = new UnitObject(0x70000, "h001", 6, 0);
        var first = new OwnedData([unit], 0);
        Check(SnapshotStability.SameInventory(first, new([unit with { Flags20 = 14, Flags5c = 0x200 }], 0), ids), "attack and movement flags do not alter inventory");
        Check(SnapshotStability.SameInventory(first, new([unit, new(0x80000, "dumy", 6, 0)], 0), ids), "temporary uncounted effects do not alter inventory");
        var battleIds = new HashSet<string>(ids, StringComparer.Ordinal) { "h06O", "h0C5" };
        var toy = new UnitObject(0x81000, "h06O", 6, 0);
        var dummy = new UnitObject(0x82000, "h0C5", 6, 0);
        Check(UnitRules.IsNonIngredientCombatObject("h06O") && UnitRules.IsNonIngredientCombatObject("h0C5") &&
            !UnitRules.IsNonIngredientCombatObject("h002") && !UnitRules.IsNonIngredientCombatObject("e018"),
            "only the two native-proven noningredient combat rawcodes are classified");
        Check(SnapshotStability.SameInventory(first, new([unit, toy], 0), battleIds), "owned Sugar toy spawn is not a recipe inventory change even when catalog playable");
        Check(SnapshotStability.SameInventory(new([unit, toy], 0), first, battleIds), "owned Sugar toy expiration is not a recipe inventory change");
        Check(SnapshotStability.SameInventory(first, new([unit, dummy], 0), battleIds), "owned universal spell dummy spawn is not a recipe inventory change");
        Check(SnapshotStability.SameInventory(new([unit, toy, dummy], 0), first, new HashSet<string>(StringComparer.Ordinal)),
            "the same shared filter covers callers that compare every active owned rawcode");
        Check(!SnapshotStability.SameInventory(new([unit, toy], 0), new([unit, toy with { Rawcode = "h002" }], 0), battleIds),
            "reusing a toy address for a real ingredient still invalidates the sample");
        Check(!SnapshotStability.SameInventory(first, new([unit, toy, new(0x83000, "e018", 6, 0)], 0), battleIds),
            "a real choice reward arriving with a combat toy still invalidates the sample");
        Check(!SnapshotStability.SameInventory(first, new([unit, toy], 1), battleIds), "combat effects cannot hide incomplete owned reads");
        Check(!SnapshotStability.SameInventory(first, new([unit with { Flags20 = 7 }], 0), ids), "consumed or dead ingredients invalidate the sample");
        Check(!SnapshotStability.SameInventory(first, new([unit with { Flags5c = 0x100 }], 0), ids), "hidden ingredients invalidate the sample");
        Check(!SnapshotStability.SameInventory(first, new([unit with { Rawcode = "h002" }], 0), ids), "same-address transformation invalidates the sample");
        Check(!SnapshotStability.SameInventory(first, new([unit, new(0x90000, "e018", 6, 0)], 0), ids), "choice wisp changes invalidate the sample");
        Check(new[] { "e0IX", "e01A", "e017", "e016", "e019", "e015" }.All(code =>
            !SnapshotStability.SameInventory(first, new([unit, new(0x90000, code, 6, 0)], 0), ids)),
            "shared-choice and random-grade wisp changes invalidate the reward inventory sample");
        Check(!SnapshotStability.SameInventory(first, new([unit], 1), ids), "partial memory reads remain blocked");
        Check(!SnapshotStability.SameInventory(first, null, ids), "a missing second sample remains blocked");

        var timed = new[] { "h05D", "h06N", "h0CB", "h0B8", "h0B9", "h0BA", "h0BB", "h078", "h076", "h079", "h0CP" };
        var liveIds = ids.Concat(timed).Concat(["h00H", "h0AF", "h067", "H099", "H0B2", "H08T", "H0BK", "H093", "H091", "H0B6"])
            .ToHashSet(StringComparer.Ordinal);
        var mapHash = GameProgression.SupportedMapHash;
        var catalog = JsonSerializer.SerializeToElement(new
        {
            map = new { sha256 = mapHash },
            units = liveIds.Append("h0F5").Select(rawcode => new { rawcode }),
            recipes = new object[]
            {
                new { resultUnitRawcode = "h067", ingredients = new[] { new { type = "KING", rawcode = "h067" } } },
                new { resultUnitRawcode = "h002", ingredients = new[] { new { type = "UNIT", rawcode = "h00H" } }, casterUnitRawcodes = new[] { "h001" } },
                new { resultUnitRawcode = "H0BK", ingredients = new[] { new { type = "UNIT", rawcode = "H099", logicalRawcode = "H08T", acceptedRawcodes = new[] { "H099", "H0B2" } } } }
            },
            progressTransformations = new { entries = new[] { new { sourceRawcode = "H091", resultRawcode = "H093" } } }
        });
        LiveInventoryIdentity? Parse(string[] temporary, Dictionary<string, string> canonical, string? declaredHash = null) =>
            LiveInventoryIdentity.Parse(JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, mapSha256 = declaredHash ?? mapHash,
                temporaryRawcodes = temporary, canonicalByRawcode = canonical
            }), catalog, mapHash, liveIds);
        var utaForms = new Dictionary<string, string>(StringComparer.Ordinal) { ["h0AF"] = "h067" };
        var identity = Parse([..timed, "h0F5"], utaForms);
        Check(identity is not null, "verified map permits non-recipe effects and a canonical recipe result");
        Check(Parse(timed, utaForms, new string('0', 64)) is null, "another map cannot supply live identity rules");
        Check(new[] { "h001", "h002", "h00H", "H08T", "H0B2", "H091", "H093", "xxxx" }.All(code => Parse([code], utaForms) is null),
            "casters, recipe results, real and accepted materials, progression forms and unknown codes cannot be excluded");
        Check(new[] { "h00H", "H0B2", "H093" }.All(code => Parse(timed, new() { ["h0AF"] = code }) is null),
            "material and paid progression identities cannot be merged");
        Check(Parse(timed, new() { ["H093"] = "H091" }) is null && Parse(timed, new() { ["H091"] = "H0B6" }) is null &&
            Parse(timed, new() { ["H0B6"] = "H091" }) is not null,
            "a reversible form can use the pre-progression unit as its canonical identity without merging the paid change");
        Check(Parse(["h0AF"], utaForms) is null, "temporary/form conflicts are rejected");

        OwnedData Add(UnitObject extra) => new([unit, extra], 0);
        var timedRows = timed.Select((code, index) => new UnitObject(0x80000u + (uint)index, code, 6, 0)).ToArray();
        var activeSummons = new OwnedData([unit, ..timedRows], 0);
        Check(SnapshotStability.SameLiveInventory(first, activeSummons, liveIds, identity) &&
            SnapshotStability.SameLiveInventory(activeSummons, first, liveIds, identity), "all audited timed spawns and expirations preserve live stability");
        Check(!SnapshotStability.SameInventory(first, activeSummons, liveIds), "command verification still uses raw object identities");
        var uta = new UnitObject(0x8A000, "h067", 6, 0);
        var utaForm = uta with { Rawcode = "h0AF" };
        Check(SnapshotStability.SameLiveInventory(Add(uta), Add(utaForm), liveIds, identity) &&
            SnapshotStability.SameLiveInventory(Add(utaForm), Add(uta), liveIds, identity), "same-address Uta combat form switches preserve live stability");
        Check(!SnapshotStability.SameInventory(Add(uta), Add(utaForm), liveIds), "command verification distinguishes both Uta rawcodes");
        Check(!SnapshotStability.SameLiveInventory(Add(uta), Add(utaForm with { Address = 0x8B000 }), liveIds, identity) &&
            !SnapshotStability.SameLiveInventory(Add(uta), first, liveIds, identity), "moving to another object or losing an owned form still invalidates live stability");
        Check(!SnapshotStability.SameLiveInventory(first, Add(new(0x8C000, "h00H", 6, 0)), liveIds, identity),
            "a real zombie ingredient changes live stability");
        Check(!SnapshotStability.SameLiveInventory(first, activeSummons with { ReadErrors = 1 }, liveIds, identity),
            "temporary objects cannot bypass a read failure");
        Console.WriteLine($"PASS: inventory snapshot stability ({passed} checks).");
    }
}
