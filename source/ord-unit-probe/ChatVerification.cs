using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// 한글을 채팅 편집 버퍼까지만 확인한다. 채팅 전송·조합 실행 경로는 호출하지 않는다.
static class ChatVerification
{
    private const string Expected = "해군영웅거프";
    private const string SupportedVersion = "1.28.5.7680";
    private static readonly byte[] ExpectedBytes = Encoding.UTF8.GetBytes(Expected);
    // 이전 고정 시험 문자열이 CP949 바이트별로 잘못 인코딩된 결과. 다른 사용자 텍스트는 취소하지 않는다.
    private static readonly byte[] KnownMojibakeBytes = Convert.FromHexString("C387C398C2B1C2BAC2BFC2B5C2BFC3B5C2B0C385C387C381");
    private static readonly byte[] KnownTruncatedBytes = Convert.FromHexString("7470C385C38670");
    private static bool IsKnownMojibake(byte[] bytes) => bytes.AsSpan().SequenceEqual(KnownMojibakeBytes) ||
        bytes.AsSpan().SequenceEqual(KnownTruncatedBytes);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string snapshotPath, string outputPath, bool tryNormalFocus = false)
    {
        var report = new Dictionary<string, object?>
        {
            ["startedUtc"] = DateTimeOffset.UtcNow,
            ["purpose"] = "korean_chat_buffer_only",
            ["craftInputInvoked"] = false,
            ["chatSubmitted"] = false,
            ["expectedByteLength"] = ExpectedBytes.Length,
            ["inputProtocol"] = "physical_korean_ime",
            ["status"] = "failed"
        };
        FileStream? output = null;
        Process? process = null;
        MemoryReader? memory = null;
        Roots? roots = null;
        uint moduleBase = 0;
        var window = IntPtr.Zero;
        var ownsChat = false;
        var textVerified = false;
        var chatClosed = false;
        var escapeInvoked = false;
        Task PressProbeKeyAsync(ushort key) => GameInput.PressChatWindowKeyAsync(window, key);
        try
        {
            var inputFile = Path.GetFullPath(snapshotPath);
            output = new FileStream(Path.GetFullPath(outputPath), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            if (!OperatingSystem.IsWindows()) throw Reject("unsupported_platform");
            var first = await ReadSnapshotAsync(inputFile);
            process = Process.GetProcessById(first.ProcessId);
            window = process.MainWindowHandle;
            if (!ValidWindow(window, first.ProcessId)) throw Reject("game_window_unavailable");
            if (tryNormalFocus && GetForegroundWindow() != window)
            {
                var focusModule = process.Modules.Cast<ProcessModule>().SingleOrDefault(m => m.ModuleName.Equals("Game.dll", StringComparison.OrdinalIgnoreCase));
                if (focusModule?.FileVersionInfo.FileVersion != SupportedVersion) throw Reject("unsupported_game_version");
                report["normalFocusAttempted"] = true;
                SetForegroundWindow(window);
                for (var attempt = 0; attempt < 10 && GetForegroundWindow() != window; attempt++) await Task.Delay(50);
                if (GetForegroundWindow() != window) throw Reject("normal_foreground_denied");
            }
            var watch = Stopwatch.StartNew();
            while (GetForegroundWindow() != window)
            {
                if (watch.Elapsed >= TimeSpan.FromSeconds(120)) throw Reject("foreground_timeout");
                if (!ValidWindow(window, first.ProcessId) || process.HasExited) throw Reject("game_window_changed");
                await Task.Delay(50);
            }
            report["foregroundWaitMs"] = watch.Elapsed.TotalMilliseconds;
            await Task.Delay(500);
            if (GetForegroundWindow() != window) throw Reject("foreground_changed");
            var snapshot = await ReadSnapshotAsync(inputFile);
            if (!SameSession(first, snapshot)) throw Reject("snapshot_session_changed");
            report["processId"] = snapshot.ProcessId;
            report["sessionIdentity"] = snapshot.SessionIdentity;
            var modules = process.Modules.Cast<ProcessModule>().ToArray();
            var game = modules.SingleOrDefault(m => m.ModuleName.Equals("Game.dll", StringComparison.OrdinalIgnoreCase));
            var storm = modules.SingleOrDefault(m => m.ModuleName.Equals("Storm.dll", StringComparison.OrdinalIgnoreCase));
            if (game?.FileVersionInfo.FileVersion != SupportedVersion || storm is null) throw Reject("unsupported_game_version");
            moduleBase = checked((uint)game.BaseAddress.ToInt64());
            memory = new MemoryReader(process.Id, 0x0410);
            if (!string.Equals(LoadedMapReader.Read(memory, checked((uint)storm.BaseAddress.ToInt64())), snapshot.LoadedMap, StringComparison.OrdinalIgnoreCase))
                throw Reject("loaded_map_changed");
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using (var map = File.OpenRead(snapshot.LoadedMap))
                if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(map, budget.Token)), snapshot.LoadedMapHash, StringComparison.OrdinalIgnoreCase))
                    throw Reject("loaded_map_hash_changed");
            roots = memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0);
            if (roots is null || !MatchesRoots(snapshot, roots)) throw Reject("game_session_changed");
            var owned = memory.ReadOwnedUnits(roots);
            var second = memory.ReadOwnedUnits(roots);
            if (owned.ReadErrors != 0 || second.ReadErrors != 0 ||
                !owned.Units.OrderBy(u => u.Address).SequenceEqual(second.Units.OrderBy(u => u.Address))) throw Reject("owned_units_unstable");
            var ready = await ReadSnapshotAsync(inputFile);
            if (!SameSession(snapshot, ready) || !MatchesRoots(ready, roots)) throw Reject("snapshot_session_changed");
            report["snapshotSequence"] = ready.Sequence;
            report["snapshotTimestamp"] = ready.Timestamp;
            for (var attempt = 0; attempt < 20 && GetForegroundWindow() == window && MouseOrModifiersHeld(); attempt++)
                await Task.Delay(50, budget.Token);
            if (!InputValid(memory, moduleBase, roots, window, process.Id)) throw Reject("game_input_state_changed");
            if (ChatOpen(memory, moduleBase))
            {
                var previousProbe = ReadChat(memory, moduleBase, roots);
                if (!IsKnownMojibake(previousProbe.Bytes)) throw Reject("chat_already_open");
                var stableProbe = ReadChat(memory, moduleBase, roots);
                if (!InputValid(memory, moduleBase, roots, window, process.Id) || !ChatOpen(memory, moduleBase) ||
                    stableProbe.Edit != previousProbe.Edit || stableProbe.Buffer != previousProbe.Buffer ||
                    !stableProbe.Bytes.AsSpan().SequenceEqual(previousProbe.Bytes)) throw Reject("previous_probe_changed");
                await PressProbeKeyAsync(0x1B);
                for (var attempt = 0; attempt < 40 && ChatOpen(memory, moduleBase); attempt++)
                {
                    if (!InputValid(memory, moduleBase, roots, window, process.Id)) throw Reject("game_input_state_changed");
                    await Task.Delay(25, budget.Token);
                }
                if (ChatOpen(memory, moduleBase)) throw Reject("previous_probe_cleanup_unconfirmed");
                report["previousOwnedMojibakeCancelled"] = true;
            }
            if (memory.U32(roots.Ui + 0x1B4) != memory.U32(roots.Ui + 0x214)) throw Reject("game_input_mode_changed");

            // 이 검사에서 Enter는 여기 한 번만 보낸다. 뒤에는 전송 Enter가 없다.
            ownsChat = true;
            await PressProbeKeyAsync(0x0D);
            for (var attempt = 0; attempt < 40 && !ChatOpen(memory, moduleBase); attempt++)
            {
                if (!InputValid(memory, moduleBase, roots, window, process.Id)) throw Reject("game_input_state_changed");
                await Task.Delay(25, budget.Token);
            }
            if (!ChatOpen(memory, moduleBase)) throw Reject("chat_did_not_open");
            var empty = ReadChat(memory, moduleBase, roots);
            report["initialByteLength"] = empty.Bytes.Length;
            report["initialBufferEmpty"] = empty.Bytes.Length == 0;
            if (empty.Bytes.Length != 0) throw Reject("chat_buffer_not_empty");
            if (!InputValid(memory, moduleBase, roots, window, process.Id) || !ChatOpen(memory, moduleBase))
                throw Reject("game_input_state_changed");
            var typing = await GameInput.TypeKoreanChatCommandAsync(Expected, window, () =>
                InputValid(memory, moduleBase, roots, window, process.Id) && ChatOpen(memory, moduleBase),
                () => GameChatText.Matches(ReadChat(memory, moduleBase, roots), Expected), budget.Token);
            report["originalImeRestored"] = typing.OriginalImeRestored;
            report["imeRestorationError"] = typing.RestorationError;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(25, budget.Token);
                if (!InputValid(memory, moduleBase, roots, window, process.Id) || !ChatOpen(memory, moduleBase))
                    throw Reject("game_input_state_changed");
                var actual = ReadChat(memory, moduleBase, roots);
                report["actualByteLength"] = actual.Bytes.Length;
                report["chatBufferMatchesExpected"] = actual.Bytes.AsSpan().SequenceEqual(ExpectedBytes);
                if (!actual.Bytes.AsSpan().SequenceEqual(ExpectedBytes)) continue;
                await Task.Delay(50, budget.Token);
                var stable = ReadChat(memory, moduleBase, roots);
                if (!InputValid(memory, moduleBase, roots, window, process.Id) || !ChatOpen(memory, moduleBase) ||
                    stable.Edit != actual.Edit || stable.Buffer != actual.Buffer || !stable.Bytes.AsSpan().SequenceEqual(ExpectedBytes))
                    throw Reject("chat_buffer_changed");
                textVerified = true;
                break;
            }
            if (!textVerified) throw Reject("chat_buffer_mismatch");
        }
        catch (VerificationException error) { report["reason"] = error.Message; }
        catch (OperationCanceledException) { report["reason"] = "verification_timeout"; }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or ArgumentException or
            OverflowException or UnauthorizedAccessException or JsonException)
        {
            report["reason"] = output is null && File.Exists(outputPath) ? "output_already_exists" : "verification_read_failed";
            report["errorType"] = error.GetType().Name;
            report["error"] = error.Message;
        }
        finally
        {
            try
            {
                if (ownsChat && memory is not null && roots is not null && process is not null &&
                    InputValid(memory, moduleBase, roots, window, process.Id))
                {
                    if (!ChatOpen(memory, moduleBase)) chatClosed = true;
                    else
                    {
                        var current = ReadChat(memory, moduleBase, roots);
                        var safeToCancel = current.Bytes.Length == 0 || current.Bytes.AsSpan().SequenceEqual(ExpectedBytes) ||
                            IsKnownMojibake(current.Bytes);
                        report["cleanupBufferSafe"] = safeToCancel;
                        if (safeToCancel && InputValid(memory, moduleBase, roots, window, process.Id) && ChatOpen(memory, moduleBase))
                        {
                            // 사용자가 쓴 다른 텍스트가 보이면 취소 키도 보내지 않는다.
                            var fresh = ReadChat(memory, moduleBase, roots);
                            if (fresh.Edit != current.Edit || fresh.Buffer != current.Buffer ||
                                !fresh.Bytes.AsSpan().SequenceEqual(current.Bytes) ||
                                !InputValid(memory, moduleBase, roots, window, process.Id) || !ChatOpen(memory, moduleBase))
                                throw Reject("chat_cleanup_state_changed");
                            await PressProbeKeyAsync(0x1B);
                            escapeInvoked = true;
                            for (var attempt = 0; attempt < 40; attempt++)
                            {
                                await Task.Delay(25);
                                if (!InputValid(memory, moduleBase, roots, window, process.Id)) break;
                                if (!ChatOpen(memory, moduleBase)) { chatClosed = true; break; }
                            }
                        }
                    }
                }
            }
            catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or ArgumentException or
                OverflowException or UnauthorizedAccessException or VerificationException) { report["cleanupErrorType"] = error.GetType().Name; }
            finally { memory?.Dispose(); process?.Dispose(); }
        }
        report["chatClosed"] = chatClosed;
        report["escapeInvoked"] = escapeInvoked;
        if (textVerified && escapeInvoked && chatClosed) report["status"] = "verified";
        else if (!report.ContainsKey("reason")) report["reason"] = textVerified ? "chat_cleanup_unconfirmed" : "chat_buffer_unverified";
        report["finishedUtc"] = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (output is not null)
        {
            try { await output.WriteAsync(Encoding.UTF8.GetBytes(json + Environment.NewLine)); await output.FlushAsync(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Chat verification output failed."); return 1; }
            finally { await output.DisposeAsync(); }
        }
        Console.WriteLine(json);
        return Equals(report["status"], "verified") ? 0 : 1;
    }

    private static ChatTextState ReadChat(MemoryReader memory, uint moduleBase, Roots roots) =>
        GameChatText.Read(memory, moduleBase, roots);

    private static bool ChatOpen(MemoryReader memory, uint moduleBase) => memory.Read(moduleBase + 0xD04FEC, 1)[0] != 0;
    private static bool InputValid(MemoryReader memory, uint moduleBase, Roots roots, IntPtr window, int pid) =>
        GetForegroundWindow() == window && ValidWindow(window, pid) && !MouseOrModifiersHeld() &&
        GameSelection.SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0));
    private static bool MouseOrModifiersHeld() => new[] { 1, 2, 4, 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    private static bool ValidWindow(IntPtr window, int pid)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || IsIconic(window)) return false;
        GetWindowThreadProcessId(window, out var owner);
        return owner == (uint)pid;
    }
    private static async Task<Snapshot> ReadSnapshotAsync(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length is < 1 or > 4_000_000) throw Reject("snapshot_size_invalid");
                using var document = await JsonDocument.ParseAsync(file);
                var root = document.RootElement;
                var reason = SelectionVerification.ValidateSnapshot(root, DateTimeOffset.UtcNow);
                if (reason is not null) throw Reject(reason);
                return new(root.GetProperty("processId").GetInt32(), root.GetProperty("localPlayer").GetInt32(),
                    root.GetProperty("sessionIdentity").GetString()!, root.GetProperty("loadedMap").GetString()!,
                    root.GetProperty("loadedMapHash").GetString()!, root.GetProperty("timestamp").GetDateTimeOffset(), root.GetProperty("sequence").GetInt64());
            }
            catch (Exception error) when (error is IOException or JsonException or VerificationException) { last = error; }
            if (attempt < 5) await Task.Delay(50);
        }
        throw last ?? Reject("snapshot_unavailable");
    }
    private static bool SameSession(Snapshot a, Snapshot b) => a.ProcessId == b.ProcessId && a.LocalPlayer == b.LocalPlayer &&
        a.SessionIdentity == b.SessionIdentity && string.Equals(a.LoadedMap, b.LoadedMap, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.LoadedMapHash, b.LoadedMapHash, StringComparison.OrdinalIgnoreCase);
    private static bool MatchesRoots(Snapshot snapshot, Roots roots) => snapshot.LocalPlayer == roots.LocalPlayer &&
        snapshot.SessionIdentity == $"{roots.Ui:X8}:{roots.Players:X8}:{roots.Table:X8}";
    private static VerificationException Reject(string reason) => new(reason);
    private sealed class VerificationException(string reason) : Exception(reason);
    private sealed record Snapshot(int ProcessId, int LocalPlayer, string SessionIdentity, string LoadedMap, string LoadedMapHash, DateTimeOffset Timestamp, long Sequence);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
