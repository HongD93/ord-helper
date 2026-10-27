static class SnapshotStability
{
    // 전역 유닛 배열은 적 생성·제거 시 길이와 주소가 바뀐다. 게임 세션의 정체성과 분리한다.
    internal static bool SameSession(Roots? current, Roots expected) => current is not null &&
        current.Ui == expected.Ui && current.Players == expected.Players && current.Table == expected.Table &&
        current.LocalPlayer == expected.LocalPlayer;

    internal static bool SameInventory(OwnedData first, OwnedData? second, IReadOnlySet<string> inventoryIds)
    {
        if (second is null || first.ReadErrors != 0 || second.ReadErrors != 0) return false;
        return Relevant(first).SequenceEqual(Relevant(second));

        IEnumerable<(uint Address, string Rawcode)> Relevant(OwnedData data) => data.Units
            .Where(unit => UnitRules.IsActive(unit) && !UnitRules.IsNonIngredientCombatObject(unit.Rawcode) &&
                (inventoryIds.Count == 0 || inventoryIds.Contains(unit.Rawcode)))
            .Select(unit => (unit.Address, unit.Rawcode)).OrderBy(unit => unit.Address).ThenBy(unit => unit.Rawcode, StringComparer.Ordinal);
    }
}
