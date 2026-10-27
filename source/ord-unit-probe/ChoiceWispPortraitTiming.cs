using System.Diagnostics;

// 초상화 클릭을 추가하지 않고, 정확히 한 개만 빠진 선택이 안정됐는지 읽기만 한다.
static class ChoiceWispPortraitTiming
{
    internal const int HoverMs = 15;
    internal const int ModifierMs = 15;
    internal const int HoldMs = 30;
    internal const int PollMs = 10;
    internal const int StableMs = 20;
    internal const int TimeoutMs = 600;

    // 본 코드가 보낸 Shift-down 또는 key-up의 반영만 기다린다. 입력은 추가하지 않는다.
    internal static async Task<bool> WaitForOwnedInputAsync(Func<bool> ready, Func<bool> valid,
        CancellationToken cancel, Func<int, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(valid);
        delay ??= Task.Delay;
        for (var attempt = 0; attempt <= 10; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            var contextValid = valid();
            cancel.ThrowIfCancellationRequested();
            if (!contextValid) return false;
            var observed = ready();
            cancel.ThrowIfCancellationRequested();
            contextValid = valid();
            cancel.ThrowIfCancellationRequested();
            if (!contextValid) return false;
            if (observed) return true;
            if (attempt == 10) return false;
            await delay(PollMs, cancel);
        }
        return false;
    }

    internal static async Task<bool> WaitForRemovalAsync(uint[] before, uint removed,
        Func<uint[]> readSelection, Func<bool> valid, CancellationToken cancel,
        Func<TimeSpan>? elapsed = null, Func<int, CancellationToken, Task>? delay = null)
    {
        cancel.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(readSelection);
        ArgumentNullException.ThrowIfNull(valid);
        if (before is null || before.Length is < 2 or > 12 || before.Any(address => address < 0x10000)) return false;
        var original = before.ToHashSet();
        if (original.Count != before.Length || !original.Contains(removed)) return false;
        var expected = original.Where(address => address != removed).ToHashSet();
        var stopwatch = elapsed is null ? Stopwatch.StartNew() : null;
        var clock = elapsed ?? (() => stopwatch!.Elapsed);
        delay ??= Task.Delay;
        var started = clock();
        var previous = started;
        TimeSpan? stableSince = null;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var contextValid = valid();
            cancel.ThrowIfCancellationRequested();
            if (!contextValid) return false;
            var readingAt = clock();
            if (readingAt < previous || (readingAt - started).TotalMilliseconds >= TimeoutMs) return false;
            var selected = readSelection();
            cancel.ThrowIfCancellationRequested();
            contextValid = valid();
            cancel.ThrowIfCancellationRequested();
            if (!contextValid) return false;
            var observedAt = clock();
            if (observedAt < readingAt || (observedAt - started).TotalMilliseconds >= TimeoutMs) return false;
            previous = observedAt;
            if (selected is null || selected.Length > 12 || selected.Any(address => address < 0x10000)) return false;
            var current = selected.ToHashSet();
            if (current.Count != selected.Length) return false;
            if (current.SetEquals(expected))
            {
                if (stableSince is not null && (observedAt - stableSince.Value).TotalMilliseconds >= StableMs) return true;
                stableSince ??= observedAt;
            }
            else if (current.Count == 0 || current.SetEquals(original))
            {
                // 선택 전환의 빈 그룹이나 아직 반영되지 않은 원래 그룹은 성공 증거를 초기화한다.
                stableSince = null;
            }
            else return false;
            await delay(PollMs, cancel);
        }
    }
}
