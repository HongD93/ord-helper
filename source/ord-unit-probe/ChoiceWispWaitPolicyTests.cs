static class ChoiceWispWaitPolicyTests
{
    internal static void Run()
    {
        var passed = 0;
        void Test(string name, Action run) { run(); passed++; Console.WriteLine("PASS: Choice wait " + name); }
        static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);
        static ChoiceWispWaitObservation Frame(ChoiceWispCommandMode mode = ChoiceWispCommandMode.Normal,
            bool active = true, bool identity = true, bool selected = true, bool moving = false, bool arrived = false,
            bool context = true, bool cancel = false) => new(context, mode, identity, active, selected, moving, arrived, cancel);
        static ChoiceWispWaitPolicy Ready()
        {
            var policy = new ChoiceWispWaitPolicy();
            Check(policy.Observe(T(0), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Action == ChoiceWispWaitAction.Wait);
            Check(policy.Observe(T(.05), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Action == ChoiceWispWaitAction.MoveModeReady);
            policy.MarkDestinationSent(T(.1)); return policy;
        }
        static ChoiceWispWaitPolicy Moving()
        {
            var policy = Ready();
            Check(policy.Observe(T(.15), Frame()).Action == ChoiceWispWaitAction.Wait);
            Check(policy.Observe(T(.2), Frame()).Action == ChoiceWispWaitAction.MoveAccepted); return policy;
        }
        Test("move mode needs two spaced observations", () =>
        {
            var p = new ChoiceWispWaitPolicy();
            Check(p.Observe(T(0), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(.025), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(.05), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Action == ChoiceWispWaitAction.MoveModeReady);
        });
        Test("a slow move mode may settle after the old 250ms cutoff", () =>
        {
            var p = new ChoiceWispWaitPolicy(); Check(p.Observe(T(1), Frame()).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(1.5), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(1.55), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Action == ChoiceWispWaitAction.MoveModeReady);
        });
        Test("move mode never appearing stays bounded", () => Check(new ChoiceWispWaitPolicy().Observe(T(2), Frame()).Reason == "move_mode_timeout"));
        Test("destination click is illegal before verified selection and mode", () =>
        {
            var rejected = false; try { new ChoiceWispWaitPolicy().MarkDestinationSent(T(0)); } catch (InvalidOperationException) { rejected = true; } Check(rejected);
        });
        Test("a pending target mode waits beyond the old 500ms cutoff", () =>
        {
            var p = Ready(); Check(p.Observe(T(.8), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(1.8), Frame()).Action == ChoiceWispWaitAction.Wait); Check(p.Observe(T(1.85), Frame()).Action == ChoiceWispWaitAction.MoveAccepted);
        });
        Test("unaccepted destination stays bounded", () => Check(Ready().Observe(T(2.1), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Reason == "move_acceptance_timeout"));
        Test("movement progress permits a real trip beyond nine seconds", () =>
        {
            var p = Moving(); foreach (var seconds in new[] { 7d, 14d, 21d }) Check(p.Observe(T(seconds), Frame(moving: true)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Stage == ChoiceWispWaitStage.Travelling);
        });
        Test("no movement still stops instead of sleeping indefinitely", () => Check(Moving().Observe(T(8.1), Frame()).Reason == "movement_not_observed"));
        Test("even continuous movement cannot exceed the absolute deadline", () =>
        {
            var p = Moving(); foreach (var seconds in new[] { 7d, 14d, 21d, 28d, 35d, 42d }) p.Observe(T(seconds), Frame(moving: true));
            Check(p.Observe(T(45), Frame(moving: true)).Reason == "absolute_wait_timeout");
        });
        Test("selection pointer transients after the single click do not authorize more input or fake consumption", () =>
        {
            var p = Moving(); Check(p.Observe(T(.3), Frame(selected: false, moving: true)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(.35), Frame(active: false, selected: false)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(.4), Frame(active: false, selected: false)).Action == ChoiceWispWaitAction.ConsumptionReady);
        });
        Test("a single inactive read or a duplicate timestamp is not consumption", () =>
        {
            var p = Moving(); Check(p.Observe(T(.3), Frame(active: false)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(.3), Frame(active: false)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(.35), Frame(active: false)).Action == ChoiceWispWaitAction.ConsumptionReady);
        });
        Test("transient identity failure is wait-only and resets inactive evidence", () =>
        {
            var p = Moving(); p.Observe(T(.3), Frame(active: false)); Check(p.Observe(T(.35), Frame(active: false, identity: false)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(.4), Frame(active: false)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(.45), Frame(active: false)).Action == ChoiceWispWaitAction.ConsumptionReady);
        });
        Test("persistent foreign identity never becomes a consumed proof", () =>
        {
            var p = Moving(); Check(p.Observe(T(.3), Frame(active: false, identity: false)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Observe(T(.6), Frame(active: false, identity: false)).Reason == "source_identity_changed");
        });
        Test("arrival without consumption has its own deadline", () =>
        {
            var p = Moving(); Check(p.Observe(T(.3), Frame(arrived: true)).Action == ChoiceWispWaitAction.Wait);
            Check(p.Stage == ChoiceWispWaitStage.AwaitConsumption); Check(p.Observe(T(5.3), Frame(arrived: true)).Reason == "consumption_timeout");
        });
        Test("foreground session geometry or user-input changes stop immediately", () => Check(Moving().Observe(T(.3), Frame(context: false)).Reason == "input_context_changed"));
        Test("foreign or reopened command modes cannot be mistaken for pending movement", () =>
        {
            Check(Ready().Observe(T(.3), Frame(ChoiceWispCommandMode.Foreign)).Reason == "foreign_command_mode");
            Check(Moving().Observe(T(.3), Frame(ChoiceWispCommandMode.OwnedMoveTarget)).Reason == "command_mode_reentered");
        });
        Test("cancelled and backwards-clock observations are terminal", () =>
        {
            var p = Moving(); Check(p.Observe(T(.3), Frame(cancel: true)).Reason == "cancelled"); Check(p.Observe(T(.4), Frame()).Action == ChoiceWispWaitAction.Stop);
            Check(Moving().Observe(T(.1), Frame()).Reason == "clock_regressed");
        });
        Test("source becoming inactive before the destination was sent cannot count as our use", () => Check(new ChoiceWispWaitPolicy().Observe(T(0), Frame(active: false)).Reason == "source_inactive_before_destination"));
        Test("result stability requires distinct fresh collection sequences", () =>
        {
            var p = new ChoiceWispResultStability(); Check(!p.Observe(10, true)); Check(!p.Observe(10, true)); Check(p.Observe(11, true));
        });
        Test("a mismatched or unreadable result resets stability", () =>
        {
            var p = new ChoiceWispResultStability(); Check(!p.Observe(10, true)); Check(!p.Observe(11, false)); Check(!p.Observe(12, true)); p.Reset(); Check(!p.Observe(13, true)); Check(p.Observe(14, true));
        });
        Console.WriteLine($"PASS: Choice wisp wait policy ({passed} pure tests; no game input or sleeping).");
    }
    private static void Check(bool value) { if (!value) throw new Exception("Choice wisp wait policy fixture failed."); }
}
