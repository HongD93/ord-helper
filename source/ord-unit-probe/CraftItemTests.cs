using System.Text.Json;

static class CraftItemTests
{
    internal static void Run()
    {
        var item = new CraftIngredient("AI00", 1);
        CraftCommand Command(string id, string target, CraftIngredient[] units, CraftIngredient[]? required = null) =>
            new(id, target, 1, units, "아이템조합", null, ["b001"], ItemRequirements: required);
        var first = Command("r001", "m001", [new("b001", 1)], [item]);
        var last = Command("r002", "g001", [new("m001", 1)], [item]);
        var fallback = Command("r003", "m001", [new("b001", 1)]);
        var counts = new Dictionary<string, int> { ["b001"] = 2 };
        var one = new Dictionary<string, int> { ["AI00"] = 1, ["AI01"] = 0, ["AI02"] = 0 };
        var zero = new Dictionary<string, int> { ["AI00"] = 0, ["AI01"] = 0, ["AI02"] = 0 };
        var tests = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Item regression: " + name); tests++; }
        Check(!CraftPlanner.Plan("r001", [first], counts).Executable, "unknown item blocks before units");
        Check(!CraftPlanner.Plan("r001", [first], counts, items: zero).Executable, "confirmed zero is insufficient");
        Check(CraftPlanner.Plan("r001", [first], counts, items: one).Executable, "confirmed owned item permits plan");
        Check(!CraftPlanner.Plan("r002", [first, last], counts, items: one).Executable, "one entitlement cannot pay twice");
        var alternative = CraftPlanner.Plan("r002", [first, last, fallback], counts, items: one);
        Check(alternative.Executable && alternative.Steps.Select(step => step.RecipeId).SequenceEqual(["r003", "r002"]), "reserve final item before alternative intermediate");
        var held = CraftPlanner.Plan("r002", [first, last], new Dictionary<string, int> { ["m001"] = 1 }, items: one);
        Check(held.Executable && held.StepCount == 1, "held intermediate has no repeated acquisition cost");
        Check(!CraftPlanner.ValidateExpected("r002", [first, last], counts,
            [new("craft", "r001"), new("craft", "r002")], items: one).Executable, "explicit sequence cannot reuse entitlement");
        Check(CraftPlanner.ValidateExpected("r002", [first, last, fallback], counts,
            [new("craft", "r003"), new("craft", "r002")], items: one).Executable, "validated alternative pays once");
        var alien = first with { ItemRequirements = [new("cnob", 1)] };
        Check(!CraftPlanner.Plan("r001", [alien], counts, items: one).Executable, "unread item type is unknown");
        JsonElement Snapshot(int ai00 = 1, int ai01 = 0, string status = "confirmed", int player = 0, bool readOnly = true) =>
            JsonSerializer.SerializeToElement(new { localPlayer = 0, items = new { status, localPlayer = player,
                counts = new Dictionary<string, int> { ["AI00"] = ai00, ["AI01"] = ai01, ["AI02"] = 0 }, source = new { readOnly } } });
        Check(CraftItems.Read(Snapshot())?.GetValueOrDefault("AI00") == 1, "confirmed state decoded");
        Check(CraftItems.Read(Snapshot(ai00: 0))?.GetValueOrDefault("AI00") == 0, "confirmed absent is known zero");
        Check(CraftItems.Read(Snapshot(status: "unknown")) is null && CraftItems.Read(Snapshot(player: 1)) is null &&
            CraftItems.Read(Snapshot(readOnly: false)) is null, "unknown foreign and unproven state rejected");
        Check(CraftItems.Read(Snapshot(ai00: 2)) is null && CraftItems.Read(JsonSerializer.SerializeToElement(new { localPlayer = "0" })) is null,
            "invalid counts and types never authorize");
        Check(CraftItems.ConsumptionMatches(Snapshot(), Snapshot(ai00: 0), [item]), "expected item consumed");
        Check(!CraftItems.ConsumptionMatches(Snapshot(), Snapshot(), [item]), "result without item consumption not verified");
        Check(CraftItems.ConsumptionMatches(Snapshot(), Snapshot(ai00: 0, ai01: 1), [item]), "unrelated random reward allowed");
        Check(!CraftItems.ConsumptionMatches(Snapshot(), Snapshot(ai00: 0, status: "unknown"), [item]), "uncertain after state cannot prove use");
        Check(CraftItems.ConsumptionMatches(Snapshot(status: "unknown"), Snapshot(status: "unknown"), []), "unrelated ordinary craft unaffected");
        Console.WriteLine($"PASS: item planning and strict entitlement checks {tests}/{tests}; no game input.");
    }
}
