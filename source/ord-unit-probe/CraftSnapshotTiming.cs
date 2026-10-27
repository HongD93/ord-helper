using System.Diagnostics;

// 조합 중에만 짧은 수집 주기를 사용한다. 취소·완료 뒤에는 기존 설정으로 돌아간다.
internal sealed class CraftCaptureCadence(int intervalMs)
{
    internal const int ActiveIntervalMs = 100;
    private readonly int configuredInterval = intervalMs > 0 ? intervalMs : throw new ArgumentOutOfRangeException(nameof(intervalMs));
    private TimeSpan? lastObserved, lastCapture;
    internal int TickMs => Math.Min(configuredInterval, 50);

    internal bool TryBegin(TimeSpan elapsed, bool active)
    {
        if (elapsed < TimeSpan.Zero || lastObserved is not null && elapsed < lastObserved.Value) return false;
        lastObserved = elapsed;
        var interval = TimeSpan.FromMilliseconds(active ? Math.Min(configuredInterval, ActiveIntervalMs) : configuredInterval);
        if (lastCapture is not null && elapsed - lastCapture.Value < interval) return false;
        lastCapture = elapsed;
        return true;
    }

    internal int DelayMs(TimeSpan elapsed, bool active)
    {
        if (lastCapture is null || elapsed < TimeSpan.Zero || lastObserved is not null && elapsed < lastObserved.Value) return TickMs;
        var interval = active ? Math.Min(configuredInterval, ActiveIntervalMs) : configuredInterval;
        var remaining = interval - (elapsed - lastCapture.Value).TotalMilliseconds;
        return (int)Math.Clamp(Math.Ceiling(remaining), 1, TickMs);
    }
}

// 새 수집 프레임을 알리기만 한다. 동일 프레임 재관측이나 시간 초과는 결과 확정 증거가 아니다.
internal sealed class CraftSnapshotSignal
{
    private readonly object gate = new();
    private long currentSequence;
    private TaskCompletionSource<bool> changed = NewPulse();
    private static TaskCompletionSource<bool> NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Publish(long sequence)
    {
        TaskCompletionSource<bool> wake;
        lock (gate)
        {
            if (sequence <= currentSequence) return;
            currentSequence = sequence;
            wake = changed;
            changed = NewPulse();
        }
        wake.TrySetResult(true);
    }

    internal async Task WaitAsync(long afterSequence, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var limit = TimeSpan.FromMilliseconds(CraftCaptureCadence.ActiveIntervalMs);
        while (true)
        {
            Task pulse;
            lock (gate)
            {
                cancel.ThrowIfCancellationRequested();
                if (currentSequence > afterSequence) return;
                pulse = changed.Task;
            }
            var remaining = limit - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { cancel.ThrowIfCancellationRequested(); return; }
            try { await pulse.WaitAsync(remaining, cancel); }
            catch (TimeoutException) { cancel.ThrowIfCancellationRequested(); return; }
            cancel.ThrowIfCancellationRequested();
        }
    }
}
