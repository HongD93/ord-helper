using System.Text.Json;
using System.Text.RegularExpressions;

static partial class UnitRules
{
    // 실제 맵의 조합 재료는 숨김·죽음 처리된다. 일시적 선택 불가·공격 상태는 제외하지 않는다.
    public static bool IsActive(UnitObject unit) => (unit.Flags5c & 0x100) == 0 && (unit.Flags20 & 1) == 0;

    // 2.323[C] BpT의 3.5초 장난감과 스킬용 만능더미는 조합 결과·재료가 아니다.
    // 등장·소멸을 재료 변화로 보지 않으며, 다른 개체의 판독 오류는 계속 차단한다.
    public static bool IsNonIngredientCombatObject(string rawcode) => rawcode is "h06O" or "h0C5";

    // 이름과 영웅명에 등급이 있는 카탈로그 항목만 캐릭터로 표시한다.
    public static bool IsCharacterLabel(string name) => TierSuffix().IsMatch(name);

    // 같은 맵 해시가 확인된 재생 가능 카탈로그에서는 등급명이 없는 영원·초월 유닛도 포함한다.
    public static bool IsCharacterCatalogUnit(JsonElement unit)
    {
        if (!unit.TryGetProperty("playable", out var playable) || playable.ValueKind != JsonValueKind.True ||
            !unit.TryGetProperty("tier", out var tier) || tier.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tier.GetString()) || tier.GetString() == "기타") return false;
        return !IsTrue(unit, "isSummon") && !IsTrue(unit, "isSummoned");
    }

    private static bool IsTrue(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"[-－]\s*(흔함|안흔함|특별함?|희귀함?|전설|히든|초월|불멸|영원|제한|변화|변이|유일|특수|신비|왜곡|세라핌|랜덤전용|랜덤)(?:\s|$|[\[(])", RegexOptions.CultureInvariant)]
    private static partial Regex TierSuffix();
}
