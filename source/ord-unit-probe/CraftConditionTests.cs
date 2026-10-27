using System.Text.Json;

static class CraftConditionTests
{
    internal static void Run()
    {
        var passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Recipe condition: " + name); passed++; }
        var requirements = new CraftRecipeCondition[] { new("UBAN", "g001", 1), new("KING", "g001", 1) };
        var allow = new CraftConditions.State(new() { ["g001"] = false }, "allowed", "no_king_road");
        var banned = allow with { UnitBans = new() { ["g001"] = true } };
        var kingBlocked = allow with { KingStatus = "blocked", KingReason = "martial_law" };
        var unknown = allow with { UnitBans = new() };
        CraftCommand Command(string id, CraftRecipeCondition[]? rules = null) => new(id, "g001", 1,
            [new("b001", 1)], "조건조합", null, ["b001"], Conditions: rules);
        var command = Command("r001", requirements);
        var stock = new Dictionary<string, int> { ["b001"] = 1 };
        Check(CraftPlanner.Plan("r001", [command], stock, recipeConditions: allow).Executable, "both native conditions permit");
        Check(!CraftPlanner.Plan("r001", [command], stock).Executable, "unknown never permits");
        Check(!CraftPlanner.Plan("r001", [command], stock, recipeConditions: banned).Executable, "unit ban blocks");
        Check(!CraftPlanner.Plan("r001", [command], stock, recipeConditions: kingBlocked).Executable, "king road blocks");
        Check(!CraftPlanner.Plan("r001", [command], stock, recipeConditions: unknown).Executable, "missing unit key is unknown");
        Check(CraftPlanner.Plan("r002", [Command("r002")], stock).Executable, "ordinary craft needs no conditional state");
        Check(!CraftPlanner.ValidateExpected("r001", [command], stock, [new("craft", "r001")], recipeConditions: banned).Executable,
            "supplied order reconstructs current server condition");
        Check(CraftPlanner.ValidateExpected("r001", [command], stock, [new("craft", "r001")], recipeConditions: allow).Executable,
            "expected order accepted when both permit");
        var first = new CraftCommand("r003", "m001", 1, [new("b001", 1)], "조건조합", null, ["b001"], Conditions: requirements);
        var second = new CraftCommand("r004", "m001", 1, [new("b001", 1)], "조건조합", null, ["b001"]);
        var root = new CraftCommand("r005", "g002", 1, [new("m001", 1)], "조건조합", null, ["m001"]);
        var alternative = CraftPlanner.Plan("r005", [first, second, root], stock, recipeConditions: banned);
        Check(alternative.Executable && alternative.Steps[0].RecipeId == "r004", "blocked route does not suppress an allowed alternative");
        Check(CraftPlanner.Plan("r005", [first, root], new Dictionary<string, int> { ["m001"] = 1 }).Executable,
            "held intermediate is not gated by its past conditions");
        JsonElement Frame(int owner = 0, int player = 0, string status = "partial", bool readOnly = true) =>
            JsonSerializer.SerializeToElement(new { localPlayer = owner, recipeConditions = new { status, localPlayer = player,
                unitBans = new Dictionary<string, bool> { ["g001"] = false }, king = new { status = "allowed", reason = "no_king_road" }, source = new { readOnly } } });
        Check(CraftConditions.Available(CraftConditions.Read(Frame()), requirements, out _, out _), "partial state may contain fully verified required gates");
        Check(CraftConditions.Read(Frame(player: 1)) is null && CraftConditions.Read(Frame(readOnly: false)) is null && CraftConditions.Read(Frame(status: "unknown")) is null,
            "foreign unproven and unknown states rejected");
        Console.WriteLine($"PASS: recipe restriction planning {passed}/{passed} pure cases; no game input.");
    }
}
