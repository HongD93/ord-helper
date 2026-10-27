using System.Numerics;

static class GameMinimapProjectionTests
{
    private static readonly GameSelection.MinimapProjection Actual = new(
        146712.109375, 0, -11583.4248046875,
        0, 148039.8125, -12953.7724609375);
    private static readonly GameSelection.VirtualRect Minimap = new(.006875f, .009375f, .145f, .14875f);
    private static readonly GameSelection.VirtualRect Square = new(0, 0, 1, 1);

    public static void Run()
    {
        (string Name, Action Run)[] cases = [
            ("observed choice wisp", VerifyObservedWisp),
            ("ordinary unit outside the original camera", VerifyOrdinaryUnit),
            ("camera-bounds formula excludes engine margins", VerifyOldBoundsDiffer),
            ("minimap boundary and invalid rectangles", VerifyBoundary),
            ("invalid world positions", VerifyInvalidWorld),
            ("invalid affine coefficients", VerifyInvalidCoefficients),
            ("singular and nearly singular matrices", VerifySingular),
            ("shear and reversed orientation", VerifyGenericAffine),
            ("float round-trip precision", VerifyFloatPrecision),
            ("client resizing preserves virtual position", VerifyResize)
        ];
        foreach (var test in cases)
        {
            try { test.Run(); }
            catch (Exception error) { throw new Exception("Minimap projection test failed: " + test.Name, error); }
        }
        Console.WriteLine($"PASS: minimap affine projection {cases.Length}/{cases.Length} pure cases; no game input.");
    }

    private static void VerifyObservedWisp()
    {
        var world = new Vector2(-2624, -5104);
        var point = Require(Actual.ToVirtual(world, Minimap));
        Near(point, new(.06106806618f, .05302473928f), 1e-7f);
        Check(Vector2.Distance(ToWorld(Actual, point), world) <= 1, "actual wisp must round-trip within one world unit");
    }

    private static void VerifyOrdinaryUnit()
    {
        var world = new Vector2(-3781, 4071);
        var point = Require(Actual.ToVirtual(world, Minimap));
        Near(point, new(.05318187325f, .11500131062f), 1e-7f);
        Check(point.X > Minimap.Left && point.X < Minimap.Right && point.Y > Minimap.Bottom && point.Y < Minimap.Top,
            "ordinary unit maps inside the minimap");
        Check(Vector2.Distance(ToWorld(Actual, point), world) <= 1, "ordinary unit must round-trip");
    }

    private static void VerifyOldBoundsDiffer()
    {
        var world = new Vector2(-2624, -5104);
        var actual = Require(Actual.ToVirtual(world, Minimap));
        // BZ only omits CameraMargins and the engine's square-map padding.
        var old = new Vector2(
            Minimap.Left + (world.X + 9696) / 19424 * (Minimap.Right - Minimap.Left),
            Minimap.Bottom + (world.Y + 10784) / 18144 * (Minimap.Top - Minimap.Bottom));
        Check(Vector2.Distance(actual, old) > .002f, "old bounds-only mapping must not be accepted as the observed mapping");
        Check(Vector2.Distance(ToWorld(Actual, old), world) > 100, "old mapping reaches a materially different world point");
    }

    private static void VerifyBoundary()
    {
        var center = new Vector2((Minimap.Left + Minimap.Right) / 2, (Minimap.Bottom + Minimap.Top) / 2);
        Vector2[] rejected = [
            new(Minimap.Left, center.Y), new(Minimap.Right, center.Y),
            new(center.X, Minimap.Bottom), new(center.X, Minimap.Top),
            new(Minimap.Left + .00005f, center.Y), new(Minimap.Right - .00005f, center.Y),
            new(center.X, Minimap.Bottom + .00005f), new(center.X, Minimap.Top - .00005f),
            new(Minimap.Left - .01f, center.Y), new(center.X, Minimap.Top + .01f)
        ];
        Check(rejected.All(point => Actual.ToVirtual(ToWorld(Actual, point), Minimap) is null),
            "outside points and the inner safety margin must be rejected");
        Vector2[] accepted = [
            new(Minimap.Left + .0002f, center.Y), new(Minimap.Right - .0002f, center.Y),
            new(center.X, Minimap.Bottom + .0002f), new(center.X, Minimap.Top - .0002f)
        ];
        Check(accepted.All(point => Actual.ToVirtual(ToWorld(Actual, point), Minimap) is not null),
            "points beyond the safety margin remain usable");
        Check(Actual.ToVirtual(new(-2624, -5104), new(0, 1, 1, 0)) is null &&
            Actual.ToVirtual(new(-2624, -5104), new(float.NaN, 0, 1, 1)) is null,
            "invalid rectangles must not produce click coordinates");
    }

    private static void VerifyInvalidWorld()
    {
        Vector2[] worlds = [new(float.NaN, 0), new(0, float.NaN),
            new(float.PositiveInfinity, 0), new(0, float.NegativeInfinity)];
        Check(worlds.All(world => Actual.ToVirtual(world, Minimap) is null), "non-finite world positions must be rejected");
    }

    private static void VerifyInvalidCoefficients()
    {
        GameSelection.MinimapProjection[] projections = [
            Actual with { Xx = double.NaN }, Actual with { Xy = double.PositiveInfinity },
            Actual with { Tx = double.NegativeInfinity }, Actual with { Yx = double.NaN },
            Actual with { Yy = double.PositiveInfinity }, Actual with { Ty = double.NegativeInfinity }
        ];
        Check(projections.All(projection => projection.ToVirtual(new(-2624, -5104), Minimap) is null),
            "each non-finite matrix or translation component must be rejected");
    }

    private static void VerifySingular()
    {
        GameSelection.MinimapProjection[] projections = [
            new(0, 0, 0, 0, 0, 0), new(2, 4, 0, 1, 2, 0),
            new(1, 1, 0, 1, 1 + 1e-14, 0)
        ];
        Check(projections.All(projection => projection.ToVirtual(new(.2f, .3f), Square) is null),
            "singular and unstable inverse matrices must be rejected");
    }

    private static void VerifyGenericAffine()
    {
        var sheared = new GameSelection.MinimapProjection(2, .4, 7, -.3, 3, -5);
        Near(Require(sheared.ToVirtual(new(7.66f, -3.11f), Square)), new(.2f, .65f), 1e-6f);
        var reversed = new GameSelection.MinimapProjection(1000, 0, 10, 0, -2000, 20);
        Near(Require(reversed.ToVirtual(new(250, -1220), Square)), new(.24f, .62f), 1e-6f);
    }

    private static void VerifyFloatPrecision()
    {
        var projection = new GameSelection.MinimapProjection(1e14, 0, -12345678900000, 0, 1, 0);
        var floatLoss = Math.Abs(projection.Xx * (float).123456789 + projection.Tx);
        Check(floatLoss > 1 && projection.ToVirtual(new(0, .5f), Square) is null,
            "a valid double inverse must be rejected when its float click coordinate loses over one world unit");
    }

    private static void VerifyResize()
    {
        var point = Require(Actual.ToVirtual(new(-2624, -5104), Minimap));
        var large = ToClient(point, 2560, 1440);
        var small = ToClient(point, 1280, 720);
        Check(Math.Abs(large.X - small.X * 2) <= 1 && Math.Abs(large.Y - small.Y * 2) <= 1,
            "the same virtual point scales with client dimensions");
        Near(point, new(.06106806618f, .05302473928f), 1e-7f);
    }

    private static Vector2 ToWorld(GameSelection.MinimapProjection projection, Vector2 point) => new(
        (float)(projection.Xx * point.X + projection.Xy * point.Y + projection.Tx),
        (float)(projection.Yx * point.X + projection.Yy * point.Y + projection.Ty));
    private static (int X, int Y) ToClient(Vector2 point, int width, int height) =>
        ((int)Math.Round(point.X / .8 * width), (int)Math.Round((.6 - point.Y) / .6 * height));
    private static Vector2 Require(Vector2? value) => value ?? throw new Exception("Expected a valid virtual point.");
    private static void Near(Vector2 actual, Vector2 expected, float tolerance) =>
        Check(Vector2.Distance(actual, expected) <= tolerance, $"Expected {expected}; got {actual}.");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
