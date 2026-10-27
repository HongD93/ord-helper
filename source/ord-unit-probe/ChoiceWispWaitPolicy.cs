internal enum ChoiceWispWaitStage { AwaitMoveMode, ReadyForDestination, AwaitMoveAcceptance, Travelling, AwaitConsumption, ConsumptionReady, Stopped }
internal enum ChoiceWispWaitAction { Wait, MoveModeReady, MoveAccepted, ConsumptionReady, Stop }
internal enum ChoiceWispCommandMode { Normal, OwnedMoveTarget, Foreign }
internal sealed record ChoiceWispWaitObservation(bool ContextValid, ChoiceWispCommandMode Mode,
    bool SourceIdentityValid, bool SourceActive, bool SelectedSourceOnly,
    bool MovementObserved = false, bool AtDestination = false, bool Cancelled = false);
internal readonly record struct ChoiceWispWaitDecision(ChoiceWispWaitStage Stage, ChoiceWispWaitAction Action, string? Reason = null);

// 관측값만으로 기다릴지 판단한다. 선택·명령·클릭을 호출하거나 소모 proof를 대신하지 않는다.
sealed class ChoiceWispWaitPolicy
{
    internal static readonly TimeSpan MoveModeTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MoveAcceptanceTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan ConsumptionTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan IdentityTransitionGrace = TimeSpan.FromMilliseconds(300);
    internal static readonly TimeSpan StableInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ResultPollInterval = TimeSpan.FromMilliseconds(100);
    internal const int RequiredStableSamples = 2;

    private readonly TimeSpan started;
    private TimeSpan lastObserved, destinationSent, lastProgress;
    private TimeSpan? invalidIdentitySince, arrivedAt, modeSampleTime, inactiveSampleTime;
    private int modeSamples, inactiveSamples;
    private string? stoppedReason;
    internal ChoiceWispWaitStage Stage { get; private set; } = ChoiceWispWaitStage.AwaitMoveMode;

    internal ChoiceWispWaitPolicy(TimeSpan startedAt = default) { started = lastObserved = lastProgress = startedAt; }

    internal void MarkDestinationSent(TimeSpan elapsed)
    {
        if (Stage != ChoiceWispWaitStage.ReadyForDestination || elapsed < lastObserved)
            throw new InvalidOperationException("A verified move mode and monotonic clock are required before the single destination click.");
        Stage = ChoiceWispWaitStage.AwaitMoveAcceptance;
        destinationSent = lastProgress = lastObserved = elapsed;
        ResetMode(); ResetInactive();
    }

    internal ChoiceWispWaitDecision Observe(TimeSpan elapsed, ChoiceWispWaitObservation sample)
    {
        if (Stage == ChoiceWispWaitStage.Stopped) return Decision(ChoiceWispWaitAction.Stop, stoppedReason);
        if (sample.Cancelled) return Stop("cancelled");
        if (!sample.ContextValid) return Stop("input_context_changed");
        if (elapsed < started || elapsed < lastObserved) return Stop("clock_regressed");
        lastObserved = elapsed;
        if (elapsed - started >= MaximumWait) return Stop("absolute_wait_timeout");
        if (sample.Mode == ChoiceWispCommandMode.Foreign) return Stop("foreign_command_mode");
        if (!sample.SourceIdentityValid)
        {
            ResetMode(); ResetInactive();
            invalidIdentitySince ??= elapsed;
            return elapsed - invalidIdentitySince.Value >= IdentityTransitionGrace ? Stop("source_identity_changed") : Decision(ChoiceWispWaitAction.Wait);
        }
        invalidIdentitySince = null;
        if (sample.MovementObserved) lastProgress = elapsed;

        if (Stage is ChoiceWispWaitStage.AwaitMoveMode or ChoiceWispWaitStage.ReadyForDestination)
        {
            if (!sample.SourceActive) return Stop("source_inactive_before_destination");
            if (sample.Mode == ChoiceWispCommandMode.OwnedMoveTarget && sample.SelectedSourceOnly)
            {
                CountStable(ref modeSamples, ref modeSampleTime, elapsed);
                if (modeSamples >= RequiredStableSamples) { Stage = ChoiceWispWaitStage.ReadyForDestination; return Decision(ChoiceWispWaitAction.MoveModeReady); }
            }
            else ResetMode();
            return elapsed - started >= MoveModeTimeout ? Stop("move_mode_timeout") : Decision(ChoiceWispWaitAction.Wait);
        }
        if (Stage == ChoiceWispWaitStage.AwaitMoveAcceptance)
        {
            // 이미 한 번 누른 목적지 클릭이 처리되는 동안 본인이 연 표적 모드는 기다린다.
            if (sample.Mode == ChoiceWispCommandMode.Normal)
            {
                CountStable(ref modeSamples, ref modeSampleTime, elapsed);
                if (!sample.SourceActive) CountStable(ref inactiveSamples, ref inactiveSampleTime, elapsed); else ResetInactive();
                if (modeSamples >= RequiredStableSamples)
                {
                    if (inactiveSamples >= RequiredStableSamples) { Stage = ChoiceWispWaitStage.ConsumptionReady; return Decision(ChoiceWispWaitAction.ConsumptionReady); }
                    Stage = ChoiceWispWaitStage.Travelling;
                    return Decision(ChoiceWispWaitAction.MoveAccepted);
                }
            }
            else { ResetMode(); ResetInactive(); }
            return elapsed - destinationSent >= MoveAcceptanceTimeout ? Stop("move_acceptance_timeout") : Decision(ChoiceWispWaitAction.Wait);
        }
        if (sample.Mode != ChoiceWispCommandMode.Normal) return Stop("command_mode_reentered");
        if (!sample.SourceActive)
        {
            arrivedAt ??= elapsed;
            Stage = ChoiceWispWaitStage.AwaitConsumption;
            CountStable(ref inactiveSamples, ref inactiveSampleTime, elapsed);
            if (inactiveSamples >= RequiredStableSamples) { Stage = ChoiceWispWaitStage.ConsumptionReady; return Decision(ChoiceWispWaitAction.ConsumptionReady); }
            return Decision(ChoiceWispWaitAction.Wait);
        }
        ResetInactive();
        if (sample.AtDestination)
        {
            arrivedAt ??= elapsed;
            Stage = ChoiceWispWaitStage.AwaitConsumption;
            return elapsed - arrivedAt.Value >= ConsumptionTimeout ? Stop("consumption_timeout") : Decision(ChoiceWispWaitAction.Wait);
        }
        arrivedAt = null; Stage = ChoiceWispWaitStage.Travelling;
        return elapsed - lastProgress >= NoProgressTimeout ? Stop("movement_not_observed") : Decision(ChoiceWispWaitAction.Wait);
    }

    private static void CountStable(ref int samples, ref TimeSpan? sampleTime, TimeSpan now)
    {
        if (sampleTime is not null && now - sampleTime.Value < StableInterval) return;
        samples++; sampleTime = now;
    }
    private void ResetMode() { modeSamples = 0; modeSampleTime = null; }
    private void ResetInactive() { inactiveSamples = 0; inactiveSampleTime = null; }
    private ChoiceWispWaitDecision Decision(ChoiceWispWaitAction action, string? reason = null) => new(Stage, action, reason);
    private ChoiceWispWaitDecision Stop(string reason) { Stage = ChoiceWispWaitStage.Stopped; stoppedReason = reason; return Decision(ChoiceWispWaitAction.Stop, reason); }
}

// 같은 수집 프레임을 반복 읽은 것은 결과가 두 번 안정된 증거가 아니다.
sealed class ChoiceWispResultStability
{
    private long lastSequence;
    private int matches;
    internal void Reset() => matches = 0;
    internal bool Observe(long sequence, bool expectedCountsMatch)
    {
        if (sequence <= lastSequence) return false;
        lastSequence = sequence;
        matches = expectedCountsMatch ? matches + 1 : 0;
        return matches >= ChoiceWispWaitPolicy.RequiredStableSamples;
    }
}
