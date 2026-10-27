internal readonly record struct SelectionArea((int X, int Y) From, (int X, int Y) To, string Tag);

static class SelectionAreaPlan
{
    private static readonly (int HalfWidth, int Above, int Below, string Tag)[] Candidates = [
        (6, 6, 6, "square6"),
        (10, 10, 10, "square10"),
        (16, 64, 20, "x16-up64-down20"),
        (16, 128, 20, "x16-up128-down20"),
        (16, 200, 24, "x16-up200-down24"),
        (32, 128, 24, "x32-up128-down24"),
        (32, 200, 24, "x32-up200-down24"),
        (64, 200, 24, "x64-up200-down24")
    ];
    private static readonly (int HalfWidth, int Above, int Below, string Tag)[] MovingCandidates = [
        (48, 128, 48, "moving-x48-up128-down48"),
        (80, 200, 64, "moving-x80-up200-down64"),
        (112, 240, 96, "moving-x112-up240-down96")
    ];

    public static SelectionArea[] Create(GameSelection.Geometry geometry, (int X, int Y) anchor, bool movingTarget = false)
    {
        if (!TrySafeBounds(geometry, out var bounds)) return [];

        var candidates = movingTarget ? MovingCandidates : Candidates;
        var result = new List<SelectionArea>(candidates.Length);
        var seen = new HashSet<(int Left, int Top, int Right, int Bottom)>();
        foreach (var candidate in candidates)
        {
            // 클라이언트 범위 밖의 극단적인 anchor도 정수 오버플로 없이 교집합을 구한다.
            var fromX = Math.Max((long)bounds.Left, (long)anchor.X - candidate.HalfWidth);
            var fromY = Math.Max((long)bounds.Top, (long)anchor.Y - candidate.Above);
            var toX = Math.Min((long)bounds.Right, (long)anchor.X + candidate.HalfWidth);
            var toY = Math.Min((long)bounds.Bottom, (long)anchor.Y + candidate.Below);
            if (toX - fromX < 8 || toY - fromY < 8) continue;
            var from = (X: (int)fromX, Y: (int)fromY);
            var to = (X: (int)toX, Y: (int)toY);
            if (!geometry.IsSafeArea(from, to) ||
                !seen.Add((from.X, from.Y, to.X, to.Y))) continue;
            result.Add(new(from, to, candidate.Tag));
        }
        return result.ToArray();
    }

    public static bool NeedsCentering(GameSelection.Geometry geometry, (int X, int Y) anchor)
    {
        if (!TrySafeBounds(geometry, out var bounds) ||
            anchor.X < bounds.Left || anchor.X > bounds.Right || anchor.Y < bounds.Top || anchor.Y > bounds.Bottom)
            return true;
        if (geometry.OverlapsMultiboard((anchor.X - 112, anchor.Y - 240), (anchor.X + 112, anchor.Y + 96))) return true;
        var horizontal = Math.Min(144, (bounds.Right - bounds.Left) * .25);
        var vertical = Math.Min(144, (bounds.Bottom - bounds.Top) * .25);
        return anchor.X - bounds.Left < horizontal || bounds.Right - anchor.X < horizontal ||
            anchor.Y - bounds.Top < vertical || bounds.Bottom - anchor.Y < vertical;
    }

    public static bool IsCenteredTarget(GameSelection.Geometry geometry, (int X, int Y)? point)
    {
        if (point is null || !TrySafeBounds(geometry, out _) || !geometry.IsSafeCursor(point.Value.X, point.Value.Y))
            return false;
        var safe = geometry.SafePoint();
        return Math.Abs((double)point.Value.X - safe.X) <= Math.Max(64, geometry.Width * .05) &&
            Math.Abs((double)point.Value.Y - safe.Y) <= Math.Max(64, geometry.Height * .06);
    }

    private static bool TrySafeBounds(GameSelection.Geometry geometry, out (int Left, int Top, int Right, int Bottom) bounds)
    {
        bounds = default;
        if (geometry is null || geometry.Width <= 32 || geometry.Height <= 32 ||
            !Finite(geometry.Root) || !Finite(geometry.Viewport)) return false;
        var root = geometry.Root;
        var viewport = geometry.Viewport;
        var rootWidth = root.Right - root.Left;
        var rootHeight = root.Top - root.Bottom;
        if (!float.IsFinite(rootWidth) || !float.IsFinite(rootHeight) || rootWidth <= 0 || rootHeight <= 0 ||
            viewport.Left >= viewport.Right || viewport.Bottom >= viewport.Top ||
            viewport.Left < root.Left || viewport.Right > root.Right ||
            viewport.Bottom < root.Bottom || viewport.Top > root.Top) return false;

        // Geometry.IsSafeCursor와 같은 정규화 및 16픽셀 여백을 사용한다.
        var leftPixel = (viewport.Left - root.Left) / rootWidth * geometry.Width;
        var rightPixel = (viewport.Right - root.Left) / rootWidth * geometry.Width;
        var topPixel = (root.Top - viewport.Top) / rootHeight * geometry.Height;
        var bottomPixel = (root.Top - viewport.Bottom) / rootHeight * geometry.Height;
        if (!float.IsFinite(leftPixel + rightPixel + topPixel + bottomPixel)) return false;
        var left = Math.Max(16, Math.Ceiling((double)leftPixel + 16));
        var right = Math.Min(geometry.Width - 16d, Math.Floor((double)rightPixel - 16));
        var top = Math.Max(16, Math.Ceiling((double)topPixel + 16));
        var bottom = Math.Min(geometry.Height - 16d, Math.Floor((double)bottomPixel - 16));
        if (right - left < 8 || bottom - top < 8) return false;
        bounds = ((int)left, (int)top, (int)right, (int)bottom);
        return true;
    }

    private static bool Finite(GameSelection.VirtualRect rect) =>
        float.IsFinite(rect.Bottom) && float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right);
}
