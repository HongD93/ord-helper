using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

record CraftIngredient(string Rawcode, int Count, string[]? AcceptedRawcodes = null);
record CraftCommand(string RecipeId, string ResultRawcode, int ResultCount,
    CraftIngredient[] Ingredients, string? ChatCommand, string? Hotkey, string[] CasterUnitRawcodes,
    int WoodCost = 0, int GoldCost = 0, CraftIngredient[]? ItemRequirements = null, CraftRecipeCondition[]? Conditions = null,
    string? MaterialChoiceRawcode = null);

sealed class GameCraftService
{
    private const int MaximumRequests = 256;
    private const int MaximumBodyBytes = 16384;
    private readonly Func<string> latestSnapshotJson;
    private readonly Dictionary<string, CraftCommand> commands = new(StringComparer.Ordinal);
    private readonly List<UnsupportedCraftRecipe> unsupportedRecipes = [];
    private sealed record UnsupportedCraftRecipe(string RecipeId, string ResultRawcode, string Reason, string Message, string[] RequirementTypes);
    private readonly HashSet<string> characterRawcodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CraftOperation> requests = new(StringComparer.Ordinal);
    private readonly object requestsGate = new();
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string catalogHash = "";
    private readonly string? initializationError;
    private readonly Func<PlannedCraftStep, JsonElement, CancellationToken, Task<string?>>? stepExecutor;
    private readonly Func<PlannedCraftStep, JsonElement, bool>? choiceConsumptionProof;
    private readonly Func<long, CancellationToken, Task>? waitForSnapshot;
    private readonly Action<bool>? craftActivityChanged;

    public GameCraftService(Func<string> latestSnapshotJson, string catalogPath, string commandsPath,
        Func<PlannedCraftStep, JsonElement, CancellationToken, Task<string?>>? stepExecutor = null,
        Func<PlannedCraftStep, JsonElement, bool>? choiceConsumptionProof = null,
        Func<long, CancellationToken, Task>? waitForSnapshot = null, Action<bool>? craftActivityChanged = null)
    {
        this.latestSnapshotJson = latestSnapshotJson;
        this.stepExecutor = stepExecutor;
        this.choiceConsumptionProof = choiceConsumptionProof;
        this.waitForSnapshot = waitForSnapshot;
        this.craftActivityChanged = craftActivityChanged;
        try
        {
            using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
            using var aliases = JsonDocument.Parse(File.ReadAllText(commandsPath));
            catalogHash = catalog.RootElement.GetProperty("map").GetProperty("sha256").GetString() ?? "";
            if (catalogHash.Length != 64 || !catalogHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid map digest.");
            foreach (var unit in catalog.RootElement.GetProperty("units").EnumerateArray())
                if (unit.GetProperty("playable").GetBoolean())
                    characterRawcodes.Add(RequiredRawcode(unit, "rawcode"));
            var chatCommands = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var alias in aliases.RootElement.GetProperty("recipes").EnumerateArray())
            {
                var id = RequiredRawcode(alias, "recipeId");
                var command = alias.GetProperty("command").GetString();
                if (!GameInput.IsKoreanChatCommand(command))
                    throw new InvalidDataException("Invalid map chat command.");
                chatCommands.Add(id, command);
            }
            foreach (var recipe in catalog.RootElement.GetProperty("recipes").EnumerateArray())
            {
                var id = RequiredRawcode(recipe, "id");
                var result = RequiredRawcode(recipe, "resultUnitRawcode");
                // 실제 입력 근거가 있어도 읽지 못하는 추가 조건을 조용히 버리지 않는다.
                var unsupportedTypes = recipe.GetProperty("ingredients").EnumerateArray().Where(item =>
                    !new[] { "UNIT", "WOOD", "GOLD", "ITEM" }.Contains(item.GetProperty("type").GetString(), StringComparer.Ordinal) &&
                    !CraftConditions.Types.Contains(item.GetProperty("type").GetString(), StringComparer.Ordinal)).Select(item => item.GetProperty("type").GetString()!).Distinct().ToArray();
                if (unsupportedTypes.Length > 0)
                {
                    var targeted = unsupportedTypes.Contains("PICK", StringComparer.Ordinal);
                    unsupportedRecipes.Add(new(id, result, targeted ? "targeted_input_required" : "unsupported_recipe_requirements",
                        targeted ? "대상 지정 조합은 자동 실행을 지원하지 않습니다." : "추가 조합 조건을 확인하지 못했습니다.", unsupportedTypes));
                    continue;
                }
                var resultCount = recipe.GetProperty("resultCount").GetInt32();
                if (resultCount <= 0 || resultCount > 1000) throw new InvalidDataException("Invalid result count.");
                var ingredients = recipe.GetProperty("ingredients").EnumerateArray()
                    .Where(item => item.GetProperty("type").GetString() == "UNIT")
                    .Select(item => new CraftIngredient(RequiredRawcode(item, "rawcode"), item.GetProperty("count").GetInt32(),
                        item.TryGetProperty("acceptedRawcodes", out var accepted) && accepted.ValueKind != JsonValueKind.Null ? accepted.EnumerateArray().Select(value => value.GetString() ?? "").ToArray() : null))
                    .ToArray();
                if (ingredients.Any(item => item.Count <= 0 || item.Count > 1000))
                    throw new InvalidDataException("Invalid ingredient count.");
                ingredients = CraftMaterialChoices.NormalizeIngredients(id, result, ingredients);
                int ResourceCost(string type)
                {
                    var total = 0;
                    foreach (var item in recipe.GetProperty("ingredients").EnumerateArray()
                        .Where(item => item.GetProperty("type").GetString() == type))
                    {
                        var amount = item.GetProperty("count").GetInt32();
                        if (amount <= 0 || amount > GameResources.MaximumSupportedAmount)
                            throw new InvalidDataException("Invalid native resource cost.");
                        total = checked(total + amount);
                    }
                    return total;
                }
                var hotkey = recipe.TryGetProperty("hotkey", out var key) && key.ValueKind == JsonValueKind.String
                    ? key.GetString() : null;
                var casters = recipe.GetProperty("casterUnitRawcodes").EnumerateArray()
                    .Select(value => value.GetString() ?? "").ToArray();
                if (casters.Any(value => !IsRawcode(value))) throw new InvalidDataException("Invalid caster ID.");
                var itemRequirements = recipe.GetProperty("ingredients").EnumerateArray()
                    .Where(item => item.GetProperty("type").GetString() == "ITEM")
                    .Select(item => new CraftIngredient(RequiredRawcode(item, "rawcode"), item.GetProperty("count").GetInt32())).ToArray();
                if (itemRequirements.Any(item => item.Count is < 1 or > 1000)) throw new InvalidDataException("Invalid item cost.");
                var conditions = recipe.GetProperty("ingredients").EnumerateArray()
                    .Where(item => CraftConditions.Types.Contains(item.GetProperty("type").GetString(), StringComparer.Ordinal))
                    .Select(item => new CraftRecipeCondition(item.GetProperty("type").GetString()!, RequiredRawcode(item, "rawcode"), item.GetProperty("count").GetInt32())).ToArray();
                if (conditions.Any(item => item.Count is < 1 or > 1000)) throw new InvalidDataException("Invalid recipe condition.");
                var command = new CraftCommand(id, result, resultCount, ingredients,
                    chatCommands.GetValueOrDefault(id), hotkey, casters, ResourceCost("WOOD"), ResourceCost("GOLD"), itemRequirements, conditions);
                if (ingredients.Any(item => item.AcceptedRawcodes is not null) &&
                    (catalogHash != GameProgression.SupportedMapHash || !CraftMaterialChoices.TryResolve(command, "H099", out _)))
                    throw new InvalidDataException("Unverified map-specific material choice.");
                if (GameInput.Supported(command)) commands.Add(id, command);
                else unsupportedRecipes.Add(new(id, result, "unverified_input_method", "조합 입력 방법을 확인하지 못했습니다.", []));
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or KeyNotFoundException or OverflowException)
        {
            commands.Clear();
            initializationError = "조합 정보를 불러오지 못했습니다. 프로그램을 다시 실행해 주세요.";
        }
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/api/craft/capabilities", () => Results.Json(new
        {
            enabled = initializationError is null,
            continuousCraftSupported = true,
            choiceWispSupported = GameChoiceInput.Supported,
            partialChoiceRecoverySupported = true,
            protectedCountsSupported = true,
            exactPlanSupported = true,
            adaptiveCraftSupported = true,
            stopCraftSupported = true,
            itemRequirementsSupported = true,
            readableItemRawcodes = CraftItems.Codes,
            recipeConditionsSupported = true,
            specialRecipeConditionsSupported = true,
            readableConditionTypes = CraftConditions.Types,
            supportedRecipeIds = commands.Keys.Order(StringComparer.Ordinal).ToArray(),
            unsupportedRecipes = unsupportedRecipes.OrderBy(recipe => recipe.RecipeId, StringComparer.Ordinal).ToArray(),
            token
        }));
        app.MapGet("/api/craft/{id}", IResult (string id) =>
        {
            if (!Guid.TryParseExact(id, "D", out var parsed))
                return Results.NotFound(new CraftResult("failed", id, "조합 요청을 찾지 못했습니다."));
            lock (requestsGate)
                return requests.TryGetValue(parsed.ToString("D"), out var operation)
                    ? Results.Json(operation.Result)
                    : Results.NotFound(new CraftResult("failed", id, "조합 요청을 찾지 못했습니다."));
        });
        app.MapPost("/api/craft/{id}/stop", IResult (HttpContext context, string id) =>
        {
            if (!Authorized(context.Request))
                return Results.Json(new { status = "failed", message = "이 화면에서 보낸 조합 요청만 중지할 수 있습니다." }, statusCode: 403);
            if (!Guid.TryParseExact(id, "D", out var parsed))
                return Results.NotFound(new CraftResult("failed", id, "조합 요청을 찾지 못했습니다."));
            lock (requestsGate)
                return requests.TryGetValue(parsed.ToString("D"), out var operation)
                    ? Results.Json(operation.RequestStop())
                    : Results.NotFound(new CraftResult("failed", id, "조합 요청을 찾지 못했습니다."));
        });
        app.MapPost("/api/craft", async Task<IResult> (HttpContext context) =>
        {
            if (!Authorized(context.Request))
                return Results.Json(new { status = "failed", message = "이 화면에서 보낸 조합 요청만 실행할 수 있습니다." }, statusCode: 403);
            if (!context.Request.HasJsonContentType())
                return Results.Json(new { status = "failed", message = "조합 요청 형식이 올바르지 않습니다." }, statusCode: 415);
            CraftRequest request;
            try { request = await ReadRequestAsync(context.Request); }
            catch (Exception error) when (error is JsonException or IOException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                return Results.Json(new { status = "failed", message = "조합 요청 형식이 올바르지 않습니다." }, statusCode: 400);
            }
            if (!Guid.TryParseExact(request.RequestId, "D", out var requestGuid) ||
                string.IsNullOrEmpty(request.RecipeId) || request.SnapshotSequence <= 0)
                return Results.Json(new { status = "failed", message = "조합 요청을 확인할 수 없습니다. 화면을 새로고침해 주세요." }, statusCode: 400);
            if (!TryProtection(request.ProtectedCounts, out var protectedCounts))
                return Results.Json(new { status = "failed", message = "보존할 유닛 정보를 확인하지 못했습니다." }, statusCode: 400);
            if (!TryExpectedSteps(request.ExpectedSteps, request.RecipeId, out var expectedSteps))
                return Results.Json(new { status = "failed", message = "제작 순서를 확인하지 못했습니다." }, statusCode: 400);
            if (request.ExpectedSessionIdentity is not null && !ValidIdentity(request.ExpectedSessionIdentity) ||
                expectedSteps is not null && request.ExpectedSessionIdentity is null)
                return Results.Json(new { status = "failed", message = "조합을 누른 게임 정보를 확인하지 못했습니다." }, statusCode: 400);
            var requestId = requestGuid.ToString("D");
            CraftOperation operation;
            lock (requestsGate)
            {
                if (requests.TryGetValue(requestId, out var previous))
                {
                    if (previous.RecipeId != request.RecipeId || !SameProtection(previous.ProtectedCounts, protectedCounts) ||
                        !SameExpectedSteps(previous.ExpectedSteps, expectedSteps) || previous.ExpectedSessionIdentity != request.ExpectedSessionIdentity ||
                        previous.Adaptive != request.Adaptive)
                        return Results.Json(new CraftResult("failed", requestId, "이미 사용된 조합 요청입니다."), statusCode: 409);
                    return Results.Json(previous.Result);
                }
                // 완료된 ID도 보존한다. 오래된 ID를 제거하면 같은 요청이 다시 실행될 수 있다.
                if (requests.Count >= MaximumRequests)
                    return Results.Json(new CraftResult("failed", requestId,
                        "이번 실행의 조합 요청 한도에 도달했습니다. 진행 중인 조합을 확인한 뒤 프로그램을 다시 실행해 주세요."), statusCode: 429);
                operation = new CraftOperation(requestId, request.RecipeId, protectedCounts, expectedSteps, request.ExpectedSessionIdentity, request.Adaptive);
                requests.Add(requestId, operation);
            }
            if (initializationError is not null)
            {
                operation.Complete("failed", initializationError);
                return Results.Json(operation.Result);
            }
            if (!commands.TryGetValue(request.RecipeId, out var command))
            {
                operation.Complete("unsupported", "이 유닛의 자동 조합은 아직 지원하지 않습니다.");
                return Results.Json(operation.Result);
            }
            _ = Task.Run(() => RunAsync(operation, command, request.SnapshotSequence,
                app.Lifetime.ApplicationStopping, app));
            return Results.Json(operation.Result, statusCode: 202);
        });
    }

    private async Task RunAsync(CraftOperation operation, CraftCommand command, long requestedSequence,
        CancellationToken stopping, WebApplication app)
    {
        var acquired = false;
        var inputAttempted = false;
        ChoiceWispCameraSession? cameraSession = null;
        try
        {
            await inputGate.WaitAsync(stopping);
            acquired = true;
            craftActivityChanged?.Invoke(true);
            cameraSession = GameChoiceInput.CreateCameraSession();
            stopping.ThrowIfCancellationRequested();
            if (operation.StopRequested) { operation.CompleteStopped(); return; }
            if (!TryReadSnapshot(out var baseline, out var reason))
            {
                operation.Complete("failed", reason);
                return;
            }
            if (operation.ExpectedSessionIdentity is not null && operation.ExpectedSessionIdentity != baseline.Session.Identity)
            {
                operation.Complete("failed", "조합을 누른 게임이 바뀌어 실행하지 않았습니다.");
                return;
            }
            if (requestedSequence > baseline.Sequence || baseline.Sequence - requestedSequence > 30)
            {
                operation.Complete("failed", "보유량이 갱신되었습니다. 현재 수량을 확인한 뒤 다시 눌러 주세요.");
                return;
            }
            var plan = operation.ExpectedSteps is null
                ? CraftPlanner.Plan(command.RecipeId, commands.Values, baseline.Counts,
                    allowChoiceWisps: GameChoiceInput.Supported, protectedCounts: operation.ProtectedCounts, items: CraftItems.Read(baseline.Json), recipeConditions: CraftConditions.Read(baseline.Json))
                : CraftPlanner.ValidateExpected(command.RecipeId, commands.Values, baseline.Counts, operation.ExpectedSteps,
                    allowChoiceWisps: GameChoiceInput.Supported, protectedCounts: operation.ProtectedCounts, items: CraftItems.Read(baseline.Json), recipeConditions: CraftConditions.Read(baseline.Json));
            if (!plan.Executable)
            {
                operation.Complete("failed", plan.Error ?? "조합에 필요한 재료 유닛이 부족합니다.");
                return;
            }
            if (!ChoiceWispBatchPlan.TryCreate(plan.Steps, baseline.Counts, operation.ProtectedCounts, out var preparedSteps))
            {
                operation.Complete("failed", "선택 위습의 제작 순서를 확인하지 못했습니다.");
                return;
            }
            plan = plan with { Steps = preparedSteps };
            // 최종 단계의 비용도 지금 확보돼야 하위 재료/선택위습부터 소비하지 않는다.
            if (!TryRemainingResourceCosts(plan.Steps, CraftConditions.Read(baseline.Json), out var resourceCosts, out reason) ||
                !ResourcesAvailable(baseline, resourceCosts[0], out reason))
            {
                operation.Complete("failed", reason);
                return;
            }
            var origin = baseline;
            var targetCount = checked(origin.Counts.GetValueOrDefault(command.ResultRawcode) + command.ResultCount);
            var cumulative = new Dictionary<string, int>(StringComparer.Ordinal);
            var index = 0;
            var fixedPlanPosition = 0;
            while (operation.Adaptive || fixedPlanPosition < plan.Steps.Length)
            {
                stopping.ThrowIfCancellationRequested();
                if (operation.StopRequested) { operation.CompleteStopped(); return; }
                // 다음 단계로 넘어가기 직전에도 세션과 전체 예상 수량을 다시 확인한다.
                if (!TryReadSnapshot(out baseline, out reason) || !origin.Session.Matches(baseline.Session) ||
                    !operation.Adaptive && !DeltaMatches(origin, baseline, cumulative))
                {
                    operation.Complete("failed", Stopped(index, "보유량이나 게임이 바뀌어 조합을 중단했습니다."));
                    return;
                }
                if (operation.Adaptive)
                {
                    if (!ProtectionPresent(operation.ProtectedCounts, baseline.Counts))
                    {
                        operation.Complete("failed", Stopped(index, "보존할 유닛 수량이 바뀌어 조합을 중단했습니다."));
                        return;
                    }
                    if (baseline.Counts.GetValueOrDefault(command.ResultRawcode) >= targetCount)
                    {
                        operation.Complete("succeeded", index == 0 ? "목표 유닛이 이미 완성되었습니다." : $"{index}단계 조합을 완료했습니다.");
                        return;
                    }
                    var remainingBudget = RemainingAdaptiveStepBudget(index);
                    if (remainingBudget == 0)
                    {
                        operation.Complete("failed", Stopped(index, "64단계 한도에 도달해 조합을 중단했습니다."));
                        return;
                    }
                    plan = CraftPlanner.Plan(command.RecipeId, commands.Values, baseline.Counts,
                        allowChoiceWisps: GameChoiceInput.Supported, maxSteps: remainingBudget, protectedCounts: operation.ProtectedCounts,
                        items: CraftItems.Read(baseline.Json), recipeConditions: CraftConditions.Read(baseline.Json));
                    if (!plan.Executable)
                    {
                        operation.Complete("failed", Stopped(index, plan.Reason == "step_limit"
                            ? "남은 조합을 64단계 안에 완료할 수 없어 중단했습니다."
                            : plan.Error ?? "현재 재료로 조합을 이어갈 수 없습니다."));
                        return;
                    }
                    if (!ChoiceWispBatchPlan.TryCreate(plan.Steps, baseline.Counts, operation.ProtectedCounts, out preparedSteps))
                    {
                        operation.Complete("failed", Stopped(index, "선택 위습의 제작 순서를 확인하지 못했습니다."));
                        return;
                    }
                    plan = plan with { Steps = preparedSteps };
                }
                var total = operation.Adaptive ? index + ChoiceWispBatchPlan.LogicalCount(plan.Steps) : ChoiceWispBatchPlan.LogicalCount(plan.Steps);
                operation.Progress(index, total);
                var step = plan.Steps[operation.Adaptive ? 0 : fixedPlanPosition];
                var remainingSteps = plan.Steps.Skip(operation.Adaptive ? 0 : fixedPlanPosition).ToArray();
                if (!TryRemainingResourceCosts(remainingSteps, CraftConditions.Read(baseline.Json), out resourceCosts, out reason) ||
                    !ResourcesAvailable(baseline, resourceCosts[0], out reason))
                {
                    operation.Complete("failed", Stopped(index, reason));
                    return;
                }
                if (!CraftItems.Available(CraftItems.Read(baseline.Json), remainingSteps.SelectMany(value => value.ItemRequirements ?? []), out reason))
                {
                    operation.Complete("failed", Stopped(index, reason));
                    return;
                }
                if (!CraftConditions.Available(CraftConditions.Read(baseline.Json), remainingSteps.SelectMany(value => value.Conditions ?? []), out reason, out _))
                {
                    operation.Complete("failed", Stopped(index, reason));
                    return;
                }
                if (!ProtectionPresent(operation.ProtectedCounts, baseline.Counts) ||
                    step.Ingredients.Any(ingredient => baseline.Counts.GetValueOrDefault(ingredient.Rawcode) - ingredient.Count < operation.ProtectedCounts.GetValueOrDefault(ingredient.Rawcode)))
                {
                    operation.Complete("failed", Stopped(index, "다음 조합에 필요한 재료가 부족해 중단했습니다."));
                    return;
                }
                var expected = new Dictionary<string, int>(StringComparer.Ordinal) { [step.ResultRawcode] = step.ResultCount };
                foreach (var ingredient in step.Ingredients)
                    expected[ingredient.Rawcode] = checked(expected.GetValueOrDefault(ingredient.Rawcode) - ingredient.Count);
                if (step.Type != "choice") await GameChoiceInput.RestoreCameraAsync(cameraSession!, CancellationToken.None);
                stopping.ThrowIfCancellationRequested();
                // 각 단계의 정상 입력은 정확히 한 번이다. 실패·시간 초과 뒤에는 재전송하지 않는다.
                if (!operation.TryBeginStep()) { operation.CompleteStopped(); return; }
                inputAttempted = true;
                string? inputError;
                var consumedWispVerified = false;
                if (stepExecutor is not null)
                {
                    inputError = await stepExecutor(step, baseline.Json, stopping);
                    consumedWispVerified = inputError is null && step.Type == "choice" && choiceConsumptionProof?.Invoke(step, baseline.Json) == true;
                }
                else if (step.Type == "choice")
                {
                    var execution = await GameChoiceInput.ExecuteWithProofAsync(step.ResultRawcode, baseline.Json, stopping, step.ResultCount, cameraSession);
                    inputError = execution.Error;
                    consumedWispVerified = execution.ConsumedWispVerified;
                }
                else
                {
                    if (!CraftMaterialChoices.TryResolve(commands[step.RecipeId!], step.MaterialChoiceRawcode, out var resolvedCommand) ||
                        !CraftMaterialChoices.SameIngredients(resolvedCommand.Ingredients, step.Ingredients))
                    {
                        operation.Complete("failed", Stopped(index, "조합 재료 선택을 확인하지 못했습니다."));
                        return;
                    }
                    inputError = await GameInput.ExecuteAsync(resolvedCommand, baseline.Json, stopping, requireForeground: true);
                }
                if (inputError is not null)
                {
                    operation.Complete("failed", Stopped(index, inputError));
                    return;
                }
                var watch = Stopwatch.StartNew();
                var verified = false;
                var unexpectedChanges = false;
                var choiceResultStability = step.Type == "choice" || step.ItemRequirements is { Length: > 0 } ||
                    (step.Conditions ?? []).Any(condition => condition.Type is "IFCT" or "CHCT" or "SKPT" or "NIKA") ? new ChoiceWispResultStability() : null;
                // 스토리 보상과 선택 위습 소모가 겹쳐도, 정확히 선택한 객체의 소모 증거가 있을 때만 순증을 허용한다.
                var allowChoiceWispReward = operation.Adaptive && ProofBackedChoice(step, consumedWispVerified);
                var seenSequence = baseline.Sequence;
                var firstResultFrame = true;
                var resultTimeout = step.Type == "choice" ? ChoiceWispWaitPolicy.ResultTimeout : TimeSpan.FromSeconds(5);
                while (watch.Elapsed < resultTimeout)
                {
                    if (!firstResultFrame)
                    {
                        if (waitForSnapshot is not null) await waitForSnapshot(seenSequence, stopping);
                        else await Task.Delay(20, stopping);
                    }
                    firstResultFrame = false;
                    stopping.ThrowIfCancellationRequested();
                    if (watch.Elapsed >= resultTimeout) break;
                    var frame = ReadFrame();
                    if (frame.TryGetProperty("sequence", out var observedSequenceValue) && observedSequenceValue.TryGetInt64(out var observedSequence))
                        seenSequence = Math.Max(seenSequence, observedSequence);
                    if (TrySession(frame, out var observedSession) && !origin.Session.Matches(observedSession))
                    {
                        operation.Complete("failed", Stopped(index, "게임이나 플레이어가 바뀌어 조합을 중단했습니다."));
                        return;
                    }
                    if (Text(frame, "status") is "game_not_running" or "multiple_games" or "unsupported_game_version"
                        or "game_module_not_ready" or "world_not_ready" or "collector_failed")
                    {
                        operation.Complete("failed", Stopped(index, "게임 연결이 끊겨 조합을 중단했습니다."));
                        return;
                    }
                    if (!TrySnapshot(frame, out var current, out _)) { choiceResultStability?.Reset(); continue; }
                    if (current.Sequence <= baseline.Sequence) continue;
                    if (!origin.Session.Matches(current.Session))
                    {
                        operation.Complete("failed", Stopped(index, "게임이나 플레이어가 바뀌어 조합을 중단했습니다."));
                        return;
                    }
                    if (!ProtectionPresent(operation.ProtectedCounts, current.Counts))
                    {
                        operation.Complete("failed", Stopped(index, "보존할 유닛 수량이 바뀌어 조합을 중단했습니다."));
                        return;
                    }
                    // 재료 소모·결과 생성이 서로 다른 프레임에 보여도 기다리되, 다른 캐릭터 변화는 허용하지 않는다.
                    if (operation.Adaptive)
                    {
                        if (UnexpectedExpectedDelta(baseline, current, expected, allowChoiceWispReward))
                        {
                            operation.Complete("failed", Stopped(index, "같은 재료나 결과 수량이 함께 바뀌어 조합 결과를 확정하지 못했습니다."));
                            return;
                        }
                    }
                    else
                    {
                        var codes = TrackedCodes(baseline, current, expected);
                        if (codes.Any(code => !expected.ContainsKey(code) &&
                            current.Counts.GetValueOrDefault(code) != baseline.Counts.GetValueOrDefault(code))) unexpectedChanges = true;
                    }
                    var resultCountsMatch = (operation.Adaptive ? ExpectedDeltaMatches(baseline, current, expected, allowChoiceWispReward) : DeltaMatches(baseline, current, expected)) &&
                        !unexpectedChanges && CraftItems.ConsumptionMatches(baseline.Json, current.Json, step.ItemRequirements ?? []) &&
                        GameSpecialRecipeConditions.ConsumptionMatches(baseline.Json, current.Json, step.Conditions ?? []);
                    if (choiceResultStability?.Observe(current.Sequence, resultCountsMatch) ?? resultCountsMatch)
                    {
                        verified = true;
                        break;
                    }
                }
                if (!verified)
                {
                    operation.Complete("failed", Stopped(index, unexpectedChanges
                        ? "다른 유닛 변화가 함께 발생해 결과를 확정하지 못했습니다. 게임에서 결과를 확인해 주세요."
                        : "재료 소모와 결과 생성을 확인하지 못했습니다. 게임에서 결과와 조합 조건을 확인해 주세요."));
                    return;
                }
                foreach (var delta in expected) cumulative[delta.Key] = checked(cumulative.GetValueOrDefault(delta.Key) + delta.Value);
                index += step.Type == "choice" ? step.ResultCount : 1;
                fixedPlanPosition++;
                operation.StepCompleted(index, total);
                app.Logger.LogInformation("Craft {RequestId} verified step {Step}/{Total}: {Type} {Recipe} -> {Result} x{Count}",
                    operation.RequestId, index, total, step.Type, step.RecipeId, step.ResultRawcode, step.ResultCount);
                if (operation.StopRequested) { operation.CompleteStopped(); return; }
            }
            operation.Complete("succeeded", index == 1 ? "조합을 완료했습니다." : $"{index}단계 조합을 완료했습니다.");
        }
        catch (OperationCanceledException)
        {
            operation.Complete("failed", inputAttempted
                ? "프로그램이 종료되어 조합 결과를 확인하지 못했습니다. 게임에서 결과를 확인해 주세요. 자동으로 다시 실행하지 않습니다."
                : "프로그램 종료로 조합 요청을 취소했습니다.");
        }
        catch (Exception error)
        {
            app.Logger.LogError(error, "Craft request {RequestId} stopped.", operation.RequestId);
            operation.Complete("failed", inputAttempted
                ? "조합 결과 확인 중 문제가 발생했습니다. 결과를 확인하기 전 같은 조합을 다시 누르지 마세요. 자동으로 다시 실행하지 않습니다."
                : "조합 요청을 확인하지 못해 실행하지 않았습니다.");
        }
        finally
        {
            try
            {
                if (cameraSession is not null) await GameChoiceInput.RestoreCameraAsync(cameraSession, CancellationToken.None);
            }
            finally
            {
                if (acquired)
                {
                    try { craftActivityChanged?.Invoke(false); }
                    finally { inputGate.Release(); }
                }
            }
        }
    }

    // 자체 검사에서는 주입한 실행기만 허용하며 Windows 입력 경로에 도달하지 않는다.
    internal async Task<JsonElement> RunFixtureAsync(string recipeId, long sequence, CancellationToken cancel, WebApplication app,
        IReadOnlyDictionary<string, int>? protectedCounts = null, bool adaptive = false,
        RequestedCraftStep[]? expectedSteps = null, Action<Action>? registerStop = null)
    {
        if (stepExecutor is null) throw new InvalidOperationException("A fixture executor is required.");
        if (!TryProtection(protectedCounts, out var normalized)) throw new ArgumentException("Invalid fixture protection.", nameof(protectedCounts));
        var operation = new CraftOperation(Guid.NewGuid().ToString("D"), recipeId, normalized, expectedSteps, adaptive: adaptive);
        registerStop?.Invoke(() => operation.RequestStop());
        await RunAsync(operation, commands[recipeId], sequence, cancel, app);
        return JsonSerializer.SerializeToElement(operation.Result, JsonSerializerOptions.Web);
    }

    private static string Stopped(int completed, string reason) => completed == 0 ? reason : $"{completed}단계 완료 후 중단했습니다. {reason}";
    internal static int RemainingAdaptiveStepBudget(int completed) => Math.Max(0, 64 - completed);
    private readonly record struct ResourceCost(int Wood, int Gold);
    private bool TryRemainingResourceCosts(PlannedCraftStep[] steps, CraftConditions.State? conditions, out ResourceCost[] remaining, out string reason)
    {
        remaining = new ResourceCost[steps.Length + 1]; reason = "";
        var costs = new ResourceCost[steps.Length];
        for (var index = 0; index < steps.Length; index++)
        {
            var cost = steps[index].Type == "craft" ? commands[steps[index].RecipeId!] : null;
            if (!CraftConditions.TryProject(conditions, steps[index].Conditions ?? [], out conditions, out var distortionWood, out reason, out _)) return false;
            costs[index] = new(checked((cost?.WoodCost ?? 0) + distortionWood), cost?.GoldCost ?? 0);
        }
        for (var index = steps.Length - 1; index >= 0; index--)
        {
            remaining[index] = new(checked(remaining[index + 1].Wood + costs[index].Wood),
                checked(remaining[index + 1].Gold + costs[index].Gold));
        }
        return true;
    }
    private static bool ResourcesAvailable(Snapshot snapshot, ResourceCost needed, out string reason)
    {
        reason = "";
        if (needed.Wood == 0 && needed.Gold == 0) return true;
        var root = snapshot.Json;
        if (needed.Wood < 0 || needed.Gold < 0 || !root.TryGetProperty("resources", out var resources) ||
            resources.ValueKind != JsonValueKind.Object || Text(resources, "status") != "confirmed" ||
            !resources.TryGetProperty("localPlayer", out var playerValue) || !playerValue.TryGetInt32(out var player) ||
            player != snapshot.Session.Player || player is < 0 or > 11 ||
            !resources.TryGetProperty("wood", out var woodValue) || !woodValue.TryGetInt32(out var wood) ||
            !resources.TryGetProperty("gold", out var goldValue) || !goldValue.TryGetInt32(out var gold) ||
            wood is < 0 or > GameResources.MaximumSupportedAmount || gold is < 0 or > GameResources.MaximumSupportedAmount)
        {
            reason = "금화와 목재를 확인하지 못해 조합을 실행할 수 없습니다.";
            return false;
        }
        if (wood < needed.Wood)
        {
            reason = $"목재가 부족합니다. 필요 {needed.Wood} / 보유 {wood}";
            return false;
        }
        if (gold < needed.Gold)
        {
            reason = $"금화가 부족합니다. 필요 {needed.Gold} / 보유 {gold}";
            return false;
        }
        return true;
    }
    private bool TryProtection(IReadOnlyDictionary<string, int>? supplied, out Dictionary<string, int> normalized)
    {
        normalized = new(StringComparer.Ordinal);
        if (supplied is null) return true;
        if (supplied.Count > 64) return false;
        foreach (var item in supplied)
        {
            if (!IsRawcode(item.Key) || !characterRawcodes.Contains(item.Key) || item.Value is < 1 or > 1000) return false;
            normalized.Add(item.Key, item.Value);
        }
        return true;
    }
    internal static bool SameProtection(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b) =>
        a.Count == b.Count && a.All(item => b.TryGetValue(item.Key, out var value) && item.Value == value);
    private bool TryExpectedSteps(RequestedCraftStep[]? requested, string recipeId, out RequestedCraftStep[]? normalized)
    {
        normalized = null;
        if (requested is null) return true;
        if (requested.Length is < 1 or > 64 || requested.Any(step => step is null)) return false;
        var steps = new List<RequestedCraftStep>();
        foreach (var step in requested)
        {
            if (step.Type == "craft" && step.RecipeId is not null && commands.TryGetValue(step.RecipeId, out var command) &&
                (step.ResultRawcode is null || step.ResultRawcode == command.ResultRawcode) &&
                CraftMaterialChoices.TryResolve(command, step.MaterialChoiceRawcode, out _))
                steps.Add(new("craft", command.RecipeId, command.ResultRawcode, step.MaterialChoiceRawcode));
            else if (step.Type == "choice" && step.RecipeId is null && step.ResultRawcode is not null &&
                step.MaterialChoiceRawcode is null && CraftPlanner.CommonChoiceResults.Contains(step.ResultRawcode, StringComparer.Ordinal))
                steps.Add(new("choice", null, step.ResultRawcode));
            else return false;
        }
        if (steps[^1].Type != "craft" || steps[^1].RecipeId != recipeId) return false;
        normalized = steps.ToArray(); return true;
    }
    internal static bool SameExpectedSteps(IReadOnlyList<RequestedCraftStep>? a, IReadOnlyList<RequestedCraftStep>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);
    private static bool ValidIdentity(string value) => value.Length == 26 && value[8] == ':' && value[17] == ':' &&
        value.Where((_, index) => index is not (8 or 17)).All(Uri.IsHexDigit);
    private static bool ProtectionPresent(IReadOnlyDictionary<string, int> protection, IReadOnlyDictionary<string, int> inventory) =>
        protection.All(item => inventory.GetValueOrDefault(item.Key) >= item.Value);
    private IEnumerable<string> TrackedCodes(Snapshot before, Snapshot after, Dictionary<string, int> expected) =>
        characterRawcodes.Concat(before.Characters).Concat(after.Characters).Concat(expected.Keys).Append("e018").Distinct(StringComparer.Ordinal);
    private bool DeltaMatches(Snapshot before, Snapshot after, Dictionary<string, int> expected) =>
        TrackedCodes(before, after, expected).All(code => after.Counts.GetValueOrDefault(code) - before.Counts.GetValueOrDefault(code) == expected.GetValueOrDefault(code));
    private static bool ProofBackedChoice(PlannedCraftStep step, bool consumedWispVerified) =>
        consumedWispVerified && step.Type == "choice" && step.ResultCount is >= 1 and <= ChoiceWispBatchPlan.MaximumGroupSize &&
        CraftPlanner.CommonChoiceResults.Contains(step.ResultRawcode, StringComparer.Ordinal) &&
        step.Ingredients is [{ Rawcode: "e018" } ingredient] && ingredient.Count == step.ResultCount;
    private static bool ExpectedDeltaMatches(Snapshot before, Snapshot after, Dictionary<string, int> expected, bool allowChoiceWispReward) =>
        expected.All(item => allowChoiceWispReward && item.Key == "e018" && item.Value < 0
            ? after.Counts.GetValueOrDefault(item.Key) - before.Counts.GetValueOrDefault(item.Key) >= item.Value
            : after.Counts.GetValueOrDefault(item.Key) - before.Counts.GetValueOrDefault(item.Key) == item.Value);
    private static bool UnexpectedExpectedDelta(Snapshot before, Snapshot after, Dictionary<string, int> expected, bool allowChoiceWispReward) =>
        expected.Any(item => after.Counts.GetValueOrDefault(item.Key) - before.Counts.GetValueOrDefault(item.Key) < Math.Min(0, item.Value) ||
            !(allowChoiceWispReward && item.Key == "e018" && item.Value < 0) &&
            after.Counts.GetValueOrDefault(item.Key) - before.Counts.GetValueOrDefault(item.Key) > Math.Max(0, item.Value));

    private bool Authorized(HttpRequest request)
    {
        var origin = request.Headers.Origin;
        var supplied = request.Headers["X-Craft-Token"];
        if (origin.Count != 1 || supplied.Count != 1 ||
            !Uri.TryCreate(origin[0], UriKind.Absolute, out var parsed) ||
            parsed.Scheme != request.Scheme || !string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.Authority != request.Host.Value || parsed.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)) return false;
        var value = supplied[0];
        return value is not null && value.Length == token.Length &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(token));
    }

    private static async Task<CraftRequest> ReadRequestAsync(HttpRequest request)
    {
        if (request.ContentLength > MaximumBodyBytes) throw new InvalidDataException("Request too large.");
        var buffer = new byte[MaximumBodyBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(length), request.HttpContext.RequestAborted);
            if (read == 0) break;
            length += read;
        }
        if (length > MaximumBodyBytes) throw new InvalidDataException("Request too large.");
        return JsonSerializer.Deserialize<CraftRequest>(buffer.AsSpan(0, length), JsonSerializerOptions.Web)
            ?? throw new JsonException("Missing request.");
    }

    private JsonElement ReadFrame()
    {
        using var document = JsonDocument.Parse(latestSnapshotJson());
        return document.RootElement.Clone();
    }

    private bool TryReadSnapshot(out Snapshot baseline, out string reason)
    {
        try { return TrySnapshot(ReadFrame(), out baseline, out reason); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            baseline = null!;
            reason = "보유량 정보를 읽지 못해 조합을 실행하지 않았습니다.";
            return false;
        }
    }

    private bool TrySnapshot(JsonElement root, out Snapshot snapshot, out string reason)
    {
        snapshot = null!;
        reason = "게임의 현재 보유량을 확인할 수 없습니다. 연결 상태를 확인해 주세요.";
        if (Text(root, "status") != "snapshot_read" || !Bool(root, "stableRoots") || !Bool(root, "stableUnits") ||
            !Bool(root, "catalogMatchesLoadedArchive") || !root.TryGetProperty("readErrors", out var errors) ||
            !errors.TryGetInt32(out var readErrors) || readErrors != 0 || !TrySession(root, out var session) ||
            !string.Equals(session.MapHash, catalogHash, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("sequence", out var sequenceValue) || !sequenceValue.TryGetInt64(out var sequence) || sequence <= 0 ||
            !root.TryGetProperty("timestamp", out var timestampValue) || !timestampValue.TryGetDateTimeOffset(out var timestamp)) return false;
        var age = DateTimeOffset.UtcNow - timestamp;
        if (age > TimeSpan.FromSeconds(3) || age < TimeSpan.FromSeconds(-1))
        {
            reason = "보유량 정보가 오래되어 조합을 실행할 수 없습니다. 연결 상태를 확인해 주세요.";
            return false;
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var characters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in root.GetProperty("counts").EnumerateArray())
        {
            var rawcode = RequiredRawcode(unit, "rawcode");
            var count = unit.GetProperty("count").GetInt32();
            if (count < 0 || count > 100000) return false;
            counts.Add(rawcode, count);
            if (Bool(unit, "isCharacter")) characters.Add(rawcode);
        }
        snapshot = new Snapshot(root, sequence, session, counts, characters);
        return true;
    }

    private static bool TrySession(JsonElement root, out GameSession session)
    {
        session = null!;
        if (!root.TryGetProperty("processId", out var processValue) || !processValue.TryGetInt32(out var processId) || processId <= 0 ||
            !root.TryGetProperty("localPlayer", out var playerValue) || !playerValue.TryGetInt32(out var player) || player < 0 || player > 31 ||
            Text(root, "loadedMapHash") is not { Length: 64 } hash || Text(root, "loadedMap") is not { Length: > 0 } map ||
            Text(root, "gameVersion") is not { Length: > 0 } version ||
            Text(root, "sessionIdentity") is not { Length: 26 } identity) return false;
        session = new GameSession(processId, player, hash, map, version, identity);
        return true;
    }

    private static string? Text(JsonElement root, string property) => root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Bool(JsonElement root, string property) => root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;
    private static bool IsRawcode(string value) => value.Length == 4 && value.All(character => character >= 0x20 && character <= 0x7E);
    private static string RequiredRawcode(JsonElement root, string property)
    {
        var value = root.GetProperty(property).GetString() ?? "";
        return IsRawcode(value) ? value : throw new InvalidDataException("Invalid rawcode.");
    }

    private sealed record CraftRequest(string RecipeId, string RequestId, long SnapshotSequence,
        Dictionary<string, int>? ProtectedCounts = null, RequestedCraftStep[]? ExpectedSteps = null, string? ExpectedSessionIdentity = null,
        bool Adaptive = false);
    private sealed record CraftResult(string Status, string RequestId, string Message,
        int CompletedSteps = 0, int TotalSteps = 0, bool StopRequested = false);
    private sealed record Snapshot(JsonElement Json, long Sequence, GameSession Session,
        Dictionary<string, int> Counts, HashSet<string> Characters);
    private sealed record GameSession(int ProcessId, int Player, string MapHash, string LoadedMap, string GameVersion, string Identity)
    {
        public bool Matches(GameSession other) => ProcessId == other.ProcessId && Player == other.Player &&
            string.Equals(MapHash, other.MapHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(LoadedMap, other.LoadedMap, StringComparison.OrdinalIgnoreCase) && GameVersion == other.GameVersion && Identity == other.Identity;
    }
    private sealed class CraftOperation(string requestId, string recipeId, IReadOnlyDictionary<string, int>? protectedCounts = null,
        RequestedCraftStep[]? expectedSteps = null, string? expectedSessionIdentity = null, bool adaptive = false)
    {
        private readonly object gate = new();
        private bool stopRequested;
        private CraftResult result = new("queued", requestId, "조합 요청을 확인하고 있습니다.");
        public string RequestId { get; } = requestId;
        public string RecipeId { get; } = recipeId;
        public IReadOnlyDictionary<string, int> ProtectedCounts { get; } = new Dictionary<string, int>(protectedCounts ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        public RequestedCraftStep[]? ExpectedSteps { get; } = expectedSteps?.ToArray();
        public string? ExpectedSessionIdentity { get; } = expectedSessionIdentity;
        public bool Adaptive { get; } = adaptive;
        public bool StopRequested { get { lock (gate) return stopRequested; } }
        public CraftResult Result => Volatile.Read(ref result);
        private bool Terminal => result.Status is "succeeded" or "failed" or "unsupported" or "stopped";
        public CraftResult RequestStop()
        {
            lock (gate)
            {
                if (Terminal) return result;
                stopRequested = true;
                Volatile.Write(ref result, result.Status == "queued"
                    ? result with { Status = "stopped", Message = "조합을 중지했습니다.", StopRequested = true }
                    : result with { Message = "현재 단계 확인 후 중지합니다.", StopRequested = true });
                return result;
            }
        }
        public bool TryBeginStep() { lock (gate) return !stopRequested && !Terminal; }
        public void Progress(int completed, int total)
        {
            lock (gate)
                if (!Terminal) Volatile.Write(ref result, new CraftResult("running", RequestId,
                    stopRequested ? "현재 단계 확인 후 중지합니다." : $"{completed + 1}/{total}단계 조합 중", completed, total, stopRequested));
        }
        public void Complete(string status, string message)
        {
            lock (gate)
            {
                if (Terminal) return;
                if (stopRequested && status == "succeeded") { status = "stopped"; message = "조합을 중지했습니다."; }
                Volatile.Write(ref result, new CraftResult(status, RequestId, message, result.CompletedSteps, result.TotalSteps, stopRequested));
            }
        }
        public void CompleteStopped() => Complete("stopped", "조합을 중지했습니다.");
        public void StepCompleted(int completed, int total)
        {
            lock (gate)
                if (!Terminal) Volatile.Write(ref result, new CraftResult("running", RequestId,
                    stopRequested ? "현재 단계 확인 후 중지합니다." : $"{completed}/{total}단계 완료", completed, total, stopRequested));
        }
    }
}
