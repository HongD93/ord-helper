// 정상 선택 입력의 우선순위와 초상화 2단계 선택을 순수 상태로 검증한다.
static class SelectionRetryPolicy
{
    internal const int MaximumWorldCandidates = 2;
    internal const int MaximumWorldClicks = 2;
    internal static readonly int[] WorldFallbackHeights = [48, 16];

    internal static uint? PreferFocusedCaster(uint? focus, IEnumerable<uint> currentCasterAddresses) =>
        focus is not null && currentCasterAddresses.Contains(focus.Value) ? focus : null;

    internal static bool WorldPointStillCurrent((int X, int Y) planned, (int X, int Y)? current) =>
        current is not null && Math.Abs((long)planned.X - current.Value.X) <= 8 &&
        Math.Abs((long)planned.Y - current.Value.Y) <= 8;

    internal static SelectionAttempt PortraitOutcome(SelectionAttempt result, bool selectedEligibleCaster) =>
        selectedEligibleCaster && result.Address is null && result.Error is null
            ? new(null, "선택한 재료의 초상화를 확인하지 못해 조합을 중단했습니다.") : result;

    internal static async Task<SelectionAttempt> AreaThenWorldAsync(
        Func<Task<SelectionAttempt>> areaAndPortrait, Func<Task<SelectionAttempt>> world)
    {
        var first = await areaAndPortrait();
        return first.Address is not null || first.Error is not null ? first : await world();
    }
}

sealed class SelectionWorldClickBudget
{
    internal int Remaining { get; private set; } = SelectionRetryPolicy.MaximumWorldClicks;
    internal bool TryUse()
    {
        if (Remaining <= 0) return false;
        Remaining--;
        return true;
    }
}

enum PortraitSelectionAction { Click, Wait, Ready, Stale, Exhausted }

sealed class PortraitSelectionPlan
{
    private readonly HashSet<uint> original;
    internal uint Target { get; }
    internal int ClickCount { get; private set; }

    internal PortraitSelectionPlan(uint[] selected, uint target)
    {
        original = selected.ToHashSet();
        if (selected.Length is < 2 or > 12 || original.Count != selected.Length ||
            selected.Any(unit => unit < 0x10000) || !original.Contains(target))
            throw new ArgumentException("Invalid original portrait selection.");
        Target = target;
    }

    internal PortraitSelectionAction Observe(uint[] selected, uint? focus)
    {
        if (selected.Length is < 1 or > 12 || selected.Any(unit => unit < 0x10000) ||
            selected.Distinct().Count() != selected.Length) return PortraitSelectionAction.Stale;
        if (selected.Length == 1)
            return selected[0] != Target ? PortraitSelectionAction.Stale :
                focus == Target ? PortraitSelectionAction.Ready : PortraitSelectionAction.Wait;
        // 첫 초상화 클릭은 같은 종류의 subgroup만 남길 수 있다. 원래 없던 개체나 목표 소실은 거부한다.
        if (!selected.Contains(Target) || selected.Any(unit => !original.Contains(unit))) return PortraitSelectionAction.Stale;
        if (focus is null || !selected.Contains(focus.Value)) return PortraitSelectionAction.Wait;
        return ClickCount < 2 ? PortraitSelectionAction.Click : PortraitSelectionAction.Exhausted;
    }

    internal void BeforeClick(uint target)
    {
        if (target != Target || ClickCount >= 2) throw new InvalidOperationException("Portrait target changed or click bound exceeded.");
        ClickCount++;
    }
}
