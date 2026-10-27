static class ChoiceWispBatchSelectionTests
{
    public static void Run() => VerifyAsync().GetAwaiter().GetResult();
    private static async Task VerifyAsync()
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("Choice-wisp batch selection test failed: " + message); checks++; }
        GameChoiceInput.WispSample Wisp(uint address, char identity = 'A') => new(address, new string(identity, 24), "e018", 0, 0, 0);
        var before = new[] { Wisp(0x10000), Wisp(0x20000, 'B'), Wisp(0x30000, 'C') };
        var addresses = before.Select(unit => unit.Address).ToArray();
        var inactive = before.Select(unit => unit with { Flags5c = 0x100 }).ToArray();
        Check(ChoiceWispBatchSelectionPolicy.Exact(addresses, before, 0, addresses[1]), "the exact three owned active sources can move together");
        Check(ChoiceWispBatchSelectionPolicy.Exact(addresses.Reverse().ToArray(), before, 0, addresses[0]), "native membership order does not change an exact group");
        Check(!ChoiceWispBatchSelectionPolicy.Exact(addresses[..2], before, 0), "fewer selected sources cannot authorize a partial group send");
        Check(!ChoiceWispBatchSelectionPolicy.Exact([.. addresses, 0x40000], before, 0), "more selected sources cannot authorize a group send");
        Check(!ChoiceWispBatchSelectionPolicy.Exact([addresses[0], addresses[0], addresses[2]], before, 0), "duplicate selected addresses are rejected");
        Check(!ChoiceWispBatchSelectionPolicy.Exact(addresses, [before[0], before[0], before[2]], 0), "duplicate locked source addresses are rejected");
        Check(!ChoiceWispBatchSelectionPolicy.Exact(addresses, before, 0, 0x40000), "a representative outside the selected source group is rejected");
        Check(!ChoiceWispBatchSelectionPolicy.Exact(addresses, before, 1), "another local player cannot authorize the group");
        Check(!ChoiceWispBatchSelectionPolicy.Exact(addresses, [before[0], before[1] with { Rawcode = "e0IX" }, before[2]], 0), "a random wisp is never sent as a common-choice source");
        Check(!ChoiceWispBatchSelectionPolicy.Exact(addresses, [before[0], before[1] with { Owner = 1 }, before[2]], 0), "foreign-owned group members are rejected");
        Check(!ChoiceWispBatchSelectionPolicy.Exact(addresses, [before[0], inactive[1], before[2]], 0), "an already inactive source cannot authorize the move");
        Check(!ChoiceWispBatchSelectionPolicy.ValidCount(0) && !ChoiceWispBatchSelectionPolicy.ValidCount(13) && ChoiceWispBatchSelectionPolicy.ValidCount(12), "each native group is limited to one through twelve sources");
        Check(ChoiceWispBatchSelectionPolicy.ExtraToRemove(addresses, before, 0, 2) == addresses[2], "excess same-kind sources are removed deterministically through HUD selection");
        var random = Wisp(0x40000, 'D') with { Rawcode = "e0IX" };
        var caster = Wisp(0x50000, 'E') with { Rawcode = "h003" };
        Check(ChoiceWispBatchSelectionPolicy.ExtraToRemove([.. addresses, random.Address, caster.Address], [.. before, random, caster], 0, 3) == random.Address,
            "a mixed owned selection removes non-choice members before any extra choice wisp");
        Check(ChoiceWispBatchSelectionPolicy.ExtraToRemove([addresses[0], random.Address], [before[0], random], 0, 2) is null,
            "mixed selection with too few real choice wisps is not authorized");
        Check(ChoiceWispBatchSelectionPolicy.ExtraToRemove([.. addresses, random.Address], [.. before, random with { Owner = 1 }], 0, 2) is null,
            "unknown or foreign members cannot authorize a trimming click");
        Check(ChoiceWispBatchSelectionPolicy.ExtraToRemove([.. addresses, 0x60000], before, 0, 2) is null,
            "unverified selected bodies cannot authorize a trimming click");
        Check(ChoiceWispBatchSelectionPolicy.RemovedExactlyOne([.. addresses, random.Address], addresses, random.Address), "HUD trimming accepts only the targeted member disappearing");
        Check(!ChoiceWispBatchSelectionPolicy.RemovedExactlyOne(addresses, [addresses[0]], addresses[2]), "a lost second member during trimming is rejected");
        Check(!ChoiceWispBatchSelectionPolicy.RemovedExactlyOne(addresses, [addresses[0], 0x40000], addresses[2]), "new members cannot be silently substituted while trimming");

        Check(GameChoiceInput.VerifyBatchConsumption(before, inactive, inactive.Select(unit => unit with { }).ToArray(), 0), "all three locked identities confirmed inactive twice prove all three consumptions");
        Check(!GameChoiceInput.VerifyBatchConsumption(before, inactive, [inactive[0], before[1], inactive[2]], 0), "one unconsumed source prevents full-batch consumption proof");
        Check(!GameChoiceInput.VerifyBatchConsumption(before, [inactive[0], before[1], inactive[2]], inactive, 0), "one transient inactive observation cannot prove the group");
        Check(!GameChoiceInput.VerifyBatchConsumption(before, inactive[..2], inactive, 0), "partial consumption reports cannot masquerade as the whole batch");
        Check(!GameChoiceInput.VerifyBatchConsumption(before, inactive, [inactive[0], inactive[1] with { Identity = new string('Z', 24) }, inactive[2]], 0), "reused address generation invalidates the source proof");
        Check(!GameChoiceInput.VerifyBatchConsumption(before, inactive, [inactive[0], inactive[1] with { Rawcode = "h003" }, inactive[2]], 0), "a different result body at the same address is not consumption evidence");
        Check(!GameChoiceInput.VerifyBatchConsumption(before, inactive, [inactive[0], inactive[1] with { Owner = 1 }, inactive[2]], 0), "ownership changes cannot prove consumption");
        Check(!GameChoiceInput.VerifyBatchConsumption(before, inactive, [inactive[0], inactive[1] with { Address = 0x70000 }, inactive[2]], 0), "a newly awarded wisp cannot replace one of the locked sources");
        Check(!GameChoiceInput.VerifyBatchConsumption(before, [.. inactive, random], [.. inactive, random], 0), "new story rewards are not mixed into the locked-source proof array");
        Check(GameChoiceInput.VerifyBatchConsumption(before, inactive, inactive, 0), "new unrelated rewards do not change proof for the original exact identities");
        Check(!GameChoiceInput.VerifyBatchConsumption([], [], [], 0), "an empty batch has no consumption proof");
        Check(GameChoiceInput.VerifyBatchConsumption([before[0]], [inactive[0]], [inactive[0]], 0), "the existing single-wisp proof path remains valid");

        var wait = new ChoiceWispWaitPolicy();
        ChoiceWispWaitObservation Frame(bool active, ChoiceWispCommandMode mode, bool selected = true) => new(true, mode, true, active, selected);
        var ready = wait.Observe(TimeSpan.Zero, Frame(true, ChoiceWispCommandMode.OwnedMoveTarget));
        Check(ready.Action == ChoiceWispWaitAction.Wait, "one move-mode sample does not dispatch the group");
        ready = wait.Observe(TimeSpan.FromMilliseconds(50), Frame(true, ChoiceWispCommandMode.OwnedMoveTarget));
        Check(ready.Action == ChoiceWispWaitAction.MoveModeReady, "two stable mode observations permit one destination click");
        wait.MarkDestinationSent(TimeSpan.FromMilliseconds(50));
        var accepted = wait.Observe(TimeSpan.FromMilliseconds(100), Frame(true, ChoiceWispCommandMode.Normal));
        Check(accepted.Action == ChoiceWispWaitAction.Wait, "one normal-mode observation does not prove move acceptance");
        accepted = wait.Observe(TimeSpan.FromMilliseconds(150), Frame(true, ChoiceWispCommandMode.Normal));
        Check(accepted.Action == ChoiceWispWaitAction.MoveAccepted, "two normal-mode observations accept the group command without another destination input");
        Check(wait.Observe(TimeSpan.FromMilliseconds(200), Frame(active: true, ChoiceWispCommandMode.Normal, selected: false)).Action == ChoiceWispWaitAction.Wait,
            "partially consumed groups remain active while any locked source remains");
        Check(wait.Observe(TimeSpan.FromMilliseconds(250), Frame(false, ChoiceWispCommandMode.Normal, selected: false)).Action == ChoiceWispWaitAction.Wait,
            "the first all-inactive sample does not prove completion");
        Check(wait.Observe(TimeSpan.FromMilliseconds(275), Frame(false, ChoiceWispCommandMode.Normal, selected: false)).Action == ChoiceWispWaitAction.Wait,
            "repeated reads before the stable interval do not count as a second observation");
        Check(wait.Observe(TimeSpan.FromMilliseconds(300), Frame(false, ChoiceWispCommandMode.Normal, selected: false)).Action == ChoiceWispWaitAction.ConsumptionReady,
            "a second separated all-inactive sample completes the native wait");
        Check(wait.Observe(TimeSpan.FromMilliseconds(350), new(false, ChoiceWispCommandMode.Normal, true, false, false)).Action == ChoiceWispWaitAction.Stop,
            "a changed session cannot continue waiting for another input");

        var tracker = new ChoiceWispBatchConsumptionTracker(before, 0);
        Check(tracker.Observe(0, TimeSpan.Zero, inactive[0]) == ChoiceWispConsumptionObservation.Pending && !tracker.IsConfirmed(0), "first arriving source needs two inactive observations");
        Check(tracker.Observe(0, TimeSpan.FromMilliseconds(25), inactive[0]) == ChoiceWispConsumptionObservation.Pending, "one source cannot confirm from rapid repeated reads");
        Check(tracker.Observe(0, TimeSpan.FromMilliseconds(50), inactive[0]) == ChoiceWispConsumptionObservation.Confirmed && tracker.IsConfirmed(0), "a source is locked as consumed independently of later arrivals");
        var reused = before[0] with { Identity = new string('Z', 24), Rawcode = "h003" };
        Check(tracker.Observe(0, TimeSpan.FromMilliseconds(100), reused) == ChoiceWispConsumptionObservation.Confirmed && tracker.ConfirmedSample(0).Rawcode == "e018", "a later reused address cannot replace already confirmed consumption evidence");
        Check(!tracker.AllConfirmed && tracker.ConfirmedCount == 1, "one completed source cannot prove the remaining batch");
        foreach (var i in new[] { 1, 2 })
        {
            tracker.Observe(i, TimeSpan.FromMilliseconds(200 + i * 100), inactive[i]);
            tracker.Observe(i, TimeSpan.FromMilliseconds(250 + i * 100), inactive[i]);
        }
        Check(tracker.AllConfirmed && GameChoiceInput.VerifyBatchConsumption(before, tracker.FirstProof, tracker.SecondProof, 0), "staggered arrivals retain early proof while waiting for the other exact sources");
        var prematureReuse = new ChoiceWispBatchConsumptionTracker(before, 0);
        prematureReuse.Observe(0, TimeSpan.Zero, inactive[0]);
        Check(prematureReuse.Observe(0, TimeSpan.FromMilliseconds(50), reused) == ChoiceWispConsumptionObservation.InvalidIdentity && !prematureReuse.IsConfirmed(0), "address reuse before the second inactive observation cannot prove consumption");
        var flicker = new ChoiceWispBatchConsumptionTracker(before, 0);
        flicker.Observe(0, TimeSpan.Zero, inactive[0]); flicker.Observe(0, TimeSpan.FromMilliseconds(50), before[0]);
        Check(flicker.Observe(0, TimeSpan.FromMilliseconds(100), inactive[0]) == ChoiceWispConsumptionObservation.Pending, "returning active resets a transient consumption candidate");
        Check(flicker.Observe(0, TimeSpan.FromMilliseconds(90), inactive[0]) == ChoiceWispConsumptionObservation.InvalidClock, "per-source proof cannot use a regressed observation clock");

        var events = new List<bool>();
        var value = await ChoiceWispBatchModifier.WithShiftAsync(() => Task.FromResult(7), down => { events.Add(down); return true; });
        Check(value == 7 && events.SequenceEqual([true, false]), "successful HUD modifier input always releases the owned Shift");
        events.Clear();
        try { await ChoiceWispBatchModifier.WithShiftAsync<int>(() => Task.FromException<int>(new OperationCanceledException()), down => { events.Add(down); return true; }); }
        catch (OperationCanceledException) { }
        Check(events.SequenceEqual([true, false]), "cancellation inside a HUD click still releases the owned Shift");
        events.Clear();
        try { await ChoiceWispBatchModifier.WithShiftAsync<int>(() => Task.FromException<int>(new IOException("fixture")), down => { events.Add(down); return true; }); }
        catch (IOException) { }
        Check(events.SequenceEqual([true, false]), "a partial click failure still releases the owned Shift");
        events.Clear(); var entered = false;
        try { await ChoiceWispBatchModifier.WithShiftAsync(() => { entered = true; return Task.FromResult(0); }, down => { events.Add(down); return false; }); }
        catch (IOException) { }
        Check(!entered && events.SequenceEqual([true]), "failed Shift-down cannot reach a mouse action");
        events.Clear(); var releases = 0;
        await ChoiceWispBatchModifier.WithShiftAsync(() => Task.FromResult(0), down => { events.Add(down); return down || ++releases > 1; });
        Check(events.SequenceEqual([true, false, false]), "only idempotent key release is retried after a cleanup failure");
        Check(ChoiceWispBatchModifier.NativeInputSize == (IntPtr.Size == 8 ? 40 : 28), "normal keyboard input uses the Windows INPUT union layout");
        Console.WriteLine($"PASS: choice-wisp batch selection and consumption {checks}/{checks} pure checks; Shift emitter was injected in every test, no native input or memory write.");
    }
}
