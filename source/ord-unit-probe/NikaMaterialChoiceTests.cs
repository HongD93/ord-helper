using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

// 사전과 주입 실행기만 사용한다. 실제 Windows 입력이나 게임 메모리 쓰기는 없다.
static class NikaMaterialChoiceTests
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Nika material choice: " + message); checks++; }
        var et = Canonical(); var commands = Commands(); var items = new Dictionary<string, int> { ["AI00"] = 0, ["AI01"] = 1, ["AI02"] = 0 };
        CraftPlan Plan(Dictionary<string, int> stock, string recipe = "ET02", IReadOnlyDictionary<string, int>? held = null) =>
            CraftPlanner.Plan(recipe, commands, stock, protectedCounts: held, items: items, recipeConditions: Conditions(1));
        foreach (var form in CraftMaterialChoices.LuffyForms)
        {
            var stock = Stock(form); var plan = Plan(stock);
            Check(plan.Executable && plan.Steps.Length == 1 && plan.Steps[0].MaterialChoiceRawcode == form, "owned real form is used directly");
            Check(plan.Steps[0].Ingredients.Any(item => item.Rawcode == form && item.Count == 1) && !plan.Steps[0].Ingredients.Any(item => item.Rawcode == "H08T"), "step contains real consumption and no virtual unit");
            Check(plan.RemainingInventory.GetValueOrDefault(form) == 0 && plan.RemainingInventory.GetValueOrDefault("H0BK") == 1 && !stock.ContainsKey("H08T"), "real inventory replay never adds a proxy count");
            var valid = CraftPlanner.ValidateExpected("ET02", commands, stock, [new("craft", "ET02", "H0BK", form)], items: items, recipeConditions: Conditions(1));
            Check(valid.Executable && valid.Steps[0].MaterialChoiceRawcode == form, "exact plan retains its real branch");
            Check(!CraftPlanner.ValidateExpected("ET02", commands, stock, [new("craft", "ET02", "H0BK", form == "H099" ? "H0B2" : "H099")], items: items, recipeConditions: Conditions(1)).Executable, "wrong branch cannot consume another form");
            Check(!Plan(stock, held: new Dictionary<string, int> { [form] = 1 }).Executable, "explicit real-form protection cannot be bypassed by alias");
            Check(CraftMaterialChoices.TryResolve(et, form, out var resolved) && GameInput.HasIngredients(resolved, Owned(form)), "native requirement accepts the resolved real form");
            Check(!GameInput.HasIngredients(resolved, Owned(form, other: form == "H099" ? "H0B2" : "H099")), "two real forms cannot authorize a native chat input");
            Check(!GameInput.HasIngredients(resolved, Owned(form, other: "H0B3")), "transient snake form remains an ambiguity candidate");
            Check(!GameInput.HasIngredients(resolved, Owned(form, other: "H0B2", hiddenOther: true)), "hidden group candidates cannot be ignored");
            var roots = new Roots(0x10000, 0x20000, 0x30000, 0x40000, 5, 0); var reads = 0;
            Check(!GameInput.TryReadCommandInventory(resolved, roots, _ => ++reads == 1 ? Owned(form) : Owned(form, other: "H0B3", hiddenOther: true), () => roots, out _), "a candidate appearing during native inventory recheck blocks the last input boundary");
        }
        var both = Stock("H099"); both["H0B2"] = 1;
        Check(!Plan(both).Executable && !Plan(both, held: new Dictionary<string, int> { ["H0B2"] = 1 }).Executable, "protecting one of two forms does not prove which map group member will be consumed");
        Check(!CraftPlanner.ValidateExpected("ET02", commands, both, [new("craft", "ET02", "H0BK", "H099")], items: items, recipeConditions: Conditions(1)).Executable, "exact plan also rejects an ambiguous map group");
        Check(!CraftPlanner.ValidateExpected("ET02", commands, Stock("H099"), [new("craft", "ET02")], items: items, recipeConditions: Conditions(1)).Executable, "old missing branch metadata fails closed");
        Check(!CraftPlanner.ValidateExpected("ET02", commands, Stock("H099"), [new("craft", "ET02", MaterialChoiceRawcode: "H08T")], items: items, recipeConditions: Conditions(1)).Executable, "proxy cannot be submitted as a real branch");
        Check(!CraftPlanner.ValidateExpected("UP01", commands, new Dictionary<string, int> { ["H0BK"] = 1 }, [new("craft", "UP01", MaterialChoiceRawcode: "H099")], recipeConditions: Conditions(1)).Executable, "choice metadata on unrelated recipes is rejected");
        var fromBase = Stock(null); fromBase["b002"] = 1;
        var built = Plan(fromBase);
        Check(built.Executable && built.Steps.Select(step => step.RecipeId).SequenceEqual(["TR07", "ET02"]) && built.Steps[^1].MaterialChoiceRawcode == "H0B2", "root planner explores supported real-form production alternatives");
        var nested = Plan(Stock("H099"), "UP01");
        Check(nested.Executable && nested.Steps.Select(step => step.RecipeId).SequenceEqual(["ET02", "UP01"]), "child ET02 production also resolves its real branch");
        Check(CraftPlanner.ValidateExpected("ET02", commands, fromBase, [new("craft", "TR07"), new("craft", "ET02", MaterialChoiceRawcode: "H0B2")], items: items, recipeConditions: Conditions(1)).Executable, "expected ancestry permits an actual-form production step");
        Check(!GameInput.HasIngredients(et, Owned("H099")), "unresolved canonical command cannot reach native input");
        Check(!CraftMaterialChoices.TryResolve(et with { Conditions = [] }, "H099", out _), "alias metadata cannot bypass the NIKA enhancement predicate");
        var badAlias = et with { Ingredients = [new("H08T", 1, ["H099", "H0B3"])] };
        Check(!CraftMaterialChoices.TryResolve(badAlias, "H099", out _), "unverified accepted rawcodes are rejected");
        Check(!GameCraftService.SameExpectedSteps([new("craft", "ET02", MaterialChoiceRawcode: "H099")], [new("craft", "ET02", MaterialChoiceRawcode: "H0B2")]), "idempotent request identity includes the material branch");
        foreach (var stage in new int?[] { null, 0, 2 })
            Check(!CraftConditions.Available(Conditions(stage), [new("NIKA", "NIKA", 1)], out _, out _), "unknown, unenhanced and completed stages cannot craft");
        var enhanced = Conditions(1);
        Check(CraftConditions.TryProject(enhanced, [new("NIKA", "NIKA", 1), new("NIKA", "NIKA", 1)], out var projected, out _, out _, out _) && projected?.Special?.NikaEnhancement.Stage == 1 && projected.Special.SkillPoints == 0, "NIKA predicate does not consume skill points or enhancement twice");
        Check(GameSpecialRecipeConditions.ConsumptionMatches(Frame(1), Frame(2), [new("NIKA", "NIKA", 1)]), "completion requires confirmed enhancement one to completed two");
        Check(!GameSpecialRecipeConditions.ConsumptionMatches(Frame(1), Frame(1), [new("NIKA", "NIKA", 1)]) && !GameSpecialRecipeConditions.ConsumptionMatches(Frame(2), Frame(2), [new("NIKA", "NIKA", 1)]), "unchanged or previously completed enhancement is not new proof");
        var invalid = Special(1) with { NikaEnhancement = Special(1).NikaEnhancement with { Source = Special(1).NikaEnhancement.Source with { SampleCount = 1 } } };
        Check(CraftConditions.Read(Frame(invalid))?.Special?.NikaEnhancement.Status == "unknown" && CraftConditions.Read(Frame(invalid))?.Special?.TransformationCount == 2, "unverified nested NG cannot erase unrelated special facts");
        checks += await ServiceChecks();
        Console.WriteLine($"PASS: Nika real-material planning, native guards and proof {checks}/{checks} cases; no game input.");
    }
    private static CraftCommand Canonical() => new("ET02", "H0BK", 1, [new("H08T", 1, ["H099", "H0B2"]), new("h03C", 1), new("h02D", 1), new("h00K", 1)], "태양신니카", null, [], WoodCost: 5, ItemRequirements: [new("AI01", 1)], Conditions: [new("NIKA", "NIKA", 1)]);
    private static CraftCommand[] Commands() => [Canonical(), new("TR09", "H099", 1, [new("b001", 1)], "루피초월", null, []), new("TR07", "H0B2", 1, [new("b002", 1)], "스네이크맨", null, []), new("UP01", "g001", 1, [new("H0BK", 1)], "다음조합", null, [])];
    private static Dictionary<string, int> Stock(string? form) { var stock = new Dictionary<string, int> { ["h03C"] = 1, ["h02D"] = 1, ["h00K"] = 1 }; if (form is not null) stock[form] = 1; return stock; }
    private static OwnedData Owned(string form, string? other = null, bool hiddenOther = false)
    {
        var units = Stock(form).Select((pair, index) => new UnitObject((uint)(0x10000 + index * 0x100), pair.Key, 0, 0)).ToList();
        if (other is not null) units.Add(new(0x20000, other, 0, hiddenOther ? 0x100u : 0));
        return new(units, 0);
    }
    private static GameSpecialRecipeConditions.State Special(int? stage) => new("confirmed", 0, 0, 5, 2, 0, 20,
        new(true, GameProgression.SupportedMapHash, 2, "fixture", "independent_special"))
    { NikaEnhancement = new(stage is null ? "unknown" : "confirmed", 0, stage, new(true, GameProgression.SupportedMapHash, stage is null ? 0 : 2, "fixture_NG", "independent_NG")) };
    private static JsonElement Frame(int? stage) => Frame(Special(stage));
    private static JsonElement Frame(GameSpecialRecipeConditions.State special) => JsonSerializer.SerializeToElement(new { localPlayer = 0, recipeConditions = new { status = "unknown", special } });
    private static CraftConditions.State Conditions(int? stage) => CraftConditions.Read(Frame(stage))!;
    private static async Task<int> ServiceChecks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ord-nika-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var catalog = Path.Combine(folder, "catalog.json"); var aliases = Path.Combine(folder, "aliases.json");
        var commands = Commands(); var allCodes = commands.Select(command => command.ResultRawcode).Concat(commands.SelectMany(command => command.Ingredients).Select(item => item.Rawcode)).Concat(CraftMaterialChoices.LuffyForms).Distinct().ToArray();
        var recipes = commands.Select(command => new { id = command.RecipeId, resultUnitRawcode = command.ResultRawcode, resultCount = 1, casterUnitRawcodes = Array.Empty<string>(), ingredients =
            command.Ingredients.Select(item => new Dictionary<string, object?> { ["type"] = "UNIT", ["rawcode"] = item.Rawcode, ["count"] = item.Count, ["acceptedRawcodes"] = item.AcceptedRawcodes })
            .Concat((command.ItemRequirements ?? []).Select(item => new Dictionary<string, object?> { ["type"] = "ITEM", ["rawcode"] = item.Rawcode, ["count"] = item.Count }))
            .Concat((command.Conditions ?? []).Select(item => new Dictionary<string, object?> { ["type"] = item.Type, ["rawcode"] = item.Rawcode, ["count"] = item.Count }))
            .Concat(command.WoodCost > 0 ? [new Dictionary<string, object?> { ["type"] = "WOOD", ["rawcode"] = "WOOD", ["count"] = command.WoodCost }] : []) });
        var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        File.WriteAllText(catalog, JsonSerializer.Serialize(new { map = new { sha256 = GameProgression.SupportedMapHash }, units = allCodes.Select(rawcode => new { rawcode, playable = true }), recipes }, jsonOptions));
        File.WriteAllText(aliases, JsonSerializer.Serialize(new { recipes = commands.Select(command => new { recipeId = command.RecipeId, command = command.ChatCommand }) }));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders(); await using var app = builder.Build();
        try
        {
            var checks = 0;
            foreach (var form in CraftMaterialChoices.LuffyForms)
            {
                var fixture = new ServiceFixture(form); var service = new GameCraftService(fixture.Snapshot, catalog, aliases, fixture.Execute);
                var result = await service.RunFixtureAsync("ET02", 1, default, app, expectedSteps: [new("craft", "ET02", MaterialChoiceRawcode: form)]);
                if (result.GetProperty("status").GetString() != "succeeded" || fixture.Calls != 1 || fixture.Stock.GetValueOrDefault("H0BK") != 1 || fixture.Stock.GetValueOrDefault(form) != 0 || fixture.Stage != 2 || fixture.Item != 0) throw new Exception("Real branch service completion failed: " + result);
                checks++;
            }
            foreach (var stage in new int?[] { null, 0, 2 })
            {
                var fixture = new ServiceFixture("H099") { Stage = stage }; var service = new GameCraftService(fixture.Snapshot, catalog, aliases, fixture.Execute);
                var result = await service.RunFixtureAsync("ET02", 1, default, app);
                if (result.GetProperty("status").GetString() != "failed" || fixture.Calls != 0) throw new Exception("Invalid enhancement allowed input: " + result);
                checks++;
            }
            var ambiguous = new ServiceFixture("H099"); ambiguous.Stock["H0B2"] = 1;
            var rejected = await new GameCraftService(ambiguous.Snapshot, catalog, aliases, ambiguous.Execute).RunFixtureAsync("ET02", 1, default, app);
            if (rejected.GetProperty("status").GetString() != "failed" || ambiguous.Calls != 0) throw new Exception("Ambiguous group reached input.");
            return checks + 1;
        }
        finally { File.Delete(catalog); File.Delete(aliases); Directory.Delete(folder); }
    }
    private sealed class ServiceFixture(string form)
    {
        internal Dictionary<string, int> Stock = NikaMaterialChoiceTests.Stock(form); internal int? Stage = 1; internal int Item = 1, Calls; private long sequence;
        internal string Snapshot() => JsonSerializer.Serialize(new { status = "snapshot_read", timestamp = DateTimeOffset.UtcNow, processId = 1234, sequence = ++sequence,
            gameVersion = GameProgression.SupportedVersion, localPlayer = 0, sessionIdentity = "00010000:00020000:00030000", loadedMap = Path.GetFullPath("fixture.w3x"), loadedMapHash = GameProgression.SupportedMapHash,
            catalogMatchesLoadedArchive = true, stableRoots = true, stableUnits = true, readErrors = 0, counts = Stock.Where(item => item.Value > 0).Select(item => new { rawcode = item.Key, count = item.Value, isCharacter = true }),
            resources = new { status = "confirmed", localPlayer = 0, wood = Calls == 0 ? 5 : 0, gold = 0 }, items = new { status = "confirmed", localPlayer = 0, counts = new { AI00 = 0, AI01 = Item, AI02 = 0 }, source = new { readOnly = true } },
            recipeConditions = new { status = "unknown", special = Special(Stage) } });
        internal Task<string?> Execute(PlannedCraftStep step, JsonElement baseline, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested(); Calls++;
            if (step.MaterialChoiceRawcode != form || step.Ingredients.Any(item => item.Rawcode == "H08T")) throw new Exception("Canonical proxy reached fixture input.");
            foreach (var item in step.Ingredients) Stock[item.Rawcode] -= item.Count;
            Stock[step.ResultRawcode] = Stock.GetValueOrDefault(step.ResultRawcode) + step.ResultCount; Stage = 2; Item = 0;
            return Task.FromResult<string?>(null);
        }
    }
}
