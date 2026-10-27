// 동일 맵 ET02의 H08T 그룹만 다룬다. 보유량에는 가상 유닛을 더하지 않는다.
static class CraftMaterialChoices
{
    internal static readonly string[] LuffyForms = ["H099", "H0B2"];
    internal static readonly string[] NativeCandidateForms = ["H099", "H0B2", "H0B3"];
    internal static CraftIngredient[] NormalizeIngredients(string recipe, string result, IEnumerable<CraftIngredient> values)
    {
        var ingredients = values.ToArray();
        var alternatives = ingredients.Where(item => item.AcceptedRawcodes is not null).ToArray();
        if (alternatives.Length > 0 && (recipe != "ET02" || result != "H0BK" || alternatives.Length != 1 ||
            alternatives[0].Rawcode != "H08T" || alternatives[0].Count != 1 ||
            alternatives[0].AcceptedRawcodes is not { Length: 2 } accepted || !accepted.SequenceEqual(LuffyForms) ||
            ingredients.Count(item => item.Rawcode == "H08T") != 1))
            throw new ArgumentException("Unverified recipe material alternatives.");
        return ingredients.GroupBy(item => item.Rawcode, StringComparer.Ordinal).Select(group =>
            new CraftIngredient(group.Key, checked(group.Sum(item => item.Count)), group.First().AcceptedRawcodes?.ToArray())).ToArray();
    }
    internal static CraftCommand[] Variants(CraftCommand command)
    {
        if (!command.Ingredients.Any(item => item.AcceptedRawcodes is not null)) return [command];
        NormalizeIngredients(command.RecipeId, command.ResultRawcode, command.Ingredients);
        return LuffyForms.Select(choice => TryResolve(command, choice, out var resolved) ? resolved : throw new ArgumentException("Invalid material choice.")).ToArray();
    }
    internal static bool TryResolve(CraftCommand command, string? choice, out CraftCommand resolved)
    {
        resolved = command;
        if (!command.Ingredients.Any(item => item.AcceptedRawcodes is not null))
            return choice is null && command.MaterialChoiceRawcode is null;
        try { NormalizeIngredients(command.RecipeId, command.ResultRawcode, command.Ingredients); }
        catch (Exception error) when (error is ArgumentException or OverflowException) { return false; }
        if (choice is null || !LuffyForms.Contains(choice, StringComparer.Ordinal) || command.MaterialChoiceRawcode is not null ||
            (command.Conditions ?? []).Count(condition => condition.Type == "NIKA" && condition.Rawcode == "NIKA" && condition.Count == 1) != 1) return false;
        resolved = command with { MaterialChoiceRawcode = choice, Ingredients = command.Ingredients.Select(item =>
            item.AcceptedRawcodes is null ? item : new CraftIngredient(choice, item.Count)).ToArray() };
        return true;
    }
    internal static bool SameIngredients(IReadOnlyList<CraftIngredient> expected, IReadOnlyList<CraftIngredient> actual) =>
        expected.Count == actual.Count && expected.Zip(actual).All(pair => pair.First.Rawcode == pair.Second.Rawcode &&
            pair.First.Count == pair.Second.Count && pair.Second.AcceptedRawcodes is null);
    internal static int FormCount(IReadOnlyDictionary<string, int> stock, IReadOnlyDictionary<string, int>? held = null) =>
        LuffyForms.Sum(code => checked(stock.GetValueOrDefault(code) + (held?.GetValueOrDefault(code) ?? 0)));
    internal static bool UniqueCurrentSource(CraftCommand command, IReadOnlyDictionary<string, int> stock) => command.MaterialChoiceRawcode is null ||
        command.RecipeId == "ET02" && command.ResultRawcode == "H0BK" && LuffyForms.Contains(command.MaterialChoiceRawcode, StringComparer.Ordinal) &&
        FormCount(stock) == 1 && stock.GetValueOrDefault(command.MaterialChoiceRawcode) == 1;
    internal static bool UniqueNativeSource(CraftCommand command, OwnedData owned)
    {
        if (command.MaterialChoiceRawcode is null) return command.RecipeId != "ET02";
        if (command.RecipeId != "ET02" || command.ResultRawcode != "H0BK" || !LuffyForms.Contains(command.MaterialChoiceRawcode, StringComparer.Ordinal) ||
            command.Ingredients.Count(item => item.Rawcode == command.MaterialChoiceRawcode && item.Count == 1 && item.AcceptedRawcodes is null) != 1 ||
            command.Ingredients.Any(item => item.Rawcode == "H08T" || item.AcceptedRawcodes is not null)) return false;
        // 맵은 숨김 생존자도 H08T 그룹에서 제거할 수 있다. 비활성 개체도 후보 수에서 빼지 않는다.
        var sources = owned.Units.Where(unit => NativeCandidateForms.Contains(unit.Rawcode, StringComparer.Ordinal)).ToArray();
        return owned.ReadErrors == 0 && sources.Length == 1 && sources[0].Rawcode == command.MaterialChoiceRawcode && UnitRules.IsActive(sources[0]);
    }
}
