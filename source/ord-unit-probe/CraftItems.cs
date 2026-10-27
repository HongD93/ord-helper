using System.Text.Json;

// 맵의 일회성 아이템 권리를 유닛 재고와 분리한다.
static class CraftItems
{
    internal static readonly string[] Codes = ["AI00", "AI01", "AI02"];
    internal static string Name(string rawcode) => rawcode switch
    {
        "AI00" => "우타의 헤드셋",
        "AI01" => "태양신의 흔적",
        "AI02" => "불사조의 깃털",
        _ => rawcode
    };

    internal static Dictionary<string, int>? Read(JsonElement snapshot)
    {
        if (!snapshot.TryGetProperty("localPlayer", out var owner) || owner.ValueKind != JsonValueKind.Number || !owner.TryGetInt32(out var localPlayer) ||
            localPlayer is < 0 or > 11 || !snapshot.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Object || !items.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String || status.GetString() != "confirmed" ||
            !items.TryGetProperty("localPlayer", out var player) || player.ValueKind != JsonValueKind.Number || !player.TryGetInt32(out var itemPlayer) || itemPlayer != localPlayer ||
            !items.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("readOnly", out var readOnly) || readOnly.ValueKind != JsonValueKind.True ||
            !items.TryGetProperty("counts", out var values) || values.ValueKind != JsonValueKind.Object) return null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var code in Codes)
        {
            if (!values.TryGetProperty(code, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count is < 0 or > 1) return null;
            counts.Add(code, count);
        }
        return counts;
    }

    internal static bool Available(IReadOnlyDictionary<string, int>? counts, IEnumerable<CraftIngredient> needed, out string reason)
    {
        reason = "";
        var requirements = needed.GroupBy(item => item.Rawcode, StringComparer.Ordinal)
            .Select(group => new CraftIngredient(group.Key, checked(group.Sum(item => item.Count)))).ToArray();
        if (requirements.Length == 0) return true;
        if (counts is null || requirements.Any(item => !Codes.Contains(item.Rawcode, StringComparer.Ordinal)))
        {
            reason = "필요한 아이템의 보유 여부를 확인하지 못했습니다.";
            return false;
        }
        foreach (var item in requirements)
        {
            if (item.Count <= 0 || counts.GetValueOrDefault(item.Rawcode) < item.Count)
            {
                reason = $"{Name(item.Rawcode)} 아이템이 부족합니다. 필요 {item.Count} / 보유 {counts.GetValueOrDefault(item.Rawcode)}";
                return false;
            }
        }
        return true;
    }

    internal static bool ConsumptionMatches(JsonElement before, JsonElement after, CraftIngredient[] needed)
    {
        if (needed.Length == 0) return true;
        var first = Read(before);
        var second = Read(after);
        if (!Available(first, needed, out _) || second is null) return false;
        foreach (var item in needed.GroupBy(item => item.Rawcode, StringComparer.Ordinal))
            if (second.GetValueOrDefault(item.Key) != first!.GetValueOrDefault(item.Key) - item.Sum(value => value.Count)) return false;
        return true;
    }
}
