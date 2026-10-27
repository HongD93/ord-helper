// 검증된 논리 경로의 선택 위습만 앞에 모은다. 일반 조합의 상대 순서와 최종 재고는 유지한다.
static class ChoiceWispBatchPlan
{
    internal const int MaximumGroupSize = 12;

    internal static int LogicalCount(IEnumerable<PlannedCraftStep> steps) =>
        checked(steps.Sum(step => step.Type == "choice" ? step.ResultCount : 1));

    internal static bool TryCreate(IReadOnlyList<PlannedCraftStep> original,
        IReadOnlyDictionary<string, int> inventory, IReadOnlyDictionary<string, int> protectedCounts,
        out PlannedCraftStep[] scheduled)
    {
        scheduled = [];
        try
        {
            if (original.Count == 0 || original.Any(step => step.Type is not ("choice" or "craft"))) return false;
            if (original.Any(step => step.Type == "choice" &&
                (step.RecipeId is not null || step.ResultCount != 1 ||
                 !CraftPlanner.CommonChoiceResults.Contains(step.ResultRawcode, StringComparer.Ordinal) ||
                 step.Ingredients is not [{ Rawcode: "e018", Count: 1 }] ||
                 step.ItemRequirements is { Length: > 0 } || step.Conditions is { Length: > 0 }))) return false;
            // 위습을 만들거나 소비하는 별도 조합이 있으면 선준비로 의존성을 바꿀 수 없다.
            if (original.Any(step => step.Type == "craft" &&
                (step.ResultRawcode == "e018" || step.Ingredients.Any(item => item.Rawcode == "e018")))) return false;
            var choices = original.Where(step => step.Type == "choice").GroupBy(step => step.ResultRawcode, StringComparer.Ordinal);
            var result = new List<PlannedCraftStep>();
            foreach (var group in choices)
            {
                var remaining = group.Count();
                while (remaining > 0)
                {
                    var count = Math.Min(MaximumGroupSize, remaining);
                    result.Add(group.First() with { ResultCount = count, Ingredients = [new("e018", count)] });
                    remaining -= count;
                }
            }
            result.AddRange(original.Where(step => step.Type == "craft"));
            if (!TryReplay(original, inventory, protectedCounts, out var before) ||
                !TryReplay(result, inventory, protectedCounts, out var after) ||
                before.Count != after.Count || before.Any(pair => after.GetValueOrDefault(pair.Key) != pair.Value) ||
                LogicalCount(original) != LogicalCount(result)) return false;
            scheduled = result.ToArray();
            return true;
        }
        catch (Exception error) when (error is OverflowException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReplay(IEnumerable<PlannedCraftStep> steps, IReadOnlyDictionary<string, int> inventory,
        IReadOnlyDictionary<string, int> protectedCounts, out Dictionary<string, int> stock)
    {
        stock = [];
        var working = new Dictionary<string, int>(inventory, StringComparer.Ordinal);
        if (working.Any(pair => pair.Value < 0) || protectedCounts.Any(pair => pair.Value < 0 || working.GetValueOrDefault(pair.Key) < pair.Value)) return false;
        foreach (var step in steps)
        {
            if (step.ResultCount <= 0 || step.Ingredients.Any(item => item.Count <= 0)) return false;
            foreach (var item in step.Ingredients)
            {
                var available = working.GetValueOrDefault(item.Rawcode);
                if (available - item.Count < protectedCounts.GetValueOrDefault(item.Rawcode)) return false;
                working[item.Rawcode] = checked(available - item.Count);
            }
            working[step.ResultRawcode] = checked(working.GetValueOrDefault(step.ResultRawcode) + step.ResultCount);
            if (protectedCounts.Any(pair => working.GetValueOrDefault(pair.Key) < pair.Value)) return false;
        }
        stock = working.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return true;
    }
}
