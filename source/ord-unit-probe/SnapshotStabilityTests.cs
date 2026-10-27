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
        Console.WriteLine($"PASS: inventory snapshot stability ({passed} checks).");
    }
}
