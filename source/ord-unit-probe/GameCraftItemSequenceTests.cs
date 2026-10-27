using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

// 자체 사전과 JSON만 바꾼다. 실제 게임·포트·키보드·마우스에 접근하지 않는다.
static class GameCraftItemSequenceTests
{
    internal static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ord-item-sequence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var catalog = Path.Combine(folder, "catalog.json");
        var aliases = Path.Combine(folder, "aliases.json");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        try
        {
            object Recipe(string id, string result, string material, string? item) => new
            {
                id, resultUnitRawcode = result, resultCount = 1, hotkey = (string?)null, casterUnitRawcodes = new[] { material },
                ingredients = item is null ? new[] { new { type = "UNIT", rawcode = material, count = 1 } } :
                    new[] { new { type = "UNIT", rawcode = material, count = 1 }, new { type = "ITEM", rawcode = item, count = 1 } }
            };
            File.WriteAllText(catalog, JsonSerializer.Serialize(new
            {
                map = new { sha256 = new string('a', 64) }, units = new[] { "b001", "m001", "g001" }.Select(rawcode => new { rawcode, playable = true }),
                recipes = new[] { Recipe("r001", "m001", "b001", "AI00"), Recipe("r002", "g001", "m001", null), Recipe("r003", "g001", "m001", "AI01") }
            }));
            File.WriteAllText(aliases, JsonSerializer.Serialize(new { recipes = new[] { "r001", "r002", "r003" }.Select(recipeId => new { recipeId, command = "아이템조합" }) }));
            async Task<JsonElement> Run(Fixture f, string recipe = "r001") => await new GameCraftService(f.Snapshot, catalog, aliases, f.Execute)
                .RunFixtureAsync(recipe, 1, CancellationToken.None, app, adaptive: true);
            var passed = 0;
            void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Item sequence: " + name); passed++; }
            foreach (var status in new[] { "unknown", "missing" })
            {
                var f = new Fixture { ItemStatus = status };
                var result = await Run(f, "r002");
                Check(result.GetProperty("status").GetString() == "failed" && f.Calls == 0, "unknown item blocks whole chain before materials");
            }
            var absent = new Fixture(); absent.Items["AI00"] = 0;
            Check((await Run(absent)).GetProperty("status").GetString() == "failed" && absent.Calls == 0, "confirmed absent makes zero input calls");
            var wrongPlayer = new Fixture { ItemPlayer = 1 };
            Check((await Run(wrongPlayer)).GetProperty("status").GetString() == "failed" && wrongPlayer.Calls == 0, "foreign player cannot authorize");
            var valid = new Fixture();
            Check((await Run(valid, "r002")).GetProperty("status").GetString() == "succeeded" && valid.Calls == 2 && valid.Units.GetValueOrDefault("g001") == 1 && valid.Items["AI00"] == 0,
                "item is paid once and chain continues");
            var delayed = new Fixture { DelayConsumptionReads = 3 };
            Check((await Run(delayed)).GetProperty("status").GetString() == "succeeded" && delayed.Calls == 1 && delayed.ReadsAfterFirst >= 5,
                "unit result waits for delayed entitlement consumption and two fresh observations");
            var reward = new Fixture { GiveOtherItem = true };
            reward.Items["AI01"] = 0;
            Check((await Run(reward)).GetProperty("status").GetString() == "succeeded" && reward.Items["AI01"] == 1,
                "unrelated random item drop does not stop result verification");
            var unconsumed = new Fixture { NeverConsume = true };
            Check((await Run(unconsumed, "r002")).GetProperty("status").GetString() == "failed" && unconsumed.Calls == 1 && unconsumed.Units.GetValueOrDefault("g001") == 0,
                "unit result alone cannot advance without item consumption");
            var lost = new Fixture { LoseSecondItem = true };
            Check((await Run(lost, "r003")).GetProperty("status").GetString() == "failed" && lost.Calls == 1,
                "remaining item lost between steps prevents second input");
            var ordinary = new Fixture { ItemStatus = "unknown" }; ordinary.Units.Clear(); ordinary.Units["m001"] = 1;
            Check((await Run(ordinary, "r002")).GetProperty("status").GetString() == "succeeded" && ordinary.Calls == 1,
                "held intermediate and ordinary craft do not require unknown acquisition item");
            Console.WriteLine($"PASS: item-aware craft service {passed}/{passed} fixture cases; no game input.");
        }
        finally
        {
            File.Delete(catalog); File.Delete(aliases); Directory.Delete(folder);
        }
    }

    private sealed class Fixture
    {
        internal Dictionary<string, int> Units { get; } = new() { ["b001"] = 1 };
        internal Dictionary<string, int> Items { get; } = new() { ["AI00"] = 1, ["AI01"] = 1, ["AI02"] = 0 };
        internal string ItemStatus { get; set; } = "confirmed";
        internal int ItemPlayer { get; set; }
        internal int Calls { get; private set; }
        internal int DelayConsumptionReads { get; set; }
        internal int ReadsAfterFirst { get; private set; }
        internal bool GiveOtherItem { get; set; }
        internal bool NeverConsume { get; set; }
        internal bool LoseSecondItem { get; set; }
        private long sequence;
        private CraftIngredient[] pending = [];
        internal string Snapshot()
        {
            if (Calls > 0)
            {
                ReadsAfterFirst++;
                if (ReadsAfterFirst > DelayConsumptionReads && !NeverConsume)
                {
                    foreach (var item in pending) Items[item.Rawcode] -= item.Count;
                    pending = [];
                }
                if (LoseSecondItem && ReadsAfterFirst >= 3) Items["AI01"] = 0;
            }
            return JsonSerializer.Serialize(new
            {
                status = NeverConsume && ReadsAfterFirst > 4 ? "world_not_ready" : "snapshot_read", timestamp = DateTimeOffset.UtcNow,
                processId = 1234, sequence = ++sequence, gameVersion = "1.28.5.7680", localPlayer = 0,
                sessionIdentity = "00010000:00020000:00030000", loadedMap = Path.GetFullPath("fixture.w3x"), loadedMapHash = new string('a', 64),
                catalogMatchesLoadedArchive = true, stableRoots = true, stableUnits = true, readErrors = 0,
                items = new { status = ItemStatus, localPlayer = ItemPlayer, counts = Items, source = new { readOnly = true } },
                counts = Units.Where(item => item.Value > 0).Select(item => new { rawcode = item.Key, count = item.Value, isCharacter = true })
            });
        }
        internal Task<string?> Execute(PlannedCraftStep step, JsonElement baseline, CancellationToken cancel)
        {
            Calls++;
            foreach (var ingredient in step.Ingredients) Units[ingredient.Rawcode] -= ingredient.Count;
            Units[step.ResultRawcode] = Units.GetValueOrDefault(step.ResultRawcode) + step.ResultCount;
            pending = step.ItemRequirements ?? [];
            if (GiveOtherItem) Items["AI01"] = 1;
            return Task.FromResult<string?>(null);
        }
    }
}
