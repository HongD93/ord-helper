using System.Text;
using System.Text.Json;

static class GameChatSubmissionTests
{
    internal static async Task RunAsync()
    {
        var passed = 0;
        async Task Test(string name, Func<Task> run) { await run(); passed++; Console.WriteLine("PASS: Chat submission " + name); }
        const string expected = "순록의사!";
        static ChatTextState Text(string value, uint edit = 0x10000, uint buffer = 0x20000) => new(edit, buffer, Encoding.UTF8.GetBytes(value));
        static Task Pause(CancellationToken cancel) { cancel.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        async Task<(string? Error, int Enters)> Run(string actual, bool materials = true, bool valid = true, bool close = true, uint edit = 0x10000, uint buffer = 0x20000)
        {
            var enters = 0;
            var error = await GameChatSubmission.SubmitAsync(expected, Text(actual), () => Text(actual, edit, buffer), () => valid,
                () => materials, () => enters++, () => !close || enters == 0, default, Pause);
            return (error, enters);
        }
        await Test("exact punctuation sends one Enter", async () => { var r = await Run(expected); Check(r.Error is null && r.Enters == 1); });
        await Test("an inserted space prevents every submit", async () => { var r = await Run("순록의사 !"); Check(r.Error is not null && r.Enters == 0); });
        await Test("a missing symbol prevents every submit", async () => { var r = await Run("순록의사"); Check(r.Error is not null && r.Enters == 0); });
        await Test("changed materials prevent every submit", async () => { var r = await Run(expected, materials: false); Check(r.Error is not null && r.Enters == 0); });
        await Test("changed foreground or session prevents every submit", async () => { var r = await Run(expected, valid: false); Check(r.Error is not null && r.Enters == 0); });
        await Test("changed editor identity prevents every submit", async () => { Check((await Run(expected, edit: 0x10004)).Enters == 0); });
        await Test("a reallocated buffer is accepted only after stable exact observations", async () => { var r = await Run(expected, buffer: 0x20004); Check(r.Error is null && r.Enters == 1); });
        await Test("a continuously moving buffer never sends Enter", async () =>
        {
            uint buffer = 0x20000; var enters = 0;
            var error = await GameChatSubmission.SubmitAsync(expected, Text(expected), () => Text(expected, buffer: ++buffer), () => true,
                () => true, () => enters++, () => true, default, Pause);
            Check(error is not null && enters == 0);
        });
        await Test("a delayed or unaccepted Enter is never resent", async () => { var r = await Run(expected, close: false); Check(r.Error is not null && r.Enters == 1); });
        await Test("final text is read after material validation", async () =>
        {
            var materialRead = false; var enters = 0;
            var error = await GameChatSubmission.SubmitAsync(expected, Text(expected), () => { Check(materialRead); return Text("순록의사 !"); }, () => true,
                () => { materialRead = true; return true; }, () => enters++, () => true, default, Pause);
            Check(error is not null && enters == 0);
        });
        await Test("a cancelled request sends no Enter", async () =>
        {
            using var source = new CancellationTokenSource(); source.Cancel(); var enters = 0; var cancelled = false;
            try { await GameChatSubmission.SubmitAsync(expected, Text(expected), () => Text(expected), () => true, () => true, () => enters++, () => true, source.Token, Pause); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && enters == 0);
        });
        await Test("a partial input failure is not retried", async () =>
        {
            var enters = 0; var failed = false;
            try { await GameChatSubmission.SubmitAsync(expected, Text(expected), () => Text(expected), () => true, () => true, () => { enters++; throw new IOException("mock input failed"); }, () => true, default, Pause); }
            catch (IOException) { failed = true; }
            Check(failed && enters == 1);
        });
        await Test("invalid UTF8 cannot pass exact text verification", async () =>
        {
            var bad = new ChatTextState(0x10000, 0x20000, [0xFF]); var enters = 0;
            var error = await GameChatSubmission.SubmitAsync(expected, bad, () => bad, () => true, () => true, () => enters++, () => true, default, Pause);
            Check(error is not null && enters == 0);
        });
        await Test("a transient buffer read retries reads without repeating input", async () =>
        {
            var reads = 0; var enters = 0;
            ChatTextState Read() { if (++reads <= 2) throw new IOException("chat_buffer_unstable"); return Text(expected); }
            var error = await GameChatSubmission.SubmitAsync(expected, Text(expected), Read, () => true, () => true,
                () => enters++, () => enters == 0, default, Pause);
            Check(error is null && enters == 1 && reads >= 5);
        });
        await Test("changed chat UI errors are never retried as transient buffers", async () =>
        {
            var enters = 0; var reads = 0; var failed = false;
            try { await GameChatSubmission.SubmitAsync(expected, Text(expected), () => { reads++; throw new IOException("chat_ui_changed"); },
                () => true, () => true, () => enters++, () => true, default, Pause); }
            catch (IOException) { failed = true; }
            Check(failed && enters == 0 && reads == 1);
        });
        await Test("a transient final read starts fresh observations without repeating input", async () =>
        {
            var reads = 0; var enters = 0;
            ChatTextState Read() { if (++reads == 3) throw new IOException("chat_memory_read_failed"); return Text(expected); }
            var error = await GameChatSubmission.SubmitAsync(expected, Text(expected), Read, () => true, () => true,
                () => enters++, () => enters == 0, default, Pause);
            Check(error is null && enters == 1 && reads == 6);
        });
        await Test("materials are checked again after stable text", async () =>
        {
            var checks = 0; var enters = 0;
            var error = await GameChatSubmission.SubmitAsync(expected, Text(expected), () => Text(expected), () => true,
                () => ++checks == 1, () => enters++, () => true, default, Pause);
            Check(error is not null && enters == 0 && checks == 2);
        });
        await Test("asynchronous Enter holds once then releases before close confirmation", async () =>
        {
            var events = new List<string>(); var open = true;
            var error = await GameChatSubmission.SubmitWithAsyncEnterAsync(expected, Text(expected), () => Text(expected), () => open,
                () => true, token => GameChatSubmission.PressOnceAsync(() => events.Add("down"), () => { events.Add("up"); open = false; },
                    () => open, token, _ => { events.Add("hold"); return Task.CompletedTask; }), () => open, () => true, default, Pause);
            Check(error is null && events.SequenceEqual(new[] { "down", "hold", "up" }));
        });
        await Test("a closed chat with a changed post-submit session is not success", async () =>
        {
            var enters = 0;
            var error = await GameChatSubmission.SubmitWithAsyncEnterAsync(expected, Text(expected), () => Text(expected), () => true,
                () => true, _ => { enters++; return Task.CompletedTask; }, () => false, () => false, default, Pause);
            Check(error is not null && enters == 1);
        });
        await Test("a delayed chat close never resends the command", async () =>
        {
            var enters = 0; var checks = 0;
            var error = await GameChatSubmission.SubmitWithAsyncEnterAsync(expected, Text(expected), () => Text(expected), () => true,
                () => true, _ => { enters++; return Task.CompletedTask; }, () => ++checks < 12, () => true, default, Pause);
            Check(error is null && enters == 1 && checks == 12);
        });
        await Test("cancellation during key hold releases the key without a second down", async () =>
        {
            using var source = new CancellationTokenSource(); var downs = 0; var ups = 0; var cancelled = false;
            try { await GameChatSubmission.PressOnceAsync(() => downs++, () => ups++, () => true, source.Token,
                token => { source.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && downs == 1 && ups == 1);
        });
        await Test("a key-down failure still releases once and is never retried", async () =>
        {
            var downs = 0; var ups = 0; var failed = false;
            try { await GameChatSubmission.PressOnceAsync(() => { downs++; throw new IOException("mock partial input"); }, () => ups++, () => true, default, Pause); }
            catch (IOException) { failed = true; }
            Check(failed && downs == 1 && ups == 1);
        });
        await Test("an invalid key context sends neither down nor up", async () =>
        {
            var downs = 0; var ups = 0; var failed = false;
            try { await GameChatSubmission.PressOnceAsync(() => downs++, () => ups++, () => false, default, Pause); }
            catch (IOException) { failed = true; }
            Check(failed && downs == 0 && ups == 0);
        });
        await Test("committed text survives an auxiliary IME restoration failure", async () =>
        {
            var releases = 0; var enters = 0;
            var typing = await KoreanImeInput.CompleteAsync(() => Task.CompletedTask, () => throw new IOException("mock restore failed"),
                () => { releases++; return null; }, () => { }, () => true, default);
            var result = await Run(expected); enters += result.Enters;
            Check(typing.TextCommitted && !typing.OriginalImeRestored && typing.RestorationError == "mock restore failed" && releases == 1 && enters == 1);
        });
        await Test("a typing failure remains a failure even when restoration succeeds", async () =>
        {
            var releases = 0; var restores = 0; var failed = false;
            try { await KoreanImeInput.CompleteAsync(() => throw new IOException("mock typing failed"), () => { restores++; return Task.CompletedTask; },
                () => { releases++; return null; }, () => { }, () => true, default); }
            catch (IOException) { failed = true; }
            Check(failed && releases == 1 && restores == 1);
        });
        await Test("IME restoration warnings cannot bypass changed text or context", async () =>
        {
            foreach (var changeText in new[] { false, true })
            {
                var failed = false;
                try { await KoreanImeInput.CompleteAsync(() => Task.CompletedTask, () => throw new IOException("mock restore failed"), () => null,
                    () => { if (!changeText) throw new IOException("mock context changed"); }, () => !changeText, default); }
                catch (IOException) { failed = true; }
                Check(failed);
            }
        });
        await Test("unreleased typing keys always prevent readiness", async () =>
        {
            var failed = false;
            try { await KoreanImeInput.CompleteAsync(() => Task.CompletedTask, () => Task.CompletedTask, () => "mock stuck key", () => { }, () => true, default); }
            catch (IOException) { failed = true; }
            Check(failed);
        });
        await Test("cancellation during IME cleanup does not produce submit readiness", async () =>
        {
            using var source = new CancellationTokenSource(); var cancelled = false;
            try { await KoreanImeInput.CompleteAsync(() => Task.CompletedTask, () => { source.Cancel(); return Task.CompletedTask; }, () => null, () => { }, () => true, source.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled);
        });
        await Test("every bundled Korean alias submits exactly once when confirmed", async () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "chat-craft-commands.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var commands = document.RootElement.GetProperty("recipes").EnumerateArray().Select(row => row.GetProperty("command").GetString()!).ToArray();
            Check(commands.Length == 81 && commands.Contains(expected, StringComparer.Ordinal) && commands.All(command => !command.Contains(' ')));
            foreach (var command in commands)
            {
                var enters = 0;
                var error = await GameChatSubmission.SubmitAsync(command, Text(command), () => Text(command), () => true, () => true, () => enters++, () => enters == 0, default, Pause);
                Check(error is null && enters == 1);
            }
        });
        Console.WriteLine($"PASS: Chat submission ({passed} mock tests; no Windows input or game calls).");
    }
    private static void Check(bool valid) { if (!valid) throw new Exception("Chat submission fixture failed."); }
}
