using System.Text;
using System.Text.Json;

static class KoreanKeyboardPlanTests
{
    public static void Run()
    {
        // 1. 실제 조합 별칭의 알려진 두벌식 입력열이다.
        Expect("해군영웅거프", "gornsduddndrjvm");

        // 2. 등록 별칭에 포함된 대문자와 기호는 한글 모드와 분리한다.
        var mixed = KoreanKeyboardPlan.Create("해적왕골D로져");
        Check(mixed.Select(s => s.Hangul).SequenceEqual(new[] { true, false, true }) &&
            mixed.Select(s => s.Text).SequenceEqual(new[] { "해적왕골", "D", "로져" }), "mixed alias segments");
        Check(mixed[1].Keys.SequenceEqual(new[] { new KoreanKey(0x44, true) }), "uppercase D uses Shift");
        Expect("해적왕골D로져", "gowjrdhkdrhfDfhwu");
        Expect("순록의사!", "tnsfhrdmltk!");
        Check(KoreanKeyboardPlan.Create("순록의사!").Select(s => s.Hangul).SequenceEqual(new[] { true, false }), "symbol suffix segment");

        // 3. 완성형 범위의 양 끝을 포함한다.
        Expect("가힣", "rkglg");
        Check(KoreanKeyboardPlan.Create("가A 가").Select(s => s.Text).SequenceEqual(new[] { "가", "A ", "가" }), "contiguous ASCII includes space");

        // 4. 쌍자음과 ㅒ·ㅖ에서만 대문자 키 표시가 필요하다.
        Expect("까따빠싸짜얘예", "RkEkQkTkWkdOdP");
        var shifted = KoreanKeyboardPlan.Create("까따빠싸짜얘예").SelectMany(s => s.Keys).Where(k => k.Shift).Select(k => k.VirtualKey);
        Check(shifted.SequenceEqual("REQTWOP".Select(c => (ushort)c)), "tense initials and shifted vowels");

        // 5. 겹모음을 포함한 모든 중성 21개의 입력열을 확인한다.
        string[] vowels = ["아", "애", "야", "얘", "어", "에", "여", "예", "오", "와", "왜", "외", "요", "우", "워", "웨", "위", "유", "으", "의", "이"];
        string[] vowelKeys = ["dk", "do", "di", "dO", "dj", "dp", "du", "dP", "dh", "dhk", "dho", "dhl", "dy", "dn", "dnj", "dnp", "dnl", "db", "dm", "dml", "dl"];
        for (var i = 0; i < vowels.Length; i++) Expect(vowels[i], vowelKeys[i]);

        // 6. 종성 없음·쌍받침·겹받침을 포함한 28개를 각각 검사한다.
        string[] finals = ["", "r", "R", "rt", "s", "sw", "sg", "e", "f", "fr", "fa", "fq", "ft", "fx", "fv", "fg", "a", "q", "qt", "t", "T", "d", "w", "c", "z", "x", "v", "g"];
        for (var i = 0; i < finals.Length; i++) Expect(((char)('가' + i)).ToString(), "rk" + finals[i]);
        Expect("닭앉많없읽", "ekfrdkswaksgdjqtdlfr");

        // 7. 미국식 키 배열의 출력 가능한 ASCII 95자를 모두 역변환한다.
        var ascii = new string(Enumerable.Range(32, 95).Select(i => (char)i).ToArray());
        var printable = KoreanKeyboardPlan.Create("가" + ascii);
        Check(printable.Length == 2 && !printable[1].Hangul && printable[1].Text == ascii &&
            Decode(printable[1].Keys) == ascii, "all printable ASCII keys and Shift flags");

        // 8. 현재 맵의 한글 별칭 81개를 정규분해 기반 참조와 대조한다.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "chat-craft-commands.json"),
            Path.Combine(Environment.CurrentDirectory, "wwwroot", "data", "chat-craft-commands.json"),
            Path.Combine(Environment.CurrentDirectory, ".local-tools", "ord-unit-probe", "wwwroot", "data", "chat-craft-commands.json")
        };
        var commandFile = candidates.FirstOrDefault(File.Exists) ?? throw new Exception("Korean keyboard test command catalog is missing.");
        using var catalog = JsonDocument.Parse(File.ReadAllText(commandFile));
        var commands = catalog.RootElement.GetProperty("recipes").EnumerateArray().Select(r => r.GetProperty("command").GetString()!).ToArray();
        Check(commands.Length == 81, "all 81 registered Korean recipe aliases are tested");
        foreach (var command in commands)
        {
            var plan = KoreanKeyboardPlan.Create(command);
            Check(string.Concat(plan.Select(s => s.Text)) == command && plan.All(s => s.Text.All(c => (c is >= '가' and <= '힣') == s.Hangul)), "registered alias segments preserve exact text");
            Check(Decode(plan.SelectMany(s => s.Keys)) == Reference(command), "registered alias key sequence: " + command);
        }

        // 9. 산술 분해와 별개인 Unicode 정규분해로 완성형 11,172자를 전수 대조한다.
        for (var value = 0xAC00; value <= 0xD7A3; value++)
        {
            var syllable = ((char)value).ToString();
            Check(Decode(KoreanKeyboardPlan.Create(syllable).Single().Keys) == Reference(syllable), "complete Hangul syllable key mapping");
        }

        // 10. 지원하지 않는 문자·길이·ASCII 단독 명령은 계획 생성 전에 거절한다.
        string?[] invalid = [null, "", "garp im", "가\n", "가\t", "가\0", "가é", "가ㄱ", "가😀", new string((char)0xD800, 1), new string('가', 43), "가" + new string('a', 128)];
        foreach (var command in invalid)
        {
            var rejected = false;
            try { KoreanKeyboardPlan.Create(command!); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "unsupported command is rejected");
        }
        // 11. 순록의사!의 기호 앞에서는 IME를 전환하거나 공백을 덧붙이지 않는다.
        var punctuationSteps = KoreanKeyboardPlan.CreateTypingSteps("순록의사!");
        var exclamation = Array.FindIndex(punctuationSteps, step => step.Action == KoreanTypingAction.Key && step.Key == new KoreanKey(0x31, true));
        Check(exclamation > 0 && punctuationSteps.Take(exclamation).All(step => step.Action != KoreanTypingAction.AlphanumericMode), "no mode commit before punctuation suffix");
        Check(punctuationSteps[^1].Action == KoreanTypingAction.AlphanumericMode && punctuationSteps.Where(step => step.Action == KoreanTypingAction.Key).All(step => step.Key.VirtualKey != 0x20), "commit after punctuation without an inserted space");
        var mixedSteps = KoreanKeyboardPlan.CreateTypingSteps("해적왕골D로져");
        var letterD = Array.FindIndex(mixedSteps, step => step.Action == KoreanTypingAction.Key && step.Key == new KoreanKey(0x44, true));
        Check(letterD > 0 && mixedSteps[letterD - 1].Action == KoreanTypingAction.AlphanumericMode, "Latin D still changes to alphanumeric mode first");

        // 12. 실제 명령 전체에서 문자를 바꾸지 않고, Enter·확정용 공백을 생성하지 않는다.
        Check(commands.All(command => !command.Contains(' ')), "all current registered map commands have zero spaces");
        foreach (var command in commands)
        {
            var typing = KoreanKeyboardPlan.CreateTypingSteps(command);
            var typedKeys = typing.Where(step => step.Action == KoreanTypingAction.Key).Select(step => step.Key).ToArray();
            Check(typedKeys.SequenceEqual(KoreanKeyboardPlan.Create(command).SelectMany(segment => segment.Keys)), "typing stages preserve every exact registered character");
            Check(typedKeys.All(key => key.VirtualKey is not (0x0D or 0x1B or 0x20)), "registered command has no implicit Enter Escape or space");
        }
        Console.WriteLine("PASS: Korean keyboard plan (12 groups, 81 registered aliases, all 11172 Hangul syllables and 95 printable ASCII characters; no game input).");
    }

    private static void Expect(string command, string expected) =>
        Check(Decode(KoreanKeyboardPlan.Create(command).SelectMany(s => s.Keys)) == expected, "known two-set sequence: " + command);

    // 정규분해된 Unicode 자모를 기준으로 삼아 production의 음절 인덱스 산술과 분리한다.
    private static string Reference(string text)
    {
        string[] initial = ["r", "R", "s", "e", "E", "f", "a", "q", "Q", "t", "T", "d", "w", "W", "c", "z", "x", "v", "g"];
        string[] medial = ["k", "o", "i", "O", "j", "p", "u", "P", "h", "hk", "ho", "hl", "y", "n", "nj", "np", "nl", "b", "m", "ml", "l"];
        string[] final = ["r", "R", "rt", "s", "sw", "sg", "e", "f", "fr", "fa", "fq", "ft", "fx", "fv", "fg", "a", "q", "qt", "t", "T", "d", "w", "c", "z", "x", "v", "g"];
        var result = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (c is >= '\u1100' and <= '\u1112') result.Append(initial[c - '\u1100']);
            else if (c is >= '\u1161' and <= '\u1175') result.Append(medial[c - '\u1161']);
            else if (c is >= '\u11A8' and <= '\u11C2') result.Append(final[c - '\u11A8']);
            else if (c is >= ' ' and <= '~') result.Append(c);
            else throw new Exception("Unexpected character in Unicode reference decomposition.");
        }
        return result.ToString();
    }

    private static string Decode(IEnumerable<KoreanKey> keys)
    {
        var punctuation = new Dictionary<ushort, string>
        {
            [0xBA] = ";:", [0xBB] = "=+", [0xBC] = ",<", [0xBD] = "-_", [0xBE] = ".>", [0xBF] = "/?",
            [0xC0] = "`~", [0xDB] = "[{", [0xDC] = "\\|", [0xDD] = "]}", [0xDE] = "'\""
        };
        var result = new StringBuilder();
        foreach (var key in keys)
        {
            if (key.VirtualKey is >= 0x41 and <= 0x5A) result.Append((char)(key.VirtualKey + (key.Shift ? 0 : 32)));
            else if (key.VirtualKey is >= 0x30 and <= 0x39) result.Append(key.Shift ? ")!@#$%^&*("[key.VirtualKey - 0x30] : (char)key.VirtualKey);
            else if (key.VirtualKey == 0x20 && !key.Shift) result.Append(' ');
            else if (punctuation.TryGetValue(key.VirtualKey, out var pair)) result.Append(pair[key.Shift ? 1 : 0]);
            else throw new Exception("Unsupported virtual key in Korean plan test.");
        }
        return result.ToString();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Korean keyboard plan test failed: " + message);
    }
}
