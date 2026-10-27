using System.Runtime.InteropServices;

// 정확한 그룹과 개체별 소모 증거만 판단한다. 입력이나 메모리 쓰기는 하지 않는다.
static class ChoiceWispBatchSelectionPolicy
{
    internal const int MaximumGroup = 12;
    internal static bool ValidCount(int count) => count is >= 1 and <= MaximumGroup;
    internal static bool Exact(IReadOnlyCollection<uint> selected, IReadOnlyList<GameChoiceInput.WispSample> expected,
        int localPlayer, uint? focus = null) => ValidCount(expected.Count) && selected.Count == expected.Count &&
        selected.Distinct().Count() == selected.Count && expected.Select(unit => unit.Address).Distinct().Count() == expected.Count &&
        expected.All(unit => unit.Active && GameChoiceInput.SameWisp(unit, unit, localPlayer)) &&
        selected.ToHashSet().SetEquals(expected.Select(unit => unit.Address)) && (focus is null || selected.Contains(focus.Value));

    internal static uint? ExtraToRemove(IReadOnlyList<uint> selected, IReadOnlyList<GameChoiceInput.WispSample> owned,
        int localPlayer, int desired)
    {
        if (!ValidCount(desired) || selected.Count <= desired || selected.Count > MaximumGroup || selected.Distinct().Count() != selected.Count) return null;
        var byAddress = owned.GroupBy(unit => unit.Address).ToDictionary(group => group.Key, group => group.ToArray());
        if (selected.Any(address => !byAddress.TryGetValue(address, out var group) || group.Length != 1 || !group[0].Active || group[0].Owner != localPlayer ||
            group[0].Address < 0x10000 || group[0].Identity.Length != 24)) return null;
        var members = selected.Select(address => byAddress[address][0]).ToArray();
        if (members.Count(unit => unit.Rawcode == "e018") < desired) return null;
        var nonWisp = members.FirstOrDefault(unit => unit.Rawcode != "e018");
        if (nonWisp is not null) return nonWisp.Address;
        return selected[^1];
    }

    internal static bool RemovedExactlyOne(IReadOnlyCollection<uint> before, IReadOnlyCollection<uint> after, uint removed) =>
        before.Count is >= 2 and <= MaximumGroup && before.Distinct().Count() == before.Count && after.Distinct().Count() == after.Count &&
        before.Contains(removed) && after.Count == before.Count - 1 && after.ToHashSet().SetEquals(before.Where(address => address != removed));
}

internal enum ChoiceWispConsumptionObservation { Pending, Confirmed, InvalidIdentity, InvalidClock }

// 먼저 도착한 소모 개체는 두 관측으로 확정한 뒤 고정한다. 이후 주소 재사용은 새 소모 증거가 아니다.
sealed class ChoiceWispBatchConsumptionTracker
{
    private readonly GameChoiceInput.WispSample[] before;
    private readonly GameChoiceInput.WispSample?[] first, second;
    private readonly TimeSpan?[] firstAt, lastAt;
    private readonly int localPlayer;
    internal ChoiceWispBatchConsumptionTracker(IReadOnlyList<GameChoiceInput.WispSample> sources, int localPlayer)
    {
        if (!ChoiceWispBatchSelectionPolicy.Exact(sources.Select(source => source.Address).ToArray(), sources, localPlayer))
            throw new ArgumentException("Distinct active owned choice-wisp sources are required.", nameof(sources));
        before = sources.ToArray(); this.localPlayer = localPlayer;
        first = new GameChoiceInput.WispSample?[before.Length]; second = new GameChoiceInput.WispSample?[before.Length];
        firstAt = new TimeSpan?[before.Length]; lastAt = new TimeSpan?[before.Length];
    }
    internal bool IsConfirmed(int index) => second[index] is not null;
    internal int ConfirmedCount => second.Count(sample => sample is not null);
    internal bool AllConfirmed => ConfirmedCount == before.Length;
    internal GameChoiceInput.WispSample ConfirmedSample(int index) => second[index] ?? throw new InvalidOperationException("Source is not confirmed consumed.");
    internal GameChoiceInput.WispSample[] FirstProof => AllConfirmed ? first.Select(sample => sample!).ToArray() : throw new InvalidOperationException("The whole batch is not confirmed.");
    internal GameChoiceInput.WispSample[] SecondProof => AllConfirmed ? second.Select(sample => sample!).ToArray() : throw new InvalidOperationException("The whole batch is not confirmed.");
    internal ChoiceWispConsumptionObservation Observe(int index, TimeSpan elapsed, GameChoiceInput.WispSample sample)
    {
        if (IsConfirmed(index)) return ChoiceWispConsumptionObservation.Confirmed;
        if (elapsed < TimeSpan.Zero || lastAt[index] is { } last && elapsed < last) return ChoiceWispConsumptionObservation.InvalidClock;
        lastAt[index] = elapsed;
        if (!GameChoiceInput.SameWisp(before[index], sample, localPlayer))
        {
            first[index] = null; firstAt[index] = null;
            return ChoiceWispConsumptionObservation.InvalidIdentity;
        }
        if (sample.Active)
        {
            first[index] = null; firstAt[index] = null;
            return ChoiceWispConsumptionObservation.Pending;
        }
        if (first[index] is null) { first[index] = sample; firstAt[index] = elapsed; return ChoiceWispConsumptionObservation.Pending; }
        if (elapsed - firstAt[index]!.Value < ChoiceWispWaitPolicy.StableInterval) return ChoiceWispConsumptionObservation.Pending;
        second[index] = sample;
        return ChoiceWispConsumptionObservation.Confirmed;
    }
}

// Shift는 본 코드가 누른 기간에만 소유하며, 클릭 실패·취소에서도 키를 해제한다.
static class ChoiceWispBatchModifier
{
    internal static async Task<T> WithShiftAsync<T>(Func<Task<T>> body, Func<bool, bool>? sendForTests = null)
    {
        var send = sendForTests ?? Send;
        if (!send(true)) throw new IOException("Shift input was incomplete.");
        try { return await body(); }
        finally
        {
            if (!send(false) && !send(false)) throw new IOException("Shift release was incomplete.");
        }
    }
    private static bool Send(bool down)
    {
        var input = new Input { Type = 1, Data = new Union { Keyboard = new Keyboard { Vk = 0x10, Flags = down ? 0u : 2u } } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }
    internal static int NativeInputSize => Marshal.SizeOf<Input>();
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] public Keyboard Keyboard; [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
