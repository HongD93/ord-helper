using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// 내부 선택 검증 전용이다. 조합·채팅·선택 위습 실행기를 호출하지 않는다.
static class SelectionVerification
{
    private const string SupportedVersion = "1.28.5.7680";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string rawcode, string snapshotPath, string outputPath, bool tryNormalFocus = false, bool forceSelection = false)
    {
        var report = new Dictionary<string, object?>
        {
            ["startedUtc"] = DateTimeOffset.UtcNow,
            ["purpose"] = "selection_only",
            ["requestedRawcode"] = rawcode,
            ["selectionPolicy"] = forceSelection ? "forced_selection_diagnostic" : "normal_portrait_first",
            ["craftInputInvoked"] = false,
            ["status"] = "failed"
        };
        FileStream? output = null;
        try
        {
            if (rawcode is not { Length: 4 } || rawcode.Any(c => c is < ' ' or > '~'))
                throw Reject("invalid_rawcode");
            var inputFile = Path.GetFullPath(snapshotPath);
            var outputFile = Path.GetFullPath(outputPath);
            // 먼저 파일을 선점한다. 기존 결과를 덮어쓰거나 검증 후 생성 경쟁을 일으키지 않는다.
            output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            report["snapshotPath"] = inputFile;
            report["outputPath"] = outputFile;
            if (!OperatingSystem.IsWindows()) throw Reject("unsupported_platform");
            var first = await ReadSnapshotAsync(inputFile);
            using var process = Process.GetProcessById(first.ProcessId);
            var window = process.MainWindowHandle;
            if (!ValidWindow(window, first.ProcessId)) throw Reject("game_window_unavailable");

            // 명시한 검증 실행에서는 표준 전면 전환을 한 번만 시도할 수 있다. 잠금·스레드 연결을 변경하지 않는다.
            if (tryNormalFocus && GetForegroundWindow() != window)
            {
                var focusModule = process.Modules.Cast<ProcessModule>().SingleOrDefault(module => module.ModuleName.Equals("Game.dll", StringComparison.OrdinalIgnoreCase));
                if (focusModule?.FileVersionInfo.FileVersion != SupportedVersion) throw Reject("unsupported_game_version");
                report["normalFocusAttempted"] = true;
                SetForegroundWindow(window);
                for (var attempt = 0; attempt < 10 && GetForegroundWindow() != window; attempt++) await Task.Delay(50);
                if (GetForegroundWindow() != window) throw Reject("normal_foreground_denied");
            }
            // 기본 실행은 사용자가 직접 게임을 활성화할 때까지 기다린다.
            var watch = Stopwatch.StartNew();
            while (GetForegroundWindow() != window)
            {
                if (watch.Elapsed >= TimeSpan.FromSeconds(120)) throw Reject("foreground_timeout");
                if (!ValidWindow(window, first.ProcessId) || process.HasExited) throw Reject("game_window_changed");
                await Task.Delay(50);
            }
            report["foregroundWaitMs"] = watch.Elapsed.TotalMilliseconds;
            // 창 활성화에 사용한 클릭과 손 움직임이 끝난 뒤 선택 검증을 시작한다.
            await Task.Delay(500);
            if (GetForegroundWindow() != window) throw Reject("foreground_changed");
            var snapshot = await ReadSnapshotAsync(inputFile);
            if (!SameSnapshotSession(first, snapshot)) throw Reject("snapshot_session_changed");
            report["processId"] = snapshot.ProcessId;
            report["sessionIdentity"] = snapshot.SessionIdentity;
            report["loadedMap"] = snapshot.LoadedMap;
            report["loadedMapHash"] = snapshot.LoadedMapHash;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var modules = process.Modules.Cast<ProcessModule>().ToArray();
            var game = modules.SingleOrDefault(m => m.ModuleName.Equals("Game.dll", StringComparison.OrdinalIgnoreCase));
            var storm = modules.SingleOrDefault(m => m.ModuleName.Equals("Storm.dll", StringComparison.OrdinalIgnoreCase));
            if (game?.FileVersionInfo.FileVersion != SupportedVersion || storm is null) throw Reject("unsupported_game_version");
            var moduleBase = checked((uint)game.BaseAddress.ToInt64());
            using var memory = new MemoryReader(process.Id, 0x0410);
            var loadedMap = LoadedMapReader.Read(memory, checked((uint)storm.BaseAddress.ToInt64()));
            if (!string.Equals(loadedMap, snapshot.LoadedMap, StringComparison.OrdinalIgnoreCase)) throw Reject("loaded_map_changed");
            using (var map = File.OpenRead(snapshot.LoadedMap))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(map, cancel.Token));
                if (!string.Equals(hash, snapshot.LoadedMapHash, StringComparison.OrdinalIgnoreCase)) throw Reject("loaded_map_hash_changed");
            }
            var roots = memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0);
            if (roots is null || !MatchesRoots(snapshot, roots)) throw Reject("game_session_changed");
            var owned = memory.ReadOwnedUnits(roots);
            var middle = memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0);
            var second = SnapshotStability.SameSession(middle, roots) ? memory.ReadOwnedUnits(middle!) : null;
            if (!SnapshotStability.SameInventory(owned, second, new HashSet<string>([rawcode], StringComparer.Ordinal)) ||
                !GameSelection.SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)))
                throw Reject("owned_units_unstable");
            var active = second!.Units.Where(UnitRules.IsActive).ToArray();
            report["activeTargetCount"] = active.Count(u => u.Rawcode == rawcode);
            if (!active.Any(u => u.Rawcode == rawcode)) throw Reject("target_not_owned");

            // 해시 계산·메모리 조회가 길어졌어도 실행 직전 스냅샷은 최신이어야 한다.
            var ready = await ReadSnapshotAsync(inputFile);
            if (!SameSnapshotSession(snapshot, ready) || !MatchesRoots(ready, roots)) throw Reject("snapshot_session_changed");
            report["snapshotTimestamp"] = ready.Timestamp;
            report["snapshotSequence"] = ready.Sequence;
            if (GetForegroundWindow() != window || !ValidWindow(window, process.Id)) throw Reject("foreground_changed");
            var command = new CraftCommand("TEST", rawcode, 1, [], null, null, [rawcode]);
            report["selectionBefore"] = GameSelection.ReadSelectedAddresses(memory, roots).Select(address => $"0x{address:X8}").ToArray();
            var selection = await GameSelection.EnsureCasterAsync(memory, moduleBase, roots, window,
                command, active, cancel.Token, forceSelection: forceSelection);
            var address = GameSelection.ReadSingleSelectedAddress(memory, roots);
            var actualRawcode = address is null ? null : MemoryReader.DecodeRawcode(memory.Read(address.Value + 0x30, 4));
            report["selectionAttemptAddress"] = selection.Address is null ? null : $"0x{selection.Address.Value:X8}";
            report["actualSelectedAddress"] = address is null ? null : $"0x{address.Value:X8}";
            report["actualSelectedRawcode"] = actualRawcode;
            report["automaticallySelected"] = selection.AutomaticallySelected;
            report["selectionError"] = selection.Error;
            if (selection.Error is not null) throw Reject("selection_failed");
            if (GetForegroundWindow() != window || !GameSelection.SameSession(roots,
                memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0))) throw Reject("game_state_changed_after_selection");
            if (selection.Address != address || actualRawcode != rawcode || address is null ||
                !GameSelection.CurrentOwnedUnit(memory, roots, address.Value, rawcode)) throw Reject("selected_unit_mismatch");
            report["status"] = "verified";
        }
        catch (VerificationException error) { report["reason"] = error.Message; }
        catch (OperationCanceledException) { report["reason"] = "selection_timeout"; }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or
            ArgumentException or OverflowException or UnauthorizedAccessException or JsonException)
        {
            report["reason"] = output is null && File.Exists(outputPath) ? "output_already_exists" : "verification_read_failed";
            report["error"] = error.Message;
        }
        report["finishedUtc"] = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (output is not null)
        {
            try
            {
                await output.WriteAsync(Encoding.UTF8.GetBytes(json + Environment.NewLine));
                await output.FlushAsync();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Selection verification output failed: {error.Message}");
                return 1;
            }
            finally { await output.DisposeAsync(); }
        }
        Console.WriteLine(json);
        return Equals(report["status"], "verified") ? 0 : 1;
    }

    // 수집기가 파일을 덮어쓰는 동안의 짧은 읽기 실패만 제한적으로 재시도한다.
    private static async Task<Snapshot> ReadSnapshotAsync(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length is < 1 or > 4_000_000) throw Reject("snapshot_size_invalid");
                using var json = await JsonDocument.ParseAsync(file);
                var root = json.RootElement;
                var reason = ValidateSnapshot(root, DateTimeOffset.UtcNow);
                if (reason is not null) throw Reject(reason);
                return new(root.GetProperty("processId").GetInt32(), root.GetProperty("timestamp").GetDateTimeOffset(),
                    root.GetProperty("sequence").GetInt64(), root.GetProperty("localPlayer").GetInt32(),
                    root.GetProperty("sessionIdentity").GetString()!, root.GetProperty("loadedMap").GetString()!,
                    root.GetProperty("loadedMapHash").GetString()!);
            }
            catch (Exception error) when (error is IOException or JsonException or VerificationException) { last = error; }
            if (attempt < 5) await Task.Delay(50);
        }
        throw last ?? Reject("snapshot_unavailable");
    }

    internal static string? ValidateSnapshot(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || !Text(root, "status", out var status) || status != "snapshot_read" ||
            !Flag(root, "stableRoots") || !Flag(root, "stableUnits") || !Flag(root, "catalogMatchesLoadedArchive") ||
            !Integer(root, "readErrors", out var errors) || errors != 0) return "snapshot_unstable";
        if (!root.TryGetProperty("timestamp", out var stamp) || stamp.ValueKind != JsonValueKind.String || !stamp.TryGetDateTimeOffset(out var timestamp) ||
            now - timestamp > TimeSpan.FromSeconds(3) || timestamp - now > TimeSpan.FromSeconds(1)) return "snapshot_expired";
        if (!Text(root, "gameVersion", out var version) || version != SupportedVersion) return "unsupported_game_version";
        if (!Integer(root, "processId", out var pid) || pid <= 0 || !Integer(root, "localPlayer", out var player) || player is < 0 or > 31 ||
            !root.TryGetProperty("sequence", out var seq) || seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out var sequence) || sequence < 0) return "snapshot_identity_invalid";
        if (!Text(root, "loadedMap", out var map) || !Path.IsPathFullyQualified(map) ||
            !Text(root, "loadedMapHash", out var hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return "snapshot_map_invalid";
        if (!Text(root, "sessionIdentity", out var session) || session.Length != 26 || session[8] != ':' || session[17] != ':' ||
            session.Where((_, index) => index is not (8 or 17)).Any(c => !Uri.IsHexDigit(c))) return "snapshot_identity_invalid";
        return null;
    }

    private static bool Text(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString() ?? "";
        return value.Length != 0;
    }
    private static bool Integer(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out value);
    }
    private static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static bool SameSnapshotSession(Snapshot a, Snapshot b) => a.ProcessId == b.ProcessId && a.LocalPlayer == b.LocalPlayer &&
        a.SessionIdentity == b.SessionIdentity && string.Equals(a.LoadedMap, b.LoadedMap, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.LoadedMapHash, b.LoadedMapHash, StringComparison.OrdinalIgnoreCase);
    private static bool MatchesRoots(Snapshot snapshot, Roots roots) => snapshot.LocalPlayer == roots.LocalPlayer &&
        snapshot.SessionIdentity == $"{roots.Ui:X8}:{roots.Players:X8}:{roots.Table:X8}";
    private static bool ValidWindow(IntPtr window, int processId)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || IsIconic(window)) return false;
        GetWindowThreadProcessId(window, out var owner);
        return owner == (uint)processId;
    }
    private static VerificationException Reject(string reason) => new(reason);
    private sealed class VerificationException(string reason) : Exception(reason);
    private sealed record Snapshot(int ProcessId, DateTimeOffset Timestamp, long Sequence, int LocalPlayer,
        string SessionIdentity, string LoadedMap, string LoadedMapHash);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
