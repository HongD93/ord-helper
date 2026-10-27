using System.Text.Json;

record CraftRecipeCondition(string Type, string Rawcode, int Count);

static class CraftConditions
{
    internal static readonly string[] Types = ["UBAN", "KING", "IFCT", "CHCT", "SKPT", "RMAX", "NIKA"];
    internal sealed record State(Dictionary<string, bool> UnitBans, string KingStatus, string KingReason, GameSpecialRecipeConditions.State? Special = null);

    internal static State? Read(JsonElement snapshot)
    {
        if (!snapshot.TryGetProperty("localPlayer", out var player) || player.ValueKind != JsonValueKind.Number ||
            !player.TryGetInt32(out var owner) || owner is < 0 or > 11 ||
            !snapshot.TryGetProperty("recipeConditions", out var state) || state.ValueKind != JsonValueKind.Object) return null;
        var special = state.TryGetProperty("special", out var extra) ? GameSpecialRecipeConditions.Read(extra, owner) : null;
        if (!state.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() is not ("confirmed" or "partial") ||
            !state.TryGetProperty("localPlayer", out var local) || local.ValueKind != JsonValueKind.Number || !local.TryGetInt32(out var actual) || actual != owner ||
            !state.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("readOnly", out var readOnly) || readOnly.ValueKind != JsonValueKind.True ||
            !state.TryGetProperty("unitBans", out var bans) || bans.ValueKind != JsonValueKind.Object)
            return special is null ? null : new(new(StringComparer.Ordinal), "unknown", "outer_gates_unverified", special);
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var value in bans.EnumerateObject())
        {
            if (value.Name.Length != 4 || value.Name.Any(character => character is < ' ' or > '~') ||
                value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || !known.TryAdd(value.Name, value.Value.GetBoolean()))
                return special is null ? null : new(new(StringComparer.Ordinal), "unknown", "outer_gates_unverified", special);
        }
        var kingStatus = "unknown";
        var kingReason = "";
        if (state.TryGetProperty("king", out var king) && king.ValueKind == JsonValueKind.Object)
        {
            if (king.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is "allowed" or "blocked")
                kingStatus = value.GetString()!;
            if (king.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String) kingReason = reason.GetString() ?? "";
        }
        return new(known, kingStatus, kingReason, special);
    }

    internal static bool Available(State? state, IEnumerable<CraftRecipeCondition> conditions, out string reason, out string? code)
        => TryProject(state, conditions, out _, out _, out reason, out code);

    internal static bool TryProject(State? state, IEnumerable<CraftRecipeCondition> conditions, out State? projected, out int distortionWood, out string reason, out string? code)
    {
        projected = state; distortionWood = 0; reason = ""; code = null;
        foreach (var condition in conditions)
        {
            if (condition.Count <= 0 || !Types.Contains(condition.Type, StringComparer.Ordinal) || state is null)
                return Unknown(out reason, out code);
            if (condition.Type == "UBAN")
            {
                if (!state.UnitBans.TryGetValue(condition.Rawcode, out var banned)) return Unknown(out reason, out code);
                if (!banned) continue;
                reason = "이 유닛은 밴 상태여서 조합할 수 없습니다.";
            }
            else if (condition.Type == "KING")
            {
                if (state.KingStatus == "allowed") continue;
                if (state.KingStatus != "blocked") return Unknown(out reason, out code);
                reason = "패왕의길 제한으로 최상위 유닛을 조합할 수 없습니다.";
            }
            else
            {
                var special = projected?.Special;
                if (special is null) return Unknown(out reason, out code);
                try
                {
                    switch (condition.Type)
                    {
                        case "IFCT":
                            if (special.DistortionWoodSurcharge is not { } surcharge || special.DistortionWoodIncrement is not { } increment)
                                return Unknown(out reason, out code);
                            distortionWood = checked(distortionWood + condition.Count + surcharge);
                            projected = projected! with { Special = special with { DistortionWoodSurcharge = checked(surcharge + increment) } };
                            continue;
                        case "CHCT":
                            if (special.TransformationCount is not { } changes) return Unknown(out reason, out code);
                            if (changes < condition.Count) { reason = "변화 가능 횟수가 부족합니다."; break; }
                            projected = projected! with { Special = special with { TransformationCount = changes - condition.Count } };
                            continue;
                        case "SKPT":
                            if (special.SkillPoints is not { } skills) return Unknown(out reason, out code);
                            if (skills < condition.Count) { reason = "특성 포인트가 부족합니다."; break; }
                            projected = projected! with { Special = special with { SkillPoints = skills - condition.Count } };
                            continue;
                        case "RMAX":
                            if (special.Round is not { } round) return Unknown(out reason, out code);
                            if (round <= condition.Count) continue;
                            reason = $"{condition.Count}라운드까지만 조합할 수 있습니다.";
                            break;
                        case "NIKA":
                            if (condition.Rawcode != "NIKA" || condition.Count != 1 ||
                                special.NikaEnhancement is not { Status: "confirmed", Stage: 0 or 1 or 2 } nika) return Unknown(out reason, out code);
                            if (nika.Stage == 1) continue; // 강화 여부이며 재료·특성 포인트를 다시 소모하지 않는다.
                            reason = nika.Stage == 2 ? "이미 니카로 승급한 상태입니다." : "니카 특성 강화가 필요합니다.";
                            break;
                    }
                }
                catch (OverflowException) { return Unknown(out reason, out code); }
            }
            code = "recipe_conditions_blocked";
            return false;
        }
        return true;
    }

    private static bool Unknown(out string reason, out string? code)
    {
        reason = "유닛 밴·패왕의길 또는 특수 조합 조건을 확인하지 못했습니다.";
        code = "recipe_conditions_unknown";
        return false;
    }
}
