using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

internal sealed record KoreanTypingOutcome(bool TextCommitted, bool OriginalImeRestored, string? RestorationError);

// 실제 전면 게임 창에만 두벌식 키 입력을 보낸다. 다른 창의 HIMC나 게임 메모리는 변경하지 않는다.
static class KoreanImeInput
{
    private const uint WmImeControl = 0x0283;
    private const ushort VkHangul = 0x15, VkShift = 0x10;
    private const uint NativeMode = 1;

    public static async Task<KoreanTypingOutcome> TypeAsync(IntPtr gameWindow, string command, Func<bool> inputValid,
        Func<bool> textMatches, CancellationToken cancel, Action<string>? trace = null)
    {
        if (!GameInput.IsKoreanChatCommand(command))
            throw new ArgumentException("A map-registered Korean command is required.", nameof(command));
        ArgumentNullException.ThrowIfNull(inputValid);
        ArgumentNullException.ThrowIfNull(textMatches);
        var steps = KoreanKeyboardPlan.CreateTypingSteps(command);
        var target = Target.Create(gameWindow, inputValid);
        var original = target.ReadState();
        var pressed = new HashSet<ushort>();
        return await CompleteAsync(async () =>
        {
            foreach (var step in steps)
            {
                cancel.ThrowIfCancellationRequested();
                if (step.Action == KoreanTypingAction.HangulMode)
                    await SetNativeAsync(target, true, pressed, cancel);
                else if (step.Action == KoreanTypingAction.AlphanumericMode)
                {
                    // ! 등 기호를 입력한 뒤에 확정하므로 기호 앞에 공백을 넣지 않는다.
                    if (target.ReadState().Native)
                    {
                        Press(target, VkHangul, false, pressed);
                        await WaitStateAsync(target, state => !state.Native, cancel,
                            "Korean IME did not commit and leave native mode.");
                    }
                }
                else { Press(target, step.Key.VirtualKey, step.Key.Shift, pressed); await Task.Delay(18, cancel); }
            }
            var until = Stopwatch.StartNew();
            while (true)
            {
                target.EnsureInput();
                cancel.ThrowIfCancellationRequested();
                if (textMatches()) break;
                if (until.ElapsedMilliseconds >= 1000)
                    throw new IOException("Korean IME committed text did not match the registered command.");
                await Task.Delay(25, cancel);
            }
        }, () => RestoreAsync(target, original), () => ReleasePressed(pressed), target.EnsureInput, textMatches, cancel, trace);
    }

    // 전송할 글자의 확정과 사용자 IME 설정 복원은 다른 결과다. 복원만 실패했어도
    // 같은 IME·포커스·세션과 확정된 원문을 다시 확인할 수 있을 때에만 전송 준비를 유지한다.
    internal static async Task<KoreanTypingOutcome> CompleteAsync(Func<Task> type, Func<Task> restore,
        Func<string?> releasePressed, Action ensureInput, Func<bool> textMatches, CancellationToken cancel,
        Action<string>? trace = null)
    {
        void Stage(string value) { try { trace?.Invoke(value); } catch (Exception error) when (error is IOException or InvalidOperationException) { } }
        Exception? failure = null, restorationFailure = null;
        string? releaseError = null;
        try { cancel.ThrowIfCancellationRequested(); Stage("typing_korean"); await type(); }
        catch (Exception error) { failure = error; }
        finally
        {
            Stage("releasing_typing_keys");
            try { releaseError = releasePressed(); }
            catch (Exception error) { failure = new IOException("Korean typing keys could not be released.", failure ?? error); }
            Stage("restoring_original_ime");
            try { await restore(); }
            catch (Exception error) { restorationFailure = error; Stage("original_ime_restore_failed"); }
        }
        if (releaseError is not null) failure = new IOException(releaseError, failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        cancel.ThrowIfCancellationRequested();
        Stage("checking_committed_text_after_ime_cleanup");
        ensureInput();
        if (!textMatches()) throw new IOException("The committed Korean command changed during IME cleanup.");
        Stage(restorationFailure is null ? "committed_text_and_ime_restored" : "committed_text_with_ime_restore_warning");
        return new(true, restorationFailure is null, restorationFailure?.Message);
    }

    private static async Task SetNativeAsync(Target target, bool native, HashSet<ushort> pressed, CancellationToken cancel)
    {
        target.EnsureInput();
        cancel.ThrowIfCancellationRequested();
        var before = target.ReadState();
        if (native)
        {
            if (before.Native && before.Open) return;
            target.SendControl(6, 1, requireInput: true);
            target.SendControl(2, before.Conversion | NativeMode, requireInput: true);
        }
        else
        {
            if (!before.Native) return;
            target.SendControl(2, before.Conversion & ~NativeMode, requireInput: true);
        }
        await Task.Delay(35, cancel);
        var changed = target.ReadState();
        if (changed.Native == native && (!native || changed.Open)) return;
        // 일부 한국어 IME는 SET 응답만으로 실제 입력 모드를 바꾸지 않는다.
        if (changed.Native != native)
        {
            Press(target, VkHangul, false, pressed);
            await WaitStateAsync(target, state => state.Native == native && (!native || state.Open), cancel,
                native ? "Korean IME native mode could not be enabled." : "Korean IME alphanumeric mode could not be enabled.");
            return;
        }
        throw new IOException("Korean IME open state could not be verified.");
    }

    private static async Task WaitStateAsync(Target target, Func<ImeState, bool> matches, CancellationToken cancel, string error)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            target.EnsureInput();
            cancel.ThrowIfCancellationRequested();
            if (matches(target.ReadState())) return;
            if (elapsed.ElapsedMilliseconds >= 500) throw new IOException(error);
            await Task.Delay(20, cancel);
        }
    }

    private static async Task RestoreAsync(Target target, ImeState original)
    {
        // 복원에는 물리 키를 쓰지 않는다. 게임이 뒤로 가도 검증한 원래 IME 창만 대상으로 한다.
        var current = target.ReadState();
        if (current.Conversion != original.Conversion) target.SendControl(2, original.Conversion, requireInput: false);
        current = target.ReadState();
        if (current.Open != original.Open) target.SendControl(6, original.Open ? 1u : 0u, requireInput: false);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var actual = target.ReadState();
            if (actual == original) return;
            if (elapsed.ElapsedMilliseconds >= 500)
                throw new IOException($"IME restoration was not confirmed (open={actual.Open}, conversion=0x{actual.Conversion:X}; expected open={original.Open}, conversion=0x{original.Conversion:X}).");
            await Task.Delay(20);
        }
    }

    private static void Press(Target target, ushort key, bool shift, HashSet<ushort> pressed)
    {
        target.EnsureInput();
        var inputs = shift
            ? new[] { Key(VkShift), Key(key), Key(key, up: true), Key(VkShift, up: true) }
            : new[] { Key(key), Key(key, up: true) };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        for (var index = 0; index < Math.Min(sent, (uint)inputs.Length); index++)
        {
            var keyboard = inputs[index].Data.Keyboard;
            if ((keyboard.Flags & 2) == 0) pressed.Add(keyboard.Vk);
            else pressed.Remove(keyboard.Vk);
        }
        if (sent != inputs.Length) throw new IOException("Windows did not accept the complete Korean keyboard input batch.");
    }

    private static string? ReleasePressed(HashSet<ushort> pressed)
    {
        var incomplete = false;
        foreach (var key in pressed.ToArray())
        {
            if (SendInput(1, [Key(key, up: true)], Marshal.SizeOf<Input>()) != 1)
                incomplete = true;
            else pressed.Remove(key);
        }
        return incomplete ? "Windows did not release a key pressed by Korean IME input." : null;
    }

    private readonly record struct ImeState(bool Open, uint Conversion)
    {
        public bool Native => (Conversion & NativeMode) != 0;
    }

    private sealed class Target(IntPtr window, IntPtr ime, uint process, uint thread, Func<bool> inputValid)
    {
        internal static Target Create(IntPtr window, Func<bool> inputValid)
        {
            if (window == IntPtr.Zero || !IsWindow(window)) throw new IOException("A live game window is required for Korean IME input.");
            var thread = GetWindowThreadProcessId(window, out var process);
            if (thread == 0 || process == 0) throw new IOException("The game window owner could not be verified.");
            if ((GetKeyboardLayout(thread).ToInt64() & 0xFFFF) != 0x0412)
                throw new IOException("The game does not have a Korean keyboard layout.");
            var ime = ImmGetDefaultIMEWnd(window);
            if (ime == IntPtr.Zero) throw new IOException("The game has no default Korean IME window.");
            var target = new Target(window, ime, process, thread, inputValid);
            target.EnsureInput();
            return target;
        }

        internal void EnsureInput()
        {
            EnsureIdentity();
            if (GetForegroundWindow() != window || !inputValid())
                throw new IOException("The foreground game chat or session changed during Korean IME input.");
        }

        private void EnsureIdentity()
        {
            if (!IsWindow(window) || GetWindowThreadProcessId(window, out var owner) != thread || owner != process ||
                (GetKeyboardLayout(thread).ToInt64() & 0xFFFF) != 0x0412 ||
                !IsWindow(ime) || ImmGetDefaultIMEWnd(window) != ime ||
                GetWindowThreadProcessId(ime, out var imeOwner) != thread || imeOwner != process || GetWindow(ime, 4) != window)
                throw new IOException("The verified game IME window identity changed.");
            var name = new StringBuilder(32);
            if (GetClassNameW(ime, name, name.Capacity) == 0 || name.ToString() != "IME")
                throw new IOException("The default game input window is not an IME window.");
        }

        internal ImeState ReadState() => new(SendControl(5, 0, false) != 0, checked((uint)SendControl(1, 0, false)));

        internal nuint SendControl(uint command, uint value, bool requireInput)
        {
            if (requireInput) EnsureInput();
            else EnsureIdentity();
            Marshal.SetLastPInvokeError(0);
            if (SendMessageTimeoutW(ime, WmImeControl, command, (IntPtr)value, 0x22, 500, out var result) == IntPtr.Zero)
                throw new IOException($"The game IME control query or update failed (command={command}, error={Marshal.GetLastPInvokeError()}).");
            return result;
        }
    }

    private static Input Key(ushort key, bool up = false) => new()
    {
        Type = 1, Data = new InputUnion { Keyboard = new Keyboard { Vk = key, Flags = up ? 2u : 0u } }
    };
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
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder text, int count);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message,
        nuint value, IntPtr detail, uint flags, uint timeout, out nuint result);
}
