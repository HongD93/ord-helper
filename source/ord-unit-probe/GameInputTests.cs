using System.Text;
using System.Text.Json;

static class GameInputTests
{
    public static void Run()
    {
        (string Name, Action Run)[] cases = [
            ("registered Korean command syntax", VerifyKoreanSyntax),
            ("invalid command characters", VerifyInvalidSyntax),
            ("UTF-8 byte limit", VerifyByteLimit),
            ("chat command never falls back to a hotkey", VerifyChatSupport),
            ("existing hotkeys and caster requirement", VerifyHotkeys),
            ("targeted recipe exclusions", VerifyTargetedRecipes),
            ("invalid typing rejected before native input", VerifyInvalidTypingGuard),
            ("bundled map Korean aliases", VerifyBundledCommands),
            ("world array churn uses the newest roots", VerifyNewestInventoryRoots),
            ("session changes stop inventory reads", VerifyInventorySessions),
            ("partial inventory reads fail closed", VerifyInventoryReadErrors),
            ("ingredient identities and activity remain stable", VerifyIngredientIdentity),
            ("casters participate in inventory stability", VerifyCasterIdentity),
            ("unrelated units and non-active flags do not block input", VerifyUnrelatedInventory),
            ("latest active material quantities are required", VerifyMaterialCounts),
            ("unit-free commands retain session and read guards", VerifyUnitFreeInventory)
        ];
        foreach (var test in cases)
        {
            try { test.Run(); }
            catch (Exception error) { throw new Exception("Game input test failed: " + test.Name, error); }
        }
        Console.WriteLine($"PASS: Game input command policy and stable relevant inventory {cases.Length}/{cases.Length} cases; no valid command typed.");
    }

    private static void VerifyKoreanSyntax()
    {
        string[] commands = ["해군영웅거프", "해적왕골D로져", "순록의사!"];
        Check(commands.All(GameInput.IsKoreanChatCommand), "all registered examples preserve their exact Korean aliases");
        Check(GameInput.IsKoreanChatCommand("한 글D!~"), "printable ASCII may accompany a complete Hangul syllable");
    }

    private static void VerifyInvalidSyntax()
    {
        string?[] commands = [null, "", "garp im", "roger im", "!123", "   ",
            "한\n", "한\r", "\t한", "한\0", "한\u007f", "한\u0085", "한\u00a0",
            "한\u202eabc", "한\u2066", "한\ud800", "한\udc00", "한\ud83d\ude00", "ㄱ"];
        Check(commands.All(command => !GameInput.IsKoreanChatCommand(command)),
            "English-only, empty, control, bidi, surrogate, emoji and incomplete Hangul commands are rejected");
    }

    private static void VerifyByteLimit()
    {
        var accepted = new string('가', 42);
        var rejected = new string('가', 43);
        Check(Encoding.UTF8.GetByteCount(accepted) == 126 && GameInput.IsKoreanChatCommand(accepted),
            "42 Hangul syllables fit the UTF-8 limit");
        Check(Encoding.UTF8.GetByteCount(rejected) == 129 && !GameInput.IsKoreanChatCommand(rejected),
            "43 Hangul syllables exceed the limit despite their short character count");
        Check(GameInput.IsKoreanChatCommand("가" + new string('A', 125)) &&
            !GameInput.IsKoreanChatCommand("가" + new string('A', 126)),
            "the exact 128-byte mixed command boundary is enforced");
    }

    private static void VerifyChatSupport()
    {
        var valid = Command(chat: "해군영웅거프", key: null, casters: []);
        Check(GameInput.Supported(valid), "registered Korean chat does not require a selected hotkey caster");
        Check(!GameInput.Supported(Command(chat: "garp im", key: "B")) &&
            !GameInput.Supported(Command(chat: "", key: "Z")) &&
            !GameInput.Supported(Command(chat: "한\n", key: "C")),
            "an invalid non-null chat command cannot silently use a valid hotkey");
        Check(GameInput.Supported(Command(chat: null, key: "B")), "an absent chat command preserves the ordinary hotkey path");
    }

    private static void VerifyHotkeys()
    {
        Check(new[] { "B", "C", "X", "Z" }.All(key => GameInput.Supported(Command(key: key))),
            "existing B C X Z hotkeys remain supported");
        string?[] rejected = [null, "", "b", "c", "Q", "ZZ", "가"];
        Check(rejected.All(key => !GameInput.Supported(Command(key: key))), "unsupported or multi-character hotkeys remain rejected");
        Check(!GameInput.Supported(Command(key: "B", casters: [])), "a hotkey still requires an actual caster type");
    }

    private static void VerifyTargetedRecipes()
    {
        string[] targeted = ["A19W", "A19U", "A19S", "A19V", "A19T", "A0QG"];
        Check(targeted.All(id => !GameInput.Supported(Command(id: id, key: "B"))),
            "all six targeted hotkey recipes remain excluded");
        Check(GameInput.Supported(Command(id: "A00P", key: "Z")), "an ordinary recipe is not excluded with targeted recipes");
    }

    private static void VerifyInvalidTypingGuard()
    {
        string?[] invalid = [null, "", "garp im", "한\n", "한\u202e", "한\ud83d\ude00"];
        foreach (var command in invalid)
        {
            // 유효한 명령은 이 메서드에 전달하지 않는다. 검사 실패 시 입력 호출 전에 테스트를 끝낸다.
            Check(!GameInput.IsKoreanChatCommand(command), "only rejected commands may reach the native-input guard test");
            var rejected = false;
            try { _ = GameInput.TypeKoreanChatCommandAsync(command!); }
            catch (ArgumentException error)
            {
                Check(error.ParamName == "command", "the argument guard identifies the rejected command");
                rejected = true;
            }
            Check(rejected, "invalid commands must throw before any Windows input call");
        }
    }

    private static void VerifyBundledCommands()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "chat-craft-commands.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var recipes = root.GetProperty("recipes").EnumerateArray().ToArray();
        Check(recipes.Length == 81 && root.GetProperty("chatRecipeCount").GetInt32() == 81,
            "all 81 map chat recipes are present in the installed snapshot");
        Check(!root.GetProperty("englishFallbackAllowed").GetBoolean(), "the snapshot forbids English fallback");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in recipes)
        {
            var id = recipe.GetProperty("recipeId").GetString()!;
            var command = recipe.GetProperty("command").GetString();
            Check(ids.Add(id) && GameInput.IsKoreanChatCommand(command), "each unique recipe has a valid Korean command");
            Check(recipe.GetProperty("aliases").EnumerateArray().Any(alias => alias.GetString() == command),
                "each selected command belongs to the map-registered aliases");
        }
        var garp = recipes.Single(recipe => recipe.GetProperty("recipeId").GetString() == "IM03");
        Check(garp.GetProperty("command").GetString() == "해군영웅거프" &&
            garp.GetProperty("resultUnitRawcode").GetString() == "h04C", "Garp uses the exact registered Korean command");
    }

    private static readonly Roots InventoryRoots = new(0x10000, 0x20000, 0x30000, 0x40000, 5, 2);
    private static UnitObject Unit(uint address, string code = "hmat", uint flags20 = 0, uint flags5c = 0) =>
        new(address, code, flags20, flags5c);
    private static OwnedData Inventory(params UnitObject[] units) => new([.. units], 0);
    private static CraftCommand MaterialCommand() => Command(casters: ["hcst"]) with
        { Ingredients = [new("hmat", 1)] };

    private static (bool Accepted, OwnedData? Latest) ProbeInventory(CraftCommand command, OwnedData first,
        OwnedData second, Roots? middle, Roots? after)
    {
        var read = 0;
        var roots = new Queue<Roots?>([middle, after]);
        var accepted = GameInput.TryReadCommandInventory(command, InventoryRoots,
            _ => read++ == 0 ? first : second, () => roots.Dequeue(), out var latest);
        return (accepted, latest);
    }

    private static void VerifyNewestInventoryRoots()
    {
        var middle = InventoryRoots with { Array = 0x50000, Count = 17 };
        var after = InventoryRoots with { Array = 0x60000, Count = 18 };
        var first = Inventory(Unit(0x70000), Unit(0x80000, "hcst"));
        var second = Inventory(Unit(0x70000), Unit(0x80000, "hcst"), Unit(0x90000, "dumy"));
        var seen = new List<Roots>();
        var roots = new Queue<Roots?>([middle, after]);
        Check(GameInput.TryReadCommandInventory(MaterialCommand(), InventoryRoots, current =>
        {
            seen.Add(current);
            return seen.Count == 1 ? first : second;
        }, () => roots.Dequeue(), out var latest), "array reallocations and enemy count changes are not a new session");
        Check(seen.SequenceEqual(new[] { InventoryRoots, middle }) && ReferenceEquals(latest, second),
            "the second inventory uses the new array and the caller receives that latest inventory");
    }

    private static void VerifyInventorySessions()
    {
        var stable = Inventory(Unit(0x70000));
        Roots?[] changed = [null, InventoryRoots with { Ui = 0x90000 },
            InventoryRoots with { Players = 0x90000 }, InventoryRoots with { Table = 0x90000 },
            InventoryRoots with { LocalPlayer = 3 }];
        foreach (var roots in changed)
        {
            var beforeSecond = ProbeInventory(MaterialCommand(), stable, stable, roots, InventoryRoots);
            var afterSecond = ProbeInventory(MaterialCommand(), stable, stable, InventoryRoots, roots);
            Check(!beforeSecond.Accepted && beforeSecond.Latest is null && !afterSecond.Accepted && afterSecond.Latest is null,
                "missing roots or a changed UI/player/table/local player fail at either boundary");
        }
    }

    private static void VerifyInventoryReadErrors()
    {
        var stable = Inventory(Unit(0x70000));
        var partial = stable with { ReadErrors = 1 };
        Check(!ProbeInventory(MaterialCommand(), partial, stable, InventoryRoots, InventoryRoots).Accepted &&
            !ProbeInventory(MaterialCommand(), stable, partial, InventoryRoots, InventoryRoots).Accepted,
            "either partial read prevents input even if the visible material list agrees");
    }

    private static void VerifyIngredientIdentity()
    {
        var stable = Inventory(Unit(0x70000));
        OwnedData[] changed = [Inventory(), Inventory(Unit(0x71000)), Inventory(Unit(0x70000, "Hmat")),
            Inventory(Unit(0x70000, flags20: 1)), Inventory(Unit(0x70000, flags5c: 0x100))];
        Check(changed.All(second => !ProbeInventory(MaterialCommand(), stable, second, InventoryRoots, InventoryRoots).Accepted),
            "removed, replaced, transformed, dead and inactive ingredients cannot pass the two-read guard");
    }

    private static void VerifyCasterIdentity()
    {
        var first = Inventory(Unit(0x70000), Unit(0x80000, "hcst"));
        var second = Inventory(Unit(0x70000), Unit(0x81000, "hcst"));
        Check(!ProbeInventory(MaterialCommand(), first, second, InventoryRoots, InventoryRoots).Accepted,
            "a caster replacement is relevant even when it is not an ingredient");
    }

    private static void VerifyUnrelatedInventory()
    {
        var first = Inventory(Unit(0x70000), Unit(0x80000, "hcst"), Unit(0x90000, "dumy"), Unit(0xA0000, "Hmat"));
        var second = Inventory(Unit(0x70000, flags20: 0x200, flags5c: 0x20), Unit(0x80000, "hcst", 0x400),
            Unit(0xB0000, "dumy"));
        Check(ProbeInventory(MaterialCommand(), first, second, InventoryRoots, InventoryRoots).Accepted,
            "attacking/moving flags and unrelated allied dummy or differently cased rawcodes do not reject a stable recipe");
    }

    private static void VerifyMaterialCounts()
    {
        var command = MaterialCommand() with { Ingredients = [new("hmat", 2)] };
        Check(GameInput.HasIngredients(command, Inventory(Unit(0x70000), Unit(0x71000))), "two active material instances meet the cost");
        Check(!GameInput.HasIngredients(command, Inventory(Unit(0x70000), Unit(0x71000, flags20: 1))) &&
            !GameInput.HasIngredients(command, Inventory(Unit(0x70000), Unit(0x71000, "Hmat"))) &&
            !GameInput.HasIngredients(command, Inventory(Unit(0x70000), Unit(0x71000)) with { ReadErrors = 1 }),
            "dead units, different rawcodes and incomplete reads do not satisfy the latest material quantities");
    }

    private static void VerifyUnitFreeInventory()
    {
        var command = Command(chat: "해군영웅거프", casters: []);
        var first = Inventory(Unit(0x70000, "dumy"));
        var second = Inventory(Unit(0x80000, "dumy"));
        Check(ProbeInventory(command, first, second, InventoryRoots, InventoryRoots).Accepted,
            "a command with no unit dependencies does not compare unrelated allied units");
        Check(!ProbeInventory(command, first, second with { ReadErrors = 1 }, InventoryRoots, InventoryRoots).Accepted &&
            !ProbeInventory(command, first, second, InventoryRoots, null).Accepted,
            "unit-free commands still require complete reads and an unchanged session");
    }

    private static CraftCommand Command(string id = "TEST", string? chat = null, string? key = "B", string[]? casters = null) =>
        new(id, "hfoo", 1, [], chat, key, casters ?? ["hfoo"]);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
