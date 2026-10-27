using System.Diagnostics;

internal static class CraftSnapshotTimingTests
{
    internal static async Task RunAsync()
    {
        var passed = 0;
        async Task Test(string name, Func<Task> run)
        {
            await run(); passed++;
            Console.WriteLine("PASS: Craft snapshot timing " + name);
        }
        Task Case(Action run) { run(); return Task.CompletedTask; }
        static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);

        await Test("invalid intervals never create a cadence", () => Case(() =>
        {
            foreach (var interval in new[] { 0, -1 })
            {
                try { _ = new CraftCaptureCadence(interval); }
                catch (ArgumentOutOfRangeException) { continue; }
                throw new Exception("Invalid interval was accepted.");
            }
        }));
        await Test("idle capture preserves configured spacing", () => Case(() =>
        {
            var cadence = new CraftCaptureCadence(250);
            Check(cadence.TickMs == 50 && cadence.TryBegin(Ms(0), false));
            Check(!cadence.TryBegin(Ms(249), false) && cadence.TryBegin(Ms(250), false));
            Check(!cadence.TryBegin(Ms(499), false) && cadence.TryBegin(Ms(500), false));
        }));
        await Test("active capture uses 100ms without duplicate captures", () => Case(() =>
        {
            var cadence = new CraftCaptureCadence(250);
            Check(cadence.TryBegin(Ms(0), true) && !cadence.TryBegin(Ms(99), true));
            Check(cadence.TryBegin(Ms(100), true) && !cadence.TryBegin(Ms(100), true) && cadence.TryBegin(Ms(200), true));
        }));
        await Test("faster configured capture is never slowed down", () => Case(() =>
        {
            var cadence = new CraftCaptureCadence(25);
            Check(cadence.TickMs == 25 && cadence.TryBegin(Ms(0), true));
            Check(!cadence.TryBegin(Ms(24), true) && cadence.TryBegin(Ms(25), true));
        }));
        await Test("idle to active transition accelerates the pending capture", () => Case(() =>
        {
            var cadence = new CraftCaptureCadence(250);
            Check(cadence.TryBegin(Ms(0), false) && !cadence.TryBegin(Ms(150), false));
            Check(cadence.TryBegin(Ms(150), true));
        }));
        await Test("ending a craft restores idle spacing from the latest capture", () => Case(() =>
        {
            var cadence = new CraftCaptureCadence(250);
            Check(cadence.TryBegin(Ms(0), true) && cadence.TryBegin(Ms(100), true));
            Check(!cadence.TryBegin(Ms(200), false) && !cadence.TryBegin(Ms(349), false) && cadence.TryBegin(Ms(350), false));
        }));
        await Test("clock regression cannot advance or reset the cadence", () => Case(() =>
        {
            var cadence = new CraftCaptureCadence(250);
            Check(!cadence.TryBegin(Ms(-1), true) && cadence.TryBegin(Ms(0), false));
            Check(!cadence.TryBegin(Ms(249), false) && !cadence.TryBegin(Ms(200), true));
            Check(cadence.TryBegin(Ms(249), true) && !cadence.TryBegin(Ms(248), true) && cadence.TryBegin(Ms(349), true));
        }));
        await Test("a tick just before the deadline waits only the remaining millisecond", () => Case(() =>
        {
            var cadence = new CraftCaptureCadence(250);
            Check(cadence.TryBegin(Ms(0), true));
            var near = TimeSpan.FromMilliseconds(99.5);
            Check(!cadence.TryBegin(near, true) && cadence.DelayMs(near, true) == 1);
            Check(cadence.TryBegin(near + Ms(1), true));
        }));
        await Test("overdue capture waits at least one millisecond without changing cadence state", () => Case(() =>
        {
            var cadence = new CraftCaptureCadence(250);
            Check(cadence.TryBegin(Ms(0), true) && cadence.DelayMs(Ms(200), true) == 1);
            Check(cadence.TryBegin(Ms(100), true) && !cadence.TryBegin(Ms(180), true));
            Check(cadence.DelayMs(Ms(179), true) == cadence.TickMs && cadence.DelayMs(Ms(-1), true) == cadence.TickMs);
            Check(new CraftCaptureCadence(250).DelayMs(Ms(0), true) == 50);
        }));
        await Test("dynamic delays follow idle and active deadlines with a bounded wake interval", () => Case(() =>
        {
            var cadence = new CraftCaptureCadence(250);
            Check(cadence.TryBegin(Ms(0), false));
            Check(cadence.DelayMs(Ms(150), false) == 50 && cadence.DelayMs(Ms(150), true) == 1);
            Check(cadence.TryBegin(Ms(150), true) && cadence.DelayMs(Ms(200), false) == 50);
            var nearIdle = TimeSpan.FromMilliseconds(399.5);
            Check(!cadence.TryBegin(nearIdle, false) && cadence.DelayMs(nearIdle, false) == 1);
            Check(cadence.TryBegin(Ms(400), false));
            var fast = new CraftCaptureCadence(25);
            Check(fast.TryBegin(Ms(0), true) && fast.DelayMs(Ms(0), true) == 25 && fast.DelayMs(Ms(24), true) == 1);
        }));
        await Test("publish before wait completes without losing the signal", async () =>
        {
            var signal = new CraftSnapshotSignal(); signal.Publish(4);
            var wait = signal.WaitAsync(3, default);
            Check(wait.IsCompletedSuccessfully); await wait;
        });
        await Test("a registered waiter wakes on the next published sequence", async () =>
        {
            var signal = new CraftSnapshotSignal(); signal.Publish(1);
            var wait = signal.WaitAsync(1, default);
            Check(!wait.IsCompleted); signal.Publish(2);
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
        });
        await Test("same or old sequences do not wake a waiter", async () =>
        {
            var signal = new CraftSnapshotSignal(); signal.Publish(5);
            using var cancel = new CancellationTokenSource();
            var wait = signal.WaitAsync(5, cancel.Token);
            signal.Publish(5); signal.Publish(4);
            Check(!wait.IsCompleted); cancel.Cancel(); await Canceled(wait);
            await signal.WaitAsync(4, default);
        });
        await Test("one publication wakes all current waiters", async () =>
        {
            var signal = new CraftSnapshotSignal();
            var waits = Enumerable.Range(0, 8).Select(_ => signal.WaitAsync(0, default)).ToArray();
            Check(waits.All(wait => !wait.IsCompleted)); signal.Publish(1);
            await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(2));
        });
        await Test("cancellation applies before ready and does not poison later waits", async () =>
        {
            var signal = new CraftSnapshotSignal(); signal.Publish(2);
            using var before = new CancellationTokenSource(); before.Cancel();
            await Canceled(signal.WaitAsync(1, before.Token));
            using var during = new CancellationTokenSource();
            var wait = signal.WaitAsync(2, during.Token); during.Cancel(); await Canceled(wait);
            signal.Publish(3); await signal.WaitAsync(2, default);
        });
        await Test("a future sequence is not satisfied by an intermediate publication", async () =>
        {
            var signal = new CraftSnapshotSignal();
            var wait = signal.WaitAsync(3, default); signal.Publish(2);
            Check(!wait.IsCompleted); signal.Publish(4);
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
        });
        await Test("intermediate publications do not bypass the bounded 100ms wakeup", async () =>
        {
            var signal = new CraftSnapshotSignal(); var clock = Stopwatch.StartNew();
            var wait = signal.WaitAsync(3, default); signal.Publish(2);
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            Check(clock.ElapsedMilliseconds >= 75 && clock.ElapsedMilliseconds < 2000);
            signal.Publish(4); await signal.WaitAsync(3, default);
        });
        Console.WriteLine($"PASS: Craft snapshot timing ({passed} cases; capture transitions and publication races, no game input).");
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Craft snapshot timing fixture failed.");
    }

    private static async Task Canceled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation.");
    }
}
