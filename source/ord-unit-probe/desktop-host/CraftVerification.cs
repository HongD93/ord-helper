using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace OrdDesktopHost;

// 명시적으로 실행한 일회성 검사만 기존 조합 컨트롤러를 호출한다. 포커스 정책은 변경하지 않는다.
internal static class CraftVerification
{
    public static async Task RunAsync(CoreWebView2 core, string recipeId, string outputPath, CancellationToken stop)
    {
        if (recipeId.Length != 4 || recipeId.Any(c => c < 0x20 || c > 0x7E) || !Path.IsPathFullyQualified(outputPath))
            throw new ArgumentException("A valid recipe ID and absolute output path are required.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var report = new Dictionary<string, object?> { ["recipeId"] = recipeId, ["startedAt"] = DateTimeOffset.UtcNow,
            ["status"] = "blocked", ["invocationAttempted"] = false, ["invocationCount"] = 0 };
        var attempted = false;
        try
        {
            var readyWatch = Stopwatch.StartNew();
            JsonElement state = default;
            while (readyWatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                stop.ThrowIfCancellationRequested();
                state = await ReadAsync(core, Script(recipeId, false), stop);
                report["lastState"] = state;
                if (True(state, "ready")) break;
                if (True(state, "fatal")) return;
                await Task.Delay(100, stop);
            }
            if (!True(state, "ready")) return;
            report["before"] = state;
            using var host = Process.GetCurrentProcess();
            var focusWatch = Stopwatch.StartNew();
            while (host.MainWindowHandle != IntPtr.Zero && (GetForegroundWindow() != host.MainWindowHandle || InputHeld()) && focusWatch.Elapsed < TimeSpan.FromSeconds(120))
            {
                await Task.Delay(100, stop);
                host.Refresh();
            }
            if (host.MainWindowHandle == IntPtr.Zero || GetForegroundWindow() != host.MainWindowHandle || InputHeld())
            {
                report["reason"] = "The helper is not the foreground window; no invocation was made.";
                return;
            }
            state = await ReadAsync(core, Script(recipeId, false), stop);
            report["before"] = state;
            if (!True(state, "ready")) { report["reason"] = "The application state changed before invocation."; return; }
            // 결과 수신 실패도 재호출하지 않는다. 실제 요청이 시작됐을 가능성을 보존한다.
            attempted = true;
            report["invocationAttempted"] = true;
            var invocation = await ReadAsync(core, Script(recipeId, true), stop);
            report["invocation"] = invocation;
            if (!True(invocation, "started")) return;
            report["invocationCount"] = 1;
            report["status"] = "observing";
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(100, stop);
                state = await ReadAsync(core, Script(recipeId, false), stop);
                report["lastState"] = state;
                if (state.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.False)
                {
                    report["status"] = Text(state, "tone") == "success" ? "succeeded" : "failed";
                    return;
                }
                if (Text(state, "tone") == "error") { report["status"] = "unknown"; return; }
            }
            report["status"] = "unknown";
            report["reason"] = "The application did not reach a terminal state within 30 seconds.";
        }
        catch (OperationCanceledException)
        {
            report["status"] = attempted ? "unknown" : "canceled";
            report["reason"] = "Verification canceled; no automatic retry.";
        }
        catch (Exception error)
        {
            report["status"] = attempted ? "unknown" : "blocked";
            report["reason"] = error.GetType().Name;
        }
        finally
        {
            report["finishedAt"] = DateTimeOffset.UtcNow;
            if (!stop.IsCancellationRequested)
            {
                try
                {
                    if (!Allowed(core.Source)) throw new InvalidOperationException("Unexpected preview origin.");
                    await using var png = File.Create(outputPath + ".png");
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png)
                        .WaitAsync(TimeSpan.FromSeconds(3), stop);
                    report["previewPath"] = outputPath + ".png";
                }
                catch (Exception error) { report["captureError"] = error.GetType().Name; }
            }
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task<JsonElement> ReadAsync(CoreWebView2 core, string script, CancellationToken stop)
    {
        if (!Allowed(core.Source)) throw new InvalidOperationException("Unexpected application origin.");
        var json = await core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(2), stop);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static bool Allowed(string? source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == "http" && uri.Authority == "127.0.0.1:8765" && uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    private static bool True(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.True;
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private static bool InputHeld() => new[] { 1, 2, 4, 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static string Script(string recipeId, bool start) => $$"""
        (() => {
          const id = {{JsonSerializer.Serialize(recipeId)}};
          if (typeof catalog === 'undefined' || !catalog || typeof requestGameCraft !== 'function' || typeof craftDisabledReason !== 'function')
            return {ready:false,reason:'Application is loading.'};
          const recipe = catalog.recipes.find(value => value.id === id);
          if (!recipe) return {ready:false,fatal:true,reason:'Recipe was not found.'};
          const reason = craftDisabledReason(recipe);
          const codes = new Set([recipe.resultUnitRawcode,...recipe.ingredients.filter(value => value.type === 'UNIT').map(value => value.rawcode)]);
          const snapshot = typeof latestSnapshot === 'undefined' ? null : latestSnapshot;
          const feedback = document.getElementById('craft-feedback');
          const result = {ready:!reason,reason,busy:typeof crafting !== 'undefined' && Boolean(crafting),
            tone:feedback?.dataset.tone || '',feedback:feedback?.textContent || '',
            snapshot:snapshot ? {sequence:snapshot.sequence,processId:snapshot.processId,timestamp:snapshot.timestamp,
              status:snapshot.status,counts:(snapshot.counts || []).filter(value => codes.has(value.rawcode)).map(value => ({rawcode:value.rawcode,count:value.count}))} : null};
          if ({{(start ? "true" : "false")}}) {
            if (reason) return {...result,started:false};
            requestGameCraft(recipe);
            return {...result,started:true};
          }
          return result;
        })()
        """;
}
