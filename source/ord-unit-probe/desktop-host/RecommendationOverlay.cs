using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OrdDesktopHost;

internal sealed class RecommendationOverlay : Form
{
    private const int ToggleHotkey = 1;
    private const uint ToggleModifiers = 0x0002 | 0x0004 | 0x4000; // Ctrl, Shift, no repeat
    private const int WmHotkey = 0x0312;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 150 };
    private readonly Font titleFont = new("Malgun Gothic", 13, FontStyle.Bold);
    private readonly Font bodyFont = new("Malgun Gothic", 9);
    private string title = "";
    private string detail = "";
    private string shortcut = "";
    private double? percent;
    private string? action;
    private string? actionId;
    private string? lastClickedActionId;
    private DateTimeOffset actionClickedAt;
    private (string Action, string Id, int ProcessId)? pressedAction;
    private int processId;
    private DateTimeOffset observedAt;
    private IntPtr trackedWindow;
    private bool hotkeyRegistered;
    private bool displayEnabled = true;
    public event Action<string, string, int>? ActionRequested;

    public RecommendationOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Opacity = 0.94;
        BackColor = Color.FromArgb(25, 31, 47);
        Text = "원랜디 추천";
        DoubleBuffered = true;
        timer.Tick += (_, _) => RefreshPosition();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= wsExToolWindow | wsExNoActivate;
            return parameters;
        }
    }

    public void UpdateRecommendation(int pid, DateTimeOffset timestamp, string heading, string explanation,
        double? progress, string? nextAction, string? nextActionId)
    {
        if (!Fresh(timestamp) || pid <= 0 || heading.Length is < 1 or > 120 || explanation.Length > 160 ||
            heading.Any(char.IsControl) || explanation.Any(char.IsControl) ||
            progress is not null && (!double.IsFinite(progress.Value) || progress < 0 || progress > 100) ||
            nextAction is not null && nextAction is not ("craft" or "stop") ||
            nextAction is not null && (nextActionId is null || nextActionId.Length is < 1 or > 64 || nextActionId.Any(char.IsControl)))
        {
            Clear();
            return;
        }
        if (pid == processId && timestamp < observedAt) return;
        if (pid != processId) trackedWindow = IntPtr.Zero;
        processId = pid;
        observedAt = timestamp;
        title = heading;
        detail = explanation;
        percent = progress;
        action = nextAction;
        actionId = nextAction is null ? null : nextActionId;
        Invalidate();
        timer.Start();
        RefreshPosition();
    }

    public void Clear()
    {
        processId = 0;
        observedAt = default;
        action = actionId = lastClickedActionId = null;
        timer.Stop();
        StopTrackingWindow();
    }

    private void RefreshPosition()
    {
        if (!Fresh(observedAt))
        {
            Clear();
            return;
        }
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out var windowPid);
        if (window == IntPtr.Zero || windowPid != processId || IsIconic(window) ||
            !GetClientRect(window, out var client) || !GetGameBounds(window, client, out var bounds))
        {
            StopTrackingWindow();
            return;
        }
        if (window != trackedWindow)
        {
            StopTrackingWindow();
            if (!IsMatchingGameWindow(window)) return;
            trackedWindow = window;
        }
        if (!hotkeyRegistered)
        {
            hotkeyRegistered = RegisterHotKey(Handle, ToggleHotkey, ToggleModifiers, (uint)Keys.F10);
            shortcut = hotkeyRegistered ? "Ctrl+Shift+F10 · 표시 전환" : "";
            Invalidate();
        }
        if (!displayEnabled) return;
        if (Bounds != bounds) Bounds = bounds;
        if (!Visible) Show();
    }

    private bool IsMatchingGameWindow(IntPtr window)
    {
        try
        {
            using var game = Process.GetProcessById(processId);
            return HelperWindow.GameProcessName(game.ProcessName) && game.MainWindowHandle == window &&
                game.StartTime.ToUniversalTime() <= observedAt.UtcDateTime;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool GetGameBounds(IntPtr window, NativeRect client, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) return false;
        var game = Rectangle.Intersect(new Rectangle(origin.X, origin.Y, client.Right, client.Bottom),
            Screen.FromHandle(window).WorkingArea);
        var scale = Math.Max(96u, GetDpiForWindow(window)) / 96d;
        var width = (int)(350 * scale);
        var height = (int)(144 * scale);
        var inset = (int)(16 * scale);
        if (game.Width < width + 2 * inset || game.Height < height + 4 * inset) return false;
        bounds = new Rectangle(game.Right - width - inset, game.Top + 3 * inset, width, height);
        return true;
    }

    private void StopTrackingWindow()
    {
        pressedAction = null;
        if (IsHandleCreated && Capture) Capture = false;
        trackedWindow = IntPtr.Zero;
        if (hotkeyRegistered)
        {
            UnregisterHotKey(Handle, ToggleHotkey);
            hotkeyRegistered = false;
        }
        if (Visible) Hide();
    }

    protected override void WndProc(ref Message message)
    {
        const int wmNcHitTest = 0x0084;
        const int wmMouseActivate = 0x0021;
        if (message.Msg == wmMouseActivate)
        {
            message.Result = (IntPtr)3; // MA_NOACTIVATE
            return;
        }
        if (message.Msg == wmNcHitTest)
        {
            var coordinates = unchecked((long)message.LParam);
            var point = PointToClient(new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff)));
            message.Result = (IntPtr)(ActionBounds.Contains(point) ? 1 : -1); // HTCLIENT / HTTRANSPARENT
            return;
        }
        if (message.Msg == WmHotkey && message.WParam == ToggleHotkey)
        {
            if (!hotkeyRegistered || GetForegroundWindow() != trackedWindow) return;
            displayEnabled = !displayEnabled;
            if (displayEnabled) RefreshPosition();
            else Hide();
            return;
        }
        base.WndProc(ref message);
    }

    public bool IsGameForeground(int pid) => pid == processId && trackedWindow != IntPtr.Zero &&
        Fresh(observedAt) && GetForegroundWindow() == trackedWindow && WindowProcess(trackedWindow) == processId;

    private static uint WindowProcess(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        return pid;
    }

    private Rectangle Scaled(int x, int y, int width, int height)
    {
        var scale = Math.Max(96, DeviceDpi) / 96f;
        return new Rectangle((int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale));
    }

    private Rectangle ActionBounds => Scaled(222, 83, 116, 31);
    private bool ActionEnabled => action is not null && actionId is not null && IsGameForeground(processId) &&
        (actionId != lastClickedActionId || DateTimeOffset.UtcNow - actionClickedAt > TimeSpan.FromSeconds(3));

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(args.Graphics, title, titleFont, Scaled(12, 5, 326, 31), Color.White, flags);
        TextRenderer.DrawText(args.Graphics, detail, bodyFont, Scaled(12, 37, 326, 35), Color.FromArgb(220, 225, 241), flags);
        TextRenderer.DrawText(args.Graphics, percent.HasValue ? "재료 " + Math.Round(percent.Value) + "%" : "",
            bodyFont, Scaled(12, 83, 198, 31), Color.White, flags);
        using var background = new SolidBrush(ActionEnabled ? Color.FromArgb(65, 105, 175) : Color.FromArgb(53, 61, 77));
        args.Graphics.FillRectangle(background, ActionBounds);
        TextRenderer.DrawText(args.Graphics, action == "stop" ? "조합 중지" : "자동 조합", bodyFont,
            ActionBounds, ActionEnabled ? Color.White : Color.FromArgb(157, 165, 181), flags | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(args.Graphics, shortcut, bodyFont, Scaled(12, 117, 326, 22), Color.FromArgb(187, 199, 225), flags);
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left || !ActionBounds.Contains(args.Location) || !ActionEnabled) return;
        pressedAction = (action!, actionId!, processId);
        Capture = true;
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        var pressed = pressedAction;
        pressedAction = null;
        if (Capture) Capture = false;
        if (args.Button != MouseButtons.Left || !ActionBounds.Contains(args.Location) || !ActionEnabled ||
            pressed is null || pressed.Value != (action!, actionId!, processId)) return;
        var requestedAction = action!;
        var requestedId = actionId!;
        lastClickedActionId = requestedId;
        actionClickedAt = DateTimeOffset.UtcNow;
        Invalidate();
        ActionRequested?.Invoke(requestedAction, requestedId, processId);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            Clear();
            timer.Dispose();
            base.Dispose(disposing);
            titleFont.Dispose();
            bodyFont.Dispose();
            return;
        }
        base.Dispose(disposing);
    }

    private static bool Fresh(DateTimeOffset timestamp) =>
        DateTimeOffset.UtcNow - timestamp is { TotalSeconds: >= -1 and <= 3 };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
