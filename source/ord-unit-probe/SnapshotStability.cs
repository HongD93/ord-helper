using System.Text.Json;

static class SnapshotStability
{
    // 전역 유닛 배열은 적 생성·제거 시 길이와 주소가 바뀐다. 게임 세션의 정체성과 분리한다.
    internal static bool SameSession(Roots? current, Roots expected) => current is not null &&
        current.Ui == expected.Ui && current.Players == expected.Players && current.Table == expected.Table &&
        current.LocalPlayer == expected.LocalPlayer;

    internal static bool SameInventory(OwnedData first, OwnedData? second, IReadOnlySet<string> inventoryIds) =>
        SameLiveInventory(first, second, inventoryIds, null);

    internal static bool SameLiveInventory(OwnedData first, OwnedData? second, IReadOnlySet<string> inventoryIds, LiveInventoryIdentity? identity)
    {
        if (second is null || first.ReadErrors != 0 || second.ReadErrors != 0) return false;
        return Relevant(first).SequenceEqual(Relevant(second));

        IEnumerable<(uint Address, string Rawcode)> Relevant(OwnedData data) => data.Units
            .Where(unit => UnitRules.IsActive(unit) && !UnitRules.IsNonIngredientCombatObject(unit.Rawcode) &&
                (inventoryIds.Count == 0 || inventoryIds.Contains(unit.Rawcode)) &&
                identity?.Temporary.Contains(unit.Rawcode) != true)
            .Select(unit => (unit.Address, Rawcode: identity?.Canonical.GetValueOrDefault(unit.Rawcode) ?? unit.Rawcode))
            .OrderBy(unit => unit.Address).ThenBy(unit => unit.Rawcode, StringComparer.Ordinal);
    }
}

sealed class LiveInventoryIdentity
{
    private LiveInventoryIdentity(HashSet<string> temporary, Dictionary<string, string> canonical)
    {
        Temporary = temporary;
        Canonical = canonical;
    }

    internal IReadOnlySet<string> Temporary { get; }
    internal IReadOnlyDictionary<string, string> Canonical { get; }

    internal static LiveInventoryIdentity? Load(string path, JsonElement catalog, string mapHash, IReadOnlySet<string> inventoryIds)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return Parse(document.RootElement, catalog, mapHash, inventoryIds);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static LiveInventoryIdentity? Parse(JsonElement policy, JsonElement catalog, string mapHash, IReadOnlySet<string> inventoryIds)
    {
        try
        {
            if (mapHash != GameProgression.SupportedMapHash ||
                catalog.GetProperty("map").GetProperty("sha256").GetString() != mapHash ||
                policy.GetProperty("schemaVersion").GetInt32() != 1 ||
                policy.GetProperty("mapSha256").GetString() != mapHash) return null;

            var unitCodes = catalog.GetProperty("units").EnumerateArray().Select(unit => unit.GetProperty("rawcode").GetString()).ToHashSet(StringComparer.Ordinal);
            var commandCodes = new HashSet<string>(StringComparer.Ordinal);
            var resultCodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in catalog.GetProperty("recipes").EnumerateArray())
            {
                if (recipe.TryGetProperty("resultUnitRawcode", out var result) && result.GetString() is { } resultCode) resultCodes.Add(resultCode);
                foreach (var ingredient in recipe.GetProperty("ingredients").EnumerateArray())
                {
                    if (ingredient.GetProperty("type").GetString() != "UNIT") continue;
                    if (ingredient.GetProperty("rawcode").GetString() is { } code) commandCodes.Add(code);
                    if (ingredient.TryGetProperty("logicalRawcode", out var logical) && logical.GetString() is { } alias) commandCodes.Add(alias);
                    if (ingredient.TryGetProperty("acceptedRawcodes", out var accepted) && accepted.ValueKind != JsonValueKind.Null)
                        foreach (var variant in accepted.EnumerateArray())
                            if (variant.GetString() is { } variantCode) commandCodes.Add(variantCode);
                }
                if (recipe.TryGetProperty("casterUnitRawcodes", out var casters))
                    foreach (var caster in casters.EnumerateArray())
                        if (caster.GetString() is { } code) commandCodes.Add(code);
            }
            var progressSources = new HashSet<string>(StringComparer.Ordinal);
            var progressResults = new HashSet<string>(StringComparer.Ordinal);
            if (catalog.TryGetProperty("progressTransformations", out var progress))
                foreach (var transform in progress.GetProperty("entries").EnumerateArray())
                {
                    if (transform.GetProperty("sourceRawcode").GetString() is { } source) progressSources.Add(source);
                    if (transform.GetProperty("resultRawcode").GetString() is { } result) progressResults.Add(result);
                }

            var temporary = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unit in policy.GetProperty("temporaryRawcodes").EnumerateArray())
            {
                if (unit.GetString() is not { } code || !unitCodes.Contains(code) ||
                    commandCodes.Contains(code) || resultCodes.Contains(code) ||
                    progressSources.Contains(code) || progressResults.Contains(code) || !temporary.Add(code)) return null;
            }

            var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var form in policy.GetProperty("canonicalByRawcode").EnumerateObject())
            {
                if (form.Value.GetString() is not { } original || form.Name == original ||
                    !inventoryIds.Contains(form.Name) || !inventoryIds.Contains(original) ||
                    temporary.Contains(form.Name) || temporary.Contains(original) ||
                    commandCodes.Contains(form.Name) || commandCodes.Contains(original) ||
                    progressSources.Contains(form.Name) || progressResults.Contains(form.Name) || progressResults.Contains(original) ||
                    resultCodes.Contains(form.Name) || !canonical.TryAdd(form.Name, original)) return null;
            }
            return canonical.Values.Any(canonical.ContainsKey) ? null : new(temporary, canonical);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
}
