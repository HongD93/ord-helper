static class ChoiceWispPortraitTimingTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var passed = 0;
        async Task Test(string name, Func<Task> run)
        {
            await run();
            passed++;
            Console.WriteLine("PASS: Choice portrait timing " + name);
        }
        uint[] group = [0x10000, 0x10004, 0x10008];
        uint[] after = [0x10000, 0x10004];
        const uint removed = 0x10008;

        await Test("immediate removal needs two observations at least 20ms apart", async () =>
        {
            var clock = new Clock();
            var observations = new List<int>();
            var result = await Wait(clock, group, removed, () => { observations.Add(clock.NowMs); return after; });
            Check(result && clock.NowMs == 20 && observations.SequenceEqual(new[] { 0, 10, 20 }));
            Check(clock.Delays.All(value => value == 10));
        });
        await Test("delayed engine reflection waits beyond the short click hold", async () =>
        {
            var clock = new Clock();
            Check(await Wait(clock, group, removed, () => clock.NowMs < 180 ? group : after));
            Check(clock.NowMs == 200);
        });
        await Test("a transient empty selection waits without authorizing another click", async () =>
        {
            var clock = new Clock();
            Check(await Wait(clock, group, removed, () => clock.NowMs < 10 ? group : clock.NowMs < 40 ? [] : after));
            Check(clock.NowMs == 60);
        });
        await Test("order changes preserve the original and expected set identities", async () =>
        {
            var clock = new Clock();
            Check(await Wait(clock, group, removed, () => clock.NowMs < 30
                ? clock.NowMs % 20 == 0 ? group.Reverse().ToArray() : group
                : clock.NowMs % 20 == 0 ? after.Reverse().ToArray() : after));
            Check(clock.NowMs == 50);
        });
        await Test("empty or original frames reset an earlier removal observation", async () =>
        {
            foreach (var transition in new[] { Array.Empty<uint>(), group.Reverse().ToArray() })
            {
                var clock = new Clock();
                Check(await Wait(clock, group, removed, () => clock.NowMs == 10 ? transition : after));
                Check(clock.NowMs == 40);
            }
        });
        await Test("unexpected foreign members or removal of a different member fail immediately", async () =>
        {
            foreach (var unexpected in new uint[][] { [0x10000, 0x10008], [0x10000, 0x20000], [0x10000], [0x10000, 0x10004, 0x20000] })
            {
                var clock = new Clock();
                Check(!await Wait(clock, group, removed, () => unexpected));
                Check(clock.NowMs == 0 && clock.Delays.Count == 0);
            }
        });
        await Test("duplicate or invalid observed addresses never pass", async () =>
        {
            foreach (var invalid in new uint[][] { [0x10000, 0x10000], [0x10000, 1], [0, 0x10004] })
            {
                var clock = new Clock();
                Check(!await Wait(clock, group, removed, () => invalid));
                Check(clock.Delays.Count == 0);
            }
        });
        await Test("invalid baselines do not start polling", async () =>
        {
            var invalidGroups = new uint[][] { [], [0x10000], [0x10000, 0x10000], [1, removed], Enumerable.Range(0, 13).Select(index => 0x10000u + (uint)index * 4).ToArray() };
            foreach (var invalid in invalidGroups)
            {
                var clock = new Clock();
                Check(!await Wait(clock, invalid, removed, () => throw new Exception("Unexpected read.")));
                Check(clock.Delays.Count == 0);
            }
            Check(!await Wait(new Clock(), group, 0x20000, () => throw new Exception("Unexpected read.")));
        });
        await Test("unchanged or empty groups time out within 600ms", async () =>
        {
            foreach (var pending in new[] { group, Array.Empty<uint>() })
            {
                var clock = new Clock(); var reads = 0;
                Check(!await Wait(clock, group, removed, () => { reads++; return pending; }));
                Check(clock.NowMs == 600 && reads == 60 && clock.Delays.Count == 60);
            }
        });
        await Test("one removal sample near the deadline cannot pass without its confirmation interval", async () =>
        {
            var clock = new Clock();
            Check(!await Wait(clock, group, removed, () => clock.NowMs < 590 ? group : after));
            Check(clock.NowMs == 600);
        });
        await Test("context loss stops before a read or before a successful return", async () =>
        {
            var clock = new Clock(); var reads = 0;
            Check(!await Wait(clock, group, removed, () => { reads++; return after; }, () => false));
            Check(reads == 0 && clock.Delays.Count == 0);
            clock = new Clock(); var context = true;
            Check(!await Wait(clock, group, removed, () => { if (clock.NowMs >= 20) context = false; return after; }, () => context));
            Check(clock.NowMs == 20);
        });
        await Test("pre-cancelled calls and cancellation during a poll throw", async () =>
        {
            using var source = new CancellationTokenSource(); source.Cancel();
            await Cancelled(() => Wait(new Clock(), group, removed, () => throw new Exception("Unexpected read."), cancel: source.Token));
            using var during = new CancellationTokenSource();
            var clock = new Clock { AfterDelay = () => during.Cancel() };
            await Cancelled(() => Wait(clock, group, removed, () => after, cancel: during.Token));
            Check(clock.NowMs == 10);
        });
        await Test("cancellation wins when validation or a selection read invalidates the context", async () =>
        {
            using var source = new CancellationTokenSource(); var reads = 0;
            await Cancelled(() => Wait(new Clock(), group, removed, () => { reads++; return after; }, () => { source.Cancel(); return false; }, source.Token));
            Check(reads == 0);
            using var readSource = new CancellationTokenSource();
            await Cancelled(() => Wait(new Clock(), group, removed, () => { readSource.Cancel(); return after; }, cancel: readSource.Token));
        });
        await Test("a removal read that crosses the deadline cannot return success", async () =>
        {
            var clock = new Clock();
            Check(!await Wait(clock, group, removed, () => { clock.NowMs = 601; return after; }));
            Check(clock.Delays.Count == 0);
        });
        await Test("twelve to two uses ten exact removals with shorter modeled click waits", async () =>
        {
            var clock = new Clock();
            var selected = Enumerable.Range(0, 12).Select(index => 0x10000u + (uint)index * 4).ToArray();
            var retained = selected.Take(2).ToArray();
            var counts = new List<int>();
            var inputConfirmations = 0;
            while (selected.Length > 2)
            {
                var before = selected.ToArray(); var remove = before[^1];
                await clock.Delay(ChoiceWispPortraitTiming.HoverMs, default);
                await clock.Delay(ChoiceWispPortraitTiming.ModifierMs, default);
                Check(await Owned(clock, () => { inputConfirmations++; return true; }));
                await clock.Delay(ChoiceWispPortraitTiming.HoldMs, default);
                Check(await Owned(clock, () => { inputConfirmations++; return true; }));
                var expected = before.Where(address => address != remove).Reverse().ToArray();
                var confirmationStarted = clock.NowMs;
                Check(await Wait(clock, before, remove, () => expected));
                Check(clock.NowMs - confirmationStarted == 20 && expected.Length == before.Length - 1 && retained.All(expected.Contains));
                Check(await Owned(clock, () => { inputConfirmations++; return true; }));
                selected = expected.Order().ToArray(); counts.Add(selected.Length);
            }
            Check(counts.SequenceEqual(Enumerable.Range(2, 10).Reverse()));
            Check(selected.SequenceEqual(retained));
            Check(ChoiceWispPortraitTiming.HoverMs == 15 && ChoiceWispPortraitTiming.ModifierMs == 15 && ChoiceWispPortraitTiming.HoldMs == 30);
            Check(inputConfirmations == 30 && clock.NowMs == 800 && clock.NowMs < 10 * (40 + 40 + 60 + 60 + 50));
        });
        await Test("an already reflected owned key transition has no fixed wait", async () =>
        {
            var clock = new Clock(); var reads = 0; var checks = 0;
            Check(await Owned(clock, () => { reads++; return true; }, () => { checks++; return true; }));
            Check(reads == 1 && checks == 2 && clock.NowMs == 0 && clock.Delays.Count == 0);
        });
        await Test("owned input reflection may arrive after thirty milliseconds", async () =>
        {
            var clock = new Clock(); var observations = new List<int>();
            Check(await Owned(clock, () => { observations.Add(clock.NowMs); return clock.NowMs >= 30; }));
            Check(clock.NowMs == 30 && observations.SequenceEqual(new[] { 0, 10, 20, 30 }));
            Check(clock.Delays.Count == 3 && clock.Delays.All(value => value == 10));
        });
        await Test("an owned transition that never arrives stops after one hundred milliseconds", async () =>
        {
            var clock = new Clock(); var reads = 0;
            Check(!await Owned(clock, () => { reads++; return false; }));
            Check(clock.NowMs == 100 && reads == 11 && clock.Delays.Count == 10);
        });
        await Test("external context changes reject owned input before polling or after a ready read", async () =>
        {
            var clock = new Clock(); var reads = 0;
            Check(!await Owned(clock, () => { reads++; return true; }, () => false));
            Check(reads == 0 && clock.Delays.Count == 0);
            var context = true;
            Check(!await Owned(clock, () => { context = false; return true; }, () => context));
            Check(clock.NowMs == 0 && clock.Delays.Count == 0);
        });
        await Test("owned input cancellation wins at every callback boundary and during polling", async () =>
        {
            using var initial = new CancellationTokenSource(); initial.Cancel();
            await Cancelled(() => Owned(new Clock(), () => throw new Exception("Unexpected read."), cancel: initial.Token));
            using var before = new CancellationTokenSource();
            await Cancelled(() => Owned(new Clock(), () => throw new Exception("Unexpected read."), () => { before.Cancel(); return false; }, before.Token));
            using var ready = new CancellationTokenSource();
            await Cancelled(() => Owned(new Clock(), () => { ready.Cancel(); return true; }, cancel: ready.Token));
            using var after = new CancellationTokenSource(); var validations = 0;
            await Cancelled(() => Owned(new Clock(), () => true, () => { if (++validations < 2) return true; after.Cancel(); return false; }, after.Token));
            using var pending = new CancellationTokenSource();
            var clock = new Clock { AfterDelay = () => pending.Cancel() };
            await Cancelled(() => Owned(clock, () => false, cancel: pending.Token));
            Check(clock.NowMs == 10);
        });
        await Test("a previous key-up confirmation is never reused for a later changing key-up", async () =>
        {
            var clock = new Clock(); var released = true;
            Check(await Owned(clock, () => released));
            Check(clock.NowMs == 0);
            released = false;
            Check(await Owned(clock, () => released || clock.NowMs >= 30));
            Check(clock.NowMs == 30);
            var context = true; var reads = 0;
            clock = new Clock { AfterDelay = () => context = false };
            Check(!await Owned(clock, () => { reads++; return clock.NowMs >= 10; }, () => context));
            Check(clock.NowMs == 10 && reads == 1);
        });
        Console.WriteLine($"PASS: Choice portrait timing ({passed} fake-clock tests; no input, process access or wall-clock sleep).");
    }

    private static Task<bool> Wait(Clock clock, uint[] before, uint removed, Func<uint[]> read,
        Func<bool>? valid = null, CancellationToken cancel = default) =>
        ChoiceWispPortraitTiming.WaitForRemovalAsync(before, removed, read, valid ?? (() => true), cancel, clock.Elapsed, clock.Delay);

    private static Task<bool> Owned(Clock clock, Func<bool> ready, Func<bool>? valid = null,
        CancellationToken cancel = default) =>
        ChoiceWispPortraitTiming.WaitForOwnedInputAsync(ready, valid ?? (() => true), cancel, clock.Delay);

    private static async Task Cancelled(Func<Task<bool>> run)
    {
        try { await run(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected immediate cancellation.");
    }

    private static void Check(bool valid)
    {
        if (!valid) throw new Exception("Choice portrait timing fixture failed.");
    }

    private sealed class Clock
    {
        internal int NowMs;
        internal readonly List<int> Delays = [];
        internal Action? AfterDelay;
        internal TimeSpan Elapsed() => TimeSpan.FromMilliseconds(NowMs);
        internal Task Delay(int milliseconds, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            Delays.Add(milliseconds); NowMs += milliseconds; AfterDelay?.Invoke();
            cancel.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
