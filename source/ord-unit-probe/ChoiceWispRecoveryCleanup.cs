// 복구 입력이 연 표적 모드만 정리한다. 실제 입력은 호출자가 전달한 action 한 번으로 제한한다.
internal static class ChoiceWispRecoveryCleanup
{
    private const int PollMilliseconds = 25;
    private const int MaximumPolls = 20;

    internal static async Task<bool> CancelOwnedModeAsync(uint observedMode, uint normalMode,
        Func<uint> readMode, Func<bool> valid, Func<bool> selectionOwned, Action cancelOnce,
        Func<int, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(readMode);
        ArgumentNullException.ThrowIfNull(valid);
        ArgumentNullException.ThrowIfNull(selectionOwned);
        ArgumentNullException.ThrowIfNull(cancelOnce);
        delay ??= milliseconds => Task.Delay(milliseconds);

        bool OwnedContext() => valid() && selectionOwned() && valid();
        if (!OwnedContext()) return false;
        var mode = readMode();
        if (!OwnedContext()) return false;

        var ownedMode = observedMode;
        if (ownedMode == 0)
        {
            // M이 큐에 있는 동안 정상 모드를 성공으로 오인하지 않는다.
            for (var poll = 0; mode == normalMode && poll < MaximumPolls; poll++)
            {
                if (!OwnedContext()) return false;
                await delay(PollMilliseconds);
                if (!OwnedContext()) return false;
                mode = readMode();
                if (!OwnedContext()) return false;
            }
            if (mode == normalMode) return false;
            ownedMode = mode;
        }

        if (mode == normalMode) return true;
        if (mode != ownedMode || !OwnedContext()) return false;

        // 마지막 관측 이후 정상/외부 모드로 바뀌었다면 Escape를 보내지 않는다.
        mode = readMode();
        if (!OwnedContext()) return false;
        if (mode == normalMode) return true;
        if (mode != ownedMode) return false;
        cancelOnce();

        for (var poll = 0; ; poll++)
        {
            if (!OwnedContext()) return false;
            mode = readMode();
            if (!OwnedContext()) return false;
            if (mode == normalMode) return true;
            if (mode != ownedMode || poll >= MaximumPolls) return false;
            await delay(PollMilliseconds);
        }
    }
}
