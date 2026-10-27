using System.Numerics;

static class SelectionAreaPlanTests
{
    public static void Run()
    {
        (string Name, Action Run)[] cases = [
            ("stable narrow-first candidate order", VerifyCentralOrder),
            ("vertical coverage beyond a ground anchor", VerifyVerticalCoverage),
            ("top-edge clipping", VerifyTopClip),
            ("bottom and right clipping", VerifyBottomRightClip),
            ("narrow viewport", VerifyNarrowViewport),
            ("deduplication and drag threshold", VerifyDuplicatesAndMinimum),
            ("no overlap and invalid geometry", VerifyNoOverlap),
            ("client resize", VerifyResize),
            ("offset virtual root", VerifyRootOffset),
            ("extreme anchors", VerifyExtremeAnchors),
            ("centering at all four edges", VerifyCenteringEdges),
            ("adaptive centering in small viewports", VerifyCenteringSmallViewport),
            ("centering after resize and root offset", VerifyCenteringGeometryChanges),
            ("invalid and outside centering targets", VerifyCenteringInvalid),
            ("centred target tolerance", VerifyCenteredTargetTolerance),
            ("scoreboard and edge targets", VerifyCenteredTargetEdges),
            ("centred target after resize", VerifyCenteredTargetResize),
            ("centred target invalid geometry and root offset", VerifyCenteredTargetGeometry),
            ("moving target starts with a wider bounded drag", VerifyMovingWidth),
            ("moving target endpoints remain inside the viewport", VerifyMovingClip),
            ("fresh moving anchors replace stale coordinates", VerifyMovingFreshAnchor),
            ("invalid moving target geometry cannot generate drags", VerifyMovingInvalid),
            ("whole drag rectangles avoid the actual multiboard", VerifyMultiboardIntersection),
            ("multiboard targets require recentering", VerifyMultiboardCentering),
            ("multiboard changes invalidate captured geometry", VerifyMultiboardChanges),
            ("multiboard coordinates follow resize and root offset", VerifyMultiboardCoordinates)
        ];
        foreach (var test in cases)
        {
            try { test.Run(); }
            catch (Exception error) { throw new Exception("Selection area plan test failed: " + test.Name, error); }
        }
        Console.WriteLine($"PASS: selection area plan {cases.Length}/{cases.Length} pure cases; no game input.");
        GamePortraitSelectionTests.Run();
        GameChoiceInputTests.Run();
    }

    private static void VerifyMultiboardIntersection()
    {
        var geometry = Square(1000, 1000) with { Multiboard = new(.3f, .7f, .8f, .9f) };
        // 시작은 보드 왼쪽, 끝은 보드 아래여도 두 점 사이의 선택 사각형은 보드를 덮는다.
        Check(geometry.IsSafeCursor(650, 100) && geometry.IsSafeCursor(950, 750) &&
            !geometry.IsSafeArea((650, 100), (950, 750)), "safe endpoints do not permit a drag through the board");
        var plan = SelectionAreaPlan.Create(geometry, (695, 800), movingTarget: true);
        Check(plan.All(area => !geometry.OverlapsMultiboard(area.From, area.To)), "every generated rectangle avoids the board including its interior");
        Check(!geometry.IsSafeCursor(800, 400) && !geometry.IsSafeCursor(698, 400) &&
            geometry.IsSafeArea((300, 300), (500, 600)), "world clicks honor the board and its four-pixel padding while clear areas remain usable");
    }

    private static void VerifyMultiboardCentering()
    {
        var geometry = Square(1000, 1000) with { Multiboard = new(.3f, .7f, .8f, .9f) };
        Check(SelectionAreaPlan.NeedsCentering(geometry, (800, 400)), "a unit covered by the board must be centered before dragging");
        Check(SelectionAreaPlan.NeedsCentering(geometry, (695, 800)), "the larger moving-unit drag is centered if its upper span hits the board");
        Check(!SelectionAreaPlan.NeedsCentering(geometry, (400, 600)), "a clear central-left unit does not cause an extra camera movement");
    }

    private static void VerifyMultiboardChanges()
    {
        var geometry = Square(1000, 1000);
        var shown = geometry with { Multiboard = new(.3f, .7f, .8f, .9f) };
        Check(!geometry.NearlyEquals(shown) && !shown.NearlyEquals(shown with { Multiboard = new(.4f, .7f, .8f, .9f) }),
            "opening, expanding or moving the board invalidates a preplanned gesture");
        var invalid = geometry with { Multiboard = new(float.NaN, .7f, .8f, .9f) };
        Check(!invalid.IsSafeArea((300, 300), (400, 400)), "unknown board geometry cannot authorize a drag");
    }

    private static void VerifyMultiboardCoordinates()
    {
        var geometry = Square(1000, 1000) with { Multiboard = new(.3f, .7f, .8f, .9f) };
        var small = geometry with { Width = 500, Height = 500 };
        Check(!small.IsSafeArea((325, 50), (475, 375)) && small.IsSafeCursor(200, 250), "board hit testing follows client resize");
        var offset = geometry with { Root = new(10, 20, 11, 21), Viewport = new(10, 20, 11, 21),
            Multiboard = new(10.3f, 20.7f, 10.8f, 20.9f) };
        Check(!offset.IsSafeArea((650, 100), (950, 750)) && offset.IsSafeCursor(400, 500), "virtual root offsets do not move the blocker to the wrong pixel region");
    }

    private static void VerifyCentralOrder()
    {
        var geometry = Standard();
        var plan = SelectionAreaPlan.Create(geometry, (1280, 588));
        string[] expected = ["square6", "square10", "x16-up64-down20", "x16-up128-down20",
            "x16-up200-down24", "x32-up128-down24", "x32-up200-down24", "x64-up200-down24"];
        Check(plan.Select(item => item.Tag).SequenceEqual(expected), "candidate order must remain bounded and narrow-first");
        Check(plan[0].From == (1274, 582) && plan[0].To == (1286, 594), "first square uses radius six");
        Check(plan[2].From == (1264, 524) && plan[2].To == (1296, 608), "first vertical candidate uses the requested asymmetric span");
        Check(plan[^1].From == (1216, 388) && plan[^1].To == (1344, 612), "last candidate has the bounded widest span");
        Valid(geometry, plan);
    }

    private static void VerifyVerticalCoverage()
    {
        var geometry = Standard();
        var plan = SelectionAreaPlan.Create(geometry, (1280, 588));
        var upperModelPoint = (X: 1280, Y: 488);
        Check(!Contains(plan[0], upperModelPoint) && !Contains(plan[1], upperModelPoint),
            "the old tiny squares exclude the upper target position");
        Check(Contains(plan[3], upperModelPoint), "a narrow tall rectangle covers the omitted target position");
        Check(plan[3].To.X - plan[3].From.X == 32, "vertical expansion does not first capture distant columns");
    }

    private static void VerifyTopClip()
    {
        var geometry = Standard();
        var plan = SelectionAreaPlan.Create(geometry, (1269, 209));
        var tall = plan.Single(item => item.Tag == "x16-up200-down24");
        Check(tall.From.Y > 9 && tall.From.Y < 100, "a 200-pixel upward span is clipped to the actual safe viewport");
        Check(tall.To.Y == 233 && tall.From.X == 1253 && tall.To.X == 1285,
            "top clipping preserves the requested horizontal span and available lower endpoint");
        Valid(geometry, plan);
    }

    private static void VerifyBottomRightClip()
    {
        var geometry = Standard();
        var plan = SelectionAreaPlan.Create(geometry, (2540, 1109));
        Check(plan.Length > 0, "partially overlapping gestures remain available near the lower right");
        Check(plan.All(item => item.To.X <= 2544 && item.To.Y < 1133), "right and bottom endpoints are clipped");
        Check(plan.Any(item => item.To.X == 2544), "the actual right safety boundary is used");
        Valid(geometry, plan);
    }

    private static void VerifyNarrowViewport()
    {
        var geometry = Standard() with { Viewport = new(.2f, .39f, .4f, .41f) };
        var plan = SelectionAreaPlan.Create(geometry, (1280, 720));
        Check(plan.Length > 0 && plan.All(item => item.To.X - item.From.X <= 32),
            "a narrow viewport limits the horizontal drag rather than discarding all tall candidates");
        Valid(geometry, plan);
    }

    private static void VerifyDuplicatesAndMinimum()
    {
        var geometry = Square(40, 40);
        var plan = SelectionAreaPlan.Create(geometry, (20, 20));
        Check(plan.Length == 1 && plan[0].Tag == "square6", "equal clipped rectangles retain only the first candidate");
        Check(plan[0].From == (16, 16) && plan[0].To == (24, 24), "an exact eight-pixel drag remains valid");
        Check(SelectionAreaPlan.Create(Square(39, 40), (20, 20)).Length == 0 &&
            SelectionAreaPlan.Create(Square(40, 39), (20, 20)).Length == 0,
            "either dimension below the drag threshold is rejected");
        Valid(geometry, plan);
    }

    private static void VerifyNoOverlap()
    {
        var geometry = Standard();
        Check(SelectionAreaPlan.Create(geometry, (-1000, -1000)).Length == 0,
            "a rectangle wholly outside the viewport is not moved into it");
        Check(SelectionAreaPlan.Create(geometry with { Viewport = new(.2f, 1.1f, .4f, 1.2f) }, (1280, 588)).Length == 0,
            "a viewport outside its virtual root is rejected");
        Check(SelectionAreaPlan.Create(geometry with { Root = new(0, 1, 1, 0) }, (1280, 588)).Length == 0 &&
            SelectionAreaPlan.Create(geometry with { Root = new(float.NaN, 0, 1, 1) }, (1280, 588)).Length == 0 &&
            SelectionAreaPlan.Create(geometry with { Width = 0 }, (1280, 588)).Length == 0,
            "degenerate, non-finite and empty client geometry cannot generate gestures");
    }

    private static void VerifyResize()
    {
        var large = Standard();
        var small = large with { Width = 1280, Height = 720 };
        var largePlan = SelectionAreaPlan.Create(large, (1280, 588));
        var smallPlan = SelectionAreaPlan.Create(small, (640, 294));
        Check(largePlan.Select(item => item.Tag).SequenceEqual(smallPlan.Select(item => item.Tag)),
            "a resized central viewport preserves candidate priority");
        Check(smallPlan[0].From == (634, 288) && smallPlan[0].To == (646, 300),
            "drag thresholds stay in client pixels after resizing");
        Check(smallPlan[^1].To.X - smallPlan[^1].From.X == 128,
            "the fixed pixel limit does not incorrectly scale with the old resolution");
        Valid(large, largePlan);
        Valid(small, smallPlan);
    }

    private static void VerifyRootOffset()
    {
        var normalized = Square(800, 600) with { Root = new(0, 0, 6, 8), Viewport = new(1, 1, 5, 7) };
        var offset = normalized with { Root = new(10, 20, 16, 28), Viewport = new(11, 21, 15, 27) };
        var first = SelectionAreaPlan.Create(normalized, (400, 300));
        var second = SelectionAreaPlan.Create(offset, (400, 300));
        Check(first.Length > 0 && first.SequenceEqual(second), "virtual root translation preserves pixel rectangles");
        Valid(offset, second);
    }

    private static void VerifyExtremeAnchors()
    {
        var geometry = Standard();
        (int X, int Y)[] anchors = [(int.MinValue, int.MinValue), (int.MaxValue, int.MaxValue),
            (int.MinValue, 588), (1280, int.MaxValue)];
        Check(anchors.All(anchor => SelectionAreaPlan.Create(geometry, anchor).Length == 0),
            "out-of-range anchors cannot wrap into the viewport through integer overflow");
    }

    private static void VerifyCenteringEdges()
    {
        var geometry = Standard();
        (int X, int Y)[] edges = [(17, 588), (2543, 588), (1280, 66), (1280, 1111)];
        Check(edges.All(anchor => SelectionAreaPlan.NeedsCentering(geometry, anchor)),
            "insufficient clearance on any edge requires centering");
        Check(!SelectionAreaPlan.NeedsCentering(geometry, (1280, 588)), "a central unit has enough room on every edge");
        Check(!SelectionAreaPlan.NeedsCentering(geometry, (160, 588)) &&
            SelectionAreaPlan.NeedsCentering(geometry, (159, 588)), "exactly 144 pixels of clearance is sufficient");
        Check(SelectionAreaPlan.NeedsCentering(geometry, (2316, 1083)) &&
            SelectionAreaPlan.NeedsCentering(geometry, (2376, 1102)),
            "the observed lower-right ground anchor and model centre require recentering");
    }

    private static void VerifyCenteringSmallViewport()
    {
        var geometry = Square(100, 100);
        Check(!SelectionAreaPlan.NeedsCentering(geometry, (50, 50)), "a small viewport centre must not trigger an endless recenter");
        Check(!SelectionAreaPlan.NeedsCentering(geometry, (33, 50)) &&
            SelectionAreaPlan.NeedsCentering(geometry, (32, 50)), "small-view clearance is one quarter of the safe width");
        Check(!SelectionAreaPlan.NeedsCentering(Square(40, 40), (20, 20)) &&
            SelectionAreaPlan.NeedsCentering(Square(40, 40), (17, 20)), "the minimum usable drag viewport still accepts its centre");
        var wideShort = Square(400, 80);
        Check(!SelectionAreaPlan.NeedsCentering(wideShort, (116, 40)) &&
            SelectionAreaPlan.NeedsCentering(wideShort, (200, 27)), "horizontal and vertical adaptive margins are independent");
    }

    private static void VerifyCenteringGeometryChanges()
    {
        var large = Standard();
        var small = large with { Width = 1280, Height = 720 };
        Check(!SelectionAreaPlan.NeedsCentering(large, (1280, 588)) &&
            !SelectionAreaPlan.NeedsCentering(small, (640, 294)) &&
            SelectionAreaPlan.NeedsCentering(small, (1200, 294)), "resizing updates safe edges while preserving the centre");
        var normalized = Square(800, 600) with { Root = new(0, 0, 6, 8), Viewport = new(1, 1, 5, 7) };
        var offset = normalized with { Root = new(10, 20, 16, 28), Viewport = new(11, 21, 15, 27) };
        (int X, int Y)[] anchors = [(400, 300), (117, 300), (400, 118), (683, 300), (400, 483)];
        Check(anchors.All(anchor => SelectionAreaPlan.NeedsCentering(normalized, anchor) == SelectionAreaPlan.NeedsCentering(offset, anchor)),
            "centering uses normalized viewport coordinates instead of a fixed virtual root origin");
        Check(!SelectionAreaPlan.NeedsCentering(offset, (400, 300)) &&
            SelectionAreaPlan.NeedsCentering(offset, (117, 300)), "offset geometry distinguishes centre and edge");
    }

    private static void VerifyCenteringInvalid()
    {
        var geometry = Standard();
        (int X, int Y)[] outside = [(-1, 588), (1280, 0), (geometry.Width, 588), (1280, geometry.Height),
            (int.MinValue, int.MinValue), (int.MaxValue, int.MaxValue)];
        Check(outside.All(anchor => SelectionAreaPlan.NeedsCentering(geometry, anchor)), "outside targets cannot be treated as already centred");
        Check(SelectionAreaPlan.NeedsCentering(geometry with { Width = 0 }, (1280, 588)) &&
            SelectionAreaPlan.NeedsCentering(geometry with { Root = new(float.NaN, 0, 1, 1) }, (1280, 588)) &&
            SelectionAreaPlan.NeedsCentering(geometry with { Viewport = new(.2f, 1.1f, .4f, 1.2f) }, (1280, 588)) &&
            SelectionAreaPlan.NeedsCentering(null!, (1280, 588)), "invalid geometry cannot suppress recentering");
    }

    private static void VerifyCenteredTargetTolerance()
    {
        var geometry = Standard();
        var safe = geometry.SafePoint();
        Check(SelectionAreaPlan.IsCenteredTarget(geometry, safe) &&
            SelectionAreaPlan.IsCenteredTarget(geometry, (safe.X + 3, safe.Y - 3)), "centre and a few rounding pixels are accepted");
        Check(SelectionAreaPlan.IsCenteredTarget(geometry, (safe.X + 128, safe.Y + 86)) &&
            !SelectionAreaPlan.IsCenteredTarget(geometry, (safe.X + 129, safe.Y)) &&
            !SelectionAreaPlan.IsCenteredTarget(geometry, (safe.X, safe.Y + 87)), "per-axis percentage tolerance is bounded and inclusive");
        var square = Square(1000, 1000);
        Check(SelectionAreaPlan.IsCenteredTarget(square, (564, 564)) &&
            !SelectionAreaPlan.IsCenteredTarget(square, (565, 500)), "the minimum 64-pixel tolerance includes its exact boundary");
    }

    private static void VerifyCenteredTargetEdges()
    {
        var geometry = Standard();
        (int X, int Y)[] blocked = [(1920, 288), (16, 588), (2544, 588), (1280, 65), (1280, 1112),
            (int.MinValue, int.MaxValue)];
        Check(blocked.All(point => !SelectionAreaPlan.IsCenteredTarget(geometry, point)),
            "the scoreboard-covered upper right and all viewport edges are not centred targets");
        Check(!SelectionAreaPlan.IsCenteredTarget(geometry, null), "a point outside the projection cannot confirm centring");
    }

    private static void VerifyCenteredTargetResize()
    {
        var large = Standard();
        var small = large with { Width = 1280, Height = 720 };
        var largeSafe = large.SafePoint();
        var smallSafe = small.SafePoint();
        Check(SelectionAreaPlan.IsCenteredTarget(large, largeSafe) &&
            SelectionAreaPlan.IsCenteredTarget(small, smallSafe), "resizing preserves the normalized centre");
        Check(SelectionAreaPlan.IsCenteredTarget(large, (largeSafe.X + 80, largeSafe.Y + 70)) &&
            !SelectionAreaPlan.IsCenteredTarget(small, (smallSafe.X + 80, smallSafe.Y)) &&
            !SelectionAreaPlan.IsCenteredTarget(small, (smallSafe.X, smallSafe.Y + 70)),
            "resizing recomputes the percentage tolerance with its 64-pixel minimum");
    }

    private static void VerifyCenteredTargetGeometry()
    {
        var geometry = Standard();
        Check(!SelectionAreaPlan.IsCenteredTarget(null!, (1280, 588)) &&
            !SelectionAreaPlan.IsCenteredTarget(geometry with { Width = 0 }, (1280, 588)) &&
            !SelectionAreaPlan.IsCenteredTarget(geometry with { Root = new(float.NaN, 0, 1, 1) }, (1280, 588)) &&
            !SelectionAreaPlan.IsCenteredTarget(geometry with { Viewport = new(.2f, 1.1f, .4f, 1.2f) }, (1280, 588)),
            "invalid geometry cannot acknowledge a centred target");
        var offset = Square(800, 600) with { Root = new(10, 20, 16, 28), Viewport = new(11, 21, 15, 27) };
        Check(SelectionAreaPlan.IsCenteredTarget(offset, (400, 300)) &&
            !SelectionAreaPlan.IsCenteredTarget(offset, (650, 200)), "the target check uses the actual offset viewport centre");
    }

    private static void VerifyMovingWidth()
    {
        var geometry = Standard();var normal = SelectionAreaPlan.Create(geometry, (1280, 588));
        var moving = SelectionAreaPlan.Create(geometry, (1280, 588), movingTarget: true);
        Check(moving.Length == 3 && moving[0].Tag == "moving-x48-up128-down48", "moving gestures have only three candidates");
        Check(moving[0].To.X - moving[0].From.X == 96 && moving[0].To.Y - moving[0].From.Y == 176 &&
            moving[0].To.X - moving[0].From.X > normal[0].To.X - normal[0].From.X,
            "a moving ally is not first targeted by a twelve-pixel square");
        Check(moving[^1].To.X - moving[^1].From.X == 224 && moving[^1].To.Y - moving[^1].From.Y == 336,
            "widening has a finite maximum");Valid(geometry, moving);
    }
    private static void VerifyMovingClip()
    {
        var geometry = Standard();var moving = SelectionAreaPlan.Create(geometry, (2500, 90), true);
        Check(moving.Length > 0, "a partly visible moving target still has bounded safe drags");
        Valid(geometry, moving);
    }
    private static void VerifyMovingFreshAnchor()
    {
        var geometry = Standard();var before = SelectionAreaPlan.Create(geometry, (1000, 600), true);
        var fresh = SelectionAreaPlan.Create(geometry, (1080, 620), true);
        Check(fresh[0].From.X - before[0].From.X == 80 && fresh[0].From.Y - before[0].From.Y == 20,
            "the next attempt can be built at the current ally position instead of the first sampled point");
    }
    private static void VerifyMovingInvalid() => Check(
        SelectionAreaPlan.Create(Standard(), (int.MaxValue, int.MinValue), true).Length == 0 &&
        SelectionAreaPlan.Create(Standard() with { Width = 0 }, (1280, 588), true).Length == 0,
        "motion does not relax viewport or overflow guards");

    private static GameSelection.Geometry Standard() => new(new(0, 0, 1000), Vector3.Zero, Vector3.UnitY,
        1.2f, new(0, 0, .6f, .8f), new(.13f, 0, .58f, .8f), 2560, 1440);
    private static GameSelection.Geometry Square(int width, int height) => new(new(0, 0, 1000), Vector3.Zero,
        Vector3.UnitY, 1.2f, new(0, 0, 1, 1), new(0, 0, 1, 1), width, height);
    private static bool Contains(SelectionArea area, (int X, int Y) point) =>
        area.From.X <= point.X && area.To.X >= point.X && area.From.Y <= point.Y && area.To.Y >= point.Y;
    private static void Valid(GameSelection.Geometry geometry, SelectionArea[] plan)
    {
        Check(plan.Length <= 8, "candidate count is bounded");
        Check(plan.Select(item => (item.From, item.To)).Distinct().Count() == plan.Length, "clipped rectangles are unique");
        Check(plan.All(item => item.To.X - item.From.X >= 8 && item.To.Y - item.From.Y >= 8), "each gesture exceeds the drag threshold");
        Check(plan.All(item => geometry.IsSafeCursor(item.From.X, item.From.Y) && geometry.IsSafeCursor(item.To.X, item.To.Y)),
            "both endpoints remain inside the viewport safety margin");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
