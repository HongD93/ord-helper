static class CraftPlannerTests
{
    public static void Run()
    {
        var direct = new[] { Command("R001", "u001", 1, ("h001", 2)) };
        var initial = Stock(("u001", 7), ("h001", 2));
        var plan = CraftPlanner.Plan("R001", direct, initial);
        Check(plan.Executable && plan.Steps.Length == 1 && plan.RemainingInventory["u001"] == 8, "root always adds to existing quantity");
        Replay(plan, initial);
        Check(initial["u001"] == 7 && initial["h001"] == 2, "planner does not mutate caller inventory");

        var siblings = new[] {
            Command("R100", "u100", 1, ("u101", 1), ("u102", 1)),
            Command("R101", "u101", 1, ("u102", 1)),
            Command("R102", "u102", 1, ("h001", 1)) };
        initial = Stock(("u102", 1), ("h001", 1));
        plan = CraftPlanner.Plan("R100", siblings, initial);
        Check(plan.Executable && plan.Steps.Select(s => s.RecipeId).SequenceEqual(new[] { "R102", "R101", "R100" }), "direct sibling inventory reserved before recursive creation");
        Replay(plan, initial);

        var shared = new[] {
            Command("R200", "u200", 1, ("u201", 1), ("u202", 1)),
            Command("R201", "u201", 1, ("h001", 2)),
            Command("R202", "u202", 1, ("h001", 2)) };
        initial = Stock(("h001", 3));
        plan = CraftPlanner.Plan("R200", shared, initial);
        Check(!plan.Executable && plan.Steps.Length == 0 && plan.Missing.Any(i => i.Rawcode == "h001" && i.Count == 1), "shared leaf cannot be spent twice");
        Check(plan.RemainingInventory["h001"] == 3, "failed plans expose original inventory only");

        var alternatives = new[] {
            Command("R300", "u300", 1, ("u301", 1), ("u302", 1)),
            Command("R301", "u301", 1, ("h001", 1)),
            Command("R302", "u301", 1, ("h002", 1)),
            Command("R303", "u302", 1, ("h001", 1)) };
        initial = Stock(("h001", 1), ("h002", 1));
        plan = CraftPlanner.Plan("R300", alternatives, initial);
        Check(plan.Executable && plan.Steps.Any(s => s.RecipeId == "R302") && plan.Steps.All(s => s.RecipeId != "R301"), "later sibling failure backtracks into earlier recipe choice");
        Replay(plan, initial);

        var cycle = new[] { Command("R400", "u400", 1, ("u401", 1)), Command("R401", "u401", 1, ("u400", 1)) };
        plan = CraftPlanner.Plan("R400", cycle, Stock());
        Check(!plan.Executable && plan.Reason == "recipe_cycle" && plan.Steps.Length == 0, "cyclic dependency bounded");
        plan = CraftPlanner.Plan("R400", cycle, Stock(("u400", 1)));
        Check(!plan.Executable && plan.RemainingInventory["u400"] == 1, "existing root cannot fund a cycle with no additional result");
        var cycleAlternative = cycle.Concat(new[] { Command("R402", "u401", 1, ("h001", 1)) }).ToArray();
        initial = Stock(("h001", 1));
        plan = CraftPlanner.Plan("R400", cycleAlternative, initial);
        Check(plan.Executable && plan.Steps.Any(s => s.RecipeId == "R402"), "valid alternative survives a cyclic recipe");
        Replay(plan, initial);
        plan = CraftPlanner.Plan("R400", cycleAlternative, Stock());
        Check(!plan.Executable && plan.Reason == "missing_material" && plan.Missing.Any(i => i.Rawcode == "h001"),
            "cyclic alternatives do not hide actionable leaf shortages");

        var batch = new[] { Command("R500", "u500", 1, ("u501", 3)), Command("R501", "u501", 2, ("h001", 1)) };
        initial = Stock(("h001", 2));
        plan = CraftPlanner.Plan("R500", batch, initial);
        Check(plan.Executable && plan.Steps.Length == 3 && plan.RemainingInventory["u501"] == 1, "batch outputs retain surplus and repeat when needed");
        Replay(plan, initial);

        plan = CraftPlanner.Plan("ZZZZ", direct, Stock());
        Check(!plan.Executable && plan.Reason == "unknown_recipe", "unknown explicit root rejected");
        plan = CraftPlanner.Plan("R001", direct, Stock(("H001", 2)));
        Check(!plan.Executable && plan.Missing.Any(i => i.Rawcode == "h001"), "rawcodes remain case sensitive");

        var choices = new[] { Command("R600", "u600", 1, ("h001", 1), ("h002", 1)) };
        initial = Stock(("e018", 2));
        Check(!CraftPlanner.Plan("R600", choices, initial).Executable, "choice wisps require explicit allowance");
        plan = CraftPlanner.Plan("R600", choices, initial, allowChoiceWisps: true);
        Check(plan.Executable && plan.ChoiceWispCount == 2 && plan.Steps.Where(s => s.Type == "choice").All(s =>
            s.ChoiceRawcode == "e018" && s.ResultCount == 1 && s.Ingredients.Single() == new CraftIngredient("e018", 1)), "one verified choice wisp per common unit");
        Replay(plan, initial);
        Check(!CraftPlanner.Plan("R600", choices, Stock(("e0IX", 99)), allowChoiceWisps: true).Executable, "random wisps are never substitutes");
        Check(!CraftPlanner.Plan("R601", [Command("R601", "u601", 1, ("h010", 1))], Stock(("e018", 99)), true).Executable,
            "choice conversion limited to nine verified common units");
        var wispSibling = new[] { Command("R602", "u602", 1, ("e018", 1), ("h001", 1)) };
        Check(!CraftPlanner.Plan("R602", wispSibling, Stock(("e018", 1)), true).Executable, "direct sibling wisp cannot be converted and consumed twice");
        initial = Stock(("e018", 2));
        plan = CraftPlanner.Plan("R602", wispSibling, initial, true);
        Check(plan.Executable && plan.ChoiceWispCount == 1, "remaining wisp can satisfy a common deficit");
        Replay(plan, initial);

        Check(CraftPlanner.Plan("R100", siblings, Stock(("u102", 1), ("h001", 1)), maxSteps: 1).Reason == "step_limit", "step cap stops partial plans");
        Check(CraftPlanner.Plan("R001", direct, Stock(("h001", 2)), maxSearchNodes: 1).Reason == "search_limit", "search cap stops safely");
        Check(CraftPlanner.Plan("R001", direct, Stock(("h001", -1))).Reason == "invalid_input", "negative stock rejected");
        Check(CraftPlanner.Plan("R700", [Command("R700", "u700", 0)], Stock()).Reason == "invalid_input", "invalid output quantities rejected");
        var repeatedChoices = new[] { Command("R701", "u701", 1, ("h001", 40)) };
        initial = Stock(("e018", 40));
        plan = CraftPlanner.Plan("R701", repeatedChoices, initial, true);
        Check(plan.Executable && plan.Steps.Length == 41, "large bounded choice batches do not trip cycle detection");
        Replay(plan, initial);
        var protectedRoute = new[] { Command("R800", "u800", 1, ("u801", 1), ("h001", 1)), Command("R801", "u801", 1, ("h002", 1)) };
        initial = Stock(("u801", 1), ("h001", 1), ("h002", 1));
        plan = CraftPlanner.Plan("R800", protectedRoute, initial, protectedCounts: Stock(("u801", 1)));
        Check(plan.Executable && plan.Steps.Select(s => s.RecipeId).SequenceEqual(new[] { "R801", "R800" }) && plan.RemainingInventory["u801"] == 1,
            "protected core is reserved while a separate same-type ingredient is rebuilt");
        Replay(plan, initial);
        plan = CraftPlanner.Plan("R800", protectedRoute, Stock(("u801", 1), ("h001", 1)), protectedCounts: Stock(("u801", 1)));
        Check(!plan.Executable && plan.Steps.Length == 0, "protected core is never consumed when its replacement cannot be built");
        initial = Stock(("u800", 2), ("u801", 1), ("h001", 1), ("h002", 1));
        plan = CraftPlanner.Plan("R800", protectedRoute, initial, protectedCounts: Stock(("u800", 1), ("u801", 1)));
        Check(plan.Executable && plan.RemainingInventory["u800"] == 3 && plan.RemainingInventory["u801"] == 1,
            "root ownership and explicit protection overlap rather than being subtracted twice");
        Replay(plan, initial);
        var protectedAlternative = new[] { Command("R810", "u802", 1, ("u801", 1)), Command("R811", "u802", 1, ("h003", 1)), Command("R812", "u803", 1, ("u802", 1)) };
        initial = Stock(("u801", 1), ("h003", 1));
        plan = CraftPlanner.Plan("R812", protectedAlternative, initial, protectedCounts: Stock(("u801", 1)));
        Check(plan.Executable && plan.Steps.Any(s => s.RecipeId == "R811") && plan.Steps.All(s => s.RecipeId != "R810"), "protected stock forces a valid alternate recipe branch");
        Replay(plan, initial);
        Check(CraftPlanner.Plan("R800", protectedRoute, initial, protectedCounts: Stock(("u801", 2))).Reason == "protected_material_missing", "missing protected quantity is rejected rather than recreated");
        Check(CraftPlanner.Plan("R800", protectedRoute, initial, protectedCounts: Stock(("U801", 1))).Reason == "protected_material_missing", "protection rawcodes remain case sensitive");
        Check(CraftPlanner.Plan("R800", protectedRoute, initial, protectedCounts: Stock(("u801", 0))).Reason == "invalid_protection", "zero protection quantities are invalid");
        initial = Stock(("e018", 2));
        plan = CraftPlanner.Plan("R601", [Command("R601", "u601", 1, ("h001", 1))], initial, allowChoiceWisps: true, protectedCounts: Stock(("e018", 1)));
        Check(plan.Executable && plan.ChoiceWispCount == 1 && plan.RemainingInventory["e018"] == 1, "pure planner protection also reserves a shared choice budget");
        Replay(plan, initial);
        VerifyExpectedPlans();
        Console.WriteLine("PASS: recursive craft planning, sibling reservations, global backtracking, shared materials, root protection, batches, cycles, case-sensitive IDs, verified choice wisps, exact deck prefixes and bounds.");
    }

    private static CraftCommand Command(string id, string result, int count, params (string Rawcode, int Count)[] ingredients) =>
        new(id, result, count, ingredients.Select(i => new CraftIngredient(i.Rawcode, i.Count)).ToArray(), null, "Z", []);
    private static Dictionary<string, int> Stock(params (string Rawcode, int Count)[] values) =>
        values.ToDictionary(v => v.Rawcode, v => v.Count, StringComparer.Ordinal);
    private static void VerifyExpectedPlans()
    {
        var commands = new[] { Command("R900", "u900", 1, ("h001", 1)),
            Command("R901", "u901", 1, ("h001", 1)), Command("R902", "u902", 1, ("u900", 1), ("h002", 1)),
            Command("R903", "u900", 1, ("h003", 1)) };
        RequestedCraftStep[] chosen = [new("craft", "R903", "u900"), new("craft", "R902", "u902")];
        var stock = Stock(("h001", 1), ("h002", 1), ("h003", 1), ("x001", 1));
        var plan = CraftPlanner.ValidateExpected("R902", commands, stock, chosen, protectedCounts: Stock(("x001", 1)));
        Check(plan.Executable && plan.Steps.Select(step => step.RecipeId).SequenceEqual(["R903", "R902"]) &&
            plan.RemainingInventory["h001"] == 1 && plan.RemainingInventory["x001"] == 1, "a chosen joint path is not silently changed to another feasible recipe");
        Replay(plan, stock);
        Check(!CraftPlanner.ValidateExpected("R902", commands, Stock(("h001", 2), ("h002", 1)), chosen).Executable,
            "stale chosen ingredients fail without falling back to a different route");
        Check(!CraftPlanner.ValidateExpected("R902", commands, stock,
            [new("craft", "R901"), new("craft", "R903"), new("craft", "R902")]).Executable, "unrelated crafts cannot be hidden in a target prefix");
        Check(!CraftPlanner.ValidateExpected("R902", commands, stock,
            [new("craft", "R900", "u999"), new("craft", "R902")]).Executable, "client result overrides cannot change a native recipe");
        Check(!CraftPlanner.ValidateExpected("R902", commands, stock,
            [new("craft", "R900"), new("craft", "R902"), new("craft", "R902")]).Executable, "a target is completed exactly once and only at the end");
        RequestedCraftStep[] withChoice = [new("choice", null, "h001"), new("craft", "R900"), new("craft", "R902")];
        plan = CraftPlanner.ValidateExpected("R902", commands, Stock(("e018", 1), ("h002", 1)), withChoice, allowChoiceWisps: true);
        Check(plan.Executable && plan.ChoiceWispCount == 1, "verified choice steps use one actual choice wisp");
        Check(!CraftPlanner.ValidateExpected("R902", commands, Stock(("e018", 1), ("h002", 1)), withChoice).Executable,
            "a choice capability veto blocks the same prefix");
        Check(!CraftPlanner.ValidateExpected("R902", commands, stock, chosen, protectedCounts: Stock(("h003", 1))).Executable,
            "a fixed prefix cannot consume preserved ingredients");
        Check(!CraftPlanner.ValidateExpected("R902", commands, stock, Enumerable.Repeat(new RequestedCraftStep("craft", "R900"), 65).ToArray()).Executable,
            "oversized exact plans fail before execution");
    }

    private static void Replay(CraftPlan plan, IReadOnlyDictionary<string, int> initial)
    {
        Check(plan.Executable, "only executable plans can be replayed");
        var stock = new Dictionary<string, int>(initial, StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            foreach (var ingredient in step.Ingredients)
            {
                Check(stock.GetValueOrDefault(ingredient.Rawcode) >= ingredient.Count, "each step has all actual inputs before execution");
                stock[ingredient.Rawcode] -= ingredient.Count;
            }
            stock[step.ResultRawcode] = stock.GetValueOrDefault(step.ResultRawcode) + step.ResultCount;
        }
        var actual = stock.Where(pair => pair.Value > 0).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var predicted = plan.RemainingInventory.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        Check(actual.SequenceEqual(predicted), "projected inventory agrees with replay of the complete step sequence");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Craft planner test failed: " + message);
    }
}
