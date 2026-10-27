using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;

static class LiveCounterServer
{
    internal sealed record Publication(long Sequence, string Json);

    internal sealed class PublicationSink(string? outputPath, JsonSerializerOptions pretty, JsonSerializerOptions compact,
        Action<Exception, string>? warning = null, GameRunRecorder? runRecorder = null)
    {
        private Publication current = new(0, "{\"status\":\"connecting\"}");
        private readonly CraftSnapshotSignal snapshotSignal = new();
        private string lastRecordedState = "";
        private DateTimeOffset nextWarning;

        public Publication Current => Volatile.Read(ref current);
        public void SetCurrent(Publication publication)
        {
            Volatile.Write(ref current, publication);
            snapshotSignal.Publish(publication.Sequence);
        }
        internal Task WaitForNewerAsync(long sequence, CancellationToken cancel) => snapshotSignal.WaitAsync(sequence, cancel);

        public async Task PublishAsync(Publication publication, JsonObject payload, CancellationToken cancel)
        {
            SetCurrent(publication);
            if (runRecorder is not null)
            {
                try { await runRecorder.ProcessAsync(payload, cancel); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
                {
                    Warn(error, "run-record");
                }
            }
            if (outputPath is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var temporary = outputPath + ".tmp";
                await File.WriteAllTextAsync(temporary, payload.ToJsonString(pretty), Encoding.UTF8, cancel);
                File.Move(temporary, outputPath, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Warn(error, "snapshot");
            }

            var semantic = new JsonObject
            {
                ["status"] = payload["status"]?.DeepClone(),
                ["map"] = payload["loadedMapHash"]?.DeepClone(),
                ["player"] = payload["localPlayer"]?.DeepClone(),
                ["process"] = payload["processId"]?.DeepClone(),
                ["session"] = payload["sessionIdentity"]?.DeepClone(),
                ["progression"] = Select(payload["progression"], "status", "localPlayer", "round", "storyIndex", "activePlayer", "difficulty", "difficultyValue", "gameEnded", "victory", "playerSlotState"),
                ["progressionSource"] = Select(payload["progression"]?["source"], "reason", "globalTypeMismatch"),
                ["randomWisp"] = Select(payload["progression"]?["randomWisp"], "status", "drawCount", "correctionInterval", "commonAcquiredCounts"),
                ["startingEvolution"] = Select(payload["startingEvolution"], "status", "localPlayer", "resultRawcode", "pendingCount", "phase"),
                ["counts"] = payload["status"]?.GetValue<string>() == "snapshot_read"
                    ? new JsonArray((payload["counts"]?.AsArray() ?? []).Select(u =>
                        (JsonNode?)new JsonObject { ["id"] = u!["rawcode"]?.DeepClone(), ["count"] = u["count"]?.DeepClone() }).ToArray())
                    : null
            }.ToJsonString(compact);
            if (semantic == lastRecordedState) return;
            try
            {
                await File.AppendAllTextAsync(outputPath + ".jsonl", publication.Json + "\n", Encoding.UTF8, cancel);
                lastRecordedState = semantic;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Warn(error, "journal");
            }
        }

        private static JsonObject Select(JsonNode? value, params string[] fields)
        {
            var selected = new JsonObject();
            foreach (var field in fields) selected[field] = value is JsonObject item ? item[field]?.DeepClone() : null;
            return selected;
        }

        private void Warn(Exception error, string destination)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < nextWarning) return;
            nextWarning = now.AddSeconds(30);
            warning?.Invoke(error, destination);
        }
    }

    public static async Task RunAsync(Func<object> capture, JsonSerializerOptions pretty, JsonSerializerOptions compact,
        int port, int intervalMs, string? output)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        await using var app = builder.Build();
        var sequence = 0L;
        var activeCraft = 0;
        var outputPath = output is null ? null : Path.GetFullPath(output);
        var runRecorder = outputPath is null ? null : new GameRunRecorder(RunDataDirectory(outputPath),
            (error, destination) => app.Logger.LogWarning(error, "Run {Destination} file is unavailable; the next sample retries.", destination));
        var publications = new PublicationSink(outputPath, pretty, compact,
            (error, destination) => app.Logger.LogWarning(error, "Snapshot {Destination} file is unavailable; live collection continues and the next sample retries.", destination), runRecorder);

        app.Use(async (context, next) =>
        {
            // 다른 호스트명으로 접근해 로컬 게임 정보가 노출되는 경우를 제한한다.
            if (context.Request.Host.Host != "127.0.0.1")
            {
                context.Response.StatusCode = 400;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });
        // 맵에서 직접 추출한 아이콘과 조합표를 같은 로컬 프로그램에서 제공한다.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot")),
            RequestPath = ""
        });
        app.MapGet("/", () => Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"), "text/html; charset=utf-8"));
        app.MapGet("/api/health", () => new { application = "ord-unit-probe", processId = Environment.ProcessId });
        app.MapGet("/api/snapshot", () => Results.Text(publications.Current.Json, "application/json", Encoding.UTF8));
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data");
        var crafting = new GameCraftService(() => publications.Current.Json,
            Path.Combine(dataDirectory, "map-catalog.json"), Path.Combine(dataDirectory, "chat-craft-commands.json"),
            waitForSnapshot: publications.WaitForNewerAsync,
            craftActivityChanged: active => Volatile.Write(ref activeCraft, active ? 1 : 0));
        crafting.Map(app);
        app.MapGet("/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var delivered = -1L;
            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var publication = publications.Current;
                    if (publication.Sequence != delivered)
                    {
                        await context.Response.WriteAsync($"id: {publication.Sequence}\ndata: {publication.Json}\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        delivered = publication.Sequence;
                    }
                    await Task.Delay(50, context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });

        var producer = Task.Run(async () =>
        {
            var cadence = new CraftCaptureCadence(intervalMs);
            var captureClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    if (cadence.TryBegin(captureClock.Elapsed, Volatile.Read(ref activeCraft) != 0))
                    {
                        var timerWatch = Stopwatch.StartNew();
                        var payload = JsonSerializer.SerializeToNode(capture(), compact)!.AsObject();
                        timerWatch.Stop();
                        payload["sampleDurationMs"] = Math.Round(timerWatch.Elapsed.TotalMilliseconds, 2);
                        payload["sequence"] = ++sequence;
                        var json = payload.ToJsonString(compact);
                        await publications.PublishAsync(new Publication(sequence, json), payload, app.Lifetime.ApplicationStopping);
                    }
                    await Task.Delay(cadence.DelayMs(captureClock.Elapsed, Volatile.Read(ref activeCraft) != 0), app.Lifetime.ApplicationStopping);
                }
            }
            catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
            catch (Exception error)
            {
                publications.SetCurrent(new Publication(++sequence,
                    JsonSerializer.Serialize(new { status = "collector_failed", timestamp = DateTimeOffset.UtcNow, error = error.Message }, compact)));
                app.Logger.LogError(error, "Collector stopped.");
            }
        });
        try { await app.RunAsync(); }
        finally
        {
            app.Lifetime.StopApplication();
            await producer;
            if (runRecorder is not null) await runRecorder.StopAsync(CancellationToken.None);
        }
    }

    internal static string RunDataDirectory(string outputPath)
    {
        var logs = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var package = Path.GetDirectoryName(logs);
        return Path.GetFileName(logs).Equals("logs", StringComparison.OrdinalIgnoreCase) && package is not null && File.Exists(Path.Combine(package, "portable-layout.json"))
            ? Path.Combine(package, "data", "local-runs", "auto")
            : Path.Combine(logs, "runs");
    }
}
