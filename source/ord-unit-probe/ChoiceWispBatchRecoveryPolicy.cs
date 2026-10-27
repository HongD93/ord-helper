internal sealed record ChoiceWispRecoverySource(GameChoiceInput.WispSample Sample, bool ConsumptionConfirmed,
    bool MovingTowardTarget, bool AtDestination);
internal enum ChoiceWispRecoveryAction { Wait, Retry, Stop }
internal sealed record ChoiceWispRecoveryDecision(ChoiceWispRecoveryAction Action, int? SourceIndex = null, string? Reason = null);

// 원래 묶음의 미확정 개체만 회복 후보로 고른다. 선택·명령·소모 증거 생성은 담당하지 않는다.
internal sealed class ChoiceWispBatchRecoveryPolicy
{
    internal static readonly TimeSpan StallInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan ConsumptionTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(45);
    internal const int RequiredStallSamples = 3;

    private readonly GameChoiceInput.WispSample[] before;
    private readonly int localPlayer;
    private readonly bool[] confirmed, retried;
    private readonly TimeSpan[] lastProgress;
    private readonly TimeSpan?[] lastStallSample, arrivedAt;
    private readonly int[] stalledSamples;
    private TimeSpan? lastObserved;
    private int? offeredIndex;
    private string? stoppedReason;

    internal ChoiceWispBatchRecoveryPolicy(IReadOnlyList<GameChoiceInput.WispSample> before, int localPlayer)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (before.Any(sample => sample is null) || !ChoiceWispBatchSelectionPolicy.Exact(
            before.Select(sample => sample.Address).ToArray(), before, localPlayer))
            throw new ArgumentException("Distinct active owned choice-wisp sources are required.", nameof(before));
        this.before = before.ToArray();
        this.localPlayer = localPlayer;
        confirmed = new bool[before.Count]; retried = new bool[before.Count];
        lastProgress = new TimeSpan[before.Count]; lastStallSample = new TimeSpan?[before.Count];
        arrivedAt = new TimeSpan?[before.Count]; stalledSamples = new int[before.Count];
    }

    internal int ConfirmedCount => confirmed.Count(value => value);
    internal int ReservedRetryCount => retried.Count(value => value);

    internal ChoiceWispRecoveryDecision Observe(TimeSpan elapsed, bool contextValid, bool normalMode,
        bool initialMoveAccepted, IReadOnlyList<ChoiceWispRecoverySource> sources)
    {
        offeredIndex = null;
        if (!CheckTime(elapsed)) return Stop(stoppedReason!);
        if (!contextValid) return Stop("input_context_changed");
        if (sources is null || sources.Count != before.Length) return Stop("source_count_changed");

        for (var index = 0; index < before.Length; index++)
        {
            var source = sources[index];
            if (source is null) return Stop("source_identity_changed");
            if (confirmed[index])
            {
                if (!source.ConsumptionConfirmed) return Stop("consumption_regressed");
                continue; // 이미 고정한 소모 증거는 이후 주소 재사용과 무관하다.
            }
            if (source.Sample is null || !GameChoiceInput.SameWisp(before[index], source.Sample, localPlayer))
                return Stop("source_identity_changed");
            if (source.ConsumptionConfirmed)
            {
                if (source.Sample.Active) return Stop("invalid_consumption_proof");
                confirmed[index] = true;
            }
        }

        if (initialMoveAccepted && !normalMode) return Stop("foreign_command_mode");
        var partial = ConfirmedCount > 0 && ConfirmedCount < before.Length;
        int? candidate = null;
        for (var index = 0; index < before.Length; index++)
        {
            if (confirmed[index]) continue;
            var source = sources[index];
            if (!initialMoveAccepted || !source.Sample.Active)
            {
                lastProgress[index] = elapsed;
                ResetStall(index); arrivedAt[index] = null;
                continue; // 아직 수락되지 않았거나 inactive 관측을 확정하는 중에는 입력하지 않는다.
            }
            if (source.AtDestination)
            {
                arrivedAt[index] ??= elapsed;
                ResetStall(index);
                if (elapsed - arrivedAt[index]!.Value >= ConsumptionTimeout) return Stop("consumption_timeout");
                continue; // EnterRect 안에서 같은 점으로 다시 보내지 않는다.
            }
            if (arrivedAt[index] is not null)
            {
                lastProgress[index] = elapsed;
                arrivedAt[index] = null;
                ResetStall(index);
            }
            if (source.MovingTowardTarget)
            {
                lastProgress[index] = elapsed;
                ResetStall(index);
                continue;
            }
            if (lastStallSample[index] is null || elapsed - lastStallSample[index]!.Value >= SampleInterval)
            {
                stalledSamples[index] = Math.Min(RequiredStallSamples, stalledSamples[index] + 1);
                lastStallSample[index] = elapsed;
            }
            if (candidate is null && partial && normalMode && !retried[index] &&
                elapsed - lastProgress[index] >= StallInterval && stalledSamples[index] >= RequiredStallSamples)
                candidate = index;
        }
        if (candidate is null) return new(ChoiceWispRecoveryAction.Wait);
        offeredIndex = candidate;
        return new(ChoiceWispRecoveryAction.Retry, candidate);
    }

    // 실제 입력 전에 예약한다. 입력 성공 여부가 불명확해도 같은 개체에는 다시 예약하지 않는다.
    internal bool TryReserveRetry(int index, TimeSpan elapsed)
    {
        if (!CheckTime(elapsed) || offeredIndex != index || index < 0 || index >= before.Length ||
            confirmed[index] || retried[index] || ReservedRetryCount >= before.Length) return false;
        retried[index] = true;
        offeredIndex = null;
        lastProgress[index] = elapsed;
        ResetStall(index);
        return true;
    }

    private bool CheckTime(TimeSpan elapsed)
    {
        if (stoppedReason is not null) return false;
        if (elapsed < TimeSpan.Zero || lastObserved is not null && elapsed < lastObserved.Value)
        {
            Stop("clock_regressed"); return false;
        }
        if (elapsed >= MaximumWait) { Stop("absolute_wait_timeout"); return false; }
        lastObserved = elapsed;
        return true;
    }

    private void ResetStall(int index) { stalledSamples[index] = 0; lastStallSample[index] = null; }
    private ChoiceWispRecoveryDecision Stop(string reason)
    {
        stoppedReason ??= reason;
        offeredIndex = null;
        return new(ChoiceWispRecoveryAction.Stop, Reason: stoppedReason);
    }
}
