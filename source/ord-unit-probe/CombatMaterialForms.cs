// 2.331[C] 재료 그룹이 같은 유닛 핸들을 유지하는 두 전투 변신만 정규화한다.
static class CombatMaterialForms
{
    internal static string Canonical(string rawcode, string? mapHash) =>
        mapHash != GameProgression.SupportedMapHash ? rawcode : rawcode switch
        {
            "H0B3" => "H0B2",
            "h07J" => "h07L",
            _ => rawcode
        };

    internal static bool IsPair(string form, string original, string? mapHash) =>
        form != original && Canonical(form, mapHash) == original;

    internal static Dictionary<string, int> Normalize(IReadOnlyDictionary<string, int> counts, string? mapHash)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in counts)
        {
            var rawcode = Canonical(item.Key, mapHash);
            result[rawcode] = checked(result.GetValueOrDefault(rawcode) + item.Value);
        }
        return result;
    }
}
