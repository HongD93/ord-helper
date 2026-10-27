static class GamePortraitSelectionTests
{
    private const uint A = 0x10000, B = 0x20000, C = 0x30000;

    public static void Run()
    {
        (string Name, Action Run)[] cases =
        [
            ("area and portrait precede world clicks", AreaPriority),
            ("interruption forbids the world fallback", Interrupted),
            ("empty HUD result permits only one world fallback", WorldFallback),
            ("one click isolation returns immediately", OneClick),
            ("both clicks lock the same unit across sorting", TwoClicks),
            ("changed selection blocks the next stale click", ChangedGroup),
            ("another single unit is not the target", WrongSingle),
            ("focused subgroup never authorizes a multi-unit cast", FocusedGroup),
            ("single selection also needs matching command focus", FocusMismatch),
            ("invalid groups cannot guide a click", InvalidGroups),
            ("different portrait identity and third click are rejected", ClickBounds),
            ("world attempts stay bounded", WorldBounds),
            ("already active caster portrait is preferred", PreferredActive),
            ("world clicks share a request-wide budget", SharedWorldBudget),
            ("moving targets invalidate stale world points", MovingWorldPoint),
            ("same-type choice wisps become one locked portrait", ChoiceWispGroup),
            ("an eligible selected group cannot fall back to world drags", EligibleGroupBlocksWorld),
            ("an original subgroup can shrink before exact isolation", ShrinkingSubgroup)
        ];
        foreach (var test in cases)
        {
            try { test.Run(); }
            catch (Exception error) { throw new Exception("Portrait selection test failed: " + test.Name, error); }
        }
        Console.WriteLine($"PASS: portrait-first single selection {cases.Length}/{cases.Length} pure/mock cases; no game input.");
    }

    private static void AreaPriority()
    {
        var calls = new List<string>();
        var result = SelectionRetryPolicy.AreaThenWorldAsync(
            () => { calls.Add("area+portrait"); return Task.FromResult(new SelectionAttempt(B, null, true)); },
            () => { calls.Add("world"); return Task.FromResult(new SelectionAttempt(A, null, true)); }).GetAwaiter().GetResult();
        Check(result.Address == B && calls.SequenceEqual(["area+portrait"]), "a successful HUD selection never reaches a world click");
    }
    private static void Interrupted()
    {
        var worldCalls = 0;
        var result = SelectionRetryPolicy.AreaThenWorldAsync(
            () => Task.FromResult(new SelectionAttempt(null, "selection changed")),
            () => { worldCalls++; return Task.FromResult(new SelectionAttempt(B, null)); }).GetAwaiter().GetResult();
        Check(result.Error is not null && worldCalls == 0, "changed input/selection cannot become a fallback retry");
    }

    private static void EligibleGroupBlocksWorld()
    {
        var worldCalls = 0;
        var result = SelectionRetryPolicy.AreaThenWorldAsync(
            () => Task.FromResult(SelectionRetryPolicy.PortraitOutcome(new(null, null), selectedEligibleCaster: true)),
            () => { worldCalls++; return Task.FromResult(new SelectionAttempt(B, null)); }).GetAwaiter().GetResult();
        Check(result.Error is not null && worldCalls == 0, "a selected eligible caster stays in the bounded HUD path even if its portrait cannot be confirmed");
        Check(SelectionRetryPolicy.PortraitOutcome(new(B, null), true).Address == B &&
            SelectionRetryPolicy.PortraitOutcome(new(null, null), false).Error is null,
            "a verified portrait succeeds and a genuinely unrelated group still permits the normal selector");
    }
    private static void WorldFallback()
    {
        var calls = new List<string>();
        var result = SelectionRetryPolicy.AreaThenWorldAsync(
            () => { calls.Add("area+portrait"); return Task.FromResult(new SelectionAttempt(null, null)); },
            () => { calls.Add("world"); return Task.FromResult(new SelectionAttempt(null, null)); }).GetAwaiter().GetResult();
        Check(result.Address is null && result.Error is null && calls.SequenceEqual(["area+portrait", "world"]), "only one bounded fallback is invoked");
    }
    private static void OneClick()
    {
        var plan = new PortraitSelectionPlan([A, B], B);
        var clicks = Simulate(plan, [([A, B], A), ([B], B)]);
        Check(clicks.SequenceEqual([B]) && plan.ClickCount == 1, "a confirmed single target does not receive another portrait click");
    }
    private static void TwoClicks()
    {
        var plan = new PortraitSelectionPlan([A, B, C], B);
        var clicks = Simulate(plan, [([A, B, C], A), ([C, B, A], B), ([B], B)]);
        Check(clicks.SequenceEqual([B, B]), "subgroup sorting does not redirect the second click to another same-type caster");
    }
    private static void ChangedGroup()
    {
        var plan = new PortraitSelectionPlan([A, B], B);
        var clicks = Simulate(plan, [([A, B], A), ([A, B, C], B), ([B], B)]);
        Check(clicks.SequenceEqual([B]) && plan.Observe([A, B, C], B) == PortraitSelectionAction.Stale,
            "a new group member blocks the second stale portrait click");
        Check(plan.Observe([B, C], B) == PortraitSelectionAction.Stale, "a substituted group member is also rejected");
    }
    private static void WrongSingle() => Check(new PortraitSelectionPlan([A, B], B).Observe([A], A) == PortraitSelectionAction.Stale,
        "another selected instance cannot stand in for the locked target");

    private static void ChoiceWispGroup()
    {
        // 같은 e018 타입 세 개라도 대표만 바꾼 상태로 이동 명령을 보내지 않는다.
        var plan = new PortraitSelectionPlan([A, B, C], B);
        var clicks = Simulate(plan, [([A, B, C], A), ([C, A, B], B), ([B], B)]);
        Check(clicks.SequenceEqual([B, B]) && plan.Observe([B], B) == PortraitSelectionAction.Ready,
            "the exact same wisp portrait is clicked at most twice and readiness requires one active selected instance");
        var singleClick = new PortraitSelectionPlan([A, B], B);
        Check(Simulate(singleClick, [([A, B], A), ([B], B)]).SequenceEqual([B]),
            "one click that already isolates the wisp cannot cause a follow-up drag or second portrait click");
    }

    private static void ShrinkingSubgroup()
    {
        var plan = new PortraitSelectionPlan([A, B, C], B);
        var clicks = Simulate(plan, [([A, B, C], A), ([B, C], B), ([B], B)]);
        Check(clicks.SequenceEqual([B, B]) && plan.Observe([B], B) == PortraitSelectionAction.Ready,
            "the first click may select an original same-type subgroup but still needs exact single isolation");
        Check(new PortraitSelectionPlan([A, B, C], B).Observe([A, C], C) == PortraitSelectionAction.Stale,
            "a shrinking group that lost the locked target cannot authorize the second click");
    }
    private static void FocusedGroup() => Check(new PortraitSelectionPlan([A, B], B).Observe([A, B], B) == PortraitSelectionAction.Click,
        "focusing the right subgroup still requires a separately verified individual selection");
    private static void FocusMismatch()
    {
        var plan = new PortraitSelectionPlan([A, B], B);
        Check(plan.Observe([B], A) == PortraitSelectionAction.Wait && plan.Observe([B], null) == PortraitSelectionAction.Wait,
            "a stale/absent active representative cannot authorize a cast");
        Check(plan.Observe([B], B) == PortraitSelectionAction.Ready, "the single selected unit and representative must agree");
    }
    private static void InvalidGroups()
    {
        var plan = new PortraitSelectionPlan([A, B], B);
        Check(plan.Observe([], null) == PortraitSelectionAction.Stale && plan.Observe([A, A], A) == PortraitSelectionAction.Stale &&
            plan.Observe([0, B], B) == PortraitSelectionAction.Stale, "invalid, empty and duplicate pointer groups cannot generate clicks");
        var rejected = false;
        try { _ = new PortraitSelectionPlan([A, B], C); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "the locked target must belong to the original group");
    }
    private static void ClickBounds()
    {
        var plan = new PortraitSelectionPlan([A, B], B);plan.BeforeClick(B);
        var different = false;
        try { plan.BeforeClick(A); } catch (InvalidOperationException) { different = true; }
        Check(different && plan.ClickCount == 1, "the second click cannot be redirected to a fresh first HUD match");
        plan.BeforeClick(B);
        Check(plan.Observe([A, B], B) == PortraitSelectionAction.Exhausted, "two ineffective clicks cannot repeat indefinitely");
        var third = false;try { plan.BeforeClick(B); } catch (InvalidOperationException) { third = true; }
        Check(third, "a third portrait click is rejected");
    }
    private static void WorldBounds() => Check(SelectionRetryPolicy.MaximumWorldCandidates == 2 &&
        SelectionRetryPolicy.WorldFallbackHeights.SequenceEqual([48, 16]), "four old heights and four old candidates cannot create repeated misses");
    private static void PreferredActive()
    {
        Check(SelectionRetryPolicy.PreferFocusedCaster(C, [B, C]) == C,
            "an active material caster is kept instead of picking another matching first portrait");
        Check(SelectionRetryPolicy.PreferFocusedCaster(A, [B, C]) is null && SelectionRetryPolicy.PreferFocusedCaster(null, [B, C]) is null,
            "an unrelated or missing representative is never treated as an existing caster");
        var clicks = Simulate(new PortraitSelectionPlan([A, B, C], C), [([A, B, C], C), ([C], C)]);
        Check(clicks.SequenceEqual([C]), "an already active subgroup needs only its one individual portrait selection");
    }
    private static void SharedWorldBudget()
    {
        var budget = new SelectionWorldClickBudget();var clicks = 0;
        for (var candidate = 0; candidate < SelectionRetryPolicy.MaximumWorldCandidates; candidate++)
            foreach (var height in SelectionRetryPolicy.WorldFallbackHeights)
                if (budget.TryUse()) clicks++;
        Check(clicks == 2 && budget.Remaining == 0 && !budget.TryUse(),
            "multiple casters cannot each restart the two-click world budget");
    }
    private static void MovingWorldPoint() => Check(
        SelectionRetryPolicy.WorldPointStillCurrent((100, 100), (108, 92)) &&
        !SelectionRetryPolicy.WorldPointStillCurrent((100, 100), (109, 100)) &&
        !SelectionRetryPolicy.WorldPointStillCurrent((100, 100), null) &&
        !SelectionRetryPolicy.WorldPointStillCurrent((int.MinValue, 0), (int.MaxValue, 0)),
        "a moved/offscreen target is skipped before a stale fallback click, without integer overflow");

    private static uint[] Simulate(PortraitSelectionPlan plan, (uint[] Selected, uint? Focus)[] frames)
    {
        var clicks = new List<uint>();
        foreach (var frame in frames)
        {
            var action = plan.Observe(frame.Selected, frame.Focus);
            if (action != PortraitSelectionAction.Click) break;
            plan.BeforeClick(plan.Target);clicks.Add(plan.Target);
        }
        return clicks.ToArray();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
