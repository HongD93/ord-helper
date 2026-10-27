using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

// Windows 입력 실행기 대신 메모리 사전만 바꾸는 실행기를 주입해 서비스의 단계 경계를 검사한다.
static class GameCraftSequenceTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly string[] CharacterCodes = ["b001", "m001", "m002", "g001", "g002", "g003", "g004", "g005", "g006", "g007", "g008", "g009", "x001", "h003", "h002"];

    public static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ord-sequence-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var catalog = Path.Combine(folder, "catalog.json");
        var aliases = Path.Combine(folder, "commands.json");
        var passed = 0;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        await using var app = builder.Build(); // 포트를 열거나 서버를 실행하지 않는다.
        try
        {
            WriteCatalog(catalog, aliases);
            async Task<JsonElement> Execute(Fixture fixture, string recipe, CancellationToken token = default, IReadOnlyDictionary<string, int>? protectedCounts = null,
                bool adaptive = false, RequestedCraftStep[]? expectedSteps = null, Action<Action>? registerStop = null, bool provideChoiceProof = false)
            {
                var service = new GameCraftService(fixture.Snapshot, catalog, aliases, fixture.Execute,
                    provideChoiceProof ? (_, _) => fixture.LastChoiceConsumed : null);
                return await service.RunFixtureAsync(recipe, 1, token, app, protectedCounts, adaptive, expectedSteps, registerStop);
            }
            var bottom = new Fixture(new() { ["b001"] = 4 });
            var result = await Execute(bottom, "r003");
            Check(Status(result) == "succeeded" && Completed(result) == 3 && Total(result) == 3, "bottom-up success result");
            Check(bottom.Calls.SequenceEqual(["r001", "r002", "r003"]) && bottom.Count("g001") == 1 && bottom.Count("b001") == 0,
                "bottom-up order and exact replacement");
            passed++;

            var direct = new Fixture(new() { ["m001"] = 1, ["m002"] = 1, ["g001"] = 2 });
            result = await Execute(direct, "r003");
            Check(Status(result) == "succeeded" && Completed(result) == 1 && direct.Calls.SequenceEqual(["r003"]) && direct.Count("g001") == 3,
                "direct craft adds one target without rebuilding held intermediate");
            passed++;

            var partial = new Fixture(new() { ["b001"] = 4 });
            partial.OnExecute = (step, call) => call == 2 ? "두 번째 단계의 모의 입력 실패" : null;
            result = await Execute(partial, "r003");
            Check(Status(result) == "failed" && Completed(result) == 1 && Total(result) == 3 &&
                Message(result).Contains("1단계 완료 후") && partial.Calls.SequenceEqual(["r001", "r002"]) &&
                partial.Count("m001") == 1 && partial.Count("g001") == 0, "second stage failure preserves first result and never invokes third");
            passed++;

            var session = new Fixture(new() { ["b001"] = 4 });
            session.OnSnapshot = fixture =>
            {
                if (fixture.Calls.Count == 1 && ++fixture.ReadsAfterFirst == 2) fixture.Identity = "00010001:00020000:00000000";
            };
            result = await Execute(session, "r003");
            Check(Status(result) == "failed" && Completed(result) == 1 && session.Calls.SequenceEqual(["r001"]),
                "session change between steps stops before more input");
            passed++;

            var betweenDraw = new Fixture(new() { ["b001"] = 4 });
            betweenDraw.OnSnapshot = fixture =>
            {
                if (fixture.Calls.Count == 1 && ++fixture.ReadsAfterFirst == 2) fixture.Add("x001", 1);
            };
            result = await Execute(betweenDraw, "r003");
            Check(Status(result) == "failed" && Completed(result) == 1 && betweenDraw.Calls.SequenceEqual(["r001"]),
                "concurrent draw between steps stops remaining sequence");
            passed++;

            var duringDraw = new Fixture(new() { ["b001"] = 4 });
            duringDraw.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("x001", 1); };
            result = await Execute(duringDraw, "r003");
            Check(Status(result) == "failed" && Completed(result) == 0 && duringDraw.Calls.SequenceEqual(["r001"]) &&
                Message(result).Contains("다른 유닛 변화"), "unrelated change during verification cannot be reported as success");
            passed++;

            var repeated = new Fixture(new() { ["b001"] = 4 });
            result = await Execute(repeated, "r004");
            Check(Status(result) == "succeeded" && Completed(result) == 3 && repeated.Calls.SequenceEqual(["r001", "r001", "r004"]) &&
                repeated.Count("g002") == 1, "repeated recipe occurs exactly once for each required plan step");
            passed++;

            var delayed = new Fixture(new() { ["m001"] = 1, ["m002"] = 1 }) { DelayResult = true };
            result = await Execute(delayed, "r003");
            Check(Status(result) == "succeeded" && delayed.Calls.Count == 1 && delayed.Count("g001") == 1,
                "ingredient and output frames may arrive separately without duplicate input");
            passed++;

            using var canceled = new CancellationTokenSource();
            var cancelBetween = new Fixture(new() { ["b001"] = 4 });
            cancelBetween.OnSnapshot = fixture =>
            {
                if (fixture.Calls.Count == 1 && ++fixture.ReadsAfterFirst == 2) canceled.Cancel();
            };
            result = await Execute(cancelBetween, "r003", canceled.Token);
            Check(Status(result) == "failed" && Completed(result) == 1 && cancelBetween.Calls.SequenceEqual(["r001"]),
                "cancellation never invokes another real or simulated input");
            passed++;

            var wisps = new Fixture(new() { ["e018"] = 1 });
            var choiceResultReads = 0;
            wisps.OnSnapshot = fixture => { if (fixture.Calls.Count == 1) choiceResultReads++; };
            wisps.OnExecute = (_, call) => { if (call == 2) Check(choiceResultReads >= 2, "a choice result must be stable in two fresh snapshots before the next input"); return null; };
            result = await Execute(wisps, "r005");
            if (GameChoiceInput.Supported)
                Check(Status(result) == "succeeded" && Completed(result) == 2 && wisps.Calls.SequenceEqual(["choice:h003", "r005"]) &&
                    wisps.Count("e018") == 0 && wisps.Count("g003") == 1, "choice wisp consumption is verified before craft step");
            else
                Check(Status(result) == "failed" && wisps.Calls.Count == 0, "unsupported choice path fails without input");
            passed++;

            var flickeringChoice = new Fixture(new() { ["e018"] = 1 });
            var flickeringReads = 0;
            flickeringChoice.OnSnapshot = fixture =>
            {
                if (fixture.Calls.Count != 1) return;
                flickeringReads++;
                if (flickeringReads == 2) fixture.Add("h003", -1);
                if (flickeringReads == 3) fixture.Add("h003", 1);
            };
            flickeringChoice.OnExecute = (_, call) => { if (call == 2) Check(flickeringReads >= 4, "a transient matching choice frame is not enough to continue"); return null; };
            result = await Execute(flickeringChoice, "r005");
            Check(Status(result) == "succeeded" && flickeringChoice.Calls.SequenceEqual(["choice:h003", "r005"]) && flickeringReads >= 4,
                "choice result flicker waits for two fresh stable frames without another input");
            passed++;

            var guard = new Fixture(new() { ["b001"] = 4 });
            var guardedService = new GameCraftService(guard.Snapshot, catalog, aliases);
            var rejected = false;
            try { await guardedService.RunFixtureAsync("r003", 1, CancellationToken.None, app); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && guard.Calls.Count == 0, "fixture seam refuses to fall back to Windows input");
            passed++;
            var protectedCore = new Fixture(new() { ["m001"] = 1, ["m002"] = 1, ["b001"] = 2, ["x001"] = 1 });
            result = await Execute(protectedCore, "r003", protectedCounts: new Dictionary<string, int> { ["m001"] = 1, ["x001"] = 1 });
            Check(Status(result) == "succeeded" && protectedCore.Calls.SequenceEqual(["r001", "r003"]) && protectedCore.Count("m001") == 1 && protectedCore.Count("x001") == 1 && protectedCore.Count("g001") == 1,
                "protected existing core and unrelated support remain through the full recursive target craft");
            passed++;
            var protectedBlocked = new Fixture(new() { ["m001"] = 1, ["m002"] = 1 });
            result = await Execute(protectedBlocked, "r003", protectedCounts: new Dictionary<string, int> { ["m001"] = 1 });
            Check(Status(result) == "failed" && protectedBlocked.Calls.Count == 0 && protectedBlocked.Count("m001") == 1, "no unprotected fallback is attempted when preserving the core needs more materials");
            passed++;
            var protectedMissing = new Fixture(new() { ["m001"] = 1, ["m002"] = 1, ["b001"] = 2 });
            result = await Execute(protectedMissing, "r003", protectedCounts: new Dictionary<string, int> { ["m001"] = 2 });
            Check(Status(result) == "failed" && protectedMissing.Calls.Count == 0, "stale requested protection fails before any input");
            passed++;
            var protectedChanged = new Fixture(new() { ["b001"] = 4, ["x001"] = 1 });
            protectedChanged.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("x001", -1); };
            result = await Execute(protectedChanged, "r003", protectedCounts: new Dictionary<string, int> { ["x001"] = 1 });
            Check(Status(result) == "failed" && Completed(result) == 0 && protectedChanged.Calls.SequenceEqual(["r001"]) && Message(result).Contains("보존할 유닛"), "protected unit loss during verification stops before another step");
            passed++;
            foreach (var invalid in new[]
            {
                new Dictionary<string, int> { ["m001"] = 0 }, new Dictionary<string, int> { ["M001"] = 1 },
                new Dictionary<string, int> { ["zzzz"] = 1 }, new Dictionary<string, int> { ["e018"] = 1 },
                new Dictionary<string, int> { ["m001"] = 1001 }
            })
            {
                var invalidFixture = new Fixture(new() { ["m001"] = 1, ["m002"] = 1 });
                var invalidRejected = false;
                try { await Execute(invalidFixture, "r003", protectedCounts: invalid); }
                catch (ArgumentException) { invalidRejected = true; }
                Check(invalidRejected && invalidFixture.Calls.Count == 0, "invalid, non-character or differently-cased protection cannot reach input");
            }
            Check(GameCraftService.SameProtection(new Dictionary<string, int> { ["m001"] = 1, ["m002"] = 2 }, new Dictionary<string, int> { ["m002"] = 2, ["m001"] = 1 }) &&
                !GameCraftService.SameProtection(new Dictionary<string, int> { ["m001"] = 1 }, new Dictionary<string, int> { ["m001"] = 2 }), "idempotent request identity includes exact protection independent of property order");
            passed++;
            RequestedCraftStep[] InitialPath() => [new("craft", "r001"), new("craft", "r002"), new("craft", "r003")];
            var adaptiveDraw = new Fixture(new() { ["b001"] = 4 });
            adaptiveDraw.OnSnapshot = fixture =>
            {
                if (fixture.Calls.Count == 1 && ++fixture.ReadsAfterFirst == 2) fixture.Add("m002", 1);
            };
            result = await Execute(adaptiveDraw, "r003", adaptive: true, expectedSteps: InitialPath());
            Check(Status(result) == "succeeded" && Completed(result) == 2 && Total(result) == 2 &&
                adaptiveDraw.Calls.SequenceEqual(["r001", "r003"]) && adaptiveDraw.Count("b001") == 2,
                "adaptive replanning uses newly owned material and omits its obsolete production step");
            passed++;
            var adaptiveDuring = new Fixture(new() { ["b001"] = 4 });
            adaptiveDuring.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("x001", 1); };
            result = await Execute(adaptiveDuring, "r003", adaptive: true, expectedSteps: InitialPath());
            Check(Status(result) == "succeeded" && Completed(result) == 3 && adaptiveDuring.Count("x001") == 1,
                "adaptive verification permits a different rawcode draw during an exactly verified step");
            passed++;
            var externalGoal = new Fixture(new() { ["b001"] = 4 });
            externalGoal.OnSnapshot = fixture => { if (++fixture.ReadsAfterFirst == 2) fixture.Add("g001", 1); };
            result = await Execute(externalGoal, "r003", adaptive: true, expectedSteps: InitialPath());
            Check(Status(result) == "succeeded" && Completed(result) == 0 && externalGoal.Calls.Count == 0 && externalGoal.Count("g001") == 1,
                "an externally completed original goal is not crafted a second time");
            passed++;
            var invalidAdaptivePrefix = new Fixture(new() { ["b001"] = 4 });
            result = await Execute(invalidAdaptivePrefix, "r003", adaptive: true, expectedSteps: [new("craft", "r003")]);
            Check(Status(result) == "failed" && invalidAdaptivePrefix.Calls.Count == 0,
                "adaptive mode still validates the complete submitted path before consuming any material");
            passed++;
            var adaptiveSession = new Fixture(new() { ["b001"] = 4 });
            adaptiveSession.OnSnapshot = fixture =>
            {
                if (fixture.Calls.Count == 1 && ++fixture.ReadsAfterFirst == 2) fixture.Identity = "00010001:00020000:00000000";
            };
            result = await Execute(adaptiveSession, "r003", adaptive: true);
            Check(Status(result) == "failed" && Completed(result) == 1 && adaptiveSession.Calls.SequenceEqual(["r001"]),
                "adaptive replanning never crosses a game session boundary");
            passed++;
            var adaptiveProtection = new Fixture(new() { ["b001"] = 4, ["x001"] = 1 });
            adaptiveProtection.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("x001", -1); };
            result = await Execute(adaptiveProtection, "r003", protectedCounts: new Dictionary<string, int> { ["x001"] = 1 }, adaptive: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && adaptiveProtection.Calls.SequenceEqual(["r001"]),
                "adaptive unrelated-draw tolerance never permits losing a protected completed unit");
            passed++;
            var sameCodeDraw = new Fixture(new() { ["b001"] = 4 });
            sameCodeDraw.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("m001", 1); };
            result = await Execute(sameCodeDraw, "r003", adaptive: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && sameCodeDraw.Calls.SequenceEqual(["r001"]),
                "a simultaneous same-result draw cannot be mistaken for a verified single craft");
            passed++;
            var stoppedBeforeInput = new Fixture(new() { ["b001"] = 4 });
            result = await Execute(stoppedBeforeInput, "r003", adaptive: true, registerStop: stop => { stop(); stop(); });
            Check(Status(result) == "stopped" && Completed(result) == 0 && stoppedBeforeInput.Calls.Count == 0,
                "a repeated queued stop has no input side effects");
            passed++;
            Action? stopCurrent = null;
            var stopDuring = new Fixture(new() { ["b001"] = 4 }) { DelayResult = true };
            stopDuring.AfterExecute = (_, _, _) => { stopCurrent!(); stopCurrent(); };
            result = await Execute(stopDuring, "r003", adaptive: true, registerStop: stop => stopCurrent = stop);
            Check(Status(result) == "stopped" && Completed(result) == 1 && Total(result) == 3 && stopDuring.Calls.SequenceEqual(["r001"]) &&
                stopDuring.Count("m001") == 1 && result.GetProperty("stopRequested").GetBoolean(),
                "stop waits for the current split-frame result and never invokes another step");
            passed++;
            var storyDuringCraft = new Fixture(new() { ["b001"] = 4, ["e018"] = 1 });
            storyDuringCraft.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("e018", 3); };
            result = await Execute(storyDuringCraft, "r003", adaptive: true, expectedSteps: InitialPath());
            Check(Status(result) == "succeeded" && storyDuringCraft.Calls.SequenceEqual(["r001", "r002", "r003"]) && storyDuringCraft.Count("e018") == 4,
                "story choice-wisp rewards during ordinary craft do not invalidate exact material/result deltas");
            passed++;
            var storyBetweenChoices = new Fixture(new() { ["e018"] = 2 });
            storyBetweenChoices.OnSnapshot = fixture =>
            {
                // 두 결과 프레임이 안정된 다음 단계 경계에서 들어오는 보상이다.
                if (fixture.Calls.Count == 1 && ++fixture.ReadsAfterFirst == 3) fixture.Add("e018", 3);
            };
            result = await Execute(storyBetweenChoices, "r008", adaptive: true);
            Check(Status(result) == "succeeded" && storyBetweenChoices.ChoiceWispBaselines.SequenceEqual([2, 4]) && storyBetweenChoices.Count("e018") == 3,
                "a story reward between choice stages is replanned from the latest actual wisp inventory");
            passed++;
            var storyDuringChoice = new Fixture(new() { ["e018"] = 2 });
            storyDuringChoice.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("e018", 2); };
            result = await Execute(storyDuringChoice, "r007", adaptive: true, provideChoiceProof: true);
            Check(Status(result) == "succeeded" && Completed(result) == 3 && storyDuringChoice.Calls.SequenceEqual(["choice:h003x2", "r007"]) &&
                storyDuringChoice.ChoiceWispBaselines.SequenceEqual([2]) && storyDuringChoice.Count("g005") == 1 && storyDuringChoice.Count("e018") == 2,
                "verified consumption of all selected wisps permits a simultaneous reward while result +N remains exact");
            passed++;
            var unprovenChoiceReward = new Fixture(new() { ["e018"] = 2 });
            unprovenChoiceReward.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("e018", 2); };
            result = await Execute(unprovenChoiceReward, "r007", adaptive: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && unprovenChoiceReward.Calls.SequenceEqual(["choice:h003x2"]),
                "result +N with a masked wisp decrement cannot succeed without independent consumption proof");
            passed++;
            var unconsumedChoice = new Fixture(new() { ["e018"] = 2 }) { SkipChoiceConsumption = true };
            unconsumedChoice.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("e018", 1); };
            result = await Execute(unconsumedChoice, "r007", adaptive: true, provideChoiceProof: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && !unconsumedChoice.LastChoiceConsumed && unconsumedChoice.Calls.Count == 1,
                "an unconsumed wisp plus an unrelated matching result never supplies consumption proof");
            passed++;
            var extraChoiceLoss = new Fixture(new() { ["e018"] = 3 });
            extraChoiceLoss.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("e018", -1); };
            result = await Execute(extraChoiceLoss, "r007", adaptive: true, provideChoiceProof: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && extraChoiceLoss.Calls.Count == 1,
                "consumption proof never permits an observed loss beyond the requested batch");
            passed++;
            var extraChoiceResult = new Fixture(new() { ["e018"] = 2 });
            extraChoiceResult.AfterExecute = (fixture, _, call) => { if (call == 1) { fixture.Add("e018", 2); fixture.Add("h003", 1); } };
            result = await Execute(extraChoiceResult, "r007", adaptive: true, provideChoiceProof: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && extraChoiceResult.Calls.Count == 1,
                "consumed-wisp proof never relaxes the exact batch result requirement");
            passed++;
            var choiceSessionChange = new Fixture(new() { ["e018"] = 2 });
            choiceSessionChange.AfterExecute = (fixture, _, call) =>
            {
                if (call == 1) { fixture.Add("e018", 2); fixture.Identity = "00010001:00020000:00000000"; }
            };
            result = await Execute(choiceSessionChange, "r007", adaptive: true, provideChoiceProof: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && choiceSessionChange.Calls.Count == 1,
                "a story reward and proof cannot cross a changed game session");
            passed++;
            Action? stopRewardChoice = null;
            var stopReward = new Fixture(new() { ["e018"] = 2 });
            stopReward.AfterExecute = (fixture, _, call) => { if (call == 1) { fixture.Add("e018", 2); stopRewardChoice!(); } };
            result = await Execute(stopReward, "r007", adaptive: true, provideChoiceProof: true, registerStop: stop => stopRewardChoice = stop);
            Check(Status(result) == "stopped" && Completed(result) == 2 && stopReward.Calls.Count == 1 && stopReward.Count("h003") == 2,
                "stop verifies a proof-backed current batch and reward but invokes no next stage");
            passed++;
            using var cancelReward = new CancellationTokenSource();
            var canceledRewardChoice = new Fixture(new() { ["e018"] = 2 });
            canceledRewardChoice.AfterExecute = (fixture, _, call) => { if (call == 1) { fixture.Add("e018", 2); cancelReward.Cancel(); } };
            result = await Execute(canceledRewardChoice, "r007", cancelReward.Token, adaptive: true, provideChoiceProof: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && canceledRewardChoice.Calls.Count == 1,
                "cancellation after choice input never resends an ambiguous current step or advances");
            passed++;
            var prefetch = new Fixture(new() { ["e018"] = 4 });
            result = await Execute(prefetch, "r009", adaptive: true);
            Check(Status(result) == "succeeded" && Completed(result) == 6 &&
                prefetch.Calls.SequenceEqual(["choice:h003x3", "choice:h002", "r005", "r009"]) && prefetch.Count("g007") == 1,
                "all missing common materials are prepared in same-result batches before ordinary crafting");
            passed++;
            var fixedPrefetch = new Fixture(new() { ["e018"] = 4 });
            result = await Execute(fixedPrefetch, "r009", expectedSteps: [new("choice", ResultRawcode: "h003"), new("craft", "r005"),
                new("choice", ResultRawcode: "h003"), new("choice", ResultRawcode: "h003"), new("choice", ResultRawcode: "h002"), new("craft", "r009")]);
            Check(Status(result) == "succeeded" && Completed(result) == 6 && Total(result) == 6 &&
                fixedPrefetch.Calls.SequenceEqual(["choice:h003x3", "choice:h002", "r005", "r009"]),
                "a validated fixed exact path can be frontloaded while logical progress and final inventory remain exact");
            passed++;
            var extraMaterial = new Fixture(new() { ["e018"] = 4 });
            extraMaterial.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("h002", 1); };
            result = await Execute(extraMaterial, "r009", adaptive: true);
            Check(Status(result) == "succeeded" && extraMaterial.Calls.SequenceEqual(["choice:h003x3", "r005", "r009"]) && extraMaterial.Count("e018") == 1,
                "a new material reward cancels the now-unnecessary next choice batch using current inventory");
            passed++;
            var existingCommon = new Fixture(new() { ["h003"] = 2 });
            result = await Execute(existingCommon, "r007", adaptive: true);
            Check(Status(result) == "succeeded" && existingCommon.Calls.SequenceEqual(["r007"]),
                "owned common material is used without preparing any choice wisps");
            passed++;
            var thirteen = new Fixture(new() { ["e018"] = 13 });
            result = await Execute(thirteen, "r010", adaptive: true);
            Check(Status(result) == "succeeded" && Completed(result) == 14 && thirteen.Calls.SequenceEqual(["choice:h003x12", "choice:h003", "r010"]),
                "thirteen required commons split into twelve and one while keeping fourteen logical steps");
            passed++;
            var batchOverBudget = new Fixture(new() { ["e018"] = 64 });
            result = await Execute(batchOverBudget, "r011", adaptive: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && batchOverBudget.Calls.Count == 0,
                "batching does not turn a sixty-five-logical-step path into an executable request");
            passed++;
            var partialBatch = new Fixture(new() { ["e018"] = 2 });
            partialBatch.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Add("h003", -1); };
            result = await Execute(partialBatch, "r007", adaptive: true, provideChoiceProof: true);
            Check(Status(result) == "failed" && Completed(result) == 0 && partialBatch.Calls.SequenceEqual(["choice:h003x2"]) && partialBatch.Count("h003") == 1,
                "partial batch output never succeeds or resends the consumed group");
            passed++;
            Check(GameCraftService.RemainingAdaptiveStepBudget(0) == 64 && GameCraftService.RemainingAdaptiveStepBudget(63) == 1 &&
                GameCraftService.RemainingAdaptiveStepBudget(64) == 0 && GameCraftService.RemainingAdaptiveStepBudget(65) == 0,
                "adaptive actual input count has an absolute 64-step budget");
            var capped = new Fixture(new() { ["b001"] = 126 });
            capped.OnSnapshot = fixture =>
            {
                if (fixture.Calls.Count == 64 && ++fixture.ReadsAfterFirst == 2) { fixture.Add("g004", -1); fixture.Add("b001", 126); }
            };
            result = await Execute(capped, "r006", adaptive: true);
            Check(Status(result) == "failed" && Completed(result) == 64 && capped.Calls.Count == 64 && Message(result).Contains("64단계"),
                "a lost goal after input 64 cannot trigger input 65 or an automatic retry");
            passed++;
            var publicFixture = new Fixture(new() { ["m001"] = 1, ["m002"] = 1, ["b001"] = 2, ["x001"] = 1 });
            var publicService = new GameCraftService(publicFixture.Snapshot, catalog, aliases, publicFixture.Execute);
            publicService.Map(app);
            var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
            async Task<(int Status, JsonElement Body)> Request(string route, string method, string? body = null, string? requestToken = null, string? id = null)
            {
                var endpoint = endpoints.Single(item => item.RoutePattern.RawText == route);
                var context = new DefaultHttpContext { RequestServices = app.Services };
                context.Request.Method = method; context.Request.Scheme = "http"; context.Request.Host = new HostString("127.0.0.1", 8765);
                context.Request.Headers.Origin = "http://127.0.0.1:8765";
                if (requestToken is not null) context.Request.Headers["X-Craft-Token"] = requestToken;
                if (id is not null) context.Request.RouteValues["id"] = id;
                if (body is not null)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    context.Request.ContentType = "application/json"; context.Request.ContentLength = bytes.Length;
                    context.Request.Body = new MemoryStream(bytes);
                }
                await using var response = new MemoryStream(); context.Response.Body = response;
                await endpoint.RequestDelegate!(context);
                response.Position = 0;
                using var document = await JsonDocument.ParseAsync(response);
                return (context.Response.StatusCode, document.RootElement.Clone());
            }
            var capabilities = await Request("/api/craft/capabilities", "GET");
            Check(capabilities.Body.GetProperty("protectedCountsSupported").GetBoolean(), "public capability advertises server-enforced protection");
            Check(capabilities.Body.GetProperty("adaptiveCraftSupported").GetBoolean() && capabilities.Body.GetProperty("stopCraftSupported").GetBoolean(),
                "public capabilities advertise adaptive and stop contracts");
            var publicToken = capabilities.Body.GetProperty("token").GetString()!;
            var publicId = Guid.NewGuid().ToString("D");
            var publicBody = JsonSerializer.Serialize(new { recipeId = "r003", requestId = publicId, snapshotSequence = 1,
                protectedCounts = new Dictionary<string, int> { ["m001"] = 1, ["x001"] = 1 } });
            var queued = await Request("/api/craft", "POST", publicBody, publicToken);
            Check(queued.Status == 202, "public protected target request is accepted");
            var duplicate = await Request("/api/craft", "POST", publicBody, publicToken);
            Check(duplicate.Status == 200, "identical public retry returns the existing operation");
            var altered = await Request("/api/craft", "POST", publicBody.Replace("\"m001\":1", "\"m001\":2", StringComparison.Ordinal), publicToken);
            Check(altered.Status == 409, "same UUID with changed protected counts is rejected");
            var alteredMode = await Request("/api/craft", "POST", publicBody.Replace("\"recipeId\":", "\"adaptive\":true,\"recipeId\":", StringComparison.Ordinal), publicToken);
            Check(alteredMode.Status == 409, "same UUID cannot switch between fixed and adaptive execution");
            var fractional = await Request("/api/craft", "POST", publicBody.Replace("\"m001\":1", "\"m001\":1.5", StringComparison.Ordinal), publicToken);
            Check(fractional.Status == 400, "fractional protected quantities are rejected by the public JSON contract");
            var publicResult = queued.Body;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var observed = await Request("/api/craft/{id}", "GET", id: publicId); publicResult = observed.Body;
                if (Status(publicResult) is "succeeded" or "failed") break;
                await Task.Delay(25);
            }
            Check(Status(publicResult) == "succeeded" && publicFixture.Calls.SequenceEqual(["r001", "r003"]) && publicFixture.Count("m001") == 1 && publicFixture.Count("x001") == 1,
                "public protected request recursively completes once without consuming the preserved core or support");
            passed++;
            Check((await Request("/api/craft/{id}/stop", "POST", requestToken: "wrong", id: publicId)).Status == 403,
                "stop uses the same authorization as craft");
            var terminalStop = await Request("/api/craft/{id}/stop", "POST", requestToken: publicToken, id: publicId);
            var duplicateStop = await Request("/api/craft/{id}/stop", "POST", requestToken: publicToken, id: publicId);
            Check(Status(terminalStop.Body) == "succeeded" && terminalStop.Body.GetRawText() == duplicateStop.Body.GetRawText() && publicFixture.Calls.Count == 2,
                "stopping a completed operation repeatedly is an idempotent no-op");
            Check((await Request("/api/craft/{id}/stop", "POST", requestToken: publicToken, id: Guid.NewGuid().ToString("D"))).Status == 404,
                "an unknown stop UUID never targets another operation");
            passed++;
            Check(capabilities.Body.GetProperty("exactPlanSupported").GetBoolean(), "public capability advertises an exact verified deck path");
            publicFixture.Add("b001", 2); publicFixture.Add("m002", 1);
            using var beforeExact = JsonDocument.Parse(publicFixture.Snapshot());
            var exactId = Guid.NewGuid().ToString("D");
            var exactBody = JsonSerializer.Serialize(new { recipeId = "r003", requestId = exactId,
                snapshotSequence = beforeExact.RootElement.GetProperty("sequence").GetInt64(),
                expectedSessionIdentity = beforeExact.RootElement.GetProperty("sessionIdentity").GetString(),
                protectedCounts = new Dictionary<string, int> { ["m001"] = 1, ["x001"] = 1 },
                expectedSteps = new[] { new RequestedCraftStep("craft", "r001"), new RequestedCraftStep("craft", "r003") } });
            var exactQueued = await Request("/api/craft", "POST", exactBody, publicToken);
            Check(exactQueued.Status == 202, "a fully specified deck prefix is accepted");
            var alteredExact = await Request("/api/craft", "POST", exactBody.Replace("\"r001\"", "\"r002\"", StringComparison.Ordinal), publicToken);
            Check(alteredExact.Status == 409, "same UUID with a different exact plan is rejected");
            var overrideResult = await Request("/api/craft", "POST", exactBody.Replace("\"ResultRawcode\":null", "\"ResultRawcode\":\"x001\"", StringComparison.Ordinal), publicToken);
            Check(overrideResult.Status == 400, "client result overrides do not change native craft outputs");
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var observed = await Request("/api/craft/{id}", "GET", id: exactId); publicResult = observed.Body;
                if (Status(publicResult) is "succeeded" or "failed") break;
                await Task.Delay(25);
            }
            Check(Status(publicResult) == "succeeded" && publicFixture.Calls.TakeLast(2).SequenceEqual(["r001", "r003"]) &&
                publicFixture.Count("m001") == 1 && publicFixture.Count("x001") == 1 && publicFixture.Count("g001") == 2,
                "the public exact plan executes only the specified prefix and preserves the completed deck members");
            passed++;
            var missingSession = System.Text.Json.Nodes.JsonNode.Parse(exactBody)!.AsObject();
            missingSession.Remove("expectedSessionIdentity"); missingSession["requestId"] = Guid.NewGuid().ToString("D");
            Check((await Request("/api/craft", "POST", missingSession.ToJsonString(), publicToken)).Status == 400,
                "an exact deck path requires the session captured at the click");
            var changedSession = System.Text.Json.Nodes.JsonNode.Parse(exactBody)!.AsObject();
            var changedId = Guid.NewGuid().ToString("D"); changedSession["requestId"] = changedId;
            changedSession["expectedSessionIdentity"] = "FFFFFFFF:FFFFFFFF:FFFFFFFF";
            var beforeWrongSessionCalls = publicFixture.Calls.Count;
            Check((await Request("/api/craft", "POST", changedSession.ToJsonString(), publicToken)).Status == 202,
                "session changes are validated before any input in the worker");
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var observed = await Request("/api/craft/{id}", "GET", id: changedId); publicResult = observed.Body;
                if (Status(publicResult) is "succeeded" or "failed") break;
                await Task.Delay(25);
            }
            Check(Status(publicResult) == "failed" && Message(publicResult).Contains("게임이 바뀌어") && publicFixture.Calls.Count == beforeWrongSessionCalls,
                "an old session cannot spend identical materials in a new game");
            passed++;
            publicFixture.Add("b001", 4);
            var stopId = Guid.NewGuid().ToString("D");
            var beforeStopCalls = publicFixture.Calls.Count;
            var requestedStop = false;
            publicFixture.AfterExecute = (_, _, _) =>
            {
                if (requestedStop) return;
                requestedStop = true;
                var stopResponse = Request("/api/craft/{id}/stop", "POST", requestToken: publicToken, id: stopId).GetAwaiter().GetResult();
                Check(stopResponse.Status == 200 && stopResponse.Body.GetProperty("stopRequested").GetBoolean(), "active stop acknowledgement preserves its operation");
            };
            using var stopSnapshot = JsonDocument.Parse(publicFixture.Snapshot());
            var stopBody = JsonSerializer.Serialize(new { recipeId = "r003", requestId = stopId, adaptive = true,
                snapshotSequence = stopSnapshot.RootElement.GetProperty("sequence").GetInt64(),
                expectedSessionIdentity = stopSnapshot.RootElement.GetProperty("sessionIdentity").GetString(),
                protectedCounts = new Dictionary<string, int> { ["m001"] = 1, ["x001"] = 1 }, expectedSteps = InitialPath() });
            Check((await Request("/api/craft", "POST", stopBody, publicToken)).Status == 202, "adaptive exact initial plan can be queued publicly");
            for (var attempt = 0; attempt < 50; attempt++)
            {
                publicResult = (await Request("/api/craft/{id}", "GET", id: stopId)).Body;
                if (Status(publicResult) is "stopped" or "failed") break;
                await Task.Delay(25);
            }
            publicFixture.AfterExecute = null;
            Check(Status(publicResult) == "stopped" && Completed(publicResult) == 1 && publicFixture.Calls.Count == beforeStopCalls + 1,
                "the authenticated public stop finishes only its in-flight stage");
            Check(Status((await Request("/api/craft", "POST", stopBody, publicToken)).Body) == "stopped" && publicFixture.Calls.Count == beforeStopCalls + 1,
                "retrying the stopped UUID returns progress without restarting its inputs");
            passed++;

            var costs = new Dictionary<string, (int Wood, int Gold)>
            {
                ["r001"] = (1, 100), ["r002"] = (2, 200), ["r003"] = (3, 300), ["r005"] = (3, 0)
            };
            WriteCatalog(catalog, aliases, costs);
            Fixture ResourceFixture(int wood = 6, int gold = 600, string? status = "confirmed") =>
                new(new() { ["b001"] = 4 }) { Wood = wood, Gold = gold, ResourcesStatus = status, ResourceCosts = costs };

            foreach (var status in new string?[] { null, "unknown" })
            {
                var unknownResources = ResourceFixture(status: status);
                result = await Execute(unknownResources, "r003");
                Check(Status(result) == "failed" && unknownResources.Calls.Count == 0 && unknownResources.Count("b001") == 4,
                    "missing or unknown resources cannot spend even a first lower-tier ingredient");
                passed++;
            }
            var shortWood = ResourceFixture(wood: 5);
            result = await Execute(shortWood, "r003");
            Check(Status(result) == "failed" && Message(result).Contains("목재") && shortWood.Calls.Count == 0,
                "all six wood are required before the affordable first one-wood step");
            passed++;
            var shortGold = ResourceFixture(gold: 599);
            result = await Execute(shortGold, "r003");
            Check(Status(result) == "failed" && Message(result).Contains("금화") && shortGold.Calls.Count == 0,
                "the complete 600-gold plan is checked before the first input");
            passed++;
            var wrongResourcePlayer = ResourceFixture(); wrongResourcePlayer.ResourcePlayer = 1;
            result = await Execute(wrongResourcePlayer, "r003");
            Check(Status(result) == "failed" && wrongResourcePlayer.Calls.Count == 0, "another player's resources cannot authorize spending");
            passed++;
            var spentWood = ResourceFixture();
            spentWood.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Wood = 4; };
            result = await Execute(spentWood, "r003");
            Check(Status(result) == "failed" && Completed(result) == 1 && spentWood.Calls.SequenceEqual(["r001"]) && Message(result).Contains("목재"),
                "external wood use after the first step stops before the next input");
            passed++;
            var spentGold = ResourceFixture();
            spentGold.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.Gold = 499; };
            result = await Execute(spentGold, "r003");
            Check(Status(result) == "failed" && Completed(result) == 1 && spentGold.Calls.SequenceEqual(["r001"]) && Message(result).Contains("금화"),
                "external gold use after the first step stops before the next input");
            passed++;
            var resourcesLost = ResourceFixture();
            resourcesLost.AfterExecute = (fixture, _, call) => { if (call == 1) fixture.ResourcesStatus = "unknown"; };
            result = await Execute(resourcesLost, "r003");
            Check(Status(result) == "failed" && Completed(result) == 1 && resourcesLost.Calls.SequenceEqual(["r001"]),
                "a resource read becoming unknown never spends the next ingredient");
            passed++;
            var funded = ResourceFixture();
            result = await Execute(funded, "r003");
            Check(Status(result) == "succeeded" && Completed(result) == 3 && funded.Calls.SequenceEqual(["r001", "r002", "r003"]) &&
                funded.Wood == 0 && funded.Gold == 0 && funded.Count("g001") == 1,
                "exact full-plan resources fund each remaining suffix once");
            passed++;
            var repeatedBudget = ResourceFixture(wood: 1, gold: 200);
            result = await Execute(repeatedBudget, "r004");
            Check(Status(result) == "failed" && repeatedBudget.Calls.Count == 0, "repeated copies of a lower recipe each contribute their resource cost");
            passed++;
            var costlyChoice = new Fixture(new() { ["e018"] = 1 })
                { Wood = 2, Gold = 0, ResourcesStatus = "confirmed", ResourceCosts = costs };
            result = await Execute(costlyChoice, "r005");
            Check(Status(result) == "failed" && costlyChoice.Calls.Count == 0 && costlyChoice.Count("e018") == 1,
                "insufficient final-step wood cannot consume a choice wisp first");
            passed++;
            var adaptiveBudget = ResourceFixture();
            adaptiveBudget.AfterExecute = (fixture, _, call) =>
            {
                if (call == 1) { fixture.Add("m002", 1); fixture.Wood = 3; fixture.Gold = 300; }
            };
            result = await Execute(adaptiveBudget, "r003", adaptive: true, expectedSteps: InitialPath());
            Check(Status(result) == "succeeded" && adaptiveBudget.Calls.SequenceEqual(["r001", "r003"]) && adaptiveBudget.Wood == 0 && adaptiveBudget.Gold == 0,
                "adaptive resource checks use the complete newly shortened suffix rather than a stale reservation");
            passed++;
            Console.WriteLine($"PASS: {passed} no-input craft-service sequence cases (ordering, direct craft, exact deck prefixes, partial failure, session, concurrent draws, repetition, split frames, cancellation, choice and fixture guard).");
        }
        finally
        {
            File.Delete(catalog);
            File.Delete(aliases);
            Directory.Delete(folder); // 직접 만든 두 파일만 지우며 재귀 삭제하지 않는다.
        }
    }

    private static void WriteCatalog(string catalog, string aliases, IReadOnlyDictionary<string, (int Wood, int Gold)>? costs = null)
    {
        object Recipe(string id, string target, params CraftIngredient[] ingredients)
        {
            var all = ingredients.Select(item => (object)new { type = "UNIT", rawcode = item.Rawcode, count = item.Count }).ToList();
            if (costs is not null && costs.TryGetValue(id, out var cost))
            {
                if (cost.Wood > 0) all.Add(new { type = "WOOD", rawcode = "WOOD", count = cost.Wood });
                if (cost.Gold > 0) all.Add(new { type = "GOLD", rawcode = "GOLD", count = cost.Gold });
            }
            return new { id, resultUnitRawcode = target, resultCount = 1, hotkey = "Z",
                casterUnitRawcodes = new[] { ingredients[0].Rawcode }, ingredients = all };
        }
        File.WriteAllText(catalog, JsonSerializer.Serialize(new
        {
            map = new { sha256 = Hash },
            units = CharacterCodes.Select(rawcode => new { rawcode, playable = true }).Concat(new[]
            {
                new { rawcode = "e018", playable = false }, new { rawcode = "e0IX", playable = false }
            }),
            recipes = new[]
            {
                Recipe("r001", "m001", new CraftIngredient("b001", 2)), Recipe("r002", "m002", new CraftIngredient("b001", 2)),
                Recipe("r003", "g001", new CraftIngredient("m001", 1), new CraftIngredient("m002", 1)), Recipe("r004", "g002", new CraftIngredient("m001", 2)),
                Recipe("r005", "g003", new CraftIngredient("h003", 1)), Recipe("r006", "g004", new CraftIngredient("m001", 63)),
                Recipe("r007", "g005", new CraftIngredient("h003", 2)),
                Recipe("r008", "g006", new CraftIngredient("h003", 1), new CraftIngredient("h002", 1)),
                Recipe("r009", "g007", new CraftIngredient("g003", 1), new CraftIngredient("h003", 2), new CraftIngredient("h002", 1)),
                Recipe("r010", "g008", new CraftIngredient("h003", 13)),
                Recipe("r011", "g009", new CraftIngredient("h003", 64))
            }
        }));
        File.WriteAllText(aliases, "{\"recipes\":[]}");
    }

    private static string Status(JsonElement result) => result.GetProperty("status").GetString()!;
    private static string Message(JsonElement result) => result.GetProperty("message").GetString()!;
    private static int Completed(JsonElement result) => result.GetProperty("completedSteps").GetInt32();
    private static int Total(JsonElement result) => result.GetProperty("totalSteps").GetInt32();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Craft sequence test failed: " + message); }

    private sealed class Fixture
    {
        private readonly Dictionary<string, int> stock;
        private long sequence;
        private PlannedCraftStep? pendingResult;
        private int pendingReads;
        public List<string> Calls { get; } = [];
        public List<int> ChoiceWispBaselines { get; } = [];
        public bool LastChoiceConsumed { get; private set; }
        public bool SkipChoiceConsumption { get; init; }
        public string Identity { get; set; } = "00010000:00020000:00000000";
        public int ReadsAfterFirst { get; set; }
        public bool DelayResult { get; init; }
        public string? ResourcesStatus { get; set; }
        public int? Wood { get; set; }
        public int? Gold { get; set; }
        public int ResourcePlayer { get; set; }
        public IReadOnlyDictionary<string, (int Wood, int Gold)>? ResourceCosts { get; init; }
        public Action<Fixture>? OnSnapshot { get; set; }
        public Func<PlannedCraftStep, int, string?>? OnExecute { get; set; }
        public Action<Fixture, PlannedCraftStep, int>? AfterExecute { get; set; }
        public Fixture(Dictionary<string, int> inventory) => stock = new(inventory, StringComparer.Ordinal);
        public int Count(string code) => stock.GetValueOrDefault(code);
        public void Add(string code, int count) => stock[code] = checked(Count(code) + count);
        public string Snapshot()
        {
            OnSnapshot?.Invoke(this);
            if (pendingResult is { } pending && ++pendingReads >= 2) { Add(pending.ResultRawcode, pending.ResultCount); pendingResult = null; }
            return JsonSerializer.Serialize(new
            {
                status = "snapshot_read", timestamp = DateTimeOffset.UtcNow, sequence = ++sequence,
                processId = 123456789, gameVersion = "1.28.5.7680", loadedMap = "fixture-map.w3x", loadedMapHash = Hash,
                catalogMatchesLoadedArchive = true, localPlayer = 0, readErrors = 0, stableRoots = true, stableUnits = true,
                sessionIdentity = Identity,
                resources = ResourcesStatus is null ? null : new { status = ResourcesStatus, wood = Wood, gold = Gold, localPlayer = ResourcePlayer },
                counts = stock.Where(pair => pair.Value > 0).Select(pair => new
                { rawcode = pair.Key, count = pair.Value, isCharacter = CharacterCodes.Contains(pair.Key, StringComparer.Ordinal) })
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        }
        public Task<string?> Execute(PlannedCraftStep step, JsonElement baseline, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            LastChoiceConsumed = false;
            Calls.Add(step.Type == "choice" ? "choice:" + step.ResultRawcode + (step.ResultCount > 1 ? "x" + step.ResultCount : "") : step.RecipeId!);
            if (step.Type == "choice") ChoiceWispBaselines.Add(baseline.GetProperty("counts").EnumerateArray()
                .Where(unit => unit.GetProperty("rawcode").GetString() == "e018").Select(unit => unit.GetProperty("count").GetInt32()).SingleOrDefault());
            var error = OnExecute?.Invoke(step, Calls.Count);
            if (error is not null) return Task.FromResult<string?>(error);
            if (step.Type == "craft" && step.RecipeId is not null && ResourceCosts is not null && ResourceCosts.TryGetValue(step.RecipeId, out var cost))
            {
                if (Wood is null || Gold is null || Wood < cost.Wood || Gold < cost.Gold)
                    throw new Exception("Simulated step overspent resources.");
                Wood -= cost.Wood; Gold -= cost.Gold;
            }
            foreach (var item in step.Ingredients)
            {
                if (step.Type == "choice" && SkipChoiceConsumption) continue;
                if (Count(item.Rawcode) < item.Count) throw new Exception("Simulated step double-spent an ingredient.");
                Add(item.Rawcode, -item.Count);
            }
            LastChoiceConsumed = step.Type == "choice" && !SkipChoiceConsumption;
            if (DelayResult) { pendingResult = step; pendingReads = 0; }
            else Add(step.ResultRawcode, step.ResultCount);
            Add("e0IX", 1); // 실제 게임의 위습 보상은 캐릭터 동시 변경으로 오인하지 않아야 한다.
            AfterExecute?.Invoke(this, step, Calls.Count);
            return Task.FromResult<string?>(null);
        }
    }
}
