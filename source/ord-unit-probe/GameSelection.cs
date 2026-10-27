using System.Numerics;
using System.Runtime.InteropServices;

sealed record SelectionAttempt(uint? Address, string? Error, bool AutomaticallySelected = false);

// Warcraft III 1.28.5.7680: 카메라·유닛 위치는 외부 읽기만 하고 선택은 정상 창 입력으로 수행한다.
static class GameSelection
{
    internal static bool SameSession(Roots expected, Roots? actual) => actual is not null &&
        expected.Ui == actual.Ui && expected.Players == actual.Players && expected.Table == actual.Table && expected.LocalPlayer == actual.LocalPlayer;

    internal static bool EquivalentCaster(string expectedRawcode, Vector2 expectedPosition, string actualRawcode, Vector2 actualPosition) =>
        expectedRawcode == actualRawcode && float.IsFinite(expectedPosition.X + expectedPosition.Y + actualPosition.X + actualPosition.Y) &&
        Vector2.Distance(expectedPosition, actualPosition) <= 8;

    internal static bool CurrentOwnedUnit(MemoryReader memory, Roots roots, uint address, string rawcode)
    {
        var body = memory.Read(address + 0x20, 0x40);
        return BitConverter.ToUInt32(body, 0x38) == roots.LocalPlayer &&
            MemoryReader.DecodeRawcode(body.AsSpan(0x10, 4)) == rawcode &&
            UnitRules.IsActive(new(address, rawcode, BitConverter.ToUInt32(body), BitConverter.ToUInt32(body, 0x3C)));
    }

    internal static bool IsCurrentCaster(MemoryReader memory, Roots roots, uint? selected, UnitObject[] casters) =>
        selected is not null && casters.Any(unit => unit.Address == selected && CurrentOwnedUnit(memory, roots, unit.Address, unit.Rawcode));

    internal static uint? ReadReusableCaster(MemoryReader memory, Roots roots, UnitObject[] casters)
    {
        var selected = ReadSingleSelectedAddress(memory, roots);
        return IsCurrentCaster(memory, roots, selected, casters) ? selected : null;
    }

    internal static async Task<Geometry?> WaitForStableCameraAsync(Func<Geometry> read, Func<bool> valid,
        CancellationToken cancel, Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        Geometry? previous = null;
        var stableFrames = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            if (!valid()) return null;
            var frame = read();
            stableFrames = previous is not null && previous.NearlyEquals(frame) ? stableFrames + 1 : 0;
            if (stableFrames >= 2) return frame;
            previous = frame;
            await delay(75, cancel);
        }
        return null;
    }

    public static uint? ReadSingleSelectedAddress(MemoryReader memory, Roots roots)
    {
        var player = memory.U32(roots.Players + 0x58 + checked((uint)roots.LocalPlayer * 4));
        var selection = memory.U32(player + 0x34);
        if (memory.U32(selection + 0x1F8) != 1) return null;
        var node = memory.U32(selection + 0x1F0);
        var unit = memory.U32(node + 8);
        if (memory.U32(selection + 0x1E0) != unit) return null;
        var body = memory.Read(unit + 0x20, 0x40);
        var rawcode = MemoryReader.DecodeRawcode(body.AsSpan(0x10, 4));
        if (rawcode is null || BitConverter.ToUInt32(body, 0x38) != roots.LocalPlayer ||
            !UnitRules.IsActive(new UnitObject(unit, rawcode, BitConverter.ToUInt32(body), BitConverter.ToUInt32(body, 0x3C)))) return null;
        return memory.U32(selection + 0x1F8) == 1 && memory.U32(selection + 0x1F0) == node &&
            memory.U32(node + 8) == unit && memory.U32(selection + 0x1E0) == unit &&
            memory.U32(unit + 0x58) == roots.LocalPlayer ? unit : null;
    }

    internal static uint[] ReadSelectedAddresses(MemoryReader memory, Roots roots)
    {
        var selected = ReadSelectedGroupAddresses(memory, roots);
        foreach (var unit in selected)
        {
            var code = MemoryReader.DecodeRawcode(memory.Read(unit + 0x30, 4));
            if (code is null || !CurrentOwnedUnit(memory, roots, unit, code)) return [];
        }
        return selected.SequenceEqual(ReadSelectedGroupAddresses(memory, roots)) ? selected : [];
    }

    // HUD 후보를 찾을 때만 사용한다. 명단 안의 다른 개체 상태는 주문 허가의 근거가 아니다.
    internal static uint[] ReadSelectedGroupAddresses(MemoryReader memory, Roots roots)
    {
        var player = memory.U32(roots.Players + 0x58 + checked((uint)roots.LocalPlayer * 4));
        var selection = memory.U32(player + 0x34);
        var count = memory.U32(selection + 0x1F8);
        if (count is < 1 or > 12) return [];
        var head = memory.U32(selection + 0x1F0);
        var node = head;
        var nodes = new HashSet<uint>();
        var units = new HashSet<uint>();
        var result = new List<uint>();
        for (var index = 0; index < count; index++)
        {
            if (node < 0x10000 || !nodes.Add(node)) return [];
            var unit = memory.U32(node + 8);
            if (unit < 0x10000) return [];
            if (!units.Add(unit)) return [];
            result.Add(unit);
            node = memory.U32(node);
        }
        return count == memory.U32(selection + 0x1F8) && head == memory.U32(selection + 0x1F0) ? result.ToArray() : [];
    }

    // player+34 -> selection+1E0은 활성 대표 포인터다. 이것만으로 다중 선택 주문을 허용하지 않는다.
    internal static uint? ReadCommandFocusAddress(MemoryReader memory, Roots roots)
    {
        var selected = ReadSelectedAddresses(memory, roots);
        if (selected.Length == 0) return null;
        var player = memory.U32(roots.Players + 0x58 + checked((uint)roots.LocalPlayer * 4));
        var selection = memory.U32(player + 0x34);
        var focus = memory.U32(selection + 0x1E0);
        if (!selected.Contains(focus) || memory.U32(selection + 0x1F8) != selected.Length) return null;
        return memory.U32(selection + 0x1E0) == focus && selected.SequenceEqual(ReadSelectedAddresses(memory, roots))
            ? focus : null;
    }

    private static uint? ReadGroupFocusAddress(MemoryReader memory, Roots roots)
    {
        var selected = ReadSelectedGroupAddresses(memory, roots);
        if (selected.Length == 0) return null;
        var player = memory.U32(roots.Players + 0x58 + checked((uint)roots.LocalPlayer * 4));
        var selection = memory.U32(player + 0x34);
        var focus = memory.U32(selection + 0x1E0);
        return selected.Contains(focus) && memory.U32(selection + 0x1E0) == focus &&
            selected.SequenceEqual(ReadSelectedGroupAddresses(memory, roots)) ? focus : null;
    }

    public static async Task<SelectionAttempt> EnsureCasterAsync(MemoryReader memory, uint moduleBase, Roots roots,
        IntPtr window, CraftCommand command, UnitObject[] owned, CancellationToken cancel, bool forceSelection = false)
    {
        var casters = owned.Where(u => command.CasterUnitRawcodes.Contains(u.Rawcode, StringComparer.Ordinal)).ToArray();
        if (casters.Length == 0) return new(null, "조합을 실행할 재료 유닛을 찾지 못했습니다.");
        if (forceSelection && GetForegroundWindow() != window) return new(null, "게임 창이 바뀌어 선택 검증을 중단했습니다.");
        var selected = ReadReusableCaster(memory, roots, casters);
        if (!forceSelection && selected is not null)
            return new(selected, null);
        if (GetForegroundWindow() != window)
        {
            if (forceSelection) return new(null, "게임 창이 바뀌어 선택 검증을 중단했습니다.");
            // 브라우저에서도 Windows가 정상 전면 전환을 허용하면 같은 선택 경로를 사용한다.
            SetForegroundWindow(window);
            for (var attempt = 0; attempt < 10 && GetForegroundWindow() != window; attempt++) await Task.Delay(50, cancel);
            if (GetForegroundWindow() != window)
                return new(null, "자동 선택은 전용 프로그램 창에서 실행해 주세요.");
        }
        var initialGeometry = ReadGeometry(memory, roots, window, moduleBase);
        var safePoint = initialGeometry.SafePoint();
        var safeScreen = MoveToClient(window, safePoint.X, safePoint.Y);
        var geometry = await WaitForStableCameraAsync(() => ReadGeometry(memory, roots, window, moduleBase), () =>
            GetForegroundWindow() == window && SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) &&
            CursorInViewport(window, initialGeometry) && !MouseOrModifiersHeld() &&
            memory.U32(roots.Ui + 0x1B4) == memory.U32(roots.Ui + 0x214) && memory.Read(moduleBase + 0xD04FEC, 1)[0] == 0, cancel);
        if (geometry is null) return new(null, "게임 화면이 안정되지 않아 선택을 중단했습니다.");
        // 이전 입력의 선택/대표 포인터가 화면 안정 대기 중 정리됐으면 새 클릭을 보내지 않는다.
        selected = ReadReusableCaster(memory, roots, casters);
        if (!forceSelection && selected is not null) return new(selected, null);
        if (!forceSelection && ReadSelectedGroupAddresses(memory, roots).Length > 1)
        {
            var portrait = await SelectPortraitAsync(memory, moduleBase, roots, window, geometry, casters, cancel);
            if (portrait.Error is not null || portrait.Address is not null) return portrait;
        }
        var visible = casters.Select(u => (Unit: u, Position: ReadPosition(memory, u)))
            .Select(u => (u.Unit, u.Position, Point: geometry.Project(u.Position + new Vector3(0, 0, 16))))
            .Where(u => u.Point is not null).ToArray();
        if (visible.Length == 0)
        {
            var nearest = casters.Select(unit => ReadPosition(memory, unit))
                .OrderBy(position => Vector2.Distance(new(position.X, position.Y), new(geometry.Focus.X, geometry.Focus.Y))).First();
            if (!await PanToWorldAsync(memory, moduleBase, roots, window, new(nearest.X, nearest.Y), cancel))
                return new(null, "재료 유닛이 있는 화면으로 이동하지 못했습니다.");
            geometry = ReadGeometry(memory, roots, window, moduleBase);
            visible = casters.Select(unit => (Unit: unit, Position: ReadPosition(memory, unit)))
                .Select(unit => (unit.Unit, unit.Position, Point: geometry.Project(unit.Position + new Vector3(0, 0, 16))))
                .Where(unit => unit.Point is not null).ToArray();
            if (visible.Length == 0) return new(null, "이동한 화면에서 재료 유닛을 확인하지 못했습니다.");
        }
        // 서로 겹쳐 있는 재료 중에서는 다른 개체와 거리가 큰 유닛을 먼저 선택한다.
        var targets = visible.OrderByDescending(u => owned.Where(other => other.Address != u.Unit.Address)
            .Select(other => Vector2.Distance(new(u.Position.X, u.Position.Y), ReadPosition2(memory, other)))
            .DefaultIfEmpty(float.MaxValue).Min()).ThenBy(u => u.Unit.Address).Take(SelectionRetryPolicy.MaximumWorldCandidates).ToArray();
        var worldBudget = new SelectionWorldClickBudget();
        foreach (var target in targets)
        {
            if (!ChoiceInputValid(memory, moduleBase, roots, window) || !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)))
                return new(null, "게임 화면이나 사용자 입력이 바뀌어 선택을 중단했습니다.");
            if (!CurrentOwnedUnit(memory, roots, target.Unit.Address, target.Unit.Rawcode))
                return new(null, "재료 유닛이 바뀌어 선택을 중단했습니다.");
            var initialPosition = ReadPosition(memory, target.Unit);
            var currentPoint = geometry.Project(initialPosition + new Vector3(0, 0, 16));
            // 가장자리의 큰 모델은 선택 영역이 잘릴 수 있으므로 중앙에 여유를 확보한다.
            if (currentPoint is null || SelectionAreaPlan.NeedsCentering(geometry, currentPoint.Value))
            {
                if (!await PanToWorldAsync(memory, moduleBase, roots, window, new(initialPosition.X, initialPosition.Y), cancel))
                    return new(null, "재료 유닛이 있는 화면으로 이동하지 못했습니다.");
                geometry = ReadGeometry(memory, roots, window, moduleBase);
                Console.WriteLine($"Selection view centered: {target.Unit.Rawcode}@0x{target.Unit.Address:X8}.");
            }
            var freshGeometry = ReadGeometry(memory, roots, window, moduleBase);
            var freshPosition = ReadPosition(memory, target.Unit);
            if (!SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)))
                return new(null, "게임 연결이 바뀌어 선택을 중단했습니다.");
            if (!geometry.NearlyEquals(freshGeometry))
                return new(null, "카메라가 움직여 선택을 중단했습니다. 화면이 멈춘 뒤 다시 눌러 주세요.");
            var moving = Vector3.Distance(initialPosition, freshPosition) > 2;
            // 같은 종류가 여러 개여도 월드 헛클릭보다 영역 선택→초상화 단독 선택을 먼저 시도한다.
            var picked = await SelectionRetryPolicy.AreaThenWorldAsync(
                () => SelectAreaAsync(memory, moduleBase, roots, window, geometry, target.Unit, freshPosition, moving, casters, cancel, allowReuse: !forceSelection),
                () => SelectWorldAsync(memory, moduleBase, roots, window, geometry, target.Unit, casters, selected, worldBudget, cancel, allowReuse: !forceSelection));
            if (picked.Error is not null || picked.Address is not null) return picked;
            Console.WriteLine($"Selection candidate exhausted: {target.Unit.Rawcode}@0x{target.Unit.Address:X8}.");
        }
        return new(null, "재료 유닛 선택을 확인하지 못해 조합하지 않았습니다.");
    }

    private static async Task<SelectionAttempt> SelectWorldAsync(MemoryReader memory, uint moduleBase, Roots roots,
        IntPtr window, Geometry geometry, UnitObject target, UnitObject[] casters,
        uint? previousSelected, SelectionWorldClickBudget budget, CancellationToken cancel, bool allowReuse = true)
    {
        var safePoint = geometry.SafePoint();
        // HUD에서 단독 선택하지 못한 경우에만 두 높이의 월드 선택을 예비 경로로 사용한다.
        foreach (var height in SelectionRetryPolicy.WorldFallbackHeights)
            {
                var alreadySelected = allowReuse ? ReadReusableCaster(memory, roots, casters) : null;
                if (alreadySelected is not null) return new(alreadySelected, null, true);
                if (allowReuse && ReadSelectedGroupAddresses(memory, roots).Length > 1)
                {
                    var portrait = await SelectPortraitAsync(memory, moduleBase, roots, window, geometry, casters, cancel);
                    if (portrait.Error is not null || portrait.Address is not null) return portrait;
                }
                if (budget.Remaining == 0) break;
                if (!CurrentOwnedUnit(memory, roots, target.Address, target.Rawcode)) return new(null, "재료 유닛이 바뀌어 선택을 중단했습니다.");
                var candidatePoint = geometry.Project(ReadPosition(memory, target) + new Vector3(0, 0, height));
                if (candidatePoint is null) continue;
                var point = candidatePoint.Value;
                if (!geometry.IsSafeCursor(point.X, point.Y)) continue;
                cancel.ThrowIfCancellationRequested();
                var expectedPlayer = memory.U32(roots.Players + 0x58 + checked((uint)roots.LocalPlayer * 4));
                if (memory.U32(roots.Ui + 0x1B4) != memory.U32(roots.Ui + 0x214) || memory.Read(moduleBase + 0xD04FEC, 1)[0] != 0 ||
                    memory.U32(target.Address + 0x58) != roots.LocalPlayer || expectedPlayer < 0x10000)
                    return new(null, "게임 화면 상태가 바뀌어 선택을 중단했습니다.");
                var screen = MoveToClient(window, point.X, point.Y);
                await Task.Delay(60, cancel);
                if (GetForegroundWindow() != window || !GetCursorPos(out var cursor) ||
                    Math.Abs(cursor.X - screen.X) > 3 || Math.Abs(cursor.Y - screen.Y) > 3 ||
                    GetAncestor(WindowFromPoint(screen), 2) != window ||
                    !SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) ||
                    !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)))
                    return new(null, "화면이나 마우스 위치가 바뀌어 선택을 중단했습니다.");
                if (!SelectionRetryPolicy.WorldPointStillCurrent(point,
                    geometry.Project(ReadPosition(memory, target) + new Vector3(0, 0, height)))) continue;
                if (!budget.TryUse()) break;
                await ClickAsync(cancel);
                if (GetForegroundWindow() != window || !CursorInViewport(window, geometry) || MouseOrModifiersHeld())
                    return new(null, "마우스 조작이 감지되어 조합을 중단했습니다.");
                // 선택 확인을 기다리는 동안에도 커서를 맵 가장자리에 남겨두지 않는다.
                MoveToClient(window, safePoint.X, safePoint.Y);
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    await Task.Delay(25, cancel);
                    if (GetForegroundWindow() != window || !SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0))) break;
                    var actualSelection = ReadSingleSelectedAddress(memory, roots);
                    // 맵이 허용하는 보유 시전자라면 어느 개체든 같은 조합을 실행할 수 있다.
                    // 실제 선택 주소와 현재 종류·소유자·생존 상태는 두 번 확인한다.
                    if (!IsCurrentCaster(memory, roots, actualSelection, casters)) continue;
                    await Task.Delay(30, cancel);
                    if (ReadSingleSelectedAddress(memory, roots) == actualSelection && IsCurrentCaster(memory, roots, actualSelection, casters))
                    {
                        Console.WriteLine($"Auto-selection verified: {target.Rawcode}, selected=0x{actualSelection.GetValueOrDefault():X8}, projected=0x{target.Address:X8}, previous=0x{previousSelected.GetValueOrDefault():X8}, client=({point.X},{point.Y}).");
                        return new(actualSelection, null, true);
                    }
                    break;
                }
                var missed = ReadSingleSelectedAddress(memory, roots);
                var missedType = missed is null ? "none" : MemoryReader.DecodeRawcode(memory.Read(missed.Value + 0x30, 4));
                Console.WriteLine($"Selection point missed: wanted={target.Rawcode}@0x{target.Address:X8}, selected={missedType}@0x{missed.GetValueOrDefault():X8}, height={height}, client=({point.X},{point.Y}).");
                if (!ChoiceInputValid(memory, moduleBase, roots, window) || !CursorInViewport(window, geometry))
                    return new(null, "게임 화면이나 사용자 입력이 바뀌어 선택을 중단했습니다.");
            }
        return new(null, null);
    }

    private static async Task<SelectionAttempt> SelectAreaAsync(MemoryReader memory, uint moduleBase, Roots roots,
        IntPtr window, Geometry geometry, UnitObject target, Vector3 previousPosition, bool moving,
        UnitObject[] casters, CancellationToken cancel, bool allowReuse = true)
    {
        var candidateIndex = 0;
        for (var totalAttempts = 0; totalAttempts < 8; totalAttempts++)
        {
            var existing = allowReuse ? ReadReusableCaster(memory, roots, casters) : null;
            if (existing is not null) return new(existing, null, true);
            if (allowReuse && ReadSelectedGroupAddresses(memory, roots).Length > 1)
            {
                var portrait = await SelectPortraitAsync(memory, moduleBase, roots, window, geometry, casters, cancel);
                if (portrait.Error is not null || portrait.Address is not null) return portrait;
            }
            if (!CurrentOwnedUnit(memory, roots, target.Address, target.Rawcode)) return new(null, "재료 유닛이 바뀌어 영역 선택을 중단했습니다.");
            var freshPosition = ReadPosition(memory, target);
            if (!moving && Vector3.Distance(previousPosition, freshPosition) > 2) { moving = true; candidateIndex = 0; }
            previousPosition = freshPosition;
            var anchor = geometry.Project(freshPosition + new Vector3(0, 0, 8));
            if (anchor is null) return new(null, null);
            var areas = SelectionAreaPlan.Create(geometry, anchor.Value, moving);
            if (candidateIndex >= areas.Length) break;
            var area = areas[candidateIndex++];
            var from = area.From;
            var to = area.To;
            if (!geometry.IsSafeArea(from, to) || !ChoiceInputValid(memory, moduleBase, roots, window) || !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)))
                return new(null, "게임 화면이나 사용자 입력이 바뀌어 영역 선택을 중단했습니다.");
            var startScreen = MoveToClient(window, from.X, from.Y);
            await Task.Delay(60, cancel);
            if (!CursorAtClient(window, from) || !ChoiceInputValid(memory, moduleBase, roots, window) ||
                GetAncestor(WindowFromPoint(startScreen), 2) != window || !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)))
                return new(null, "마우스 조작이 감지되어 영역 선택을 중단했습니다.");
            SendMouse([new() { Data = new() { Flags = 2 } }]);
            var previous = from;
            try
            {
                await Task.Delay(80, cancel);
                for (var step = 1; step <= 4; step++)
                {
                    if (GetForegroundWindow() != window || MouseOrModifiersHeld(allowLeft: true) || !CursorAtClient(window, previous) ||
                        !SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) ||
                        !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)))
                        return new(null, "사용자 입력이 감지되어 영역 선택을 중단했습니다.");
                    var point = (X: from.X + (to.X - from.X) * step / 4, Y: from.Y + (to.Y - from.Y) * step / 4);
                    MoveToClient(window, point.X, point.Y, allowLeft: true);
                    previous = point;
                    await Task.Delay(30, cancel);
                }
            }
            finally { SendMouse([new() { Data = new() { Flags = 4 } }]); }
            await Task.Delay(100, cancel);
            for (var attempt = 0; attempt < 20 && memory.U32(roots.Ui + 0x1B4) != memory.U32(roots.Ui + 0x214); attempt++)
            {
                if (GetForegroundWindow() != window || MouseOrModifiersHeld() ||
                    !SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0))) break;
                await Task.Delay(25, cancel);
            }
            if (GetForegroundWindow() != window || MouseOrModifiersHeld() || !CursorInViewport(window, geometry))
                return new(null, "게임 화면이나 사용자 입력이 바뀌어 영역 선택을 중단했습니다.");
            var safe = geometry.SafePoint();
            MoveToClient(window, safe.X, safe.Y);
            if (ReadSelectedGroupAddresses(memory, roots).Length > 1)
            {
                var portrait = await SelectPortraitAsync(memory, moduleBase, roots, window, geometry, casters, cancel);
                if (portrait.Error is not null || portrait.Address is not null) return portrait;
            }
            for (var attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(30, cancel);
                if (!ChoiceInputValid(memory, moduleBase, roots, window)) return new(null, "게임 화면이 바뀌어 영역 선택을 중단했습니다.");
                var actual = ReadSingleSelectedAddress(memory, roots);
                if (!IsCurrentCaster(memory, roots, actual, casters)) continue;
                await Task.Delay(30, cancel);
                if (ReadSingleSelectedAddress(memory, roots) != actual || !IsCurrentCaster(memory, roots, actual, casters)) continue;
                Console.WriteLine($"Area selection verified: selected=0x{actual.GetValueOrDefault():X8}, area={area.Tag}, client=({anchor.Value.X},{anchor.Value.Y}).");
                return new(actual, null, true);
            }
            var player = memory.U32(roots.Players + 0x58 + checked((uint)roots.LocalPlayer * 4));
            var selection = memory.U32(player + 0x34);
            Console.WriteLine($"Area selection unconfirmed: count={memory.U32(selection + 0x1F8)}, area={area.Tag}, client=({anchor.Value.X},{anchor.Value.Y}).");
        }
        return new(null, null);
    }

    private sealed record Portrait(uint Button, uint Child, uint Unit, VirtualRect Rect);

    internal static uint? ReadPortraitCaster(MemoryReader memory, uint moduleBase, Roots roots, Geometry geometry, UnitObject[] casters,
        uint? requiredUnit = null) =>
        FindPortrait(memory, moduleBase, roots, geometry, casters, ReadSelectedGroupAddresses(memory, roots), requiredUnit)?.Unit;

    private static Portrait? FindPortrait(MemoryReader memory, uint moduleBase, Roots roots, Geometry geometry, UnitObject[] casters,
        uint[] selected, uint? requiredUnit = null)
    {
        if (selected.Length is < 2 or > 12 || requiredUnit is not null && !selected.Contains(requiredUnit.Value)) return null;
        var bar = memory.U32(roots.Ui + 0x3C4);
        var group = memory.U32(bar + 0x13C);
        if (memory.U32(bar) != moduleBase + 0xAAFC58 || memory.U32(group) != moduleBase + 0xAAFB44) return null;
        var grid = memory.U32(group + 0x130);
        var rows = memory.U32(grid + 0x154);
        Portrait? found = null;
        for (var index = 0; index < 12; index++)
        {
            try
            {
                var row = memory.U32(rows + checked((uint)(index / 6 * 16 + 8)));
                if (row < 0x10000) continue;
                var button = memory.U32(row + checked((uint)(index % 6 * 4)));
                if (button < 0x10000 || memory.U32(button) != moduleBase + 0xAAFAD4 ||
                    memory.U32(button + 0x90) != 1 || memory.U32(button + 0x94) != 1) continue;
                var child = memory.U32(button + 0x134);
                var unit = memory.U32(button + 0x124);
                if (requiredUnit is not null && unit != requiredUnit || !selected.Contains(unit)) continue;
                var caster = casters.FirstOrDefault(candidate => candidate.Address == unit);
                if (caster is null || !CurrentOwnedUnit(memory, roots, unit, caster.Rawcode)) continue;
                var code = MemoryReader.DecodeRawcode(memory.Read(button + 0x128, 4));
                if (code != caster.Rawcode) continue;
                var rect = Rect(memory, button + 0x44);
                if (!Valid(rect) || rect.Left < geometry.Root.Left || rect.Right > geometry.Root.Right ||
                    rect.Bottom < geometry.Root.Bottom || rect.Top > geometry.Root.Top) continue;
                // +124/+128은 현재 버튼 대상이다(로컬 RVA40E954/40E95D).
                // +134 child의 +6A0은 렌더 캐시여서 현재 대상과 달라도 클릭 허가에 쓰지 않는다.
                found = new(button, child, unit, rect);
                break;
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OverflowException)
            {
                // 읽을 수 없는 무관한 슬롯은 후보로 쓰지 않는다. 목표 개체와 명단은 아래에서 재확인한다.
            }
        }
        return found is not null && selected.SequenceEqual(ReadSelectedGroupAddresses(memory, roots)) &&
            IsCurrentCaster(memory, roots, found.Unit, casters) ? found : null;
    }

    private static async Task<SelectionAttempt> SelectPortraitAsync(MemoryReader memory, uint moduleBase, Roots roots,
        IntPtr window, Geometry geometry, UnitObject[] casters, CancellationToken cancel)
    {
        var original = ReadSelectedGroupAddresses(memory, roots);
        var eligible = original.Any(address => IsCurrentCaster(memory, roots, address, casters));
        if (original.Length < 2 || !eligible) return new(null, null);
        var preferred = SelectionRetryPolicy.PreferFocusedCaster(ReadGroupFocusAddress(memory, roots),
            casters.Where(unit => CurrentOwnedUnit(memory, roots, unit.Address, unit.Rawcode)).Select(unit => unit.Address));
        var initial = FindPortrait(memory, moduleBase, roots, geometry, casters, original, preferred);
        initial ??= FindPortrait(memory, moduleBase, roots, geometry, casters, original);
        if (initial is null)
        {
            Console.WriteLine($"Portrait mapping unconfirmed: selected={original.Length}, eligible=true, focus=0x{preferred.GetValueOrDefault():X8}; world retry blocked.");
            return SelectionRetryPolicy.PortraitOutcome(new(null, null), eligible);
        }
        var plan = new PortraitSelectionPlan(original, initial.Unit);
        while (plan.ClickCount < 2)
        {
            if (!ChoiceInputValid(memory, moduleBase, roots, window)) return new(null, "게임 화면이 바뀌어 초상화 선택을 중단했습니다.");
            var selected = ReadSelectedGroupAddresses(memory, roots);
            var focus = ReadGroupFocusAddress(memory, roots);
            var action = plan.Observe(selected, focus);
            if (action == PortraitSelectionAction.Ready) return await VerifySingleAsync() ?? new(null, "선택한 유닛이 바뀌어 중단했습니다.");
            if (action != PortraitSelectionAction.Click) return new(null, "선택한 유닛 구성을 확인하지 못해 중단했습니다.");
            var portrait = FindPortrait(memory, moduleBase, roots, geometry, casters, selected, plan.Target);
            if (portrait is null) return SelectionRetryPolicy.PortraitOutcome(new(null, null), eligible);
            var x = (int)Math.Round(((portrait.Rect.Left + portrait.Rect.Right) * .5 - geometry.Root.Left) / (geometry.Root.Right - geometry.Root.Left) * geometry.Width);
            var y = (int)Math.Round((geometry.Root.Top - (portrait.Rect.Bottom + portrait.Rect.Top) * .5) / (geometry.Root.Top - geometry.Root.Bottom) * geometry.Height);
            var screen = MoveToClient(window, x, y);
            await Task.Delay(60, cancel);
            var freshSelection = ReadSelectedGroupAddresses(memory, roots);
            var fresh = FindPortrait(memory, moduleBase, roots, geometry, casters, freshSelection, plan.Target);
            if (!CursorAtClient(window, (x, y)) || !ChoiceInputValid(memory, moduleBase, roots, window) || fresh != portrait ||
                plan.Observe(freshSelection, ReadGroupFocusAddress(memory, roots)) != PortraitSelectionAction.Click ||
                GetAncestor(WindowFromPoint(screen), 2) != window || !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)))
                return new(null, "초상화나 사용자 입력이 바뀌어 선택을 중단했습니다.");
            // 첫 클릭 후 subgroup/정렬이 달라져도 두 번째 클릭은 같은 개체를 대상으로 한다.
            plan.BeforeClick(portrait.Unit);
            await ClickAsync(cancel);
            if (GetForegroundWindow() != window || MouseOrModifiersHeld()) return new(null, "사용자 입력이 감지되어 초상화 선택을 중단했습니다.");
            var safe = geometry.SafePoint();
            MoveToClient(window, safe.X, safe.Y);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(40, cancel);
                if (!ChoiceInputValid(memory, moduleBase, roots, window)) return new(null, "게임 화면이 바뀌어 초상화 선택을 중단했습니다.");
                var current = ReadSelectedGroupAddresses(memory, roots);
                if (current.Length == 0) continue; // 선택 전환 중에는 클릭을 더 보내지 않고 기다린다.
                var active = ReadGroupFocusAddress(memory, roots);
                var observed = plan.Observe(current, active);
                if (observed == PortraitSelectionAction.Stale)
                    return new(null, "선택한 유닛 구성이 바뀌어 초상화 선택을 중단했습니다.");
                if (observed == PortraitSelectionAction.Ready && await VerifySingleAsync() is { } verified) return verified;
                if (observed == PortraitSelectionAction.Click && active == plan.Target) break;
            }
        }
        return SelectionRetryPolicy.PortraitOutcome(new(null, null), eligible);

        async Task<SelectionAttempt?> VerifySingleAsync()
        {
            if (ReadSingleSelectedAddress(memory, roots) != plan.Target || !IsCurrentCaster(memory, roots, plan.Target, casters)) return null;
            await Task.Delay(30, cancel);
            if (!ChoiceInputValid(memory, moduleBase, roots, window) || ReadSingleSelectedAddress(memory, roots) != plan.Target ||
                ReadCommandFocusAddress(memory, roots) != plan.Target || !IsCurrentCaster(memory, roots, plan.Target, casters)) return null;
            Console.WriteLine($"Portrait single selection verified: selected=0x{plan.Target:X8}, clicks={plan.ClickCount}.");
            return new(plan.Target, null, true);
        }
    }

    private sealed record WispGroupSelection(UnitObject[] Units, string? Error);

    private static async Task<WispGroupSelection> SelectChoiceWispGroupAsync(MemoryReader memory, uint moduleBase, Roots roots,
        IntPtr window, UnitObject[] owned, string resultRawcode, int count, CancellationToken cancel)
    {
        var wisps = owned.Where(unit => unit.Rawcode == "e018" && UnitRules.IsActive(unit)).ToArray();
        if (!ChoiceWispBatchSelectionPolicy.ValidCount(count) || wisps.Length < count) return new([], "선택 위습이 부족합니다.");
        if (count == 1)
        {
            var single = await EnsureCasterAsync(memory, moduleBase, roots, window,
                new CraftCommand("WISP", resultRawcode, 1, [new("e018", 1)], null, null, ["e018"]), owned, cancel);
            var unit = wisps.SingleOrDefault(candidate => candidate.Address == single.Address);
            return unit is not null && single.Error is null ? new([unit], null) : new([], single.Error ?? "선택한 위습을 확인하지 못했습니다.");
        }
        var selected = ReadSelectedAddresses(memory, roots);
        if (SelectedOwned(selected) is not { } initial || initial.Count(unit => unit.Rawcode == "e018") < count)
        {
            var single = await EnsureCasterAsync(memory, moduleBase, roots, window,
                new CraftCommand("WISP", resultRawcode, 1, [new("e018", 1)], null, null, ["e018"]), owned, cancel);
            if (single.Error is not null || single.Address is null) return new([], single.Error ?? "선택 위습을 선택하지 못했습니다.");
            var anchorUnit = wisps.SingleOrDefault(unit => unit.Address == single.Address);
            if (anchorUnit is null) return new([], "선택한 위습을 확인하지 못했습니다.");
            var anchor = ReadPosition(memory, anchorUnit);
            if (!await PanToWorldAsync(memory, moduleBase, roots, window, new(anchor.X, anchor.Y), cancel)) return new([], "선택 위습 위치를 확인하지 못했습니다.");
            var geometry = ReadGeometry(memory, roots, window, moduleBase);
            var nearest = wisps.Where(unit => CurrentOwnedUnit(memory, roots, unit.Address, "e018"))
                .Select(unit => (Unit: unit, Position: ReadPosition(memory, unit)))
                .OrderBy(unit => Vector3.DistanceSquared(unit.Position, anchor)).Take(count)
                .Select(unit => geometry.Project(unit.Position + new Vector3(0, 0, 8))).ToArray();
            if (nearest.Length != count || nearest.Any(point => point is null)) return new([], "필요한 선택 위습을 한 화면에서 확인하지 못했습니다.");
            var from = (X: nearest.Min(point => point!.Value.X) - 22, Y: nearest.Min(point => point!.Value.Y) - 24);
            var to = (X: nearest.Max(point => point!.Value.X) + 22, Y: nearest.Max(point => point!.Value.Y) + 24);
            if (!geometry.IsSafeArea(from, to)) return new([], "선택 위습 영역이 가려져 일괄 선택을 중단했습니다.");
            if (!await DragChoiceAreaAsync(memory, moduleBase, roots, window, geometry, from, to, cancel)) return new([], "선택 위습 영역을 확인하지 못했습니다.");
            selected = ReadSelectedAddresses(memory, roots);
        }
        for (var attempt = 0; attempt < ChoiceWispBatchSelectionPolicy.MaximumGroup; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            if (!ChoiceInputValid(memory, moduleBase, roots, window)) return new([], "게임 화면이 바뀌어 위습 선택을 중단했습니다.");
            selected = ReadSelectedAddresses(memory, roots);
            var samples = SelectedOwned(selected);
            if (samples is null || samples.Count(unit => unit.Rawcode == "e018") < count) return new([], "필요한 개수의 선택 위습이 선택되지 않았습니다.");
            if (selected.Length == count)
            {
                await Task.Delay(50, cancel);
                var fresh = ReadSelectedAddresses(memory, roots); var focus = ReadGroupFocusAddress(memory, roots);
                var current = SelectedOwned(fresh);
                if (current is not null && focus is not null && ChoiceWispBatchSelectionPolicy.Exact(fresh, samples, roots.LocalPlayer, focus) &&
                    current.All(unit => GameChoiceInput.SameWisp(samples.Single(expected => expected.Address == unit.Address), unit, roots.LocalPlayer)))
                    return new(current.Select(unit => new UnitObject(unit.Address, "e018", unit.Flags20, unit.Flags5c)).ToArray(), null);
                return new([], "위습 선택이 바뀌어 이동하지 않았습니다.");
            }
            var extra = ChoiceWispBatchSelectionPolicy.ExtraToRemove(selected, samples, roots.LocalPlayer, count);
            var geometry = ReadGeometry(memory, roots, window, moduleBase);
            var casters = samples.Select(unit => new UnitObject(unit.Address, unit.Rawcode!, unit.Flags20, unit.Flags5c)).ToArray();
            var portrait = extra is null ? null : FindPortrait(memory, moduleBase, roots, geometry, casters, selected, extra);
            if (portrait is null) return new([], "초과 선택된 위습의 초상화를 확인하지 못했습니다.");
            var point = (X: (int)Math.Round(((portrait.Rect.Left + portrait.Rect.Right) * .5 - geometry.Root.Left) / (geometry.Root.Right - geometry.Root.Left) * geometry.Width),
                Y: (int)Math.Round((geometry.Root.Top - (portrait.Rect.Top + portrait.Rect.Bottom) * .5) / (geometry.Root.Top - geometry.Root.Bottom) * geometry.Height));
            var screen = MoveToClient(window, point.X, point.Y);
            await Task.Delay(40, cancel);
            if (!ChoiceInputValid(memory, moduleBase, roots, window) || !CursorAtClient(window, point) || GetAncestor(WindowFromPoint(screen), 2) != window ||
                !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)) || !selected.SequenceEqual(ReadSelectedAddresses(memory, roots)) ||
                FindPortrait(memory, moduleBase, roots, geometry, casters, selected, extra)?.Button != portrait.Button)
                return new([], "초상화나 선택이 바뀌어 위습 선택을 중단했습니다.");
            var clicked = await ChoiceWispBatchModifier.WithShiftAsync(async () =>
            {
                await Task.Delay(40, cancel);
                if (!ShiftContext() || !selected.SequenceEqual(ReadSelectedAddresses(memory, roots)) ||
                    FindPortrait(memory, moduleBase, roots, geometry, casters, selected, extra)?.Button != portrait.Button) return false;
                SendMouse([new() { Data = new() { Flags = 2 } }]);
                try { await Task.Delay(60, cancel); if (!ShiftContext(allowLeft: true)) return false; }
                finally { ReleaseBatchMouse(); }
                await Task.Delay(60, cancel);
                return ShiftContext();
            });
            await Task.Delay(50, cancel);
            if (!clicked || !ChoiceInputValid(memory, moduleBase, roots, window) || !ChoiceWispBatchSelectionPolicy.RemovedExactlyOne(selected, ReadSelectedAddresses(memory, roots), extra!.Value))
                return new([], "초과 위습의 선택 해제를 확인하지 못했습니다.");
            var safe = geometry.SafePoint(); MoveToClient(window, safe.X, safe.Y);

            bool ShiftContext(bool allowLeft = false) => GetForegroundWindow() == window && !MouseOrModifiersHeld(allowLeft, allowOwnedShift: true) &&
                (GetAsyncKeyState(0x10) & 0x8000) != 0 && CursorAtClient(window, point) &&
                SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) && memory.Read(moduleBase + 0xD04FEC, 1)[0] == 0 &&
                memory.U32(roots.Ui + 0x1B4) == memory.U32(roots.Ui + 0x214) &&
                geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase));
        }
        return new([], "정확한 개수의 선택 위습을 선택하지 못했습니다.");

        GameChoiceInput.WispSample[]? SelectedOwned(uint[] addresses)
        {
            if (addresses.Length is < 1 or > 12 || addresses.Distinct().Count() != addresses.Length) return null;
            var samples = addresses.Select(address => GameChoiceInput.ReadWispSample(memory, address)).ToArray();
            return samples.All(sample => sample.Address >= 0x10000 && sample.Identity.Length == 24 && sample.Rawcode is not null && sample.Active && sample.Owner == roots.LocalPlayer) ? samples : null;
        }
    }

    private static async Task<bool> DragChoiceAreaAsync(MemoryReader memory, uint moduleBase, Roots roots, IntPtr window,
        Geometry geometry, (int X, int Y) from, (int X, int Y) to, CancellationToken cancel)
    {
        var screen = MoveToClient(window, from.X, from.Y); await Task.Delay(50, cancel);
        if (!ChoiceInputValid(memory, moduleBase, roots, window) || !CursorAtClient(window, from) || GetAncestor(WindowFromPoint(screen), 2) != window ||
            !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase))) return false;
        SendMouse([new() { Data = new() { Flags = 2 } }]);
        var previous = from;
        try
        {
            for (var step = 1; step <= 4; step++)
            {
                await Task.Delay(35, cancel);
                if (GetForegroundWindow() != window || MouseOrModifiersHeld(allowLeft: true) || !CursorAtClient(window, previous) ||
                    !SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) || !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase))) return false;
                previous = (from.X + (to.X - from.X) * step / 4, from.Y + (to.Y - from.Y) * step / 4);
                MoveToClient(window, previous.X, previous.Y, allowLeft: true);
            }
        }
        finally { ReleaseBatchMouse(); }
        await Task.Delay(80, cancel);
        if (!ChoiceInputValid(memory, moduleBase, roots, window) || !CursorAtClient(window, to)) return false;
        var safe = geometry.SafePoint(); MoveToClient(window, safe.X, safe.Y); await Task.Delay(50, cancel);
        return true;
    }

    private static void ReleaseBatchMouse()
    {
        try { SendMouse([new() { Data = new() { Flags = 4 } }]); }
        catch (IOException) { SendMouse([new() { Data = new() { Flags = 4 } }]); }
    }

    internal static async Task<string?> UseChoiceWispAsync(MemoryReader memory, uint moduleBase, Roots roots,
        IntPtr window, string resultRawcode, UnitObject[] owned, CancellationToken cancel, Action? consumptionVerified = null, int count = 1)
    {
        cancel.ThrowIfCancellationRequested();
        var index = Array.IndexOf(GameChoiceInput.RegionResults, resultRawcode);
        if (index < 0) return "선택 위습으로 뽑을 수 없는 유닛입니다.";
        var wisps = owned.Where(unit => unit.Rawcode == "e018").ToArray();
        if (!ChoiceWispBatchSelectionPolicy.ValidCount(count) || wisps.Length < count) return "선택 위습이 부족합니다.";
        if (GetForegroundWindow() != window || MouseOrModifiersHeld()) return "게임 창을 벗어나 선택 위습 사용을 중단했습니다.";
        var original = ReadGeometry(memory, roots, window, moduleBase);
        var panned = false;
        var moveMode = false;
        uint ownedMoveMode = 0;
        GameChoiceInput.WispSample[] beforeWisps = [];
        ChoiceWispBatchConsumptionTracker? consumptionTracker = null;
        try
        {
            // 묶음은 정확한 N개를 먼저 고정한다. 초과 선택은 HUD에서 빼고 이동은 한 번만 한다.
            panned = true;
            var selection = await SelectChoiceWispGroupAsync(memory, moduleBase, roots, window, owned, resultRawcode, count, cancel);
            if (selection.Error is not null) return selection.Error;
            var sources = selection.Units;
            if (sources.Length != count) return "정확한 개수의 위습을 확인하지 못해 이동하지 않았습니다.";
            beforeWisps = sources.Select(unit => GameChoiceInput.ReadWispSample(memory, unit.Address)).ToArray();
            if (!ExactSourceSelection()) return "선택한 위습 개체를 확인하지 못해 중단했습니다.";
            var target = new Vector3(-3904 + 320 * index, -4688, GameChoiceInput.RegionGroundZ);
            // 화면 안의 좌표도 게임 내 집계판에 가릴 수 있다. 선택 구역은 매번 화면 중앙으로 옮긴다.
            panned = true;
            if (!await PanToWorldAsync(memory, moduleBase, roots, window, new(target.X, target.Y), cancel))
                return "선택 구역을 확인하지 못했습니다.";
            var geometry = ReadGeometry(memory, roots, window, moduleBase);
            var point = geometry.Project(target);
            if (point is null || !ChoiceInputValid(memory, moduleBase, roots, window) ||
                !ExactSourceSelection()) return "선택 위습이나 게임 화면이 바뀌어 중단했습니다.";
            if (!SelectionAreaPlan.IsCenteredTarget(geometry, point))
                return "선택 구역이 화면 중앙에 오지 않아 위습 사용을 중단했습니다.";
            var readyWisps = sources.Select(unit => GameChoiceInput.ReadWispSample(memory, unit.Address)).ToArray();
            if (readyWisps.Where((unit, i) => !unit.Active || !GameChoiceInput.SameWisp(beforeWisps[i], unit, roots.LocalPlayer)).Any())
            {
                return "선택한 위습 개체가 바뀌어 중단했습니다.";
            }
            Console.WriteLine($"Choice target centered: result={resultRawcode}, count={count}, world=({target.X:F1},{target.Y:F1}), client=({point.Value.X},{point.Value.Y}), focus=({geometry.Focus.X:F1},{geometry.Focus.Y:F1}).");
            // M과 목적지 클릭은 각각 한 번만 보낸다. 표적 모드 전환은 정상 선택 모드와 구분해 관측한다.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var wait = new ChoiceWispWaitPolicy();
            consumptionTracker = new ChoiceWispBatchConsumptionTracker(beforeWisps, roots.LocalPlayer);
            var previousPositions = sources.ToDictionary(unit => unit.Address, unit => ReadPosition2(memory, unit));
            var previousDistances = previousPositions.ToDictionary(pair => pair.Key, pair => Vector2.Distance(pair.Value, new(target.X, target.Y)));
            var destinationAccepted = false;
            cancel.ThrowIfCancellationRequested();
            if (!ChoiceInputValid(memory, moduleBase, roots, window) ||
                !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)) || !ExactSourceSelection() ||
                GetForegroundWindow() != window)
                return "위습 선택이나 게임 화면이 바뀌어 이동하지 않았습니다.";
            cancel.ThrowIfCancellationRequested();
            moveMode = true;
            GameInput.PressNormalKey(0x4D);
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var frame = ReadWaitObservation(out _);
                var decision = wait.Observe(clock.Elapsed, frame);
                if (decision.Action == ChoiceWispWaitAction.MoveModeReady) break;
                if (decision.Action == ChoiceWispWaitAction.Stop) return WaitFailure(decision);
                await Task.Delay(ChoiceWispWaitPolicy.PollInterval, cancel);
            }
            var screen = MoveToClient(window, point.Value.X, point.Value.Y);
            await Task.Delay(50, cancel);
            if (GetForegroundWindow() != window || !GetCursorPos(out var cursor) ||
                Math.Abs(cursor.X - screen.X) > 3 || Math.Abs(cursor.Y - screen.Y) > 3 ||
                GetAncestor(WindowFromPoint(screen), 2) != window || !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)) ||
                !ChoiceContextValid(memory, moduleBase, roots, window) || memory.U32(roots.Ui + 0x1B4) != ownedMoveMode ||
                !ExactSourceSelection())
                return "화면이나 마우스 위치가 바뀌어 위습 이동을 중단했습니다.";
            await ClickAsync(cancel);
            if (GetForegroundWindow() != window || MouseOrModifiersHeld() || !CursorAtClient(window, point.Value) ||
                !SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) ||
                !geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase)))
                return "화면이나 마우스 위치가 바뀌어 위습 이동을 중단했습니다.";
            // 확인을 기다릴 때도 커서를 중앙에 두어 가장자리 이동을 방지하고 실패 시 복귀할 수 있게 한다.
            var safe = geometry.SafePoint();
            MoveToClient(window, safe.X, safe.Y);
            wait.MarkDestinationSent(clock.Elapsed);
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var frame = ReadWaitObservation(out var currentWisps);
                var decision = wait.Observe(clock.Elapsed, frame);
                if (decision.Action == ChoiceWispWaitAction.MoveAccepted)
                {
                    destinationAccepted = true;
                    moveMode = false;
                    Console.WriteLine($"Choice move accepted: count={count}, result={resultRawcode}.");
                }
                if (decision.Action == ChoiceWispWaitAction.ConsumptionReady)
                {
                    destinationAccepted = true;
                    moveMode = false;
                    if (!consumptionTracker.AllConfirmed) { await Task.Delay(ChoiceWispWaitPolicy.PollInterval, cancel); continue; }
                    if (!ChoiceContextValid(memory, moduleBase, roots, window) ||
                        !GameChoiceInput.VerifyBatchConsumption(beforeWisps, consumptionTracker.FirstProof, consumptionTracker.SecondProof, roots.LocalPlayer))
                        return "선택 위습의 소모 상태를 확인하지 못했습니다.";
                    consumptionVerified?.Invoke();
                    Console.WriteLine($"Choice wisp move completed: count={count}, result={resultRawcode}.");
                    return null;
                }
                if (decision.Action == ChoiceWispWaitAction.Stop) return WaitFailure(decision);
                await Task.Delay(ChoiceWispWaitPolicy.PollInterval, cancel);
            }

            ChoiceWispWaitObservation ReadWaitObservation(out GameChoiceInput.WispSample[] samples)
            {
                var context = ChoiceContextValid(memory, moduleBase, roots, window) && CursorInViewport(window, geometry) &&
                    geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase));
                var normal = memory.U32(roots.Ui + 0x214);
                var mode = memory.U32(roots.Ui + 0x1B4);
                samples = new GameChoiceInput.WispSample[sources.Length];
                var identity = true;
                for (var i = 0; i < sources.Length; i++)
                {
                    // 이미 소모를 확정한 주소는 재조회하지 않는다. 다른 위습이 도착하기 전에 재사용될 수 있다.
                    samples[i] = consumptionTracker.IsConfirmed(i) ? consumptionTracker.ConfirmedSample(i) : GameChoiceInput.ReadWispSample(memory, sources[i].Address);
                    var observation = consumptionTracker.Observe(i, clock.Elapsed, samples[i]);
                    identity &= observation is not (ChoiceWispConsumptionObservation.InvalidIdentity or ChoiceWispConsumptionObservation.InvalidClock);
                }
                var active = samples.Any(sample => sample.Active);
                var selectedOnly = !destinationAccepted && consumptionTracker.ConfirmedCount == 0 && ExactSourceSelection();
                if (ownedMoveMode == 0 && mode != normal && context && identity && active && selectedOnly)
                    ownedMoveMode = mode;
                // M이 아직 큐에 있을 때 정상 모드가 보였다는 이유로 취소 정리 책임을 버리지 않는다.
                if (!destinationAccepted && (mode != normal || wait.Stage == ChoiceWispWaitStage.AwaitMoveMode)) moveMode = true;
                else if (!destinationAccepted) moveMode = false;
                var moving = false;
                var arrived = active;
                for (var i = 0; identity && i < sources.Length; i++)
                {
                    if (!samples[i].Active) continue;
                    var unit = sources[i]; var position = ReadPosition2(memory, unit);
                    var distance = Vector2.Distance(position, new(target.X, target.Y));
                    moving |= Vector2.Distance(previousPositions[unit.Address], position) >= 1 && previousDistances[unit.Address] - distance >= .5;
                    previousPositions[unit.Address] = position;
                    previousDistances[unit.Address] = distance;
                    arrived &= Math.Abs(position.X - target.X) <= 128 && Math.Abs(position.Y - target.Y) <= 112;
                }
                return new(context, mode == normal ? ChoiceWispCommandMode.Normal :
                    ownedMoveMode != 0 && mode == ownedMoveMode ? ChoiceWispCommandMode.OwnedMoveTarget : ChoiceWispCommandMode.Foreign,
                    identity, active, selectedOnly, moving, arrived);
            }

            string WaitFailure(ChoiceWispWaitDecision decision)
            {
                Console.WriteLine($"Choice observation stopped: count={count}, result={resultRawcode}, stage={decision.Stage}, reason={decision.Reason}.");
                return decision.Reason switch
                {
                    "move_mode_timeout" => "위습 이동 명령을 확인하지 못했습니다.",
                    "move_acceptance_timeout" => "위습 이동 지점을 확인하지 못했습니다.",
                    "movement_not_observed" => "선택 위습의 이동이 확인되지 않아 중단했습니다. 게임에서 결과를 확인해 주세요.",
                    "source_identity_changed" => "선택 위습의 개체가 바뀌어 소모 여부를 확인하지 못했습니다.",
                    "consumption_timeout" or "absolute_wait_timeout" => "선택 위습의 변환을 확인하지 못했습니다. 게임에서 결과를 확인해 주세요.",
                    _ => "게임 화면이나 사용자 입력이 바뀌어 위습 결과 확인을 중단했습니다. 게임에서 결과를 확인해 주세요."
                };
            }
        }
        finally
        {
            // 입력 큐의 M이 처리되기 전에 취소돼도 본인이 시작한 표적 모드만 정리한다.
            if (moveMode && ownedMoveMode == 0 && GetForegroundWindow() == window && !MouseOrModifiersHeld() &&
                CursorAtClient(window, original.SafePoint()) && SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)))
            {
                for (var attempt = 0; attempt < 4 && memory.U32(roots.Ui + 0x1B4) == memory.U32(roots.Ui + 0x214); attempt++)
                    await Task.Delay(25, CancellationToken.None);
                ownedMoveMode = memory.U32(roots.Ui + 0x1B4);
            }
            if (moveMode && GetForegroundWindow() == window && !MouseOrModifiersHeld() &&
                SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) &&
                memory.U32(roots.Ui + 0x1B4) != memory.U32(roots.Ui + 0x214) &&
                memory.U32(roots.Ui + 0x1B4) == ownedMoveMode && ExactSourceSelection())
            {
                GameInput.PressNormalKey(0x1B);
                for (var attempt = 0; attempt < 20 && GetForegroundWindow() == window && !MouseOrModifiersHeld() &&
                    memory.U32(roots.Ui + 0x1B4) == ownedMoveMode; attempt++)
                    await Task.Delay(25, CancellationToken.None);
            }
            if (GetForegroundWindow() == window && !MouseOrModifiersHeld() && CursorAtClient(window, original.SafePoint()) &&
                SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)))
            {
                if (panned && !cancel.IsCancellationRequested)
                    await PanToWorldAsync(memory, moduleBase, roots, window, new(original.Focus.X, original.Focus.Y), CancellationToken.None);
            }
        }

        bool ExactSourceSelection()
        {
            if (beforeWisps.Length != count || consumptionTracker?.ConfirmedCount > 0) return false;
            var selected = ReadSelectedAddresses(memory, roots); var focus = ReadGroupFocusAddress(memory, roots);
            return focus is not null && ChoiceWispBatchSelectionPolicy.Exact(selected, beforeWisps, roots.LocalPlayer, focus) &&
                beforeWisps.All(before => CurrentOwnedUnit(memory, roots, before.Address, "e018") &&
                    GameChoiceInput.SameWisp(before, GameChoiceInput.ReadWispSample(memory, before.Address), roots.LocalPlayer));
        }
    }

    private static bool CursorAtClient(IntPtr window, (int X, int Y) client)
    {
        var point = new MousePoint { X = client.X, Y = client.Y };
        return ClientToScreen(window, ref point) && GetCursorPos(out var current) &&
            Math.Abs(point.X - current.X) <= 3 && Math.Abs(point.Y - current.Y) <= 3;
    }

    private static bool CursorInViewport(IntPtr window, Geometry geometry)
    {
        var origin = new MousePoint();
        return ClientToScreen(window, ref origin) && GetCursorPos(out var current) &&
            geometry.IsSafeCursor(current.X - origin.X, current.Y - origin.Y);
    }

    private static bool ChoiceContextValid(MemoryReader memory, uint moduleBase, Roots roots, IntPtr window) =>
        GetForegroundWindow() == window && !MouseOrModifiersHeld() &&
        SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0)) && memory.Read(moduleBase + 0xD04FEC, 1)[0] == 0;

    private static bool ChoiceInputValid(MemoryReader memory, uint moduleBase, Roots roots, IntPtr window) =>
        ChoiceContextValid(memory, moduleBase, roots, window) && memory.U32(roots.Ui + 0x1B4) == memory.U32(roots.Ui + 0x214);

    private static async Task ClickAsync(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        SendMouse([new() { Data = new() { Flags = 2 } }]);
        try { await Task.Delay(80, cancel); }
        finally { SendMouse([new() { Data = new() { Flags = 4 } }]); }
        for (var attempt = 0; attempt < 5 && MouseOrModifiersHeld(); attempt++) await Task.Delay(10, cancel);
        // 게임이 버튼 해제를 처리하기 전에 커서를 다른 위치로 옮기지 않는다.
        await Task.Delay(80, cancel);
    }

    private static async Task<bool> PanToWorldAsync(MemoryReader memory, uint moduleBase, Roots roots,
        IntPtr window, Vector2 world, CancellationToken cancel)
    {
        if (!ChoiceInputValid(memory, moduleBase, roots, window)) return NavigationBlocked("initial_input_state");
        var geometry = ReadGeometry(memory, roots, window, moduleBase);
        var minimap = memory.U32(roots.Ui + 0x3C0);
        if (memory.U32(minimap) != moduleBase + 0xAB0704) return NavigationBlocked("minimap_profile");
        var rect = Rect(memory, minimap + 0xB4 + 0x44);
        if (!Valid(rect) || rect.Left < geometry.Root.Left || rect.Right > geometry.Root.Right ||
            rect.Bottom < geometry.Root.Bottom || rect.Top > geometry.Root.Top) return NavigationBlocked("minimap_rect");
        // 이 빌드의 미니맵 입력 행렬을 역산한다. 카메라 제한 사각형은 여백·정사각 패딩을 포함하지 않는다.
        var matrixBytes = memory.Read(minimap + 0x72C, 36);
        var matrix = new MinimapProjection(BitConverter.ToSingle(matrixBytes), BitConverter.ToSingle(matrixBytes, 12),
            BitConverter.ToSingle(matrixBytes, 24), BitConverter.ToSingle(matrixBytes, 4),
            BitConverter.ToSingle(matrixBytes, 16), BitConverter.ToSingle(matrixBytes, 28));
        var virtualPoint = matrix.ToVirtual(world, rect);
        if (virtualPoint is null) return NavigationBlocked($"world_projection ({world.X:F1},{world.Y:F1})");
        var clientX = (int)Math.Round((virtualPoint.Value.X - geometry.Root.Left) / (geometry.Root.Right - geometry.Root.Left) * geometry.Width);
        var clientY = (int)Math.Round((geometry.Root.Top - virtualPoint.Value.Y) / (geometry.Root.Top - geometry.Root.Bottom) * geometry.Height);
        var screen = MoveToClient(window, clientX, clientY);
        await Task.Delay(50, cancel);
        var stateValid = ChoiceInputValid(memory, moduleBase, roots, window);
        var cursorRead = GetCursorPos(out var cursor);
        var underPointer = GetAncestor(WindowFromPoint(screen), 2);
        var matrixSame = matrixBytes.AsSpan().SequenceEqual(memory.Read(minimap + 0x72C, 36));
        var geometrySame = geometry.NearlyEquals(ReadGeometry(memory, roots, window, moduleBase));
        if (!stateValid || !cursorRead ||
            Math.Abs(cursor.X - screen.X) > 3 || Math.Abs(cursor.Y - screen.Y) > 3 || GetAncestor(WindowFromPoint(screen), 2) != window ||
            !matrixSame || !geometrySame)
            return NavigationBlocked($"before_click state={stateValid},cursor=({cursor.X},{cursor.Y}),expected=({screen.X},{screen.Y}),under=0x{underPointer.ToInt64():X},game=0x{window.ToInt64():X},matrix={matrixSame},geometry={geometrySame}");
        await ClickAsync(cancel);
        // 미니맵 자체의 잠깐 유지되는 드래그 모드가 해제될 때까지 추가 입력 없이 기다린다.
        for (var attempt = 0; attempt < 20 && memory.U32(roots.Ui + 0x1B4) != memory.U32(roots.Ui + 0x214); attempt++)
        {
            if (GetForegroundWindow() != window || MouseOrModifiersHeld() ||
                !SameSession(roots, memory.ReadRoots(moduleBase + 0xD326F0, moduleBase + 0xD305E0))) break;
            await Task.Delay(25, cancel);
        }
        if (!ChoiceInputValid(memory, moduleBase, roots, window))
        {
            Console.WriteLine($"Camera click interrupted: fg=0x{GetForegroundWindow().ToInt64():X}, mode=0x{memory.U32(roots.Ui + 0x1B4):X8}, normal=0x{memory.U32(roots.Ui + 0x214):X8}, held={MouseOrModifiersHeld()}.");
            return false;
        }
        var safe = geometry.SafePoint();
        var parked = MoveToClient(window, safe.X, safe.Y);
        var stable = await WaitForStableCameraAsync(() => ReadGeometry(memory, roots, window, moduleBase), () =>
            ChoiceInputValid(memory, moduleBase, roots, window) && CursorInViewport(window, geometry), cancel);
        var arrived = stable is not null && Vector2.Distance(world, new(stable.Focus.X, stable.Focus.Y)) < 900;
        Console.WriteLine($"Camera navigation {(arrived ? "verified" : "unconfirmed")}: world=({world.X:F1},{world.Y:F1}), client=({clientX},{clientY}), focus=({stable?.Focus.X:F1},{stable?.Focus.Y:F1}).");
        if (stable is null) Console.WriteLine($"Camera settling interrupted: fg=0x{GetForegroundWindow().ToInt64():X}, mode=0x{memory.U32(roots.Ui + 0x1B4):X8}, normal=0x{memory.U32(roots.Ui + 0x214):X8}, held={MouseOrModifiersHeld()}, cursorInViewport={CursorInViewport(window, geometry)}.");
        return arrived;
    }

    private static bool NavigationBlocked(string reason)
    {
        Console.WriteLine("Camera navigation blocked: " + reason);
        return false;
    }

    private static Vector2 ReadPosition2(MemoryReader memory, UnitObject unit)
    {
        var data = memory.Read(unit.Address + 0x284, 8);
        return new(BitConverter.ToSingle(data), BitConverter.ToSingle(data, 4));
    }
    private static Vector3 ReadPosition(MemoryReader memory, UnitObject unit)
    {
        var sprite = memory.U32(unit.Address + 0x28);
        var data = memory.Read(sprite + 0xC0, 12);
        var position = Vector(data);
        var xy = ReadPosition2(memory, unit);
        if (!Finite(position) || position.Length() > 100000 || Vector2.Distance(new(position.X, position.Y), xy) > 2 ||
            memory.U32(unit.Address + 0x28) != sprite) throw new IOException("Unstable unit position.");
        return position;
    }
    private static Geometry ReadGeometry(MemoryReader memory, Roots roots, IntPtr window, uint moduleBase)
    {
        if (!GetClientRect(window, out var client) || client.Right < 320 || client.Bottom < 240 ||
            client.Right > 32000 || client.Bottom > 32000) throw new IOException("Invalid game window.");
        var frame = memory.U32(roots.Ui + 0x3BC);
        if (frame != roots.Table) throw new IOException("Changed world frame.");
        var camera = memory.U32(frame + 0x140);
        var eye = Vector(memory.Read(camera + 0x3C, 12));
        var focus = Vector(memory.Read(camera + 0x64, 12));
        var fov = Float(memory, camera + 0x114);
        var near = Float(memory, camera + 0xCC);
        var far = Float(memory, camera + 0xAC);
        var upHint = new Vector3(
            Float(memory, camera + 0x144) * Float(memory, camera + 0x16C) - Float(memory, camera + 0xF4) * Float(memory, camera + 0x168) * Float(memory, camera + 0x140),
            -Float(memory, camera + 0x168) * Float(memory, camera + 0x144) - Float(memory, camera + 0xF4) * Float(memory, camera + 0x16C) * Float(memory, camera + 0x140),
            Float(memory, camera + 0xF0) * Float(memory, camera + 0x140));
        var root = Rect(memory, roots.Ui + 0xB4 + 0x44);
        var viewport = Rect(memory, frame + 0x12C);
        if (!Finite(eye) || !Finite(focus) || !Finite(upHint) || Vector3.Distance(eye, focus) < 1 ||
            upHint.Length() < .01 || fov < .05 || fov > 3 || near < .1 || far <= near || far > 100000 || !Valid(root) || !Valid(viewport) ||
            viewport.Left < root.Left - .001 || viewport.Right > root.Right + .001 ||
            viewport.Bottom < root.Bottom - .001 || viewport.Top > root.Top + .001)
            throw new IOException("Invalid camera geometry.");
        var board = ReadMultiboardRect(memory, moduleBase, roots);
        return new(eye, focus, upHint, fov, root, viewport, client.Right, client.Bottom, near, far, board);
    }

    private static VirtualRect? ReadMultiboardRect(MemoryReader memory, uint moduleBase, Roots roots)
    {
        // 1.28.5 MultiboardDisplay: RVA 3A8595의 CGameUI+3AC 현재 보드.
        // 생성자410A30: 본체 vtable AB356C, layout+B4 vtable AB3654.
        var board = memory.U32(roots.Ui + 0x3AC);
        if (board == 0) return null;
        if (board < 0x10000 || memory.U32(board) != moduleBase + 0xAB356C ||
            memory.U32(board + 0xB4) != moduleBase + 0xAB3654)
            throw new IOException("Unknown multiboard frame.");
        var visibility = memory.U32(board + 0xB0);
        // native IsShown RVA11CDF0은 flags+B0의 하위 두 비트를 검사한다.
        var rect = (visibility & 3) == 0 ? Rect(memory, board + 0xB4 + 0x44) : (VirtualRect?)null;
        if (memory.U32(roots.Ui + 0x3AC) != board || memory.U32(board + 0xB0) != visibility ||
            rect is { } shown && !Valid(shown)) throw new IOException("Changed multiboard frame.");
        return rect;
    }
    private static VirtualRect Rect(MemoryReader memory, uint address)
    {
        var values = memory.Read(address, 16);
        return new(BitConverter.ToSingle(values), BitConverter.ToSingle(values, 4), BitConverter.ToSingle(values, 8), BitConverter.ToSingle(values, 12));
    }
    private static bool Valid(VirtualRect rect) => float.IsFinite(rect.Bottom + rect.Left + rect.Top + rect.Right) &&
        rect.Top - rect.Bottom > .001 && rect.Right - rect.Left > .001 && rect.Top - rect.Bottom < 10 && rect.Right - rect.Left < 10;
    private static float Float(MemoryReader memory, uint address) => BitConverter.ToSingle(memory.Read(address, 4));
    private static Vector3 Vector(byte[] value) => new(BitConverter.ToSingle(value), BitConverter.ToSingle(value, 4), BitConverter.ToSingle(value, 8));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool MouseOrModifiersHeld(bool allowLeft = false, bool allowOwnedShift = false) => new[] { 1, 2, 4, 0x10, 0x11, 0x12, 0x5B, 0x5C }
        .Any(k => (!allowLeft || k != 1) && (!allowOwnedShift || k != 0x10) && (GetAsyncKeyState(k) & 0x8000) != 0);
    private static MousePoint MoveToClient(IntPtr window, int x, int y, bool allowLeft = false)
    {
        var screen = new MousePoint { X = x, Y = y };
        if (GetForegroundWindow() != window || MouseOrModifiersHeld(allowLeft) || !ClientToScreen(window, ref screen)) throw new IOException("Changed game window or user input.");
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (width < 2 || height < 2) throw new IOException("Invalid screen size.");
        SendMouse([new() { Data = new() { X = (int)Math.Round((screen.X - left) * 65535d / (width - 1)),
            Y = (int)Math.Round((screen.Y - top) * 65535d / (height - 1)), Flags = 0xC001 } }]);
        return screen;
    }
    private static void SendMouse(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return;
        if (inputs.Length == 2 && sent == 1) SendInput(1, [new() { Data = new() { Flags = 4 } }], Marshal.SizeOf<Input>());
        throw new IOException("Mouse input was incomplete.");
    }

    internal readonly record struct VirtualRect(float Bottom, float Left, float Top, float Right);
    internal sealed record MinimapProjection(double Xx, double Xy, double Tx, double Yx, double Yy, double Ty)
    {
        public Vector2? ToVirtual(Vector2 world, VirtualRect rect)
        {
            var determinant = Xx * Yy - Xy * Yx;
            var scale = Math.Max(Math.Abs(Xx), Math.Abs(Xy)) * Math.Max(Math.Abs(Yx), Math.Abs(Yy));
            if (!Valid(rect) || !double.IsFinite(Xx + Xy + Tx + Yx + Yy + Ty + world.X + world.Y) ||
                !double.IsFinite(determinant) || scale == 0 || Math.Abs(determinant) <= scale * 1e-6) return null;
            var x = ((world.X - Tx) * Yy - Xy * (world.Y - Ty)) / determinant;
            var y = (Xx * (world.Y - Ty) - (world.X - Tx) * Yx) / determinant;
            var result = new Vector2((float)x, (float)y);
            if (!double.IsFinite(x + y) || result.X <= rect.Left + .0001 || result.X >= rect.Right - .0001 ||
                result.Y <= rect.Bottom + .0001 || result.Y >= rect.Top - .0001) return null;
            var backX = Xx * result.X + Xy * result.Y + Tx;
            var backY = Yx * result.X + Yy * result.Y + Ty;
            return Math.Abs(backX - world.X) <= 1 && Math.Abs(backY - world.Y) <= 1 ? result : null;
        }
    }
    internal sealed record Geometry(Vector3 Eye, Vector3 Focus, Vector3 UpHint, float Fov, VirtualRect Root, VirtualRect Viewport, int Width, int Height, float Near = 100, float Far = 5000, VirtualRect? Multiboard = null)
    {
        public bool IsSafeCursor(int x, int y)
        {
            var left = (Viewport.Left - Root.Left) / (Root.Right - Root.Left) * Width;
            var right = (Viewport.Right - Root.Left) / (Root.Right - Root.Left) * Width;
            var top = (Root.Top - Viewport.Top) / (Root.Top - Root.Bottom) * Height;
            var bottom = (Root.Top - Viewport.Bottom) / (Root.Top - Root.Bottom) * Height;
            return x >= left + 16 && x <= right - 16 && y >= top + 16 && y <= bottom - 16 && !OverlapsMultiboard((x, y), (x, y));
        }
        public bool IsSafeArea((int X, int Y) from, (int X, int Y) to) =>
            IsSafeCursor(from.X, from.Y) && IsSafeCursor(to.X, to.Y) && !OverlapsMultiboard(from, to);

        public bool OverlapsMultiboard((int X, int Y) from, (int X, int Y) to)
        {
            if (Multiboard is not { } board) return false;
            if (!Valid(board) || !Valid(Root) || Width <= 0 || Height <= 0) return true;
            var left = (board.Left - Root.Left) / (Root.Right - Root.Left) * Width - 4;
            var right = (board.Right - Root.Left) / (Root.Right - Root.Left) * Width + 4;
            var top = (Root.Top - board.Top) / (Root.Top - Root.Bottom) * Height - 4;
            var bottom = (Root.Top - board.Bottom) / (Root.Top - Root.Bottom) * Height + 4;
            return Math.Max(from.X, to.X) >= left && Math.Min(from.X, to.X) <= right &&
                Math.Max(from.Y, to.Y) >= top && Math.Min(from.Y, to.Y) <= bottom;
        }
        public (int X, int Y) SafePoint() => ((int)Math.Round(((Viewport.Left + Viewport.Right) * .5 - Root.Left) / (Root.Right - Root.Left) * Width),
            (int)Math.Round((Root.Top - (Viewport.Top + Viewport.Bottom) * .5) / (Root.Top - Root.Bottom) * Height));
        public (int X, int Y)? Project(Vector3 position)
        {
            var forward = Vector3.Normalize(Focus - Eye);
            var right = Vector3.Normalize(Vector3.Cross(forward, UpHint));
            var up = Vector3.Normalize(Vector3.Cross(right, forward));
            var relative = position - Eye;
            var depth = Vector3.Dot(relative, forward);
            if (!float.IsFinite(depth) || depth < Near || depth > Far) return null;
            var aspect = (Viewport.Right - Viewport.Left) / (Viewport.Top - Viewport.Bottom);
            // 로컬 1.28.5의 투영 함수 RVA 1554F0: 대각 시야각에서 수직 시야각을 계산한다.
            var tangent = Math.Tan(Fov * .5 / Math.Sqrt(1 + aspect * aspect));
            var nx = Vector3.Dot(relative, right) / (depth * tangent * aspect);
            var ny = Vector3.Dot(relative, up) / (depth * tangent);
            if (!double.IsFinite(nx + ny) || Math.Abs(nx) >= .92 || Math.Abs(ny) >= .92) return null;
            var virtualX = Viewport.Left + (nx + 1) * .5 * (Viewport.Right - Viewport.Left);
            var virtualY = Viewport.Bottom + (ny + 1) * .5 * (Viewport.Top - Viewport.Bottom);
            var x = (int)Math.Round((virtualX - Root.Left) / (Root.Right - Root.Left) * Width);
            var y = (int)Math.Round((Root.Top - virtualY) / (Root.Top - Root.Bottom) * Height);
            return x > 5 && y > 5 && x < Width - 5 && y < Height - 5 ? (x, y) : null;
        }
        public bool NearlyEquals(Geometry other) => Width == other.Width && Height == other.Height && Root == other.Root && Viewport == other.Viewport && Multiboard == other.Multiboard &&
            Vector3.Distance(Eye, other.Eye) < .1 && Vector3.Distance(Focus, other.Focus) < .1 &&
            Vector3.Distance(UpHint, other.UpHint) < .0001 && Math.Abs(Fov - other.Fov) < .0001 && Near == other.Near && Far == other.Far;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ClientRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MousePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Data; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ClientRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref MousePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out MousePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(MousePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
