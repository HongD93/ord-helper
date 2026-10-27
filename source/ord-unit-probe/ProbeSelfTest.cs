using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

// 게임 대신 직접 생성한 자식 프로세스만 사용해 외부 메모리 읽기와 수량 교체를 검증한다.
static class ProbeSelfTest
{
    public static void Run()
    {
        VerifyCancellationGuards();
        var executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--fixture");
        using var child = Process.Start(start) ?? throw new Exception("Fixture process could not start.");
        try
        {
            var lineTask = child.StandardOutput.ReadLineAsync();
            if (!lineTask.Wait(TimeSpan.FromSeconds(10)) || lineTask.Result is not { } line)
                throw new Exception("Fixture process did not become ready.");
            var fixture = JsonSerializer.Deserialize<FixtureInfo>(line) ?? throw new Exception("Invalid fixture output.");
            using var reader = new MemoryReader(child.Id, 0x0410);
            var roots = reader.ReadRoots(fixture.UiAddress, fixture.PlayersAddress) ?? throw new Exception("Fixture roots missing.");
            var first = reader.ReadOwnedUnits(roots);
            Check(first.ReadErrors == 0, "complete cross-process read");
            Check(first.Units.Count == 3, "owner filtering and duplicate pointer exclusion");
            Check(first.Units.Count(u => u.Rawcode == "hfoo") == 2, "rawcode byte order and initial quantity");
            Check(first.Units.All(u => u.Rawcode != "hrif"), "other player's unit excluded");
            Check(GameSelection.CurrentOwnedUnit(reader, roots, fixture.FirstUnitAddress, "hfoo") &&
                !GameSelection.CurrentOwnedUnit(reader, roots, fixture.FirstUnitAddress, "Hfoo"),
                "current caster rawcode is verified from live memory with case sensitivity");
            Check(GameSelection.ReadSingleSelectedAddress(reader, roots) == fixture.FirstUnitAddress,
                "one active owned selection returns the exact first unit address");
            Check(GameSelection.ReadReusableCaster(reader, roots, first.Units.Where(unit => unit.Rawcode == "hfoo").ToArray()) == fixture.FirstUnitAddress,
                "an already selected active material is reused without any selector or input call");
            Select("select-second", fixture.SecondUnitAddress, "same-type second unit returns its own address");
            Check(GameSelection.ReadSingleSelectedAddress(reader, roots) != fixture.FirstUnitAddress,
                "same rawcode does not make a different selected instance the expected target");
            Select("select-none", null, "empty selection is rejected");
            Select("select-many", null, "multiple selected units are rejected");
            Check(GameSelection.ReadSelectedAddresses(reader, roots).SequenceEqual(new[] { fixture.FirstUnitAddress, fixture.SecondUnitAddress }),
                "bounded group selection reads both actual owned instances");
            var portraitGeometry = new GameSelection.Geometry(new(0, 0, 1000), Vector3.Zero, Vector3.UnitY, 1.2f,
                new(0, 0, .6f, .8f), new(.13f, 0, .58f, .8f), 1280, 720);
            var casterGroup = first.Units.Where(unit => unit.Rawcode == "hfoo").ToArray();
            Portrait("hud-valid", fixture.SecondUnitAddress, "HUD ordering is matched by unit address rather than selection-list order");
            Check(GameSelection.ReadPortraitCaster(reader, fixture.UiAddress, roots, portraitGeometry, casterGroup,
                fixture.FirstUnitAddress) == fixture.FirstUnitAddress, "a locked portrait identity survives a different first matching HUD unit");
            Focus("hud-focus-second", fixture.SecondUnitAddress, "active representative follows its exact address rather than the first selected node");
            Focus("hud-focus-outside", null, "a stale active pointer outside the selected set is rejected");
            Portrait("hud-valid", fixture.SecondUnitAddress, "HUD can recover after a stale active representative");
            Portrait("hud-hidden", null, "a hidden target HUD button cannot be used", fixture.SecondUnitAddress);
            Portrait("hud-stale-child", fixture.SecondUnitAddress, "a stale render child does not replace the current main-button unit", fixture.SecondUnitAddress);
            Portrait("hud-wrong-rawcode", null, "target main-button rawcode must match its current live unit body", fixture.SecondUnitAddress);
            Portrait("hud-duplicate", fixture.SecondUnitAddress, "unrelated cached slots do not invalidate a verified target main button", fixture.SecondUnitAddress);
            Portrait("hud-inactive-peer", fixture.SecondUnitAddress, "an inactive unrelated group member cannot authorize a cast but does not block the owned target portrait", fixture.SecondUnitAddress);
            Check(GameSelection.ReadSelectedAddresses(reader, roots).Length == 0 &&
                GameSelection.ReadSelectedGroupAddresses(reader, roots).Length == 2,
                "partial raw membership is separate from the strict active-owned selection contract");
            Portrait("hud-many-stale-children", fixture.SecondUnitAddress, "the observed eleven-wisp stale-child layout still identifies the exact main-button target", fixture.SecondUnitAddress);
            Check(GameSelection.ReadSingleSelectedAddress(reader, roots) is null,
                "a valid HUD candidate never authorizes a multi-selection command");
            Portrait("hud-valid", fixture.SecondUnitAddress, "HUD fixture resets after the eleven-member stale-render case");
            Chat("chat-valid");
            var chat = GameChatText.Read(reader, fixture.UiAddress, roots);
            Check(GameChatText.Matches(chat, "해군영웅거프") && !GameChatText.Matches(chat, "해군영웅거프 "),
                "chat bytes match the exact Korean UTF-8 command without accepting trailing text");
            Check(!GameChatText.Matches(new(0, 0, [0xED, 0xA0, 0x80]), new string((char)0xD800, 1)),
                "malformed expected UTF-16 cannot match replacement UTF-8 bytes");
            RejectChat("chat-hidden", "hidden chat bar is rejected");
            RejectChat("chat-wrong-vtable", "a different edit control is rejected");
            RejectChat("chat-length", "overlong chat buffers are rejected before reading text");
            RejectChat("chat-capacity", "capacity bounds are checked independently of length");
            RejectChat("chat-length-cap", "length cannot exceed allocated capacity");
            RejectChat("chat-termination", "chat text needs a trailing NUL");
            RejectChat("chat-embedded-nul", "embedded NUL bytes cannot hide extra chat text");
            RejectChat("chat-ui-changed", "a replaced global UI cannot reuse a cached chat bar");
            Chat("chat-valid");
            Select("select-other-owner", null, "another player's selected unit is rejected");
            Check(GameSelection.ReadSelectedAddresses(reader, roots).Length == 0, "HUD selection never accepts another player's group member");
            Select("select-dead", null, "dead or hidden selected material is rejected");
            Select("select-first", fixture.FirstUnitAddress, "selection can recover to the exact active owned target");
            Select("select-focus-mismatch", null, "a single selected unit cannot cast while its active representative is stale");
            Check(GameSelection.ReadReusableCaster(reader, roots, casterGroup) is null,
                "a stale representative cannot authorize the already-selected fast path");
            Select("select-first", fixture.FirstUnitAddress, "single selection recovers after matching focus is restored");
            child.StandardInput.WriteLine("combine");
            Check(child.StandardOutput.ReadLine() == "combined", "fixture mutation acknowledgement");
            var afterRoots = reader.ReadRoots(fixture.UiAddress, fixture.PlayersAddress)!;
            var after = reader.ReadOwnedUnits(afterRoots);
            Check(after.ReadErrors == 0 && after.Units.Count == 4, "consumed objects remain readable as corpses");
            var active = after.Units.Where(UnitRules.IsActive).ToArray();
            Check(active.Length == 2 && active.All(u => u.Rawcode != "hfoo") && active.Count(u => u.Rawcode == "H000") == 1,
                "dead ingredients disappear from active quantities while result appears");
            Check(UnitRules.IsCharacterLabel("루피 - 흔함") && UnitRules.IsCharacterLabel("징베 - 초월") &&
                !UnitRules.IsCharacterLabel("연구소"), "character catalog labels exclude infrastructure");
            Check(GameSelection.ReadSingleSelectedAddress(reader, afterRoots) is null,
                "a selected ingredient becomes invalid after consumption");
            Check(!GameSelection.CurrentOwnedUnit(reader, afterRoots, fixture.FirstUnitAddress, "hfoo"),
                "consumed cached caster is rejected by the fresh body guard");
            Console.WriteLine("PASS: cross-process ReadProcessMemory, owner filter, duplicate exclusion, rawcodes, exact selection identity, selection failure cases, retained corpse exclusion, ingredient replacement, and character labels.");

            void Select(string command, uint? expectedAddress, string message)
            {
                child.StandardInput.WriteLine(command);
                Check(child.StandardOutput.ReadLine() == "selected", "fixture selection acknowledgement: " + command);
                Check(GameSelection.ReadSingleSelectedAddress(reader, roots) == expectedAddress, message);
            }
            void Portrait(string command, uint? expectedAddress, string message, uint? requiredUnit = null)
            {
                child.StandardInput.WriteLine(command);
                Check(child.StandardOutput.ReadLine() == "hud", "fixture HUD acknowledgement");
                Check(GameSelection.ReadPortraitCaster(reader, fixture.UiAddress, roots, portraitGeometry, casterGroup, requiredUnit) == expectedAddress, message);
            }
            void Chat(string command)
            {
                child.StandardInput.WriteLine(command);
                Check(child.StandardOutput.ReadLine() == "chat", "fixture chat acknowledgement");
            }
            void Focus(string command, uint? expectedAddress, string message)
            {
                child.StandardInput.WriteLine(command);
                Check(child.StandardOutput.ReadLine() == "hud", "fixture focus acknowledgement");
                Check(GameSelection.ReadCommandFocusAddress(reader, roots) == expectedAddress, message);
            }
            void RejectChat(string command, string message)
            {
                Chat(command);
                var rejected = false;
                try { GameChatText.Read(reader, fixture.UiAddress, roots); }
                catch (IOException) { rejected = true; }
                Check(rejected, message);
            }
        }
        finally
        {
            if (!child.HasExited)
            {
                child.StandardInput.WriteLine("exit");
                if (!child.WaitForExit(3000)) child.Kill();
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Self-test failed: " + message);
    }

    private static void VerifyCancellationGuards()
    {
        // 네이티브 입력 함수를 실행하지 않는다. 컴파일된 비동기 본문에서 가드의 호출 순서를 검사한다.
        var choice = Calls("UseChoiceWispAsync");
        var click = Calls("ClickAsync");
        static bool Guard(MethodBase method) => method.DeclaringType == typeof(CancellationToken) && method.Name == nameof(CancellationToken.ThrowIfCancellationRequested);
        var entry = choice.FindIndex(Guard);
        var geometry = choice.FindIndex(method => method.DeclaringType == typeof(GameSelection) && method.Name == "ReadGeometry");
        var command = choice.FindIndex(method => method.DeclaringType == typeof(GameInput) && method.Name == nameof(GameInput.PressNormalKey));
        var mouse = click.FindIndex(method => method.DeclaringType == typeof(GameSelection) && method.Name == "SendMouse");
        Check(entry >= 0 && entry < geometry, "a cancelled choice is rejected before camera or selection input can begin");
        Check(command > 0 && Guard(choice[command - 1]), "the M command has its own immediately preceding cancellation guard");
        Check(choice.Take(command).TakeLast(8).Any(method => method.Name == "ChoiceInputValid") &&
            choice.Take(command).TakeLast(8).Any(method => method.Name.Contains("ExactSourceSelection", StringComparison.Ordinal)),
            "the M command rechecks foreground, session and exact source selection after reading batch positions");
        Check(mouse > 0 && click.Take(mouse).Any(Guard), "a cancelled click is rejected before mouse-down while mouse-up cleanup remains available");
        Console.WriteLine("PASS: native choice/click input guards 4/4 IL checks; no input method invoked.");

        static List<MethodBase> Calls(string name)
        {
            var method = typeof(GameSelection).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            var machine = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType;
            var body = machine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var il = body.GetMethodBody()!.GetILAsByteArray()!;
            var opcodes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public).Where(field => field.FieldType == typeof(OpCode))
                .Select(field => (OpCode)field.GetValue(null)!).GroupBy(opcode => unchecked((ushort)opcode.Value)).ToDictionary(group => group.Key, group => group.First());
            var result = new List<MethodBase>();
            for (var offset = 0; offset < il.Length;)
            {
                var value = (ushort)il[offset++];
                if (value == 0xFE) value = (ushort)(0xFE00 | il[offset++]);
                var opcode = opcodes[value];
                if (opcode.OperandType == OperandType.InlineMethod)
                    result.Add(body.Module.ResolveMethod(BitConverter.ToInt32(il, offset))!);
                offset += opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => checked(4 + BitConverter.ToInt32(il, offset) * 4),
                    _ => 4
                };
            }
            return result;
        }
    }

    public static void Fixture()
    {
        // 32비트 게임 포인터를 모사하기 위해 이 테스트 프로세스 안에 낮은 주소를 예약한다.
        // 채팅 검증의 module+D326F0 UI 전역까지 이 가짜 프로세스 안에서 모사한다.
        var address = VirtualAlloc((IntPtr)0x20000000, 0xE00000, 0x3000, 0x04);
        if (address != (IntPtr)0x20000000) throw new Exception("Could not reserve fixture memory.");
        try
        {
            const uint b = 0x20000000, ui = b + 0x1000, players = b + 0x2000, table = b + 0x3000, array = b + 0x4000;
            // 기존 유닛 영역 +0x5000..+0x59FF와 겹치지 않는 선택 전용 영역이다.
            const uint player = b + 0x7000, selection = b + 0x8000, node = b + 0x9000, secondNode = node + 0x20;
            Write(b, ui); Write(b + 4, players); Write(ui + 0x3BC, table); Write(players + 0x28, 0);
            Write(players + 0x58, player); Write(player + 0x34, selection);
            Write(table + 0x604, 5); Write(table + 0x608, array);
            Unit(b + 0x5000, "hfoo", 0); Unit(b + 0x5200, "hfoo", 0);
            Unit(b + 0x5400, "hrif", 1); Unit(b + 0x5600, "n000", 0); Unit(b + 0x5800, "H000", 0);
            Write(array, b + 0x5000); Write(array + 4, b + 0x5200); Write(array + 8, b + 0x5400);
            Write(array + 12, b + 0x5600); Write(array + 16, b + 0x5000);
            SetSelection(b + 0x5000);
            Console.WriteLine(JsonSerializer.Serialize(new FixtureInfo(b, b + 4, b + 0x5000, b + 0x5200)));
            while (Console.ReadLine() is { } command && command != "exit")
            {
                if (command.StartsWith("chat-", StringComparison.Ordinal))
                {
                    const uint bar = b + 0xC000, edit = b + 0xC400, buffer = b + 0xC800;
                    var text = System.Text.Encoding.UTF8.GetBytes("해군영웅거프");
                    Write(b + 0xD326F0, ui); Write(ui + 0x3FC, bar); Write(bar, b + 0xAADFC4);
                    Write(bar + 0x1DC, 1); Write(bar + 0x1E0, edit); Write(edit, b + 0xAADE84);
                    Write(edit + 0x1E4, buffer); Write(edit + 0x1E8, (uint)text.Length); Write(edit + 0x1EC, 64);
                    Marshal.Copy(text, 0, (IntPtr)(long)buffer, text.Length);
                    Marshal.WriteByte((IntPtr)(long)(buffer + text.Length), 0);
                    switch (command)
                    {
                        case "chat-valid": break;
                        case "chat-hidden": Write(bar + 0x1DC, 0); break;
                        case "chat-wrong-vtable": Write(edit, b + 0xAADFC4); break;
                        case "chat-length": Write(edit + 0x1E8, 4097); Write(edit + 0x1EC, 8192); break;
                        case "chat-capacity": Write(edit + 0x1EC, 65537); break;
                        case "chat-length-cap": Write(edit + 0x1EC, (uint)text.Length - 1); break;
                        case "chat-termination": Marshal.WriteByte((IntPtr)(long)(buffer + text.Length), 1); break;
                        case "chat-embedded-nul": Marshal.WriteByte((IntPtr)(long)(buffer + 1), 0); break;
                        case "chat-ui-changed": Write(b + 0xD326F0, ui + 4); break;
                        default: throw new Exception("Unknown chat fixture command.");
                    }
                    Console.WriteLine("chat");
                    continue;
                }
                if (command.StartsWith("hud-", StringComparison.Ordinal))
                {
                    SetupHud();
                    switch (command)
                    {
                        case "hud-valid": break;
                        case "hud-focus-second": Write(selection + 0x1E0, b + 0x5200); break;
                        case "hud-focus-outside": Write(selection + 0x1E0, b + 0x5400); break;
                        case "hud-hidden": Write(b + 0xB000 + 0x94, 0); break;
                        case "hud-stale-child": Write(b + 0xB200 + 0x6A0, b + 0x5000); break;
                        case "hud-wrong-rawcode": Write(b + 0xB000 + 0x128, 0x48464F4F); break;
                        case "hud-duplicate": Write(b + 0xB900 + 0x124, b + 0x5200); Write(b + 0xBB00 + 0x6A0, b + 0x5200); break;
                        case "hud-inactive-peer": Write(b + 0x5000 + 0x20, 7); break;
                        case "hud-many-stale-children": SetupManyHud(); break;
                        default: throw new Exception("Unknown HUD fixture command.");
                    }
                    Console.WriteLine("hud");
                    continue;
                }
                if (command.StartsWith("select-", StringComparison.Ordinal))
                {
                    switch (command)
                    {
                        case "select-first":
                            Write(b + 0x5000 + 0x20, 6); Write(b + 0x5000 + 0x5C, 0);
                            SetSelection(b + 0x5000);
                            break;
                        case "select-second": SetSelection(b + 0x5200); break;
                        case "select-focus-mismatch": SetSelection(b + 0x5000); Write(selection + 0x1E0, b + 0x5200); break;
                        case "select-none":
                            Write(selection + 0x1F8, 0); Write(selection + 0x1F0, 0);
                            Write(selection + 0x1E0, 0);
                            break;
                        case "select-many":
                            SetSelection(b + 0x5000);
                            Write(selection + 0x1F8, 2); Write(node, secondNode);
                            Write(secondNode, 0); Write(secondNode + 8, b + 0x5200);
                            break;
                        case "select-other-owner": SetSelection(b + 0x5400); break;
                        case "select-dead":
                            Write(b + 0x5000 + 0x20, 7); Write(b + 0x5000 + 0x5C, 0x100);
                            SetSelection(b + 0x5000);
                            break;
                        default: throw new Exception("Unknown fixture selection command.");
                    }
                    Console.WriteLine("selected");
                    continue;
                }
                if (command != "combine") continue;
                // 실제 맵처럼 소모한 재료 객체를 목록에 남긴 채 숨김·죽음 처리한다.
                Write(b + 0x5000 + 0x20, 7); Write(b + 0x5000 + 0x5C, 0x100);
                Write(b + 0x5200 + 0x20, 7); Write(b + 0x5200 + 0x5C, 0x100);
                Write(array + 16, b + 0x5800);
                Console.WriteLine("combined");
            }

            void SetSelection(uint unit)
            {
                Write(selection + 0x1F0, node); Write(selection + 0x1F8, 1);
                Write(selection + 0x1E0, unit);
                Write(node, 0); Write(node + 8, unit);
            }
            void SetupHud()
            {
                Write(b + 0x5000 + 0x20, 6); Write(b + 0x5000 + 0x5C, 0);
                SetSelection(b + 0x5000);
                Write(selection + 0x1F8, 2); Write(node, secondNode); Write(secondNode, 0); Write(secondNode + 8, b + 0x5200);
                const uint bar = b + 0xA000, group = b + 0xA400, grid = b + 0xA800, rows = b + 0xAD00, rowData = b + 0xAE00;
                Write(ui + 0x3C4, bar); Write(bar, b + 0xAAFC58); Write(bar + 0x13C, group);
                Write(group, b + 0xAAFB44); Write(group + 0x130, grid); Write(grid + 0x154, rows); Write(rows + 8, rowData);
                HudButton(rowData, b + 0xB000, b + 0xB200, b + 0x5200, .26f);
                HudButton(rowData + 4, b + 0xB900, b + 0xBB00, b + 0x5000, .33f);
            }
            void SetupManyHud()
            {
                const uint rows = b + 0xAD00, firstRow = b + 0xAE00, secondRow = b + 0xAE40;
                Write(rows + 8, firstRow); Write(rows + 24, secondRow);
                Write(selection + 0x1F8, 11); Write(selection + 0x1E0, b + 0x5200);
                Write(selection + 0x1F0, b + 0x20000);
                for (var index = 0; index < 11; index++)
                {
                    var unit = index == 0 ? b + 0x5200 : b + 0x10000 + (uint)index * 0x200;
                    Unit(unit, "hfoo", 0);
                    var itemNode = b + 0x20000 + (uint)index * 0x20;
                    Write(itemNode, index == 10 ? 0 : itemNode + 0x20); Write(itemNode + 8, unit);
                    var button = b + 0x30000 + (uint)index * 0x1000;
                    var child = button + 0x200;
                    HudButton((index < 6 ? firstRow : secondRow) + (uint)(index % 6 * 4), button, child, unit, .26f + (index % 6) * .065f);
                    // 실제 관측처럼 렌더 child는 여러 행/과거 개체를 가리키지만 main button은 현재 명단과 일치한다.
                    Write(child + 0x6A0, b + 0x5400);
                }
            }
            void HudButton(uint slot, uint button, uint child, uint unit, float left)
            {
                Write(slot, button); Write(button, b + 0xAAFAD4); Write(button + 0x90, 1); Write(button + 0x94, 1);
                Write(button + 0x124, unit); Write(button + 0x128, 0x68666F6F); Write(button + 0x134, child);
                Write(child, b + 0xAAD938); Write(child + 0x94, 1); Write(child + 0x6A0, unit);
                Write(child + 0x44, BitConverter.SingleToUInt32Bits(.02f)); Write(child + 0x48, BitConverter.SingleToUInt32Bits(left));
                Write(child + 0x4C, BitConverter.SingleToUInt32Bits(.08f)); Write(child + 0x50, BitConverter.SingleToUInt32Bits(left + .06f));
                Write(button + 0x44, BitConverter.SingleToUInt32Bits(.02f)); Write(button + 0x48, BitConverter.SingleToUInt32Bits(left));
                Write(button + 0x4C, BitConverter.SingleToUInt32Bits(.08f)); Write(button + 0x50, BitConverter.SingleToUInt32Bits(left + .06f));
            }
        }
        finally { VirtualFree(address, 0, 0x8000); }
    }

    private static void Write(uint address, uint value) => Marshal.WriteInt32((IntPtr)(long)address, unchecked((int)value));
    private static void Unit(uint address, string rawcode, uint owner)
    {
        uint value = 0;
        foreach (var character in rawcode) value = (value << 8) | character;
        Write(address + 0x30, value); Write(address + 0x58, owner); Write(address + 0x20, 6);
    }
    private record FixtureInfo(uint UiAddress, uint PlayersAddress, uint FirstUnitAddress, uint SecondUnitAddress);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint freeType);
}
