internal static class ChoiceWispRecoveryCleanupTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        const uint Normal = 10, Owned = 20, Foreign = 30;
        var passed = 0;
        async Task Test(string name, Func<Task> run)
        {
            await run(); passed++;
            Console.WriteLine("PASS: Choice recovery cleanup " + name);
        }

        await Test("a queued M is observed after cancellation and Escape is sent only once", async () =>
        {
            var elapsed = 0; var escaped = 0; uint mode = Normal;
            var result = await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(0, Normal, () => mode,
                () => true, () => true, () => { escaped++; mode = Normal; }, milliseconds =>
                { Check(milliseconds == 25); elapsed += milliseconds; if (elapsed == 75) mode = Owned; return Task.CompletedTask; });
            Check(result && escaped == 1 && elapsed == 75);
        });
        await Test("a consumed original with two proofs can own an empty selection during cleanup", async () =>
        {
            var firstProof = true; var secondProof = true; var selectedCount = 0; var escaped = 0; uint mode = Owned;
            var result = await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => mode,
                () => true, () => firstProof && secondProof && selectedCount == 0,
                () => { escaped++; mode = Normal; }, _ => throw new Exception("Unexpected delay."));
            Check(result && escaped == 1);
        });
        await Test("an incomplete consumption proof never authorizes an empty selection", async () =>
        {
            var escaped = 0;
            Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => Owned,
                () => true, () => false, () => escaped++, _ => throw new Exception("Unexpected delay.")) && escaped == 0);
        });
        await Test("an observed owned mode already returned to normal needs no extra input", async () =>
        {
            var escaped = 0; var delays = 0;
            Check(await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => Normal,
                () => true, () => true, () => escaped++, _ => { delays++; return Task.CompletedTask; }));
            Check(escaped == 0 && delays == 0);
        });
        await Test("a foreign mode is not cancelled", async () =>
        {
            var escaped = 0;
            Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => Foreign,
                () => true, () => true, () => escaped++, _ => throw new Exception("Unexpected delay.")) && escaped == 0);
        });
        await Test("context or source ownership lost before observing a mode stops without input", async () =>
        {
            foreach (var lost in new[] { "context", "selection" })
            {
                var reads = 0; var escaped = 0;
                Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(0, Normal, () => { reads++; return Owned; },
                    () => lost != "context", () => lost != "selection", () => escaped++, _ => throw new Exception("Unexpected delay.")));
                Check(reads == 0 && escaped == 0);
            }
        });
        await Test("context or selection loss while waiting for the queued M stops before Escape", async () =>
        {
            foreach (var lost in new[] { "context", "selection" })
            {
                var active = true; var delays = 0; var escaped = 0;
                Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(0, Normal, () => Normal,
                    () => active || lost != "context", () => active || lost != "selection", () => escaped++, _ =>
                    { delays++; active = false; return Task.CompletedTask; }));
                Check(delays == 1 && escaped == 0);
            }
        });
        await Test("a mode read that loses the input context cannot send Escape", async () =>
        {
            var active = true; var escaped = 0;
            Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => { active = false; return Owned; },
                () => active, () => true, () => escaped++, _ => throw new Exception("Unexpected delay.")) && escaped == 0);
        });
        await Test("the final mode read preserves normal and foreign transitions instead of cancelling them", async () =>
        {
            foreach (var finalMode in new[] { Normal, Foreign })
            {
                var reads = 0; var escaped = 0;
                var result = await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => ++reads == 1 ? Owned : finalMode,
                    () => true, () => true, () => escaped++, _ => throw new Exception("Unexpected delay."));
                Check(result == (finalMode == Normal) && escaped == 0 && reads == 2);
            }
        });
        await Test("a queued M that never appears expires after exactly five hundred milliseconds", async () =>
        {
            var elapsed = 0; var delays = 0; var escaped = 0;
            Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(0, Normal, () => Normal,
                () => true, () => true, () => escaped++, milliseconds =>
                { elapsed += milliseconds; delays++; return Task.CompletedTask; }));
            Check(escaped == 0 && elapsed == 500 && delays == 20);
        });
        await Test("delayed Escape acceptance is observed without a repeated Escape", async () =>
        {
            var elapsed = 0; var escaped = 0; uint mode = Owned;
            var result = await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => mode,
                () => true, () => true, () => escaped++, milliseconds =>
                { elapsed += milliseconds; if (elapsed == 100) mode = Normal; return Task.CompletedTask; });
            Check(result && escaped == 1 && elapsed == 100);
        });
        await Test("unaccepted Escape expires after five hundred milliseconds with one input", async () =>
        {
            var elapsed = 0; var delays = 0; var escaped = 0;
            Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => Owned,
                () => true, () => true, () => escaped++, milliseconds =>
                { elapsed += milliseconds; delays++; return Task.CompletedTask; }));
            Check(escaped == 1 && elapsed == 500 && delays == 20);
        });
        await Test("a foreign mode after Escape ends observation without an extra input", async () =>
        {
            var escaped = 0; uint mode = Owned;
            Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => mode,
                () => true, () => true, () => escaped++, _ => { mode = Foreign; return Task.CompletedTask; }));
            Check(escaped == 1);
        });
        await Test("context or selection changes after Escape stop waiting with no further input", async () =>
        {
            foreach (var lost in new[] { "context", "selection" })
            {
                var active = true; var escaped = 0; var delays = 0;
                Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(Owned, Normal, () => Owned,
                    () => active || lost != "context", () => active || lost != "selection", () => escaped++, _ =>
                    { delays++; active = false; return Task.CompletedTask; }));
                Check(escaped == 1 && delays == 1);
            }
        });
        await Test("both queue and release waits remain bounded independently", async () =>
        {
            var elapsed = 0; var escaped = 0; var delays = 0; uint mode = Normal;
            Check(!await ChoiceWispRecoveryCleanup.CancelOwnedModeAsync(0, Normal, () => mode,
                () => true, () => true, () => escaped++, milliseconds =>
                { elapsed += milliseconds; delays++; if (elapsed == 500) mode = Owned; return Task.CompletedTask; }));
            Check(escaped == 1 && delays == 40 && elapsed == 1000);
        });
        Console.WriteLine($"PASS: Choice recovery cleanup ({passed} pure cases; no game input, process access or wall-clock delay).");
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Choice recovery cleanup fixture failed.");
    }
}
