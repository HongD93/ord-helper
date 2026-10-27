sealed record PlannedCraftStep(string Type, string? RecipeId, string? ChoiceRawcode,
    CraftIngredient[] Ingredients, string ResultRawcode, int ResultCount, CraftIngredient[]? ItemRequirements = null, CraftRecipeCondition[]? Conditions = null,
    string? MaterialChoiceRawcode = null);

sealed record RequestedCraftStep(string Type, string? RecipeId = null, string? ResultRawcode = null, string? MaterialChoiceRawcode = null);

sealed record CraftPlan(bool Executable, PlannedCraftStep[] Steps, CraftIngredient[] Missing, string? Error,
    IReadOnlyDictionary<string, int> RemainingInventory, string? Reason)
{
    public int StepCount => Steps.Length;
    public int ChoiceWispCount => Steps.Count(step => step.Type == "choice");
}

// 게임이나 파일에 접근하지 않는 순수 계획기다. 실제 실행·추가 조건 검증은 서비스와 맵이 담당한다.
static class CraftPlanner
{
    internal static readonly string[] CommonChoiceResults = ["h001", "h002", "h003", "h004", "h005", "h006", "h007", "h008", "h009"];
    private static readonly HashSet<string> CommonChoiceSet = new(CommonChoiceResults, StringComparer.Ordinal);
    private const string ChoiceWisp = "e018";

    // 공동 덱 계획에서 고른 순서를 재탐색 없이 검증한다. 게임 입력을 실행하지 않는다.
    public static CraftPlan ValidateExpected(string recipeId, IEnumerable<CraftCommand> supported,
        IReadOnlyDictionary<string, int> inventory, IReadOnlyList<RequestedCraftStep> requested,
        bool allowChoiceWisps = false, IReadOnlyDictionary<string, int>? protectedCounts = null,
        IReadOnlyDictionary<string, int>? items = null, CraftConditions.State? recipeConditions = null)
    {
        var original = new Dictionary<string, int>(inventory, StringComparer.Ordinal);
        try
        {
            if (original.Any(item => !Rawcode(item.Key) || item.Value is < 0 or > 100000) || protectedCounts?.Count > 128 ||
                items?.Any(item => !CraftItems.Codes.Contains(item.Key, StringComparer.Ordinal) || item.Value is < 0 or > 1) == true)
                return Failure("invalid_expected_plan", original);
            var commands = supported.ToDictionary(command => command.RecipeId, StringComparer.Ordinal);
            if (!commands.TryGetValue(recipeId, out var root) || requested.Count is < 1 or > 64 ||
                requested[^1].Type != "craft" || requested[^1].RecipeId != recipeId)
                return Failure("invalid_expected_plan", original);
            var held = new Dictionary<string, int>(protectedCounts ?? new Dictionary<string, int>(), StringComparer.Ordinal);
            foreach (var item in held)
                if (!Rawcode(item.Key) || item.Value < 1 || original.GetValueOrDefault(item.Key) < item.Value)
                    return Failure("protected_material_missing", original);
            held[root.ResultRawcode] = Math.Max(held.GetValueOrDefault(root.ResultRawcode), original.GetValueOrDefault(root.ResultRawcode));
            var allowedResults = new HashSet<string>(StringComparer.Ordinal) { root.ResultRawcode };
            var pending = new Queue<string>(); pending.Enqueue(root.ResultRawcode);
            while (pending.TryDequeue(out var result))
                foreach (var command in commands.Values.Where(command => command.ResultRawcode == result))
                    foreach (var item in CraftMaterialChoices.Variants(command).SelectMany(variant => variant.Ingredients))
                        if (allowedResults.Add(item.Rawcode)) pending.Enqueue(item.Rawcode);
            var stock = new Dictionary<string, int>(original, StringComparer.Ordinal);
            var itemStock = items is null ? null : new Dictionary<string, int>(items, StringComparer.Ordinal);
            var steps = new List<PlannedCraftStep>();
            var conditionStock = recipeConditions;
            for (var index = 0; index < requested.Count; index++)
            {
                var item = requested[index]; PlannedCraftStep step;
                if (item.Type == "choice" && allowChoiceWisps && item.RecipeId is null && item.MaterialChoiceRawcode is null &&
                    item.ResultRawcode is not null && CommonChoiceSet.Contains(item.ResultRawcode))
                    step = new("choice", null, item.ResultRawcode, [new(ChoiceWisp, 1)], item.ResultRawcode, 1);
                else if (item.Type == "craft" && item.RecipeId is not null && commands.TryGetValue(item.RecipeId, out var command) &&
                    (item.ResultRawcode is null || item.ResultRawcode == command.ResultRawcode) &&
                    CraftMaterialChoices.TryResolve(command, item.MaterialChoiceRawcode, out var resolved))
                {
                    if (!CraftMaterialChoices.UniqueCurrentSource(resolved, stock)) return Failure("ambiguous_material_choice", original);
                    step = new("craft", resolved.RecipeId, null, resolved.Ingredients, resolved.ResultRawcode, resolved.ResultCount, resolved.ItemRequirements, resolved.Conditions, resolved.MaterialChoiceRawcode);
                }
                else return Failure("invalid_expected_plan", original);
                if (!allowedResults.Contains(step.ResultRawcode) ||
                    step.ResultRawcode == root.ResultRawcode && index != requested.Count - 1)
                    return Failure("invalid_expected_plan", original);
                var neededItems = step.ItemRequirements ?? [];
                if (!CraftConditions.TryProject(conditionStock, step.Conditions ?? [], out conditionStock, out _, out _, out var conditionError))
                    return Failure(conditionError!, original);
                if (!CraftItems.Available(itemStock, neededItems, out _))
                    return Failure(itemStock is null ? "items_unconfirmed" : "missing_item", original);
                foreach (var requiredItem in neededItems) itemStock![requiredItem.Rawcode] -= requiredItem.Count;
                foreach (var ingredient in step.Ingredients)
                {
                    var available = stock.GetValueOrDefault(ingredient.Rawcode);
                    if (available - ingredient.Count < held.GetValueOrDefault(ingredient.Rawcode))
                        return Failure("expected_plan_material_changed", original);
                    stock[ingredient.Rawcode] = checked(available - ingredient.Count);
                }
                stock[step.ResultRawcode] = checked(stock.GetValueOrDefault(step.ResultRawcode) + step.ResultCount);
                if (held.Any(value => stock.GetValueOrDefault(value.Key) < value.Value))
                    return Failure("protected_material_missing", original);
                steps.Add(step);
            }
            if (stock.GetValueOrDefault(root.ResultRawcode) - original.GetValueOrDefault(root.ResultRawcode) != root.ResultCount)
                return Failure("invalid_expected_plan", original);
            return new(true, steps.ToArray(), [], null, Positive(stock), null);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or InvalidOperationException or NullReferenceException)
        {
            return Failure("invalid_expected_plan", original);
        }
    }

    public static CraftPlan Plan(string recipeId, IEnumerable<CraftCommand> supported,
        IReadOnlyDictionary<string, int> inventory, bool allowChoiceWisps = false, int maxSteps = 64, int maxSearchNodes = 12000,
        IReadOnlyDictionary<string, int>? protectedCounts = null, IReadOnlyDictionary<string, int>? items = null, CraftConditions.State? recipeConditions = null)
    {
        var original = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            if (!Rawcode(recipeId) || maxSteps is < 1 or > 512 || maxSearchNodes is < 1 or > 200000 ||
                items?.Any(item => !CraftItems.Codes.Contains(item.Key, StringComparer.Ordinal) || item.Value is < 0 or > 1) == true)
                return Failure("invalid_input", original);
            foreach (var item in inventory)
            {
                if (!Rawcode(item.Key) || item.Value is < 0 or > 100000) return Failure("invalid_input", original);
                original.Add(item.Key, item.Value);
            }
            var commands = new Dictionary<string, CraftCommand>(StringComparer.Ordinal);
            foreach (var command in supported)
            {
                if (!Rawcode(command.RecipeId) || !Rawcode(command.ResultRawcode) || command.ResultCount is < 1 or > 1000 ||
                    command.Ingredients.Any(i => !Rawcode(i.Rawcode) || i.Count is < 1 or > 10000) ||
                    (command.Conditions ?? []).Any(i => !Rawcode(i.Rawcode) || i.Count is < 1 or > 10000 || !CraftConditions.Types.Contains(i.Type, StringComparer.Ordinal)) ||
                    (command.ItemRequirements ?? []).Any(i => !Rawcode(i.Rawcode) || i.Count is < 1 or > 10000) || commands.ContainsKey(command.RecipeId))
                    return Failure("invalid_input", original);
                var ingredients = CraftMaterialChoices.NormalizeIngredients(command.RecipeId, command.ResultRawcode, command.Ingredients);
                commands.Add(command.RecipeId, command with { Ingredients = ingredients });
            }
            if (!commands.TryGetValue(recipeId, out var root)) return Failure("unknown_recipe", original);
            var available = new Dictionary<string, int>(original, StringComparer.Ordinal);
            var held = new Dictionary<string, int>(StringComparer.Ordinal);
            if (protectedCounts is not null)
            {
                if (protectedCounts.Count > 128) return Failure("invalid_protection", original);
                foreach (var item in protectedCounts)
                {
                    if (!Rawcode(item.Key) || item.Value is < 1 or > 100000) return Failure("invalid_protection", original);
                    if (item.Value > original.GetValueOrDefault(item.Key)) return Failure("protected_material_missing", original);
                    held.Add(item.Key, item.Value);
                }
            }
            // 기존 목표 유닛을 하위 조합의 재료로 쓰면 목표가 늘지 않을 수 있으므로 별도로 보호한다.
            var protectedRootCount = available.GetValueOrDefault(root.ResultRawcode);
            if (protectedRootCount > 0) held[root.ResultRawcode] = protectedRootCount;
            foreach (var item in held) available[item.Key] = checked(available.GetValueOrDefault(item.Key) - item.Value);
            available[root.ResultRawcode] = 0;
            var search = new Search(commands.Values, allowChoiceWisps, maxSteps, maxSearchNodes, recipeConditions, held);
            State? bestFailure = null;
            foreach (var candidate in search.Run(root, new State(available, items), new HashSet<string>(StringComparer.Ordinal), 0))
            {
                if (candidate.Missing.Count == 0 && candidate.MissingItems.Count == 0 && candidate.ConditionError is null)
                {
                    foreach (var item in held) candidate.Stock[item.Key] = checked(candidate.Stock.GetValueOrDefault(item.Key) + item.Value);
                    if (candidate.Stock.GetValueOrDefault(root.ResultRawcode) <= original.GetValueOrDefault(root.ResultRawcode))
                        return Failure("recipe_cycle", original);
                    return new(true, candidate.Steps.ToArray(), [], null, Positive(candidate.Stock), null);
                }
                if (bestFailure is null || Score(candidate) < Score(bestFailure)) bestFailure = candidate;
                if (search.SearchLimited) break;
            }
            var reason = search.SearchLimited ? "search_limit" : search.StepLimited ? "step_limit" :
                bestFailure?.Cycle == true ? "recipe_cycle" : bestFailure?.ConditionError ?? (bestFailure?.MissingItems.Count > 0
                    ? items is null ? "items_unconfirmed" : "missing_item" : "missing_material");
            return Failure(reason, original, bestFailure?.Missing);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or InvalidOperationException)
        {
            return Failure("invalid_input", original);
        }
    }

    private static long Score(State state) => (state.Cycle ? 1_000_000_000_000L : 0) +
        (state.Missing.Values.Sum(value => (long)value) + state.MissingItems.Values.Sum(value => (long)value) + (state.ConditionError is null ? 0 : 1)) * 1000 + state.Missing.Count + state.MissingItems.Count;
    private static bool Rawcode(string? value) => value is { Length: 4 } && value.All(c => c is >= ' ' and <= '~');
    private static IReadOnlyDictionary<string, int> Positive(Dictionary<string, int> stock) =>
        stock.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static CraftPlan Failure(string reason, Dictionary<string, int> original, Dictionary<string, int>? missing = null) =>
        new(false, [], missing?.Where(pair => pair.Value > 0).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new CraftIngredient(pair.Key, pair.Value)).ToArray() ?? [], reason switch
        {
            "unknown_recipe" => "이 조합은 자동 조합을 지원하지 않습니다.",
            "recipe_cycle" => "재료 조합이 순환해 자동 조합할 수 없습니다.",
            "step_limit" => "연속 조합 단계가 너무 많습니다.",
            "search_limit" => "조합 경로 확인 범위를 넘었습니다.",
            "invalid_input" => "조합 정보를 확인하지 못했습니다.",
            "invalid_protection" => "보존할 유닛 정보를 확인하지 못했습니다.",
            "invalid_expected_plan" => "제작 순서를 확인하지 못해 조합하지 않았습니다.",
            "expected_plan_material_changed" => "목표 덱의 재료가 바뀌어 조합하지 않았습니다.",
            "protected_material_missing" => "보존할 유닛 수량이 바뀌었습니다. 현재 수량을 확인해 주세요.",
            "ambiguous_material_choice" => "소모할 루피 초월 유닛을 확정하지 못했습니다.",
            "items_unconfirmed" => "필요한 아이템의 보유 여부를 확인하지 못했습니다.",
            "missing_item" => "조합 경로에 필요한 아이템이 부족합니다.",
            "recipe_conditions_unknown" => "유닛 밴과 패왕의길 제한을 확인하지 못했습니다.",
            "recipe_conditions_blocked" => "유닛 밴 또는 패왕의길 제한으로 조합할 수 없습니다.",
            _ => "필요한 재료가 부족하거나 하위 자동 조합을 지원하지 않습니다."
        }, Positive(original), reason);

    private sealed class State
    {
        public Dictionary<string, int> Stock { get; }
        public Dictionary<string, int>? Items { get; }
        public Dictionary<string, int> Missing { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> MissingItems { get; } = new(StringComparer.Ordinal);
        public List<PlannedCraftStep> Steps { get; } = [];
        public bool Cycle { get; set; }
        public string? ConditionError { get; set; }
        public State(Dictionary<string, int> stock, IReadOnlyDictionary<string, int>? items = null)
        {
            Stock = new(stock, StringComparer.Ordinal);
            Items = items is null ? null : new(items, StringComparer.Ordinal);
        }
        public State Clone()
        {
            var copy = new State(Stock, Items) { Cycle = Cycle, ConditionError = ConditionError };
            foreach (var pair in Missing) copy.Missing.Add(pair.Key, pair.Value);
            foreach (var pair in MissingItems) copy.MissingItems.Add(pair.Key, pair.Value);
            copy.Steps.AddRange(Steps);
            return copy;
        }
        public int Take(string rawcode, int count)
        {
            var take = Math.Min(count, Stock.GetValueOrDefault(rawcode));
            Stock[rawcode] = Stock.GetValueOrDefault(rawcode) - take;
            return take;
        }
    }

    private sealed class Search
    {
        private readonly Dictionary<string, CraftCommand[]> byResult;
        private readonly bool choices;
        private readonly int maxSteps, maxNodes;
        private readonly CraftConditions.State? recipeConditions;
        private readonly IReadOnlyDictionary<string, int> held;
        private int nodes;
        public bool SearchLimited { get; private set; }
        public bool StepLimited { get; private set; }
        public Search(IEnumerable<CraftCommand> commands, bool choices, int maxSteps, int maxNodes, CraftConditions.State? recipeConditions, IReadOnlyDictionary<string, int> held)
        {
            byResult = commands.GroupBy(c => c.ResultRawcode, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => g.OrderBy(c => c.RecipeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            this.choices = choices; this.maxSteps = maxSteps; this.maxNodes = maxNodes;
            this.recipeConditions = recipeConditions;
            this.held = held;
        }
        private bool Visit(int depth)
        {
            if (++nodes <= maxNodes && depth <= Math.Min(1024, maxSteps * 4 + 64)) return true;
            SearchLimited = true;
            return false;
        }
        public IEnumerable<State> Run(CraftCommand command, State input, HashSet<string> ancestors, int depth)
        {
            foreach (var variant in CraftMaterialChoices.Variants(command).OrderByDescending(variant =>
                variant.MaterialChoiceRawcode is { } choice ? input.Stock.GetValueOrDefault(choice) : 0))
                foreach (var result in RunResolved(variant, input, ancestors, depth)) yield return result;
        }
        private IEnumerable<State> RunResolved(CraftCommand command, State input, HashSet<string> ancestors, int depth)
        {
            if (!Visit(depth)) yield break;
            if (input.Steps.Count >= maxSteps) { StepLimited = true; yield break; }
            if (ancestors.Contains(command.ResultRawcode)) yield break;
            var path = new HashSet<string>(ancestors, StringComparer.Ordinal) { command.ResultRawcode };
            var state = input.Clone();
            if (command.MaterialChoiceRawcode is not null && CraftMaterialChoices.FormCount(state.Stock, held) > 1)
            { state.ConditionError = "ambiguous_material_choice"; yield return state; yield break; }
            if (!CraftConditions.Available(recipeConditions, command.Conditions ?? [], out _, out var error)) state.ConditionError ??= error;
            var itemRequirements = command.ItemRequirements ?? [];
            foreach (var group in itemRequirements.GroupBy(item => item.Rawcode, StringComparer.Ordinal))
            {
                var needed = checked(group.Sum(item => item.Count));
                var have = CraftItems.Codes.Contains(group.Key, StringComparer.Ordinal) ? state.Items?.GetValueOrDefault(group.Key) ?? 0 : 0;
                var take = Math.Min(needed, have);
                if (state.Items is not null) state.Items[group.Key] = have - take;
                if (take < needed) state.MissingItems[group.Key] = checked(state.MissingItems.GetValueOrDefault(group.Key) + needed - take);
            }
            // 먼저 모든 직속 형제 재료를 예약한다. 앞선 하위 조합이 뒤 형제의 현재 재료를 먹지 못한다.
            var deficits = command.Ingredients.Select(ingredient => new CraftIngredient(ingredient.Rawcode,
                ingredient.Count - state.Take(ingredient.Rawcode, ingredient.Count))).Where(i => i.Count > 0).ToArray();
            foreach (var ready in ResolveSiblings(deficits, 0, state, path, depth + 1))
            {
                if (ready.Steps.Count >= maxSteps) { StepLimited = true; continue; }
                if (command.MaterialChoiceRawcode is not null && CraftMaterialChoices.FormCount(ready.Stock, held) > 0)
                { ready.ConditionError = "ambiguous_material_choice"; yield return ready; continue; }
                ready.Steps.Add(new("craft", command.RecipeId, null, command.Ingredients.ToArray(), command.ResultRawcode, command.ResultCount, itemRequirements, command.Conditions, command.MaterialChoiceRawcode));
                if (!CraftConditions.Available(recipeConditions, ready.Steps.SelectMany(step => step.Conditions ?? []), out _, out var combinedConditionError))
                    ready.ConditionError ??= combinedConditionError;
                ready.Stock[command.ResultRawcode] = checked(ready.Stock.GetValueOrDefault(command.ResultRawcode) + command.ResultCount);
                yield return ready;
            }
        }
        private IEnumerable<State> ResolveSiblings(CraftIngredient[] deficits, int index, State state, HashSet<string> path, int depth)
        {
            if (!Visit(depth)) yield break;
            if (index == deficits.Length) { yield return state; yield break; }
            var deficit = deficits[index];
            foreach (var reserved in MakeAndReserve(deficit.Rawcode, deficit.Count, state, path, depth + 1))
                foreach (var complete in ResolveSiblings(deficits, index + 1, reserved, path, depth + 1))
                    yield return complete;
        }
        private IEnumerable<State> MakeAndReserve(string rawcode, int requested, State input, HashSet<string> path, int depth)
        {
            if (!Visit(depth)) yield break;
            var state = input.Clone();
            var remaining = requested - state.Take(rawcode, requested);
            if (remaining <= 0) { yield return state; yield break; }
            var producedBranch = false;
            var cycle = false;
            foreach (var command in byResult.GetValueOrDefault(rawcode) ?? [])
            {
                if (path.Contains(rawcode)) { cycle = true; continue; }
                foreach (var made in Run(command, state, path, depth + 1))
                {
                    producedBranch = true;
                    foreach (var reserved in MakeAndReserve(rawcode, remaining, made, path, depth + 1)) yield return reserved;
                }
                if (SearchLimited) yield break;
            }
            if (choices && CommonChoiceSet.Contains(rawcode) && state.Stock.GetValueOrDefault(ChoiceWisp) > 0)
            {
                if (state.Steps.Count >= maxSteps) StepLimited = true;
                else
                {
                    producedBranch = true;
                    var choice = state.Clone();
                    choice.Take(ChoiceWisp, 1);
                    choice.Stock[rawcode] = checked(choice.Stock.GetValueOrDefault(rawcode) + 1);
                    choice.Steps.Add(new("choice", null, ChoiceWisp, [new(ChoiceWisp, 1)], rawcode, 1));
                    foreach (var reserved in MakeAndReserve(rawcode, remaining, choice, path, depth + 1)) yield return reserved;
                }
            }
            if (!producedBranch)
            {
                var missing = state.Clone();
                missing.Missing[rawcode] = checked(missing.Missing.GetValueOrDefault(rawcode) + remaining);
                missing.Cycle |= cycle;
                yield return missing;
            }
        }
    }
}
