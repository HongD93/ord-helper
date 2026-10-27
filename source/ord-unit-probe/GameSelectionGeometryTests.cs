using System.Numerics;

static class GameSelectionGeometryTests
{
    public static void Run()
    {
        var geometry = new GameSelection.Geometry(new(0, 0, 1000), Vector3.Zero, Vector3.UnitY,
            70 * MathF.PI / 180, new(0, 0, .6f, .8f), new(.13f, 0, .58f, .8f), 2560, 1440);
        Check(geometry.Project(Vector3.Zero) == (1280, 588), "target maps to world viewport centre");
        var smaller = geometry with { Width = 1280, Height = 720 };
        Check(smaller.Project(Vector3.Zero) == (640, 294), "client resize preserves UI coordinates");
        Check(geometry.Project(new(100, 0, 0)) is { X: > 1280, Y: 588 }, "right axis projects right");
        Check(geometry.Project(new(0, 100, 0)) is { X: 1280, Y: < 588 }, "up axis projects up");
        Check(geometry.Project(new(0, 0, 1100)) is null, "points behind camera rejected");
        Check(geometry.Project(new(10000, 0, 0)) is null, "offscreen points rejected");
        Check(geometry.Project(new(float.NaN, 0, 0)) is null, "invalid coordinates rejected");
        Check(!geometry.NearlyEquals(geometry with { Eye = new(1, 0, 1000) }), "moving camera invalidates click");
        Check(geometry.SafePoint() == (1280, 588) && smaller.SafePoint() == (640, 294), "cursor rests at world viewport centre");
        Check(geometry.IsSafeCursor(1280, 588) && geometry.IsSafeCursor(1143, 698), "ordinary cursor movement inside the world view does not cancel camera settling");
        Check(!geometry.IsSafeCursor(5, 588) && !geometry.IsSafeCursor(1280, 1420) && !geometry.IsSafeCursor(3000, 588),
            "screen edges, command panels and other monitors still interrupt automatic input");
        Check(geometry.Project(new(540, 0, 0)) is null, "screen edge targets rejected before mouse movement");
        Check(GameSelection.EquivalentCaster("h002", new(100, 200), "h002", new(100, 200)), "stacked identical materials may use the actual selected instance");
        Check(GameSelection.EquivalentCaster("h002", new(100, 200), "h002", new(104, 200)), "only tightly stacked same-type instances are equivalent");
        Check(!GameSelection.EquivalentCaster("h002", new(100, 200), "h003", new(100, 200)) &&
            !GameSelection.EquivalentCaster("h002", new(100, 200), "H002", new(100, 200)) &&
            !GameSelection.EquivalentCaster("h002", new(100, 200), "h002", new(109, 200)) &&
            !GameSelection.EquivalentCaster("h002", new(100, 200), "h002", new(float.NaN, 200)),
            "other types, case, distant instances and invalid positions are rejected");
        var session = new Roots(0x10000, 0x20000, 0x30000, 0x40000, 250, 0);
        Check(GameSelection.SameSession(session, session with { Count = 280, Array = 0x50000 }), "world spawns and list reallocation do not imply camera movement");
        Check(!GameSelection.SameSession(session, session with { LocalPlayer = 1 }) &&
            !GameSelection.SameSession(session, session with { Ui = 0x60000 }) &&
            !GameSelection.SameSession(session, session with { Table = 0x70000 }) &&
            !GameSelection.SameSession(session, session with { Players = 0x80000 }) &&
            !GameSelection.SameSession(session, null), "session changes still abort input");
        VerifyCameraSettling(geometry).GetAwaiter().GetResult();
        Console.WriteLine("PASS: camera projection, safe cursor point, viewport resize, edge clipping, world spawn tolerance, session identity and camera settling.");
    }
    private static async Task VerifyCameraSettling(GameSelection.Geometry geometry)
    {
        var moving = geometry with { Eye = geometry.Eye + Vector3.UnitX };
        var frames = new Queue<GameSelection.Geometry>([geometry, moving, geometry, geometry, geometry]);
        var delays = 0;
        var settled = await GameSelection.WaitForStableCameraAsync(() => frames.Dequeue(), () => true,
            CancellationToken.None, (_, _) => { delays++; return Task.CompletedTask; });
        Check(settled == geometry && delays == 4, "waits for consecutive stable frames after movement");
        var reads = 0;
        var unstable = await GameSelection.WaitForStableCameraAsync(() => geometry with { Eye = geometry.Eye + new Vector3(++reads, 0, 0) },
            () => true, CancellationToken.None, (_, _) => Task.CompletedTask);
        Check(unstable is null && reads == 12, "continuous camera movement has bounded wait");
        var invalid = await GameSelection.WaitForStableCameraAsync(() => throw new Exception("Must not read invalid session."),
            () => false, CancellationToken.None, (_, _) => Task.CompletedTask);
        Check(invalid is null, "invalidated session aborts before camera read");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await GameSelection.WaitForStableCameraAsync(() => geometry, () => true, canceled.Token, (_, _) => Task.CompletedTask);
            throw new Exception("Camera wait ignored cancellation.");
        }
        catch (OperationCanceledException) { }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Selection geometry test failed: " + message);
    }
}
