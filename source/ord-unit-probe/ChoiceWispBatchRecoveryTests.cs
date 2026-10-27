internal static class ChoiceWispBatchRecoveryTests
{
    internal static void Run()
    {
        var passed = 0;
        void Test(string name, Action run) { run(); passed++; Console.WriteLine("PASS: Choice batch recovery " + name); }
        static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);
        static GameChoiceInput.WispSample[] Originals(int count) => Enumerable.Range(0, count)
            .Select(index => new GameChoiceInput.WispSample(0x10000u + (uint)index * 4, index.ToString("X24"), "e018", 0, 0, 0)).ToArray();
        static ChoiceWispRecoverySource Source(GameChoiceInput.WispSample sample, bool confirmed = false, bool moving = false, bool arrived = false) =>
            new(confirmed ? sample with { Flags5c = 0x100 } : sample, confirmed, moving, arrived);
        static ChoiceWispRecoverySource[] Partial(GameChoiceInput.WispSample[] before) =>
            before.Select((sample, index) => Source(sample, confirmed: index == 0)).ToArray();
        static ChoiceWispRecoveryDecision Read(ChoiceWispBatchRecoveryPolicy policy, int milliseconds, ChoiceWispRecoverySource[] frame,
            bool context = true, bool normal = true, bool accepted = true) => policy.Observe(Ms(milliseconds), context, normal, accepted, frame);

        Test("five sources retain three proofs and retry only the remaining two once", () =>
        {
            var before = Originals(5); var tracker = new ChoiceWispBatchConsumptionTracker(before, 0);
            var policy = new ChoiceWispBatchRecoveryPolicy(before, 0);
            var current = before.ToArray();
            ChoiceWispRecoverySource[] Frame(bool movingFourth = false) => current.Select((sample, index) =>
                new ChoiceWispRecoverySource(sample, tracker.IsConfirmed(index), movingFourth && index == 3, false)).ToArray();
            Check(Read(policy, 0, Frame()).Action == ChoiceWispRecoveryAction.Wait);
            for (var index = 0; index < 3; index++)
            {
                current[index] = before[index] with { Flags5c = 0x100 };
                tracker.Observe(index, Ms(1000), current[index]);
            }
            Check(Read(policy, 1000, Frame()).Action == ChoiceWispRecoveryAction.Wait);
            for (var index = 0; index < 3; index++) tracker.Observe(index, Ms(1050), current[index]);
            Check(Read(policy, 1050, Frame()).Action == ChoiceWispRecoveryAction.Wait && policy.ConfirmedCount == 3);
            Check(Read(policy, 1999, Frame()).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 2000, Frame()).SourceIndex == 3 && policy.TryReserveRetry(3, Ms(2000)));
            Check(!policy.TryReserveRetry(3, Ms(2000)));
            Check(Read(policy, 2050, Frame(movingFourth: true)).SourceIndex == 4 && policy.TryReserveRetry(4, Ms(2050)));
            current[0] = before[0] with { Identity = new string('F', 24), Rawcode = "h003" };
            for (var index = 3; index < 5; index++)
            {
                current[index] = before[index] with { Flags5c = 0x100 };
                tracker.Observe(index, Ms(2100), current[index]);
            }
            Check(Read(policy, 2100, Frame()).Action == ChoiceWispRecoveryAction.Wait && policy.ConfirmedCount == 3);
            for (var index = 3; index < 5; index++) tracker.Observe(index, Ms(2150), current[index]);
            Check(Read(policy, 2150, Frame()).Action == ChoiceWispRecoveryAction.Wait && policy.ConfirmedCount == 5);
            Check(policy.ReservedRetryCount == 2 && !policy.TryReserveRetry(4, Ms(2150)));
            Check(tracker.AllConfirmed && GameChoiceInput.VerifyBatchConsumption(before, tracker.FirstProof, tracker.SecondProof, 0));
            Check(tracker.ConfirmedSample(0).Identity == before[0].Identity && tracker.ConfirmedSample(0).Rawcode == "e018");
        });
        Test("invalid initial groups cannot create a recovery policy", () =>
        {
            var before = Originals(2);
            var invalid = new[] { Array.Empty<GameChoiceInput.WispSample>(), Originals(13), new[] { before[0], before[0] },
                new[] { before[0], before[1] with { Owner = 1 } }, new[] { before[0], before[1] with { Rawcode = "h003" } },
                new[] { before[0], before[1] with { Identity = "short" } }, new[] { before[0], before[1] with { Flags5c = 0x100 } } };
            foreach (var group in invalid)
            {
                try { _ = new ChoiceWispBatchRecoveryPolicy(group, 0); }
                catch (ArgumentException) { continue; }
                throw new Exception("Invalid recovery group was accepted.");
            }
        });
        Test("a new reward wisp or a missing source cannot alter the locked group", () =>
        {
            var before = Originals(5); var frame = Partial(before);
            foreach (var changed in new[] { frame[..4], frame.Append(Source(Originals(6)[5])).ToArray() })
            {
                var policy = new ChoiceWispBatchRecoveryPolicy(before, 0);
                Check(Read(policy, 0, changed).Reason == "source_count_changed" && policy.ReservedRetryCount == 0);
            }
        });
        Test("unconfirmed sources must keep the original identity owner type and slot", () =>
        {
            var before = Originals(2);
            foreach (var changed in new[] { before[1] with { Identity = new string('F', 24) }, before[1] with { Owner = 1 },
                before[1] with { Rawcode = "h003" }, before[1] with { Address = 0x20000 } })
            {
                var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
                frame[1] = Source(changed);
                Check(Read(policy, 0, frame).Reason == "source_identity_changed" && !policy.TryReserveRetry(1, Ms(0)));
            }
        });
        Test("no partial consumption means no recovery input", () =>
        {
            var before = Originals(3); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0);
            var frame = before.Select(sample => Source(sample)).ToArray();
            foreach (var time in new[] { 0, 50, 2000, 2050 }) Check(Read(policy, time, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(!policy.TryReserveRetry(1, Ms(2050)) && policy.ReservedRetryCount == 0);
        });
        Test("three separated stalled observations are required beyond the time threshold", () =>
        {
            var before = Originals(2); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            Check(Read(policy, 0, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 2000, frame).Action == ChoiceWispRecoveryAction.Wait);
            for (var repeat = 0; repeat < 20; repeat++) Check(Read(policy, 2000, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 2049, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 2050, frame).SourceIndex == 1);
        });
        Test("one moving source cannot reset another source stall clock", () =>
        {
            var before = Originals(3); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            Read(policy, 0, frame); Read(policy, 50, frame);
            frame[1] = Source(before[1], moving: true);
            Check(Read(policy, 1999, frame).Action == ChoiceWispRecoveryAction.Wait);
            frame[1] = Source(before[1]);
            Check(Read(policy, 2000, frame).SourceIndex == 2 && policy.TryReserveRetry(2, Ms(2000)));
            Check(Read(policy, 3999, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 4049, frame).SourceIndex == 1);
        });
        Test("unconfirmed inactive sources wait for proof without another move", () =>
        {
            var before = Originals(2); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            frame[1] = Source(before[1] with { Flags5c = 0x100 });
            foreach (var time in new[] { 0, 50, 2000, 44000 }) Check(Read(policy, time, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(!policy.TryReserveRetry(1, Ms(44000)));
        });
        Test("arrived sources get a five second consumption grace and are never resent", () =>
        {
            var before = Originals(2); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            frame[1] = Source(before[1], arrived: true);
            foreach (var time in new[] { 0, 50, 2000, 4999 }) Check(Read(policy, time, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 5000, frame).Reason == "consumption_timeout" && !policy.TryReserveRetry(1, Ms(5000)));
        });
        Test("leaving the destination starts a fresh outside stall interval", () =>
        {
            var before = Originals(2); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            frame[1] = Source(before[1], arrived: true); Read(policy, 0, frame);
            frame[1] = Source(before[1]);
            Check(Read(policy, 4999, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 5050, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 6998, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 6999, frame).SourceIndex == 1);
        });
        Test("confirmed evidence never regresses to an active source", () =>
        {
            var before = Originals(2); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            Read(policy, 0, frame); frame[0] = Source(before[0]);
            Check(Read(policy, 50, frame).Reason == "consumption_regressed");
        });
        Test("new consumption confirmations require a matching inactive source", () =>
        {
            var before = Originals(2);
            foreach (var sample in new[] { before[0], before[0] with { Identity = new string('F', 24), Flags5c = 0x100 } })
            {
                var policy = new ChoiceWispBatchRecoveryPolicy(before, 0);
                var frame = before.Select(unit => Source(unit)).ToArray();
                frame[0] = new(sample, true, false, false);
                Check(Read(policy, 0, frame).Action == ChoiceWispRecoveryAction.Stop);
            }
        });
        Test("context loss is terminal and cannot reserve an earlier candidate", () =>
        {
            var before = Originals(2); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            Read(policy, 0, frame); Read(policy, 50, frame); Check(Read(policy, 2000, frame).SourceIndex == 1);
            Check(Read(policy, 2050, frame, context: false).Reason == "input_context_changed");
            Check(!policy.TryReserveRetry(1, Ms(2050)) && Read(policy, 2100, frame).Action == ChoiceWispRecoveryAction.Stop);
        });
        Test("the initial command must be accepted and foreign modes stop recovery", () =>
        {
            var before = Originals(2); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            foreach (var time in new[] { 0, 50, 2000 }) Check(Read(policy, time, frame, normal: false, accepted: false).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 2050, frame, normal: false, accepted: true).Reason == "foreign_command_mode");
        });
        Test("a retry reservation never extends the original forty five second deadline", () =>
        {
            var before = Originals(2); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            Read(policy, 0, frame); Read(policy, 50, frame);
            Check(Read(policy, 44999, frame).SourceIndex == 1 && policy.TryReserveRetry(1, Ms(44999)));
            Check(Read(policy, 45000, frame).Reason == "absolute_wait_timeout" && !policy.TryReserveRetry(1, Ms(45000)));
        });
        Test("regressed observations and reservations stop instead of reusing old decisions", () =>
        {
            var before = Originals(2); var frame = Partial(before);
            var policy = new ChoiceWispBatchRecoveryPolicy(before, 0);
            Read(policy, 0, frame); Read(policy, 50, frame); Read(policy, 2000, frame);
            Check(!policy.TryReserveRetry(1, Ms(1999)) && Read(policy, 2000, frame).Reason == "clock_regressed");
            var negative = new ChoiceWispBatchRecoveryPolicy(before, 0);
            Check(Read(negative, -1, frame).Reason == "clock_regressed");
            var regressed = new ChoiceWispBatchRecoveryPolicy(before, 0); Read(regressed, 0, frame); Read(regressed, 50, frame);
            Check(Read(regressed, 49, frame).Reason == "clock_regressed");
        });
        Test("the maximum group cannot acquire more retries or replace original members", () =>
        {
            var before = Originals(12); var policy = new ChoiceWispBatchRecoveryPolicy(before, 0); var frame = Partial(before);
            Read(policy, 0, frame); Read(policy, 50, frame);
            for (var index = 1; index < before.Length; index++)
            {
                var time = 2000 + index * 50;
                Check(Read(policy, time, frame).SourceIndex == index && policy.TryReserveRetry(index, Ms(time)));
                Check(!policy.TryReserveRetry(index, Ms(time)));
            }
            Check(policy.ReservedRetryCount == 11 && Read(policy, 3000, frame).Action == ChoiceWispRecoveryAction.Wait);
            Check(Read(policy, 3050, frame.Append(Source(Originals(13)[12])).ToArray()).Reason == "source_count_changed");
        });
        Console.WriteLine($"PASS: Choice batch recovery ({passed} pure cases; no game input, process access or wall-clock waits).");
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Choice batch recovery fixture failed.");
    }
}
