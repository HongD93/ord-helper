using System.Numerics;

static class ChoiceWispCameraSessionTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var passed = 0;
        async Task Test(string name, Func<Task> run) { await run(); passed++; Console.WriteLine("PASS: Choice camera " + name); }
        static ChoiceWispCameraIdentity Identity() => new(1, 12345, (IntPtr)5, 0x40000000,
            0x10000, 0x20000, 0x30000, 0, "fixture.w3x", new string('a', 64));
        static GameSelection.Geometry View(float x = 0, float y = 0) => new(new(x, y, 1000), new(x, y, 0), Vector3.UnitY,
            1.2f, new(0, 0, 1, 1), new(0, 0, 1, 1), 1280, 720);
        static ChoiceWispCameraSession Moved(ChoiceWispCameraIdentity id, GameSelection.Geometry original, GameSelection.Geometry owned)
        {
            var session = new ChoiceWispCameraSession(); Check(session.Begin(id, original, true, false)); session.RecordOwnedView(owned); return session;
        }

        await Test("empty or never moved fixture scopes perform no restoration callback", async () =>
        {
            var calls = 0; var session = new ChoiceWispCameraSession();
            Task<bool> Restore(ChoiceWispCameraCheckpoint _, CancellationToken __) { calls++; return Task.FromResult(true); }
            Check(!await session.RestoreAsync(Restore, default));
            session.RecordOwnedView(View(1000));
            Check(!await session.RestoreAsync(Restore, default));
            Check(session.Begin(Identity(), View(), true, false));
            Check(!await session.RestoreAsync(Restore, default) && calls == 0);
        });
        await Test("two choices retain the first origin and restore only the last owned view once", async () =>
        {
            var id = Identity(); var origin = View(); var first = View(1000); var second = View(2000);
            var session = Moved(id, origin, first); var calls = 0;
            Check(session.Begin(id, first, true, true)); session.RecordOwnedView(second);
            Check(await session.RestoreAsync((checkpoint, _) =>
            {
                calls++; Check(checkpoint.OriginalView == origin && checkpoint.OwnedView == second);
                Check(checkpoint.CanRestore(id, second, true, true)); return Task.FromResult(true);
            }, default));
            Check(!await session.RestoreAsync((_, _) => { calls++; return Task.FromResult(true); }, default) && calls == 1);
        });
        await Test("an ordinary craft boundary permits a fresh camera segment", async () =>
        {
            var id = Identity(); var session = Moved(id, View(), View(1000)); var calls = 0;
            Check(await session.RestoreAsync((_, _) => { calls++; return Task.FromResult(true); }, default));
            Check(session.Begin(id, View(3000), true, false)); session.RecordOwnedView(View(4000));
            Check(await session.RestoreAsync((checkpoint, _) =>
            { calls++; Check(checkpoint.OriginalView.Focus.X == 3000); return Task.FromResult(true); }, default));
            Check(calls == 2);
        });
        await Test("failed or cancelled restoration resets the scope without propagating cleanup errors", async () =>
        {
            foreach (var cancelled in new[] { false, true })
            {
                var session = Moved(Identity(), View(), View(1000)); var calls = 0;
                Check(!await session.RestoreAsync((_, _) =>
                {
                    calls++; if (cancelled) throw new OperationCanceledException(); throw new IOException("Synthetic restore failure.");
                }, default));
                Check(!await session.RestoreAsync((_, _) => { calls++; return Task.FromResult(true); }, default) && calls == 1);
            }
        });
        await Test("request cancellation can still restore through an independent cleanup token", async () =>
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var session = Moved(Identity(), View(), View(1000)); var calls = 0;
            Check(await session.RestoreAsync((checkpoint, token) =>
            { calls++; Check(!token.IsCancellationRequested && checkpoint.OriginalView.Focus.X == 0); return Task.FromResult(true); }, CancellationToken.None));
            Check(calls == 1 && cancelled.IsCancellationRequested);
        });
        await Test("changed process start window map player or roots never become another segment of the same scope", async () =>
        {
            var id = Identity();
            var changed = new[] { id with { ProcessId = 2 }, id with { ProcessStartedUtcTicks = 12346 }, id with { Window = (IntPtr)6 },
                id with { ModuleBase = 0x50000000 }, id with { Ui = 0x10004 }, id with { Players = 0x20004 }, id with { Table = 0x30004 },
                id with { LocalPlayer = 1 }, id with { MapPath = "another.w3x" }, id with { MapHash = new string('b', 64) } };
            foreach (var identity in changed)
            {
                var session = Moved(id, View(), View(1000)); var calls = 0;
                Check(!session.Begin(identity, View(1000), true, true));
                Check(!await session.RestoreAsync((_, _) => { calls++; return Task.FromResult(true); }, default) && calls == 0);
            }
        });
        await Test("user camera cursor and input changes abandon deferred restoration", async () =>
        {
            foreach (var change in new[] { "camera", "cursor", "input" })
            {
                var session = Moved(Identity(), View(), View(1000)); var calls = 0;
                Check(!session.Begin(Identity(), change == "camera" ? View(1100) : View(1000), change != "input", change != "cursor"));
                Check(!await session.RestoreAsync((_, _) => { calls++; return Task.FromResult(true); }, default) && calls == 0);
            }
        });
        await Test("a detected interruption is not cleared by recording a later matching view", async () =>
        {
            var session = Moved(Identity(), View(), View(1000)); var calls = 0;
            session.Abandon(); session.RecordOwnedView(View(2000));
            Check(!session.CanUseView(View(2000)) && !session.Begin(Identity(), View(1000), true, true));
            Check(!await session.RestoreAsync((_, _) => { calls++; return Task.FromResult(true); }, default) && calls == 0);
        });
        await Test("fresh cleanup observations can reject identity view cursor or normal-mode changes", async () =>
        {
            var id = Identity(); var session = Moved(id, View(), View(1000));
            Check(!await session.RestoreAsync((checkpoint, _) =>
            {
                Check(!checkpoint.CanRestore(id with { ProcessStartedUtcTicks = 99 }, View(1000), true, true));
                Check(!checkpoint.CanRestore(id, View(1200), true, true));
                Check(!checkpoint.CanRestore(id, View(1000), false, true));
                Check(!checkpoint.CanRestore(id, View(1000), true, false));
                return Task.FromResult(false);
            }, default));
        });
        await Test("a visible central safe target reuses the camera while offscreen or edge targets do not", () =>
        {
            var view = View();
            Check(ChoiceWispCameraSession.CanReuseTarget(view, new(0, 0, 9.5f)));
            Check(ChoiceWispCameraSession.CanReuseTarget(view, new(20, 0, 9.5f)));
            Check(!ChoiceWispCameraSession.CanReuseTarget(view, new(100000, 0, 9.5f)));
            Check(!ChoiceWispCameraSession.CanReuseTarget(view, new(400, 0, 9.5f)));
            return Task.CompletedTask;
        });
        await Test("a multiboard or invalid viewport blocks target reuse and parked restoration", async () =>
        {
            var board = View() with { Multiboard = new(.45f, .45f, .55f, .55f) };
            Check(!ChoiceWispCameraSession.CanReuseTarget(board, new(0, 0, 9.5f)));
            Check(!ChoiceWispCameraSession.CanReuseTarget(View() with { Width = 0 }, new(0, 0, 9.5f)));
            var session = Moved(Identity(), View(1000), board);
            Check(!await session.RestoreAsync((checkpoint, _) => Task.FromResult(checkpoint.CanRestore(Identity(), board, true, true)), default));
        });
        await Test("source reuse requires the whole bounded drag rectangle to remain visible and unoccluded", () =>
        {
            Vector3[] nearby = [new(-10, 0, 0), new(10, 0, 0)];
            Check(ChoiceWispCameraSession.CanReuseSourceView(View(), Vector3.Zero, nearby));
            Check(!ChoiceWispCameraSession.CanReuseSourceView(View(), Vector3.Zero, []));
            Check(!ChoiceWispCameraSession.CanReuseSourceView(View(), Vector3.Zero, [Vector3.Zero, new(100000, 0, 0)]));
            var board = View() with { Multiboard = new(.55f, .45f, .7f, .7f) };
            Check(!ChoiceWispCameraSession.CanReuseSourceView(board, Vector3.Zero, [Vector3.Zero, new(200, 100, 0)]));
            return Task.CompletedTask;
        });
        await Test("source navigation followed by an already safe anchor never adds a duplicate source pan", async () =>
        {
            var id = Identity(); var origin = View(); var source = View(2000); var destination = View(4000);
            var session = Moved(id, origin, source); var pans = 1;
            Check(ChoiceWispCameraSession.CanReuseSourceView(source, new(2000, 0, 0), [new(1990, 0, 0), new(2010, 0, 0)]));
            if (!ChoiceWispCameraSession.CanReuseSourceView(source, new(2000, 0, 0), [new(1990, 0, 0), new(2010, 0, 0)])) pans++;
            Check(pans == 1 && session.CanUseView(source));
            session.RecordOwnedView(destination); pans++;
            Check(await session.RestoreAsync((checkpoint, _) =>
            { Check(checkpoint.OriginalView == origin && checkpoint.OwnedView == destination); pans++; return Task.FromResult(true); }, default));
            Check(pans == 3);
        });
        await Test("consecutive target choices reuse a safe view and defer origin restoration until the boundary", async () =>
        {
            var id = Identity(); var origin = View(-1000); var current = View(); var session = Moved(id, origin, current); var pans = 1;
            Check(session.Begin(id, current, true, true));
            if (!ChoiceWispCameraSession.CanReuseTarget(current, new(20, 0, 9.5f))) pans++;
            Check(pans == 1 && session.CanUseView(current));
            Check(await session.RestoreAsync((checkpoint, _) =>
            { Check(checkpoint.OriginalView == origin && checkpoint.CanRestore(id, current, true, true)); pans++; return Task.FromResult(true); }, default));
            Check(pans == 2);
        });
        await Test("cursor interruption while settling abandons ownership instead of adopting a stable camera", async () =>
        {
            var view = View(1000); var session = Moved(Identity(), View(), view);
            var parked = true; var delays = 0; var restoreCalls = 0;
            var settled = await GameSelection.WaitForStableCameraAsync(() => view, () =>
            {
                if (!parked) session.Abandon();
                return parked;
            }, default, (_, _) => { delays++; parked = false; return Task.CompletedTask; });
            Check(settled is null && delays == 1 && !session.CanUseView(view));
            Check(!await session.RestoreAsync((_, _) => { restoreCalls++; return Task.FromResult(true); }, default) && restoreCalls == 0);
        });
        Console.WriteLine($"PASS: Choice camera ({passed} pure session/geometry tests; no process access, input or wall-clock delay).");
    }

    private static void Check(bool valid)
    {
        if (!valid) throw new Exception("Choice camera fixture failed.");
    }
}
