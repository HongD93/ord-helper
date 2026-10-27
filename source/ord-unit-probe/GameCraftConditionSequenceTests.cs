using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

static class GameCraftConditionSequenceTests
{
    internal static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ord-condition-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var catalog = Path.Combine(folder, "catalog.json"); var aliases = Path.Combine(folder, "aliases.json");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders();
        await using var app = builder.Build();
        try
        {
            object Recipe(string id, string target, string material) => new { id, resultUnitRawcode = target, resultCount = 1,
                hotkey = (string?)null, casterUnitRawcodes = new[] { material }, ingredients = new[] {
                    new { type = "UNIT", rawcode = material, count = 1 }, new { type = "UBAN", rawcode = target, count = 1 }, new { type = "KING", rawcode = target, count = 1 } } };
            File.WriteAllText(catalog, JsonSerializer.Serialize(new { map = new { sha256 = new string('a', 64) },
                units = new[] { "b001", "m001", "g001" }.Select(rawcode => new { rawcode, playable = true }),
                recipes = new[] { Recipe("r001", "m001", "b001"), Recipe("r002", "g001", "m001") } }));
            File.WriteAllText(aliases, JsonSerializer.Serialize(new { recipes = new[] { "r001", "r002" }.Select(recipeId => new { recipeId, command = "조건조합" }) }));
            async Task<JsonElement> Run(Fixture f) => await new GameCraftService(f.Snapshot, catalog, aliases, f.Execute)
                .RunFixtureAsync("r002", 1, CancellationToken.None, app, adaptive: true);
            var passed = 0;
            void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Condition sequence: " + name); passed++; }
            var normal = new Fixture();
            Check((await Run(normal)).GetProperty("status").GetString() == "succeeded" && normal.Calls == 2,
                "normal mode runs both restricted recipes");
            foreach (var f in new[] { new Fixture { Status = "unknown" }, new Fixture { Banned = true }, new Fixture { King = "blocked" }, new Fixture { Player = 1 } })
                Check((await Run(f)).GetProperty("status").GetString() == "failed" && f.Calls == 0,
                    "unknown banned king restriction and foreign player stop before material use");
            var transition = new Fixture { RestrictAfterFirst = true };
            Check((await Run(transition)).GetProperty("status").GetString() == "failed" && transition.Calls == 1 && transition.Units.GetValueOrDefault("m001") == 1,
                "new restriction between steps blocks next input and preserves intermediate");
            Console.WriteLine($"PASS: recipe restriction craft service {passed}/{passed} fixture cases; no game input.");
        }
        finally { File.Delete(catalog); File.Delete(aliases); Directory.Delete(folder); }
    }

    private sealed class Fixture
    {
        internal Dictionary<string, int> Units { get; } = new() { ["b001"] = 1 };
        internal int Calls { get; private set; }
        internal string Status { get; set; } = "confirmed";
        internal string King { get; set; } = "allowed";
        internal int Player { get; set; }
        internal bool Banned { get; set; }
        internal bool RestrictAfterFirst { get; set; }
        private long sequence;
        private int after;
        internal string Snapshot()
        {
            if (Calls > 0 && ++after >= 2 && RestrictAfterFirst) King = "blocked";
            return JsonSerializer.Serialize(new {
                status = "snapshot_read", timestamp = DateTimeOffset.UtcNow, processId = 1234, sequence = ++sequence,
                gameVersion = "1.28.5.7680", localPlayer = 0, sessionIdentity = "00010000:00020000:00030000",
                loadedMap = Path.GetFullPath("fixture.w3x"), loadedMapHash = new string('a', 64), catalogMatchesLoadedArchive = true,
                stableRoots = true, stableUnits = true, readErrors = 0,
                counts = Units.Where(x => x.Value > 0).Select(x => new { rawcode = x.Key, count = x.Value, isCharacter = true }),
                recipeConditions = new { status = Status, localPlayer = Player, unitBans = new Dictionary<string, bool> { ["m001"] = Banned, ["g001"] = Banned },
                    king = new { status = King, reason = "fixture" }, source = new { readOnly = true } } });
        }
        internal Task<string?> Execute(PlannedCraftStep step, JsonElement baseline, CancellationToken cancel)
        {
            Calls++; foreach (var item in step.Ingredients) Units[item.Rawcode] -= item.Count;
            Units[step.ResultRawcode] = Units.GetValueOrDefault(step.ResultRawcode) + step.ResultCount;
            return Task.FromResult<string?>(null);
        }
    }
}
