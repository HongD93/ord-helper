internal readonly record struct KoreanKey(ushort VirtualKey, bool Shift);
internal sealed record KoreanInputSegment(bool Hangul, string Text, KoreanKey[] Keys);
internal enum KoreanTypingAction { HangulMode, AlphanumericMode, Key }
internal readonly record struct KoreanTypingStep(KoreanTypingAction Action, KoreanKey Key = default);

// 표준 두벌식과 미국식 키 배열의 순수 계획만 만든다. 입력 API를 호출하지 않는다.
static class KoreanKeyboardPlan
{
    private static readonly string[] Initial = ["r", "R", "s", "e", "E", "f", "a", "q", "Q", "t", "T", "d", "w", "W", "c", "z", "x", "v", "g"];
    private static readonly string[] Medial = ["k", "o", "i", "O", "j", "p", "u", "P", "h", "hk", "ho", "hl", "y", "n", "nj", "np", "nl", "b", "m", "ml", "l"];
    private static readonly string[] Final = ["", "r", "R", "rt", "s", "sw", "sg", "e", "f", "fr", "fa", "fq", "ft", "fx", "fv", "fg", "a", "q", "qt", "t", "T", "d", "w", "c", "z", "x", "v", "g"];

    internal static KoreanTypingStep[] CreateTypingSteps(string command)
    {
        var steps = new List<KoreanTypingStep>();
        foreach (var segment in Create(command))
        {
            if (segment.Hangul) steps.Add(new(KoreanTypingAction.HangulMode));
            // 숫자·문장기호는 한국어 IME에서도 그대로 입력한다. '사!' 사이에 확정용 공백이나 전환을 넣지 않는다.
            else if (segment.Text.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'))
                steps.Add(new(KoreanTypingAction.AlphanumericMode));
            foreach (var key in segment.Keys) steps.Add(new(KoreanTypingAction.Key, key));
        }
        // 전송 Enter와 분리된 한/영 전환으로 마지막 음절을 확정한다. 출력 문자는 추가하지 않는다.
        steps.Add(new(KoreanTypingAction.AlphanumericMode));
        return steps.ToArray();
    }

    internal static KoreanInputSegment[] Create(string command)
    {
        if (!GameInput.IsKoreanChatCommand(command))
            throw new ArgumentException("A supported Korean chat command is required.", nameof(command));
        var segments = new List<KoreanInputSegment>();
        var start = 0;
        while (start < command.Length)
        {
            var hangul = IsHangul(command[start]);
            var end = start + 1;
            while (end < command.Length && IsHangul(command[end]) == hangul) end++;
            var keys = new List<KoreanKey>();
            for (var index = start; index < end; index++)
            {
                var value = command[index];
                if (!hangul) keys.Add(AsciiKey(value));
                else
                {
                    var syllable = value - 0xAC00;
                    Append(Initial[syllable / (21 * 28)], keys);
                    Append(Medial[syllable / 28 % 21], keys);
                    Append(Final[syllable % 28], keys);
                }
            }
            segments.Add(new(hangul, command[start..end], keys.ToArray()));
            start = end;
        }
        return segments.ToArray();
    }

    private static bool IsHangul(char value) => value is >= '가' and <= '힣';
    private static void Append(string letters, List<KoreanKey> keys)
    {
        foreach (var letter in letters) keys.Add(AsciiKey(letter));
    }

    private static KoreanKey AsciiKey(char value)
    {
        if (value is >= 'a' and <= 'z') return new((ushort)(value - 'a' + 'A'), false);
        if (value is >= 'A' and <= 'Z') return new(value, true);
        if (value is >= '0' and <= '9') return new(value, false);
        return value switch
        {
            ' ' => new(0x20, false),
            ')' => new(0x30, true), '!' => new(0x31, true), '@' => new(0x32, true),
            '#' => new(0x33, true), '$' => new(0x34, true), '%' => new(0x35, true),
            '^' => new(0x36, true), '&' => new(0x37, true), '*' => new(0x38, true), '(' => new(0x39, true),
            ';' or ':' => new(0xBA, value == ':'), '=' or '+' => new(0xBB, value == '+'),
            ',' or '<' => new(0xBC, value == '<'), '-' or '_' => new(0xBD, value == '_'),
            '.' or '>' => new(0xBE, value == '>'), '/' or '?' => new(0xBF, value == '?'),
            '`' or '~' => new(0xC0, value == '~'), '[' or '{' => new(0xDB, value == '{'),
            '\\' or '|' => new(0xDC, value == '|'), ']' or '}' => new(0xDD, value == '}'),
            '\'' or '"' => new(0xDE, value == '"'),
            _ => throw new ArgumentException("The command contains a character without a supported key mapping.", nameof(value))
        };
    }
}
