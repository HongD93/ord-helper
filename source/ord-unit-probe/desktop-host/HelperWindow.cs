using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OrdDesktopHost;

internal sealed class HelperWindow : Form
{
    private const string StartUrl = "http://127.0.0.1:8765/";
    private const string SupportedGameVersion = "1.28.5.7680";
    private readonly string profilePath;
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill, AllowExternalDrop = false };
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(StartUrl), Timeout = TimeSpan.FromSeconds(2)
    };
    private readonly CancellationTokenSource closing = new();
    private int serverProcessId;
    private DateTime serverStartedUtc;
    private bool focusInProgress;
    private string? focusedRequestId;
    private IntPtr focusedGameWindow;
    private readonly string? verificationRecipe;
    private readonly string? verificationOutput;
    private bool verificationStarted;
    internal bool StartupSucceeded { get; private set; }

    public HelperWindow(string profilePath, string? verificationRecipe = null, string? verificationOutput = null)
    {
        this.profilePath = profilePath;
        this.verificationRecipe = verificationRecipe;
        this.verificationOutput = verificationOutput;
        Text = "원랜디 도우미";
        ClientSize = new Size(1400, 850);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        // 최초 표시가 다른 앱 뒤에 가려지지 않게 한다. 실제 게임 전환 전에 해제한다.
        TopMost = true;
        Activated += (_, _) => { if (GetForegroundWindow() == Handle) TopMost = false; };
        Controls.Add(browser);
        Shown += InitializeAsync;
        FormClosing += (_, _) => closing.Cancel();
        FormClosed += (_, _) => { client.Dispose(); closing.Dispose(); };
    }

    private async void InitializeAsync(object? sender, EventArgs args)
    {
        try
        {
            BringToFront();
            Activate();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            using var health = await ReadJsonAsync("api/health", budget.Token);
            serverProcessId = GetServerProcessId(health.RootElement);
            using (var server = Process.GetProcessById(serverProcessId))
            {
                if (server.HasExited) throw new InvalidOperationException("The local server has stopped.");
                serverStartedUtc = server.StartTime.ToUniversalTime();
            }
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
            await browser.EnsureCoreWebView2Async(environment);
            if (closing.IsCancellationRequested) return;
            var core = browser.CoreWebView2;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, navigation) => navigation.Cancel = !AllowedOrigin(navigation.Uri);
            core.FrameNavigationStarting += (_, navigation) => navigation.Cancel = !AllowedOrigin(navigation.Uri);
            core.NewWindowRequested += (_, request) => request.Handled = true;
            core.DownloadStarting += (_, download) => download.Cancel = true;
            core.PermissionRequested += (_, permission) => permission.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += OnWebMessageReceived;
            if (verificationRecipe is not null && verificationOutput is not null)
                core.NavigationCompleted += async (_, navigation) =>
                {
                    if (!navigation.IsSuccess || verificationStarted || closing.IsCancellationRequested) return;
                    verificationStarted = true;
                    await CraftVerification.RunAsync(core, verificationRecipe, verificationOutput, closing.Token);
                };
            core.Navigate(StartUrl);
            StartupSucceeded = true;
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowStartupError("WebView2 Runtime을 찾지 못했습니다. 설치 상태를 확인해 주세요.");
        }
        catch (Exception)
        {
            ShowStartupError("도우미 화면에 연결하지 못했습니다. 수집 프로그램이 실행 중인지 확인해 주세요.");
        }
    }

    private void ShowStartupError(string message)
    {
        if (IsDisposed || Disposing || closing.IsCancellationRequested) return;
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        Close();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!AllowedOrigin(args.Source) || !AllowedOrigin(browser.CoreWebView2.Source)) return;
        var json = args.WebMessageAsJson;
        if (json.Length > 4096) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => _ = HandleMessageAsync(json)));
            return;
        }
        _ = HandleMessageAsync(json);
    }

    private async Task HandleMessageAsync(string json)
    {
        string? requestId = null;
        var ownsPendingRequest = false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object) return;
            requestId = TextValue(request, "requestId");
            if (!Guid.TryParseExact(requestId, "D", out _)) return;
            if (TextValue(request, "type") == "craft-finished")
            {
                if (requestId != focusedRequestId) return;
                var previousGame = focusedGameWindow;
                focusedRequestId = null;
                focusedGameWindow = IntPtr.Zero;
                // 추적을 정리하고, 조합 완료 또는 현재 단계 확인 후 중지했을 때 도우미로 복귀한다.
                // 사용자가 다른 앱으로 전환했다면 창을 가져오지 않는다.
                if (TextValue(request, "outcome") is not ("succeeded" or "stopped") ||
                    previousGame == IntPtr.Zero || GetForegroundWindow() != previousGame) return;
                TopMost = true;
                BringToFront();
                Activate();
                return;
            }
            if (TextValue(request, "type") != "focus-game") return;
            if (!request.TryGetProperty("processId", out var processValue) || !processValue.TryGetInt32(out var processId) || processId <= 0)
            {
                Reply(requestId!, false, "게임 연결 정보를 확인하지 못했습니다.");
                return;
            }
            // 이 창에서 사용자가 조작한 요청만 정상적인 Windows 전면 전환을 시도한다.
            if (GetForegroundWindow() != Handle)
            {
                Reply(requestId!, false, "도우미 창을 선택한 뒤 다시 눌러 주세요.");
                return;
            }
            if (focusInProgress)
            {
                Reply(requestId!, false, "게임 창으로 전환 중입니다.");
                return;
            }
            focusInProgress = true;
            ownsPendingRequest = true;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            budget.CancelAfter(TimeSpan.FromMilliseconds(2200));
            var healthTask = ReadJsonAsync("api/health", budget.Token);
            var snapshotTask = ReadJsonAsync("api/snapshot", budget.Token);
            await Task.WhenAll(healthTask, snapshotTask);
            using var health = await healthTask;
            using var snapshot = await snapshotTask;
            if (GetServerProcessId(health.RootElement) != serverProcessId || !SameServerProcess())
            {
                Reply(requestId!, false, "도우미 연결이 바뀌었습니다. 창을 다시 실행해 주세요.");
                return;
            }
            if (!ValidGameSnapshot(snapshot.RootElement, processId))
            {
                Reply(requestId!, false, "게임의 현재 연결 상태를 확인하지 못했습니다.");
                return;
            }
            using var game = Process.GetProcessById(processId);
            if (game.HasExited || !GameProcessName(game.ProcessName))
            {
                Reply(requestId!, false, "연결된 워크래프트를 찾지 못했습니다.");
                return;
            }
            var module = game.Modules.Cast<ProcessModule>().SingleOrDefault(value =>
                value.ModuleName.Equals("Game.dll", StringComparison.OrdinalIgnoreCase));
            if (module?.FileVersionInfo.FileVersion != SupportedGameVersion)
            {
                Reply(requestId!, false, "지원하는 워크래프트 버전을 확인하지 못했습니다.");
                return;
            }
            var window = game.MainWindowHandle;
            if (window == IntPtr.Zero || !IsWindow(window) || IsIconic(window) || WindowProcess(window) != processId)
            {
                Reply(requestId!, false, "워크래프트 창을 연 뒤 다시 눌러 주세요.");
                return;
            }
            budget.Token.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != Handle || !SameServerProcess())
            {
                Reply(requestId!, false, "화면이 바뀌어 게임 창으로 전환하지 않았습니다.");
                return;
            }
            // 전면 잠금이나 스레드 연결을 변경하지 않고 한 번만 호출한다.
            // 전환 직후 커서가 화면 가장자리에 놓여 카메라가 이동하지 않게 한다.
            var releaseWatch = Stopwatch.StartNew();
            while (MouseOrModifiersHeld() && releaseWatch.ElapsedMilliseconds < 500)
                await Task.Delay(25, budget.Token);
            if (GetForegroundWindow() != Handle) { Reply(requestId!, false, "화면이 바뀌어 게임 창으로 전환하지 않았습니다."); return; }
            if (!ParkCursorBeforeFocus(window))
            {
                Reply(requestId!, false, "마우스 버튼을 놓은 뒤 다시 눌러 주세요.");
                return;
            }
            TopMost = false;
            SetForegroundWindow(window);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 500)
            {
                if (GetForegroundWindow() == window && IsWindow(window) && WindowProcess(window) == processId && !game.HasExited)
                {
                    await Task.Delay(120, closing.Token);
                    if (GetForegroundWindow() != window) break;
                    focusedRequestId = requestId;
                    focusedGameWindow = window;
                    Reply(requestId!, true, "");
                    return;
                }
                await Task.Delay(25, closing.Token);
            }
            Reply(requestId!, false, "게임 창으로 전환하지 못했습니다. 게임 창을 확인해 주세요.");
        }
        catch (OperationCanceledException)
        {
            if (requestId is not null) Reply(requestId, false, "게임 연결 확인이 지연되어 창을 전환하지 않았습니다.");
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or IOException or InvalidDataException or Win32Exception
            or InvalidOperationException or ArgumentException or UnauthorizedAccessException or OverflowException)
        {
            if (requestId is not null) Reply(requestId, false, "게임 창을 확인하지 못했습니다. 연결 상태를 확인해 주세요.");
        }
        finally { if (ownsPendingRequest) focusInProgress = false; }
    }

    private bool SameServerProcess()
    {
        using var server = Process.GetProcessById(serverProcessId);
        return !server.HasExited && server.StartTime.ToUniversalTime() == serverStartedUtc;
    }

    private void Reply(string requestId, bool ok, string message)
    {
        if (IsDisposed || Disposing || closing.IsCancellationRequested || browser.IsDisposed ||
            browser.CoreWebView2 is not { } core || !AllowedOrigin(core.Source)) return;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "game-focus-result", requestId, ok, message }));
    }

    private async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancel)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("The local server is unavailable.");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != "application/json") throw new InvalidDataException("Unexpected response type.");
        await using var source = await response.Content.ReadAsStreamAsync(cancel);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancel);
            if (count == 0) break;
            if (body.Length + count > 1_048_576) throw new InvalidDataException("Response too large.");
            body.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(body.ToArray());
    }

    private static int GetServerProcessId(JsonElement health)
    {
        if (TextValue(health, "application") != "ord-unit-probe" ||
            !health.TryGetProperty("processId", out var value) || !value.TryGetInt32(out var processId) || processId <= 0)
            throw new InvalidDataException("Unexpected local server.");
        return processId;
    }

    private static bool ValidGameSnapshot(JsonElement snapshot, int processId)
    {
        if (TextValue(snapshot, "status") != "snapshot_read" || TextValue(snapshot, "gameVersion") != SupportedGameVersion ||
            !snapshot.TryGetProperty("processId", out var processValue) || !processValue.TryGetInt32(out var observedProcessId) || observedProcessId != processId ||
            !TrueValue(snapshot, "stableRoots") || !TrueValue(snapshot, "stableUnits") || !TrueValue(snapshot, "catalogMatchesLoadedArchive") ||
            !snapshot.TryGetProperty("readErrors", out var errorValue) || !errorValue.TryGetInt32(out var errors) || errors != 0 ||
            !snapshot.TryGetProperty("timestamp", out var timeValue) || !timeValue.TryGetDateTimeOffset(out var timestamp)) return false;
        var age = DateTimeOffset.UtcNow - timestamp;
        return age >= TimeSpan.FromSeconds(-1) && age <= TimeSpan.FromSeconds(3);
    }

    private static bool AllowedOrigin(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == 8765 &&
        uri.Authority == "127.0.0.1:8765" && string.IsNullOrEmpty(uri.UserInfo);
    private static string? TextValue(JsonElement root, string name) => root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TrueValue(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool GameProcessName(string name) => name.Equals("War3", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Warcraft III", StringComparison.OrdinalIgnoreCase);
    private static uint WindowProcess(IntPtr window) { GetWindowThreadProcessId(window, out var processId); return processId; }
    private static bool ParkCursorBeforeFocus(IntPtr window)
    {
        if (MouseOrModifiersHeld() ||
            !GetClientRect(window, out var rect) || rect.Right < 320 || rect.Bottom < 240) return false;
        var point = new CursorPoint { X = rect.Right / 2, Y = rect.Bottom / 2 };
        return ClientToScreen(window, ref point) && SetCursorPos(point.X, point.Y);
    }
    private static bool MouseOrModifiersHeld() => new[] { 1, 2, 4, 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct ClientRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ClientRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
