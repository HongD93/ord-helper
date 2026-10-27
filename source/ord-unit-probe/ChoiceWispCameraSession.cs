using System.Numerics;

internal sealed record ChoiceWispCameraIdentity(int ProcessId, long ProcessStartedUtcTicks, IntPtr Window,
    uint ModuleBase, uint Ui, uint Players, uint Table, int LocalPlayer, string MapPath, string MapHash)
{
    internal bool Matches(ChoiceWispCameraIdentity other) => ProcessId == other.ProcessId &&
        ProcessStartedUtcTicks == other.ProcessStartedUtcTicks && Window == other.Window && ModuleBase == other.ModuleBase &&
        Ui == other.Ui && Players == other.Players && Table == other.Table && LocalPlayer == other.LocalPlayer &&
        string.Equals(MapPath, other.MapPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(MapHash, other.MapHash, StringComparison.OrdinalIgnoreCase);
}

internal sealed record ChoiceWispCameraCheckpoint(ChoiceWispCameraIdentity Identity,
    GameSelection.Geometry OriginalView, GameSelection.Geometry OwnedView)
{
    internal bool CanRestore(ChoiceWispCameraIdentity identity, GameSelection.Geometry current,
        bool contextValid, bool cursorParked) => contextValid && cursorParked && Identity.Matches(identity) &&
        OwnedView.NearlyEquals(current) && current.IsSafeCursor(current.SafePoint().X, current.SafePoint().Y);
}

// 구간의 원래 화면과 마지막 자동 화면 값만 보관한다. 프로세스·메모리 핸들이나 복원 closure는 저장하지 않는다.
internal sealed class ChoiceWispCameraSession
{
    private ChoiceWispCameraIdentity? identity;
    private GameSelection.Geometry? original, owned;
    private bool abandoned;
    internal GameSelection.Geometry? OwnedView => owned;

    internal bool Begin(ChoiceWispCameraIdentity currentIdentity, GameSelection.Geometry current,
        bool contextValid, bool cursorParked)
    {
        if (abandoned) return false;
        if (!contextValid || identity is not null && (!identity.Matches(currentIdentity) || owned is null ||
            !owned.NearlyEquals(current) || !cursorParked))
        {
            Abandon(); return false;
        }
        if (identity is null) { identity = currentIdentity; original = owned = current; }
        return true;
    }

    internal bool CanUseView(GameSelection.Geometry current) => !abandoned && owned is not null && owned.NearlyEquals(current);
    internal void RecordOwnedView(GameSelection.Geometry current) { if (!abandoned && identity is not null) owned = current; }
    internal void Abandon() => abandoned = true;

    internal async Task<bool> RestoreAsync(Func<ChoiceWispCameraCheckpoint, CancellationToken, Task<bool>> restore,
        CancellationToken cancel)
    {
        var checkpoint = !abandoned && identity is not null && original is not null && owned is not null &&
            !original.NearlyEquals(owned) ? new ChoiceWispCameraCheckpoint(identity, original, owned) : null;
        // 실패하거나 취소돼도 같은 구간을 두 번 복원하지 않는다. 다음 일반 조합 뒤에는 새 구간으로 시작할 수 있다.
        identity = null; original = owned = null; abandoned = false;
        if (checkpoint is null) return false;
        try { return await restore(checkpoint, cancel); }
        catch (Exception) { return false; }
    }

    internal static bool CanReuseTarget(GameSelection.Geometry geometry, Vector3 target)
    {
        var point = geometry.Project(target);
        var safe = geometry.SafePoint();
        return geometry.IsSafeCursor(safe.X, safe.Y) && SelectionAreaPlan.IsCenteredTarget(geometry, point) && point is { } position &&
            geometry.IsSafeArea((position.X - 8, position.Y - 8), (position.X + 8, position.Y + 8));
    }

    internal static bool CanReuseSourceView(GameSelection.Geometry geometry, Vector3 anchor, IReadOnlyList<Vector3> positions)
    {
        if (positions.Count is < 2 or > 12 || !CanReuseTarget(geometry, anchor + new Vector3(0, 0, 8))) return false;
        var projected = positions.Select(position => geometry.Project(position + new Vector3(0, 0, 8))).ToArray();
        return projected.All(point => point is not null) && geometry.IsSafeArea(
            (projected.Min(point => point!.Value.X) - 22, projected.Min(point => point!.Value.Y) - 24),
            (projected.Max(point => point!.Value.X) + 22, projected.Max(point => point!.Value.Y) + 24));
    }
}
