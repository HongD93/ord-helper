static class ChoiceWispBatchPlanTests
{
    internal static void Run()
    {
        var passed = 0;
        static PlannedCraftStep Choice(string code) => new("choice", null, code, [new CraftIngredient("e018", 1)], code, 1);
        static PlannedCraftStep Craft(string id, string code, params CraftIngredient[] ingredients) => new("craft", id, null, ingredients, code, 1);
        void Check(bool value, string name) { if (!value) throw new Exception("Choice batch plan: " + name); passed++; }
        var path = new[] { Choice("h003"), Craft("r001", "m001", new CraftIngredient("h003", 1)), Choice("h002"), Choice("h003"),
            Craft("r002", "g001", new CraftIngredient("m001", 1), new CraftIngredient("h002", 1), new CraftIngredient("h003", 1)) };
        Check(ChoiceWispBatchPlan.TryCreate(path, new Dictionary<string, int> { ["e018"] = 3 }, new Dictionary<string, int>(), out var batch) &&
            batch.Length == 4 && batch[0].ResultRawcode == "h003" && batch[0].ResultCount == 2 && batch[1].ResultRawcode == "h002" &&
            batch.Skip(2).Select(step => step.RecipeId).SequenceEqual(["r001", "r002"]) && ChoiceWispBatchPlan.LogicalCount(batch) == 5,
            "same results are prepared first while craft order and logical budget remain intact");
        var many = Enumerable.Repeat(Choice("h003"), 25).Append(Craft("r003", "g001", new CraftIngredient("h003", 25))).ToArray();
        Check(ChoiceWispBatchPlan.TryCreate(many, new Dictionary<string, int> { ["e018"] = 25 }, new Dictionary<string, int>(), out batch) &&
            batch.Take(3).Select(step => step.ResultCount).SequenceEqual([12, 12, 1]) && ChoiceWispBatchPlan.LogicalCount(batch) == 26,
            "selection cap splits batches without increasing the logical execution limit");
        Check(!ChoiceWispBatchPlan.TryCreate(path, new Dictionary<string, int> { ["e018"] = 2 }, new Dictionary<string, int>(), out _),
            "an insufficient wisp inventory cannot be frontloaded");
        Check(!ChoiceWispBatchPlan.TryCreate(path, new Dictionary<string, int> { ["e018"] = 3, ["h003"] = 1 },
            new Dictionary<string, int> { ["h003"] = 2 }, out _), "missing protected inventory blocks preparation");
        Check(!ChoiceWispBatchPlan.TryCreate([Craft("r004", "e018", new CraftIngredient("b001", 1)), Choice("h003")],
            new Dictionary<string, int> { ["b001"] = 1 }, new Dictionary<string, int>(), out _), "a future wisp producer forbids unsafe reordering");
        Check(!ChoiceWispBatchPlan.TryCreate([Choice("h003"), Craft("r004", "g001", new CraftIngredient("e018", 1), new CraftIngredient("h003", 1))],
            new Dictionary<string, int> { ["e018"] = 2 }, new Dictionary<string, int>(), out _), "non-choice wisp consumption forbids reordering");
        Check(!ChoiceWispBatchPlan.TryCreate([Choice("h003") with { ResultCount = 3 }],
            new Dictionary<string, int> { ["e018"] = 3 }, new Dictionary<string, int>(), out _), "an unvalidated aggregate is not accepted as a logical path");
        Check(!ChoiceWispBatchPlan.TryCreate([Choice("m001")], new Dictionary<string, int> { ["e018"] = 1 }, new Dictionary<string, int>(), out _),
            "only map-supported common results can be batched");
        var owned = new Dictionary<string, int> { ["m001"] = 1, ["h002"] = 1, ["h003"] = 1 };
        var direct = new[] { path[^1] };
        Check(ChoiceWispBatchPlan.TryCreate(direct, owned, new Dictionary<string, int>(), out batch) && batch.SequenceEqual(direct),
            "already owned intermediate materials do not create any new wisp requests");
        Check(!ChoiceWispBatchPlan.TryCreate([Choice("h003"), Craft("r005", "g001", new CraftIngredient("h003", 2))],
            new Dictionary<string, int> { ["e018"] = 1, ["h003"] = 1 }, new Dictionary<string, int> { ["h003"] = 1 }, out _),
            "reordered steps cannot consume protected units");
        Console.WriteLine($"PASS: Choice batch plan ({passed} checks; no game input).");
    }
}
