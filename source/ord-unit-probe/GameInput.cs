using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// 게임 메모리는 읽기만 사용한다. 단축키는 게임 창의 메시지 큐로, 채팅은 전면 창에 Windows 입력으로 보낸다.
static class GameInput
{
    private static readonly HashSet<string> Targeted = new(StringComparer.Ordinal)
        { "A19W", "A19U", "A19S", "A19V", "A19T", "A0QG" };
    internal static bool IsKoreanChatCommand([NotNullWhen(true)] string? command) =>
        command is { Length: > 0 and <= 128 } && command.Any(c => c is >= '가' and <= '힣') &&
        command.All(c => c is >= ' ' and <= '~' or >= '가' and <= '힣') && Encoding.UTF8.GetByteCount(command) <= 128;
    public static bool Supported(CraftCommand command) =>
        command.ChatCommand is not null ? IsKoreanChatCommand(command.ChatCommand) :
        !Targeted.Contains(command.RecipeId) && command.Hotkey is { Length: 1 } key &&
        "BCXZ".Contains(key[0]) && command.CasterUnitRawcodes.Length > 0;

    public static async Task<string?> ExecuteAsync(CraftCommand command, JsonElement baseline, CancellationToken cancel, bool requireForeground = false)
    {
        if (command.ChatCommand is not null && !IsKoreanChatCommand(command.ChatCommand))
            return "한글 조합 명령을 확인하지 못했습니다.";
        string? chatStage = null;
        void TraceChat(string stage, string? detail = null)
        {
            chatStage = stage;
            try { Console.Error.WriteLine(JsonSerializer.Serialize(new { component = "ord_chat_input", timestamp = DateTimeOffset.UtcNow,
                recipeId = command.RecipeId, stage, detail })); }
            catch (Exception error) when (error is IOException or InvalidOperationException) { }
        }
        try
        {
            using var process = Process.GetProcessById(baseline.GetProperty("processId").GetInt32());
            var game = process.Modules.Cast<ProcessModule>().SingleOrDefault(m => m.ModuleName.Equals("Game.dll", StringComparison.OrdinalIgnoreCase));
            if (game?.FileVersionInfo.FileVersion != "1.28.5.7680") return "지원하는 워크래프트 버전을 확인할 수 없습니다.";
            var moduleBase = checked((uint)game.BaseAddress.ToInt64());
            using var memory = new MemoryReader(process.Id, 0x0410);
            var storm = process.Modules.Cast<ProcessModule>().SingleOrDefault(m => m.ModuleName.Equals("Storm.dll", StringComparison.OrdinalIgnoreCase));
            var map = storm is null ? null : LoadedMapReader.Read(memory, checked((uint)storm.BaseAddress.ToInt64()));
            if (map is null || !string.Equals(map, baseline.GetProperty("loadedMap").GetString(), StringComparison.OrdinalIgnoreCase))
                return "실행 중인 맵이 바뀌어 입력하지 않았습니다.";
            using (var file = File.OpenRead(map))
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(file)), baseline.GetProperty("loadedMapHash").GetString(), StringComparison.OrdinalIgnoreCase))
                    return "실행 중인 맵을 확인하지 못해 입력하지 않았습니다.";
            var player = baseline.GetProperty("localPlayer").GetInt32();
            var initial = memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0);
            if (initial is null || initial.LocalPlayer != player) return "플레이어가 바뀌어 조합을 실행하지 않았습니다.";
            if (baseline.GetProperty("sessionIdentity").GetString() != $"{initial.Ui:X8}:{initial.Players:X8}:{initial.Table:X8}")
                return "게임이 바뀌어 조합을 실행하지 않았습니다.";
            bool ItemsPresent()
            {
                var required = command.ItemRequirements ?? [];
                if (required.Length == 0) return true;
                var state = GameItems.Capture(memory, initial, moduleBase, game.FileVersionInfo.FileVersion,
                    baseline.GetProperty("loadedMapHash").GetString());
                return state.Status == "confirmed" && state.LocalPlayer == initial.LocalPlayer &&
                    CraftItems.Available(state.Counts, required, out _);
            }
            if (!ItemsPresent()) return "필요한 아이템의 보유 여부를 확인하지 못해 조합하지 않았습니다.";
            bool ConditionsPresent()
            {
                var required = command.Conditions ?? [];
                if (required.Length == 0 && command.WoodCost == 0 && command.GoldCost == 0) return true;
                CraftConditions.State? conditions = null;
                if (required.Length > 0)
                {
                    var state = GameRecipeConditions.Capture(memory, initial, moduleBase, game.FileVersionInfo.FileVersion,
                        baseline.GetProperty("loadedMapHash").GetString());
                    var frame = JsonSerializer.SerializeToElement(new { localPlayer = initial.LocalPlayer, recipeConditions = state }, JsonSerializerOptions.Web);
                    conditions = CraftConditions.Read(frame);
                }
                if (!CraftConditions.TryProject(conditions, required, out _, out var distortionWood, out _, out _)) return false;
                if (command.WoodCost == 0 && command.GoldCost == 0 && distortionWood == 0) return true;
                var resources = GameResources.Capture(memory, initial, moduleBase, game.FileVersionInfo.FileVersion);
                return resources.Status == "confirmed" && resources.LocalPlayer == initial.LocalPlayer &&
                    resources.Wood >= checked(command.WoodCost + distortionWood) && resources.Gold >= command.GoldCost;
            }
            if (!ConditionsPresent()) return "조합 제한 상태가 바뀌어 입력하지 않았습니다.";
            bool ReadInventory(out OwnedData? inventory) => TryReadCommandInventory(command, initial,
                memory.ReadOwnedUnits, () => memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0), out inventory);
            if (!ReadInventory(out var owned))
                return "유닛 정보가 바뀌었습니다. 보유량이 갱신된 뒤 다시 눌러 주세요.";
            var active = owned!.Units.Where(UnitRules.IsActive).ToArray();
            if (!HasIngredients(command, owned))
                return "조합에 필요한 재료 유닛이 부족합니다.";
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero || !IsWindow(window) || IsIconic(window))
                return "워크래프트 창을 연 뒤 다시 눌러 주세요.";
            bool SameGameWindow() => IsWindow(window) && !IsIconic(window) &&
                GetWindowThreadProcessId(window, out var windowOwner) != 0 && windowOwner == (uint)process.Id;
            if (requireForeground && GetForegroundWindow() != window)
                return "게임 창을 벗어나 조합을 중단했습니다.";
            if (!NormalMode(memory, moduleBase, initial.Ui))
                return "게임의 메뉴와 채팅창을 닫은 뒤 다시 눌러 주세요.";
            if (command.ChatCommand is null)
            {
                // 사용자가 승인한 게임 창에만 단축키 메시지를 한 번 전달한다.
                for (var attempt = 0; attempt < 10 && KeysHeld(); attempt++) await Task.Delay(50, cancel);
                if (KeysHeld()) return "키와 마우스 버튼을 놓은 뒤 다시 눌러 주세요.";
                cancel.ThrowIfCancellationRequested();
                GetWindowThreadProcessId(window, out var initialOwner);
                if (initialOwner != process.Id) return "게임 창이 바뀌어 입력하지 않았습니다.";
                var selected = await GameSelection.EnsureCasterAsync(memory, moduleBase, initial, window, command, active, cancel);
                if (selected.Error is not null) return selected.Error;
                if (!ReadInventory(out var beforeHotkey) || !HasIngredients(command, beforeHotkey!) || !ItemsPresent() || !ConditionsPresent())
                    return "조합 재료가 바뀌어 입력하지 않았습니다. 보유량을 확인해 주세요.";
                GetWindowThreadProcessId(window, out var owner);
                if (owner != process.Id || !NormalMode(memory, moduleBase, initial.Ui) ||
                    KeysHeld() || ((requireForeground || selected.AutomaticallySelected) && GetForegroundWindow() != window) ||
                    !GameSelection.SameSession(initial, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) ||
                    GameSelection.ReadSingleSelectedAddress(memory, initial) != selected.Address ||
                    !SelectedCaster(memory, initial, command)) return "게임이나 선택한 유닛이 바뀌어 입력하지 않았습니다.";
                cancel.ThrowIfCancellationRequested();
                PostKey(window, (ushort)command.Hotkey![0]);
                return null;
            }
            // Windows가 허용하는 전면 전환만 사용한다. 포커스 제한을 우회하지 않는다.
            if (GetForegroundWindow() != window) SetForegroundWindow(window);
            for (var attempt = 0; attempt < 10 && GetForegroundWindow() != window; attempt++)
                await Task.Delay(50, cancel);
            if (GetForegroundWindow() != window) return "게임 창으로 전환하지 못해 입력하지 않았습니다.";
            for (var attempt = 0; attempt < 10 && KeysHeld(); attempt++) await Task.Delay(50, cancel);
            if (KeysHeld()) return "키와 마우스 버튼을 놓은 뒤 다시 눌러 주세요.";
            cancel.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != window || !SameGameWindow() || !NormalMode(memory, moduleBase, initial.Ui) ||
                !GameSelection.SameSession(initial, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)))
                return "게임 화면 상태가 바뀌어 입력하지 않았습니다.";
            TraceChat("opening_chat");
            await PressChatWindowKeyAsync(window, 0x0D, () => SameGameWindow() && !KeysHeld() &&
                NormalMode(memory, moduleBase, initial.Ui) &&
                GameSelection.SameSession(initial, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)), cancel);
            for (var attempt = 0; attempt < 20 && !ChatOpen(memory, moduleBase); attempt++)
            {
                if (GetForegroundWindow() != window) return "게임 창이 바뀌어 채팅 입력을 중단했습니다.";
                await Task.Delay(25, cancel);
            }
            if (GetForegroundWindow() != window || !ChatOpen(memory, moduleBase))
                return "채팅창이 열렸는지 확인하지 못해 조합 명령을 입력하지 않았습니다.";
            if (GameChatText.Read(memory, moduleBase, initial).Bytes.Length != 0)
                return "채팅창에 글자가 남아 있어 조합 명령을 입력하지 않았습니다. 채팅창을 닫고 다시 눌러 주세요.";
            var typing = await TypeKoreanChatCommandAsync(command.ChatCommand, window, () =>
                GetForegroundWindow() == window && SameGameWindow() && ChatOpen(memory, moduleBase) && !KeysHeld() &&
                GameSelection.SameSession(initial, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)),
                () => GameChatText.Matches(GameChatText.Read(memory, moduleBase, initial), command.ChatCommand), cancel,
                stage => TraceChat(stage));
            TraceChat(typing.OriginalImeRestored ? "typing_completed" : "typing_completed_with_ime_warning", typing.RestorationError);
            await Task.Delay(50, cancel);
            if (GetForegroundWindow() != window || !ChatOpen(memory, moduleBase))
                return "채팅 입력 중 게임 화면이 바뀌었습니다. 입력한 내용을 확인해 주세요.";
            var typed = GameChatText.Read(memory, moduleBase, initial);
            if (!GameChatText.Matches(typed, command.ChatCommand))
                return "한글 명령이 정확히 입력되지 않아 전송하지 않았습니다. 채팅창을 닫고 다시 눌러 주세요.";
            await Task.Delay(30, cancel);
            return await GameChatSubmission.SubmitWithAsyncEnterAsync(command.ChatCommand, typed,
                () => GameChatText.Read(memory, moduleBase, initial),
                () => GetForegroundWindow() == window && SameGameWindow() && ChatOpen(memory, moduleBase) && !KeysHeld() &&
                    GameSelection.SameSession(initial, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)),
                () => ReadInventory(out var beforeChatSubmit) && HasIngredients(command, beforeChatSubmit!) && ItemsPresent() && ConditionsPresent(),
                token => PressChatWindowKeyAsync(window, 0x0D, () => GetForegroundWindow() == window && SameGameWindow() && ChatOpen(memory, moduleBase) &&
                    !KeysHeld() && GameSelection.SameSession(initial, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) &&
                    (command.MaterialChoiceRawcode is null || ReadInventory(out var finalChoiceInventory) && HasIngredients(command, finalChoiceInventory!)), token),
                () => ChatOpen(memory, moduleBase),
                () => GetForegroundWindow() == window && SameGameWindow() &&
                    GameSelection.SameSession(initial, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)), cancel,
                trace: stage => TraceChat(stage));
        }
        catch (OperationCanceledException)
        {
            if (chatStage is not null) TraceChat("cancelled_after_" + chatStage);
            throw;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or ArgumentException or OverflowException)
        {
            if (chatStage is not null) TraceChat("failed_after_" + chatStage, error.GetType().Name + ": " + error.Message);
            return "게임 상태나 입력 결과를 확인하지 못했습니다. 게임에서 결과를 확인한 뒤 다시 시도해 주세요.";
        }
    }

    // 적의 생성·제거는 전역 배열을 바꿀 수 있다. 현재 명령과 관련된 소유 개체만 비교한다.
    internal static bool TryReadCommandInventory(CraftCommand command, Roots initial,
        Func<Roots, OwnedData> readOwned, Func<Roots?> readRoots, [NotNullWhen(true)] out OwnedData? owned)
    {
        owned = null;
        var first = readOwned(initial);
        if (first.ReadErrors != 0 || !CraftMaterialChoices.UniqueNativeSource(command, first)) return false;
        var middle = readRoots();
        if (!SnapshotStability.SameSession(middle, initial)) return false;
        var second = readOwned(middle!);
        if (!SnapshotStability.SameSession(readRoots(), initial) || second.ReadErrors != 0 || !CraftMaterialChoices.UniqueNativeSource(command, second)) return false;
        var relevantIds = command.Ingredients.Select(ingredient => ingredient.Rawcode)
            .Concat(command.CasterUnitRawcodes).Concat(command.RecipeId == "ET02" ? CraftMaterialChoices.NativeCandidateForms : []).ToHashSet(StringComparer.Ordinal);
        if (relevantIds.Count > 0 && !SnapshotStability.SameInventory(first, second, relevantIds)) return false;
        owned = second;
        return true;
    }

    internal static bool HasIngredients(CraftCommand command, OwnedData owned) => owned.ReadErrors == 0 && CraftMaterialChoices.UniqueNativeSource(command, owned) &&
        command.Ingredients.All(ingredient => owned.Units.Count(unit =>
            UnitRules.IsActive(unit) && unit.Rawcode == ingredient.Rawcode) >= ingredient.Count);

    private static bool NormalMode(MemoryReader memory, uint moduleBase, uint ui)
    {
        if (ChatOpen(memory, moduleBase) || memory.U32(moduleBase + 0xD326F0) != ui ||
            memory.U32(moduleBase + 0xD32318) != 4 || memory.U32(moduleBase + 0xD3231C) != 4) return false;
        // 선택 모드만 허용한다. 메뉴·타겟 지정·대화상자·알 수 없는 모드는 입력하지 않는다.
        var normal = memory.U32(ui + 0x214);
        return normal >= 0x10000 && memory.U32(ui + 0x1B4) == normal;
    }
    private static bool ChatOpen(MemoryReader memory, uint moduleBase) => memory.Read(moduleBase + 0xD04FEC, 1)[0] != 0;
    private static bool SelectedCaster(MemoryReader memory, Roots roots, CraftCommand command)
    {
        var player = memory.U32(roots.Players + 0x58 + checked((uint)roots.LocalPlayer * 4));
        var selection = memory.U32(player + 0x34);
        if (memory.U32(selection + 0x1F8) != 1) return false;
        var node = memory.U32(selection + 0x1F0);
        var unit = memory.U32(node + 8);
        if (memory.U32(unit + 0x58) != roots.LocalPlayer) return false;
        var body = memory.Read(unit + 0x20, 0x40);
        var rawcode = MemoryReader.DecodeRawcode(body.AsSpan(0x10, 4));
        if (rawcode is null) return false;
        var item = new UnitObject(unit, rawcode,
            BitConverter.ToUInt32(body, 0), BitConverter.ToUInt32(body, 0x3C));
        return UnitRules.IsActive(item) && command.CasterUnitRawcodes.Contains(item.Rawcode, StringComparer.Ordinal) &&
            memory.U32(selection + 0x1F8) == 1 && memory.U32(selection + 0x1F0) == node && memory.U32(node + 8) == unit;
    }
    private static bool KeysHeld() => new[] { 1, 2, 4, 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);
    private static Input Key(ushort vk, ushort scan = 0, uint flags = 0) => new() { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { Vk = vk, Scan = scan, Flags = flags } } };
    internal static void PressNormalKey(ushort key) => Press(key);
    internal static void PressChatWindowKey(IntPtr window, ushort key)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || GetForegroundWindow() != window || key is not (0x0D or 0x1B))
            throw new IOException("A verified chat window and an Enter or Escape key are required.");
        // IME·키 상태와 같은 입력 경로로 보낸다. 단축키의 직접 메시지 경로는 변경하지 않는다.
        Press(key);
    }
    internal static Task PressChatWindowKeyAsync(IntPtr window, ushort key, Func<bool>? inputValid = null, CancellationToken cancel = default)
    {
        if (window == IntPtr.Zero || key is not (0x0D or 0x1B)) throw new IOException("A verified chat window and an Enter or Escape key are required.");
        var thread = GetWindowThreadProcessId(window, out var owner);
        if (thread == 0 || owner == 0) throw new IOException("The game chat window owner could not be verified.");
        return GameChatSubmission.PressOnceAsync(() => SendChatKey(false), () => SendChatKey(true),
            () => IsWindow(window) && !IsIconic(window) && GetWindowThreadProcessId(window, out var currentOwner) == thread &&
                currentOwner == owner && GetForegroundWindow() == window && (inputValid?.Invoke() ?? true), cancel);
        void SendChatKey(bool up)
        {
            if (SendInput(1, [Key(key, 0, up ? 2u : 0u)], Marshal.SizeOf<Input>()) != 1)
                throw new IOException("Windows chat key input was incomplete.");
        }
    }
    internal static Task<KoreanTypingOutcome> TypeKoreanChatCommandAsync(string command, IntPtr window = default, Func<bool>? inputValid = null,
        Func<bool>? textMatches = null, CancellationToken cancel = default, Action<string>? trace = null)
    {
        if (!IsKoreanChatCommand(command)) throw new ArgumentException("A map-registered Korean command is required.", nameof(command));
        if (window == IntPtr.Zero || inputValid is null || textMatches is null)
            throw new IOException("A verified game chat and exact-text check are required.");
        return KoreanImeInput.TypeAsync(window, command, inputValid, textMatches, cancel, trace);
    }
    private static void Press(ushort key)
    {
        var inputs = new[] { Key(key), Key(key, 0, 2) };
        var sent = SendInput(2, inputs, Marshal.SizeOf<Input>());
        if (sent == 2) return;
        // 일부만 전달됐을 때 누른 키 해제만 시도하며 조합 키를 다시 누르지 않는다.
        if (sent == 1) SendInput(1, [Key(key, 0, 2)], Marshal.SizeOf<Input>());
        throw new IOException("Windows input was incomplete.");
    }
    private static void PostKey(IntPtr window, ushort key)
    {
        var scan = MapVirtualKeyW(key, 0) & 0xFF;
        var down = PostMessageW(window, 0x0100, key, (IntPtr)(1L | (scan << 16)));
        var up = PostMessageW(window, 0x0101, key, (IntPtr)(1L | (scan << 16) | 0xC0000000L));
        if (!down || !up) throw new IOException("Windows window message was incomplete.");
    }
    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new IOException("Windows input was incomplete.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public Keyboard Keyboard;
        [FieldOffset(0)] public Mouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(IntPtr window, uint message, nuint value, IntPtr detail);
}
