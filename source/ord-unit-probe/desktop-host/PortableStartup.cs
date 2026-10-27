using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OrdDesktopHost;

internal sealed record PortableLayout(string Root, string Server, string Catalog, string Logs)
{
    internal static PortableLayout? Find(string directory)
    {
        var root = Path.GetFullPath(directory);
        var manifest = Path.Combine(root, "portable-layout.json");
        if (!File.Exists(manifest)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        var value = json.RootElement;
        if (!value.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var number) || number != 1)
            throw new PortableStartupException("도우미 실행 설정을 읽지 못했습니다. 프로그램 폴더를 확인해 주세요.");
        ValidateRelative(value, "serverRelativePath", "app/server/OrdUnitProbe.exe");
        ValidateRelative(value, "catalogRelativePath", "data/unit_catalog_2.323C.json");
        return new(root, Path.Combine(root, "app", "server", "OrdUnitProbe.exe"),
            Path.Combine(root, "data", "unit_catalog_2.323C.json"), Path.Combine(root, "logs"));
    }

    private static void ValidateRelative(JsonElement manifest, string name, string expected)
    {
        if (manifest.TryGetProperty(name, out var item) && (item.ValueKind != JsonValueKind.String ||
            item.GetString()?.Replace('\\', '/') != expected))
            throw new PortableStartupException("도우미 실행 설정을 읽지 못했습니다. 프로그램 폴더를 확인해 주세요.");
    }

    internal void ValidateFiles()
    {
        if (!File.Exists(Server) || !File.Exists(Catalog) || !File.Exists(Path.Combine(Path.GetDirectoryName(Server)!, "wwwroot", "index.html")))
            throw new PortableStartupException("도우미 실행 파일이나 데이터가 없습니다. 프로그램 폴더를 확인해 주세요.");
        Directory.CreateDirectory(Logs);
    }

    internal IReadOnlyList<string> Arguments => ["--serve", "--port", "8765", "--interval-ms", "250", "--catalog", Catalog,
        "--output", Path.Combine(Logs, "live-counter.json")];
}

internal sealed class PortableStartupException(string message) : Exception(message);
internal enum ProbeKind { NoListener, Owned, Foreign, Unavailable }
internal sealed record ServerProbe(ProbeKind Kind, int ProcessId = 0);
internal interface IStartupPlatform
{
    DateTime UtcNow { get; }
    Task<ServerProbe> ProbeAsync(PortableLayout layout);
    PortableServerHandle Start(PortableLayout layout);
    Task DelayAsync();
    void RecordIdentity(PortableLayout layout, int processId);
}

internal abstract class PortableServerHandle : IDisposable
{
    internal abstract int ProcessId { get; }
    internal abstract bool HasExited { get; }
    internal abstract void Stop();
    public abstract void Dispose();
}

internal static class PortableStartup
{
    internal static async Task<PortableServerHandle?> EnsureServerAsync(PortableLayout layout, IStartupPlatform platform,
        TimeSpan? startupTimeout = null)
    {
        layout.ValidateFiles();
        var probe = await platform.ProbeAsync(layout).ConfigureAwait(false);
        if (probe.Kind == ProbeKind.Owned) { platform.RecordIdentity(layout, probe.ProcessId); return null; }
        if (probe.Kind != ProbeKind.NoListener) throw ProbeFailure(probe.Kind);
        var started = platform.Start(layout);
        try
        {
            var deadline = platform.UtcNow + (startupTimeout ?? TimeSpan.FromSeconds(15));
            while (platform.UtcNow < deadline)
            {
                if (started.HasExited) throw new PortableStartupException("수집 프로그램이 시작 중 종료됐습니다. logs 폴더의 실행 기록을 확인해 주세요.");
                probe = await platform.ProbeAsync(layout).ConfigureAwait(false);
                if (probe.Kind == ProbeKind.Owned && probe.ProcessId == started.ProcessId)
                {
                    platform.RecordIdentity(layout, started.ProcessId);
                    return started;
                }
                if (probe.Kind != ProbeKind.NoListener) throw ProbeFailure(probe.Kind);
                await platform.DelayAsync().ConfigureAwait(false);
            }
            throw new PortableStartupException("수집 프로그램에 연결하지 못했습니다. 도우미를 다시 실행해 주세요.");
        }
        catch { started.Stop(); started.Dispose(); throw; }
    }

    private static PortableStartupException ProbeFailure(ProbeKind kind) => new(kind == ProbeKind.Foreign
        ? "도우미 연결 주소를 다른 프로그램이 사용 중입니다. 기존 프로그램을 종료한 뒤 다시 실행해 주세요."
        : "수집 프로그램 연결 상태를 확인하지 못했습니다. logs 폴더의 실행 기록을 확인해 주세요.");

    internal static void WriteStartupFailure(string root, Exception error)
    {
        try
        {
            var logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "desktop-startup.log"), DateTimeOffset.Now.ToString("o") + " " + error + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed class WindowsStartupPlatform(TimeSpan? probeTimeout = null, Action<Exception>? diagnostic = null) : IStartupPlatform
{
    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(8);
    public DateTime UtcNow => DateTime.UtcNow;
    public async Task<ServerProbe> ProbeAsync(PortableLayout layout)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = probeTimeout ?? DefaultProbeTimeout };
        try
        {
            using var response = await client.GetAsync("http://127.0.0.1:8765/api/health").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536) return new(ProbeKind.Foreign);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var value = json.RootElement;
            if (!value.TryGetProperty("application", out var app) || app.GetString() != "ord-unit-probe" ||
                !value.TryGetProperty("processId", out var id) || !id.TryGetInt32(out var processId) || processId <= 0)
                return new(ProbeKind.Foreign);
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && SamePath(process.MainModule?.FileName, layout.Server) ? new(ProbeKind.Owned, processId) : new(ProbeKind.Foreign);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or
            InvalidOperationException or ArgumentException or Win32Exception)
        {
            diagnostic?.Invoke(error);
            var kind = ClassifyProbeFailure(error);
            if (kind != ProbeKind.NoListener)
                PortableStartup.WriteStartupFailure(layout.Root, new InvalidOperationException("Local health probe was inconclusive; no process was started or stopped.", error));
            return new(kind);
        }
    }

    internal static bool SamePath(string? actual, string expected) => actual is not null &&
        string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    internal static bool IsConnectionRefused(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused) return true;
        return false;
    }
    internal static ProbeKind ClassifyProbeFailure(Exception error) => IsConnectionRefused(error) ? ProbeKind.NoListener :
        error is JsonException or InvalidOperationException ? ProbeKind.Foreign : ProbeKind.Unavailable;
    public PortableServerHandle Start(PortableLayout layout) => NativeServerHandle.Start(layout);
    public Task DelayAsync() => Task.Delay(150);
    public void RecordIdentity(PortableLayout layout, int processId)
    {
        using var process = Process.GetProcessById(processId);
        File.WriteAllText(Path.Combine(layout.Logs, "counter-process.json"), JsonSerializer.Serialize(new
        {
            processId, startTimeUtc = process.StartTime.ToUniversalTime().ToString("o"), url = "http://127.0.0.1:8765",
            serverPath = layout.Server
        }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }
}

internal static class PortableProbeDiagnostics
{
    internal static async Task<int> RunAsync(string root, string output)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(output)) return 2;
        var layout = PortableLayout.Find(root) ?? throw new ArgumentException("Missing package manifest.");
        var results = new List<object>();
        foreach (var timeout in new[] { 2, 8, 10 })
        {
            Exception? error = null;
            var stopwatch = Stopwatch.StartNew();
            var probe = await new WindowsStartupPlatform(timeout == 8 ? null : TimeSpan.FromSeconds(timeout), value => error = value).ProbeAsync(layout);
            var chain = new List<object>();
            for (var current = error; current is not null; current = current.InnerException)
                chain.Add(new { type = current.GetType().FullName, current.Message,
                    socketError = current is SocketException socket ? socket.SocketErrorCode.ToString() : null,
                    nativeErrorCode = current is SocketException native ? native.NativeErrorCode : (int?)null });
            results.Add(new { timeoutSeconds = timeout, elapsedMilliseconds = stopwatch.ElapsedMilliseconds, kind = probe.Kind.ToString(), probe.ProcessId, chain });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { results, scope = "Read-only localhost health diagnostics; no server, UI or game input started." }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return 0;
    }
}

// 자식의 stdout/stderr는 상속한 파일 핸들에 직접 연결한다. 도우미 창이 종료돼도 수집기의 로그 파이프가 끊기지 않는다.
internal sealed class NativeServerHandle(IntPtr process, int processId) : PortableServerHandle
{
    private IntPtr handle = process;
    internal override int ProcessId => processId;
    internal override bool HasExited => handle == IntPtr.Zero || !GetExitCodeProcess(handle, out var code) || code != 259;
    internal override void Stop()
    {
        if (handle != IntPtr.Zero && !HasExited) { _ = TerminateProcess(handle, 1); _ = WaitForSingleObject(handle, 3000); }
    }
    public override void Dispose() { if (handle != IntPtr.Zero) { _ = CloseHandle(handle); handle = IntPtr.Zero; } }

    internal static NativeServerHandle Start(PortableLayout layout)
    {
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        using var stdout = CreateFile(Path.Combine(layout.Logs, "counter-server.log"), 4, 3, ref attributes, 4, 128, IntPtr.Zero);
        using var stderr = CreateFile(Path.Combine(layout.Logs, "counter-server-error.log"), 4, 3, ref attributes, 4, 128, IntPtr.Zero);
        using var stdin = CreateFile("NUL", 0x80000000, 3, ref attributes, 3, 128, IntPtr.Zero);
        if (stdout.IsInvalid || stderr.IsInvalid || stdin.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100 | 1, ShowWindow = 0,
            StdInput = stdin.DangerousGetHandle(), StdOutput = stdout.DangerousGetHandle(), StdError = stderr.DangerousGetHandle()
        };
        var command = new StringBuilder(string.Join(" ", new[] { layout.Server }.Concat(layout.Arguments).Select(QuoteArgument)));
        if (!CreateProcess(layout.Server, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000, IntPtr.Zero,
            Path.GetDirectoryName(layout.Server)!, ref startup, out var information)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _ = CloseHandle(information.Thread);
        return new NativeServerHandle(information.Process, checked((int)information.ProcessId));
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1).Append(c); slashes = 0; continue; }
            result.Append('\\', slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public uint X, Y, Width, Height, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, Reserved2;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class PackageInstance : IDisposable
{
    private readonly Mutex mutex;
    internal bool IsOwner { get; }
    private PackageInstance(Mutex mutex, bool owner) { this.mutex = mutex; IsOwner = owner; }
    internal static PackageInstance Acquire(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        var mutex = new Mutex(false, "Local\\OrdHelperDesktop." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
        bool owner; try { owner = mutex.WaitOne(0); } catch (AbandonedMutexException) { owner = true; }
        return new(mutex, owner);
    }
    public void Dispose() { if (IsOwner) mutex.ReleaseMutex(); mutex.Dispose(); }
    internal static void RestoreExisting()
    {
        var expected = Environment.ProcessPath; if (expected is null) return;
        var deadline = DateTime.UtcNow.AddSeconds(4);
        do
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId || !WindowsStartupPlatform.SamePath(process.MainModule?.FileName, expected)) continue;
                        process.Refresh(); var window = process.MainWindowHandle; if (window == IntPtr.Zero) continue;
                        _ = ShowWindowAsync(window, 9); _ = SetForegroundWindow(window); return;
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { }
                }
            }
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
}
