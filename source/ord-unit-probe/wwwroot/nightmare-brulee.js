(function (root, factory) {
  const api = factory(typeof module === "object" && module.exports ? require("./recipe-graph.js") : root.OrdRecipeGraph);
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root) root.OrdNightmareBrulee = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function (Graph) {
  "use strict";
  const mapSha256 = "20ce1b0918e56d18ad02a11a6dddf05298d6478037721f20a29702d4cd7b145d";
  const scriptSha256 = "308258a35af767807cabcc595f0f9513ff1aea52c82bcc0171dbdae6239fef13";
  const sourceRawcode = "h08S";
  const creationRecipeId = "A113";
  const allowedRequirements = new Set(["WOOD", "GOLD", "ITEM", "UBAN", "KING", "IFCT", "CHCT", "SKPT", "RMAX", "NIKA"]);
  const list = value => Array.isArray(value) ? value : [];
  const nonnegative = value => Number.isSafeInteger(value) && value >= 0;
  const stockOf = value => new Map(value instanceof Map ? value : Object.entries(value || {}));
  const nameOf = unit => unit.displayName || unit.name || unit.rawcode;
  const readyForCost = (amount, cost) => nonnegative(cost) && (cost === 0 || nonnegative(amount) && amount >= cost);
  const remaining = (amount, cost) => nonnegative(amount) ? amount - cost : null;

  function create(catalog, data, options = {}) {
    if (!Graph || catalog?.map?.sha256 !== mapSha256 || catalog.map.scriptSha256 !== scriptSha256 ||
        data?.schemaVersion !== 1 || data.mapSha256 !== mapSha256 || data.scriptSha256 !== scriptSha256 ||
        data.sourceRawcode !== sourceRawcode || data.creationRecipeId !== creationRecipeId || data.creationHotkey !== "X" ||
        data.copyAbilityRawcode !== "A114" || data.copyHotkey !== "Z" || data.targetAbilityRawcode !== "A912" ||
        data.resultMarkerRawcode !== "A115" || data.resultAttackSpeedPercentDelta !== -5 ||
        data.skillPointCost !== 1 || data.recipeBoardOwnerPlayerIndex !== 7 ||
        data.consumesSource !== true || data.consumesTarget !== false || data.resultUsesTargetRawcode !== true ||
        !Array.isArray(data.recipeBoardTargetRawcodes) || !data.recipeBoardTargetRawcodes.length ||
        !Array.isArray(data.ownTargetRawcodes)) return null;

    const units = new Map(list(catalog.units).map(unit => [unit.rawcode, unit]));
    const legend = code => units.get(code)?.playable === true && units.get(code)?.tier === "전설";
    const targets = [...data.recipeBoardTargetRawcodes, ...data.ownTargetRawcodes];
    if (new Set(targets).size !== targets.length || !legend(sourceRawcode) ||
        targets.some(code => code === sourceRawcode || !legend(code))) return null;
    const sourceRecipe = list(catalog.recipes).find(recipe => String(recipe.id) === creationRecipeId);
    if (sourceRecipe?.resultUnitRawcode !== sourceRawcode || sourceRecipe.resultCount !== 1 ||
        sourceRecipe.abilityRawcode !== creationRecipeId || sourceRecipe.hotkey !== data.creationHotkey ||
        !Graph.isProductionRecipe(sourceRecipe)) return null;

    const graph = options.recipeGraph || Graph.create(catalog);
    const boardTargets = new Set(data.recipeBoardTargetRawcodes);
    const ownTargets = new Set(data.ownTargetRawcodes);
    const skillRequirement = {type:"SKPT", rawcode:"SKPT", count:data.skillPointCost};

    function prepare(counts, settings = {}, plannerOptions = {}) {
      if (settings.craftRunning === true || typeof graph?.planDeck !== "function") return null;
      const stock = stockOf(counts);
      const protectedCounts = stockOf(plannerOptions.protectedCounts ?? settings.protectedCounts);
      if ([...stock].some(([code, count]) => typeof code !== "string" || !nonnegative(count)) ||
          [...protectedCounts].some(([code, count]) => !nonnegative(count) || count > (stock.get(code) || 0))) return null;
      const skillPoints = Graph.normalizeRecipeSpecial(settings.recipeConditions?.special, settings.localPlayer, mapSha256).skillPoints;
      if (skillPoints === null || skillPoints < data.skillPointCost) return null;
      const recipeConditions = {...settings.recipeConditions, localPlayer:settings.localPlayer,
        source:{...settings.recipeConditions?.source, mapSha256}};
      const sourceOwned = (stock.get(sourceRawcode) || 0) > (protectedCounts.get(sourceRawcode) || 0);
      const supported = new Set(plannerOptions.supportedRecipeIds ?? settings.supportedRecipeIds ?? []);
      const sourceCraftAutomatic = !sourceOwned && supported.has(creationRecipeId);
      const automaticRecipeIds = new Set(supported);
      supported.add(creationRecipeId);
      const plan = sourceOwned ? null : graph.planDeck(
        [{rawcode:sourceRawcode, count:(protectedCounts.get(sourceRawcode) || 0) + 1}], stock,
        {...plannerOptions, protectedCounts, supportedRecipeIds:supported,
          allowChoiceWisps:(plannerOptions.allowChoiceWisps ?? settings.allowChoiceWisps) === true,
          items:settings.items, recipeConditions, localPlayer:settings.localPlayer, planningAlternativeLimit:1}
      );
      if (plan && (!plan.executable || !plan.steps.length ||
          plan.steps.at(-1).recipeId !== creationRecipeId || plan.steps.some(step =>
            step.type !== "choice" && (step.type !== "craft" ||
              String(step.recipeId) !== creationRecipeId && !automaticRecipeIds.has(String(step.recipeId)))))) return null;
      const requirements = [...list(plan?.nonUnitRequirements), skillRequirement];
      if (requirements.some(item => !allowedRequirements.has(item.type)) ||
          !Graph.evaluateRecipeConditions(requirements, recipeConditions).conditionsReady ||
          !Graph.evaluateItems(requirements, settings.items).itemsReady) return null;
      const {wood, gold} = Graph.effectiveResourceCosts(requirements, recipeConditions);
      if (!readyForCost(settings.wood, wood) || !readyForCost(settings.gold, gold)) return null;
      const finalCounts = plan ? {...plan.finalCounts} : Object.fromEntries(stock);
      if ((finalCounts[sourceRawcode] || 0) <= (protectedCounts.get(sourceRawcode) || 0) ||
          [...protectedCounts].some(([code, count]) => (finalCounts[code] || 0) < count)) return null;
      finalCounts[sourceRawcode]--;
      const projectedSettings = {
        wood:remaining(settings.wood, wood), gold:remaining(settings.gold, gold),
        recipeConditions:Graph.projectRecipeConditions(requirements, recipeConditions),
        items:plan && settings.items?.status === "confirmed" ?
          {...settings.items, counts:plan.projectedItems} : settings.items
      };
      return {stock, sourceOwned, sourceCraftAutomatic, requiresManualSourceCraft:!sourceOwned && !sourceCraftAutomatic,
        sourceCraftPlan:plan, wood, gold, choiceWispCount:plan?.choiceWispCount || 0,
        stepCount:(plan?.stepCount || 0) + 1, finalCounts, projectedSettings};
    }

    function route(targetRawcode, counts, settings = {}, plannerOptions = {}, prepared) {
      if (!boardTargets.has(targetRawcode) && !ownTargets.has(targetRawcode)) return null;
      const result = prepared === undefined ? prepare(counts, settings, plannerOptions) : prepared;
      if (!result) return null;
      const {stock, finalCounts, ...plan} = result;
      const targetOwner = boardTargets.has(targetRawcode) ? "recipe_board" : "own";
      if (targetOwner === "own" && (!(stock.get(targetRawcode) > 0) || !(finalCounts[targetRawcode] > 0))) return null;
      const targetCount = (finalCounts[targetRawcode] || 0) + 1;
      if (!nonnegative(targetCount)) return null;
      return {...plan, sourceRawcode, sourceName:nameOf(units.get(sourceRawcode)), targetRawcode,
        targetName:nameOf(units.get(targetRawcode)), targetOwner, creationRecipeId, creationHotkey:data.creationHotkey,
        copyAbilityRawcode:data.copyAbilityRawcode, copyHotkey:data.copyHotkey, skillPointCost:data.skillPointCost,
        resultAttackSpeedPercentDelta:data.resultAttackSpeedPercentDelta, manual:true,
        finalCounts:{...finalCounts, [targetRawcode]:targetCount}};
    }

    function prefer(candidate, direct) {
      if (!candidate) return false;
      if (!direct || direct.available === false || direct.resourcesReady === false || direct.itemsReady === false ||
          direct.conditionsReady === false || direct.executable === false) return true;
      const choice = direct.choiceWispCount ?? direct.choiceWisps;
      const steps = direct.stepCount ?? direct.expectedStepCount;
      if (![choice, direct.wood, direct.gold, steps].every(nonnegative)) return false;
      if (candidate.choiceWispCount > choice || candidate.wood > direct.wood ||
          candidate.gold > direct.gold || candidate.stepCount > steps) return false;
      return candidate.choiceWispCount < choice || candidate.wood < direct.wood ||
        candidate.gold < direct.gold || candidate.stepCount < steps;
    }

    return {prepare, route, prefer};
  }

  return {create};
});
