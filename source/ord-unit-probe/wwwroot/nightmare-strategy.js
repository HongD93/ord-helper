(function(root,factory){
  const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph,typeof module==="object"&&module.exports?require("./nightmare-records.js"):root.OrdNightmareRecords,typeof module==="object"&&module.exports?require("./nightmare-reward-budget.js"):root.OrdNightmareRewardBudget,typeof module==="object"&&module.exports?require("./nightmare-random-forecast.js"):root.OrdNightmareRandomForecast,typeof module==="object"&&module.exports?require("./nightmare-combat-verifier.js"):root.OrdNightmareCombatVerifier,typeof module==="object"&&module.exports?require("./nightmare-support-adaptation.js"):root.OrdNightmareSupportAdaptation,typeof module==="object"&&module.exports?require("./nightmare-early-story.js"):root.OrdNightmareEarlyStory,typeof module==="object"&&module.exports?require("./nightmare-hand-fit.js"):root.OrdNightmareHandFit,typeof module==="object"&&module.exports?require("./nightmare-reinforcements.js"):root.OrdNightmareReinforcements);
  if(typeof module==="object"&&module.exports)module.exports=api;
  root.OrdNightmareStrategy=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(Graph,Records,RewardBudget,RandomForecast,CombatVerifier,SupportAdaptation,EarlyStory,HandFit,Reinforcements){
  "use strict";
  const finite=x=>typeof x==="number"&&Number.isFinite(x);
  const unique=xs=>Array.from(new Set(xs.filter(Boolean)));
  const countsOf=x=>new Map(Array.from(x instanceof Map?x.entries():Array.isArray(x)?x.map(u=>[u.rawcode,u.count]):Object.entries(x||{}),([k,v])=>[k,finite(v)?Math.max(0,Math.floor(v)):0]));
  const dict=x=>Object.fromEntries(Array.from(x).filter(([,v])=>v>0));
  const semanticValue=value=>value instanceof Map?Array.from(value,([key,item])=>[key,semanticValue(item)]).sort(([a],[b])=>String(a).localeCompare(String(b))):value instanceof Set?Array.from(value).sort():Array.isArray(value)?value.map(semanticValue):value&&typeof value==="object"?Object.fromEntries(Object.keys(value).sort().map(key=>[key,semanticValue(value[key])])):value;
  const semanticKey=value=>JSON.stringify(semanticValue(value));
  const rankResource=x=>x===true?2:x===false?0:1;
  const advanced=new Set(["전설","히든","초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]);
  // 근거가 없는 후보는 원래 순위 자리를 유지하고, 같은 선행 정책 안의 검증된 후보만 재정렬한다.
  function rerankKnown(rows,prefix,metric,compare){
    const result=rows.slice();let begin=0;
    while(begin<result.length){let end=begin+1;while(end<result.length&&prefix(result[begin],result[end])===0)end++;
      const slots=[],known=[];for(let i=begin;i<end;i++){const value=metric(result[i]);if(value!==null&&value!==undefined){slots.push(i);known.push({row:result[i],value,index:i});}}
      known.sort((a,b)=>compare(a.value,b.value)||a.index-b.index);slots.forEach((slot,i)=>result[slot]=known[i].row);begin=end;
    }return result;
  }
  function normalizeKingPathMode(settings){return Object.prototype.hasOwnProperty.call(settings||{},"kingPathMode")?["off","standard","martial_law"].includes(settings.kingPathMode)?settings.kingPathMode:"off":settings?.includeKingPathDecks===true?"standard":"off";}
  function validateClearRecords(data,catalog){
    if(data?.schemaVersion!==1||data.catalogMapHash!==catalog?.map?.sha256||!Array.isArray(data.records)||data.records.length<1||data.records.length>50000)return false;
    const ids=new Set(),codes=new Set((catalog.units||[]).map(unit=>unit.rawcode));
    for(const record of data.records){
      if(typeof record?.id!=="string"||!record.id||record.id.length>120||ids.has(record.id)||!Array.isArray(record.goals)||!Array.isArray(record.entries)||record.goals.length>512||record.entries.length>512)return false;
      ids.add(record.id);
      for(const [name,rows]of [["goals",record.goals],["entries",record.entries]])for(const row of rows){
        if(!Number.isSafeInteger(row?.count)||row.count<1||row.count>1000000)return false;
        if(name==="goals"||row.rawcode!=null)if(typeof row.rawcode!=="string"||!codes.has(row.rawcode))return false;
      }
    }
    return true;
  }
  function classifyTranscendence(catalog){
    const heroes=(catalog.units||[]).filter(unit=>unit.playable===true&&unit.tier==="초월"&&/^H[A-Za-z0-9]{3}$/.test(unit.rawcode)).map(unit=>unit.rawcode);
    const slotCosts=Object.fromEntries(heroes.map(code=>[code,1]));
    // ET02의 H08T는 H099/H0B2 초월 루피가 등록되는 가상 재료다. 니카로 승급해도 이미 사용한 초월 슬롯은 유지한다.
    if((catalog.units||[]).some(unit=>unit.rawcode==="H0BK"))slotCosts.H0BK=1;
    return {catalogMapHash:catalog.map?.sha256||null,slotCosts,maximum:1,excludedMaterialRawcodes:["h04S"],excludedProxyRawcodes:["H08T"],policy:"final_transcendence_heroes_and_nika_upgrade_one_slot"};
  }
  function recommendationGroupKey(deck,catalog){
    const units=new Map((catalog.units||[]).map(unit=>[unit.rawcode,unit])),tiers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]),excluded=new Set(["h04S","H08T","h076","h04T","h04Y","h05D","h0CA"]);
    const final=rawcode=>{const unit=units.get(rawcode);return unit?.playable===true&&unit.isSummon!==true&&unit.isSummoned!==true&&!excluded.has(rawcode)&&tiers.has(unit.tier);};
    const normalize=rows=>{const counts=new Map();for(const row of rows||[]){const rawcode=typeof row==="string"?row:row?.rawcode,count=typeof row==="string"?1:row?.count;if(typeof rawcode==="string"&&Number.isSafeInteger(count)&&count>0)counts.set(rawcode,Math.max(counts.get(rawcode)||0,count));}return Array.from(counts).sort(([a],[b])=>a.localeCompare(b));};
    const evidence=deck.sourceEvidence,original=["clear_record","record_support_adaptation"].includes(evidence?.kind)&&Array.isArray(evidence.mapping)&&evidence.mapping.length>0&&Array.isArray(evidence.recordIds)&&evidence.recordIds.length>0&&Array.isArray(evidence.urls)&&evidence.urls.length>0;
    const finals=original?normalize(evidence.mapping.filter(entry=>final(entry.rawcode))):[];
    if(!original||!finals.length)return JSON.stringify([deck.mode||deck.direction||"unknown","roster",normalize(deck.goals)]);
    const mainCodes=new Set(deck.assessment?.coreComposition?.dpsRawcodes||[]),additionalMain=normalize((deck.goals||[]).filter(goal=>mainCodes.has(goal.rawcode)&&["전설","히든"].includes(units.get(goal.rawcode)?.tier)));
    return JSON.stringify([deck.mode||deck.direction||"unknown","original_final",finals,additionalMain]);
  }
  function deduplicateAlternatives(candidates,target,catalog){
    // candidates는 기존 compareFit 순서다. 선택한 목표는 같은 구성 그룹의 대표로 유지한다.
    const seen=new Set(target?[recommendationGroupKey(target,catalog)]:[]),alternatives=[];
    for(const candidate of candidates){const key=recommendationGroupKey(candidate,catalog);if(seen.has(key))continue;seen.add(key);alternatives.push(candidate);}
    return {alternatives,groupCount:seen.size,duplicateCount:Math.max(0,candidates.length-alternatives.length-Number(Boolean(target)))};
  }
  const ally=new Set(["ally","allies","allied","all_allies","team"]),enemy=new Set(["enemy","enemies","all_enemies","boss","boss_enemies","line_enemies"]);
  function create(catalog,combat,rules,clearRecords,options){
    if(clearRecords&&!Array.isArray(clearRecords.records)&&clearRecords.recipeGraph){options=clearRecords;clearRecords=null;}
    combat=combat||{};rules=rules||{};options=options||{};
    const recordOnly=options.recordOnly===true;
    const minRecordVersion=recordOnly?"2.321":options.minRecordVersion||null;
    const units=new Map((catalog.units||[]).map(u=>[u.rawcode,u])),mysterious=code=>units.get(code)?.tier==="신비";
    let includeMysteriousDecks=false;
    const baseGraph=options.recipeGraph||Graph.create(catalog),ordinaryRecipes=(catalog.recipes||[]).filter(recipe=>!mysterious(recipe.resultUnitRawcode));
    const ordinaryGraph=ordinaryRecipes.length===(catalog.recipes||[]).length?baseGraph:Graph.create({...catalog,recipes:ordinaryRecipes});
    // 추천용 경로만 제한한다. 실제 보유 재고와 일반 조합 그래프는 변경하지 않는다.
    const graph={...baseGraph};for(const method of ["analyze","plan","planDeck"])if(typeof baseGraph[method]==="function")graph[method]=(...args)=>(includeMysteriousDecks?baseGraph:ordinaryGraph)[method](...args);
    const mysteryRows=rows=>(rows||[]).some(row=>[typeof row==="string"?row:row?.rawcode,...(Array.isArray(row?.candidateRawcodes)?row.candidateRawcodes:[])].some(mysterious));
    const recommendationRecordData=data=>!data||includeMysteriousDecks?data:{...data,records:(data.records||[]).filter(record=>!mysteryRows(record.goals)&&!mysteryRows(record.entries)&&!mysteryRows(record.goalProjection?.excluded))};
    const makeRecordProvider=data=>data&&Records?Records.create(catalog,combat,recommendationRecordData(data),{recipeGraph:graph,recordOnly,minRecordVersion,generateAlternatives:!recordOnly,handFitAdvisor}):null;
    const handFitAdvisor=options.handFitRecommendations===true&&HandFit?HandFit.create(catalog,{recipeGraph:graph}):null;
    const earlyStoryAdvisor=options.earlyStoryRecommendations===true&&EarlyStory?EarlyStory.create(catalog,combat,options.storyRankings,{recipeGraph:graph}):null;
    let earlyStorySnapshot=null,earlyStoryAdvancedSeen=false;
    const supportAdapter=recordOnly&&options.supportAdaptation===true&&SupportAdaptation?SupportAdaptation.create(catalog,combat,{recipeGraph:graph,unitModels:options.combatValidation?.unitModels}):null;
    const recommendationPolicy=recordOnly?(supportAdapter?"clear_records_with_hand_adapted_support":"original_clear_records_only"):"records_and_native_profiles";
    const recipes=new Map((catalog.recipes||[]).map(r=>[String(r.id),r]));
    const profiles=combat.units||{},curated=combat.curatedStrategyProfiles||{},strategyUnits=curated.units||{};
    const fallbackTemplates=Array.isArray(curated.deckTemplates)?curated.deckTemplates:[];
    let recordProvider=makeRecordProvider(clearRecords),recordProviderMysteriousPolicy=false,lastIncludeMysteriousDecks=null;
    let pendingClearRecords=null,recordDataRevision=null,kingRecordProvider=null,kingRecordProviderData=null;
    const combatVerifier=CombatVerifier&&options.combatValidation?CombatVerifier.create(catalog,{...options.combatValidation,nativeCombat:combat}):null;
    const nativeManaUsage=CombatVerifier?.createNativeManaUsage?.(catalog,combat)||(()=>({usesManaSkills:null,status:"unknown",declarations:[]}));
    // 원문에서 수명·제거가 확인된 소환체는 실제 제작 목표나 보호 유닛으로 보지 않는다.
    const excludedSummons=new Set(["h076","h04T","h04Y","h05D","h0CA"]);
    const investmentMaterialProxies=new Set(["h04S","H08T"]);
    const nonForecastable=new Set([...excludedSummons,...(combat.acquisitionModel?.randomOnlyRawcodes||[]),...(combat.acquisitionModel?.nonForecastableRawcodes||[]),
      ...(catalog.recipes||[]).filter(r=>!Graph.isProductionRecipe(r)&&(r.ingredients||[]).some(i=>i.type==="RDUN"&&i.rawcode==="A800")).map(r=>r.resultUnitRawcode)]);
    const hash=catalog.map?.sha256||catalog.source_sha256;
    const transcendenceCapacity=classifyTranscendence(catalog);
    const reinforcementAdvisor=options.reinforcementRecommendations===true&&Reinforcements?Reinforcements.create(catalog,combat,{recipeGraph:graph,unitModels:options.combatValidation?.unitModels,transcendenceSlotCosts:transcendenceCapacity.slotCosts}):null;
    const transcendenceState=(goals,inventory)=>{
      const needed=new Map();
      for(const goal of goals||[])if(transcendenceCapacity.slotCosts[goal.rawcode])needed.set(goal.rawcode,Math.max(needed.get(goal.rawcode)||0,goal.count||1));
      for(const [rawcode,count]of inventory)if(count>0&&transcendenceCapacity.slotCosts[rawcode])needed.set(rawcode,Math.max(needed.get(rawcode)||0,count));
      // 현재 초월을 소모하는 니카 승급은 새 슬롯을 사용하지 않는다. 별도 유지 목표는 그대로 센다.
      const missingNika=Math.max(0,(goals||[]).filter(goal=>goal.rawcode==="H0BK").reduce((max,goal)=>Math.max(max,goal.count||1),0)-(inventory.get("H0BK")||0));
      let upgraded=0;for(const rawcode of ["H099","H0B2"]){const retained=(goals||[]).filter(goal=>goal.rawcode===rawcode).reduce((max,goal)=>Math.max(max,goal.count||1),0),available=Math.max(0,(inventory.get(rawcode)||0)-retained),used=Math.min(available,Math.max(0,missingNika-upgraded));if(used){const count=(needed.get(rawcode)||0)-used;if(count>0)needed.set(rawcode,count);else needed.delete(rawcode);upgraded+=used;}}
      const required=Array.from(needed.values()).reduce((sum,count)=>sum+count,0);
      return {maximum:1,required,valid:required<=1,units:Array.from(needed,([rawcode,count])=>({rawcode,count}))};
    };
    const nativeMap=Boolean(hash&&hash===combat.map?.sha256),rulesMap=Boolean(hash&&hash===rules.map?.sha256);
    const resolved=new Map(),goalContexts=new Map();let selectedId=null,session=null,firstInvestmentRawcode=null,progressCache=new Map();
    let lastRecommendation=null,lastRecommendationKey=null,feasibilityEvidenceContext=null;const feasibilityEvidence=new Map();
    let supportAdaptationCache=null,lastSelectedSupportBase=null;
    let lastKnownRecipeConditions=new Map(),pendingRecipeConditionReplan=false,feasibilityPlanner=null,feasibilityDeadline=Infinity;
    let activeRewardBudget=null,lastConfirmedRewardBudget=null,pendingRewardBudgetReplan=false,budgetPlanner=null;
    let lastRandomHistoryFacts=null,pendingRandomHistoryReplan=false;const randomRoutes=new Map();
    let lastConfirmedCombatScenario=null,pendingCombatScenarioReplan=false;
    let lastIncludeKingPathDecks=null,lastKingPathMode=null,pendingKingPathPreferenceReplan=false,pendingKingPathTargetSelection=false,observedKingTopLowerBound=0;
    const inventoryWispRawcodes=new Set(["e018","e0IX","e017","e016","e019","e015",...(combat.acquisitionModel?.sharedChoiceResources||[]).map(source=>source.sourceRawcode)]);
    const upperTiers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]);
    let previousUpper=null,upperAnchors=new Map(),ownUpperIntermediates=new Map(),deferredUpper=new Map(),wasCraftRunning=false,lastCraftTarget=null,ownExpectedResults=new Set();
    let lastConfirmedItems=null,pendingItemReplan=false,pendingFirstInvestmentReplan=false,previousPermanentStock=null,pendingStockRecompare=false;
    let lastManualRevision=null,lastPreferredId=null,suppressedPreferredId=null,rebaseRevision=0,rebaseInfo=null,lastSelectedTemplate=null,lastIdleDirection=null,releasedOwnIntermediateRawcodes=[];
    const nextTiers=new Set(["희귀","전설","히든","초월","불멸","영원","제한","신비","왜곡","변화","특수","세라핌"]),dependencyCache=new Map();
    const unitDependencies=new Map(),ancestryCache=new Map();
    for(const recipe of recipes.values())if(Graph.isProductionRecipe(recipe)){
      if(!unitDependencies.has(recipe.resultUnitRawcode))unitDependencies.set(recipe.resultUnitRawcode,new Set());
      for(const item of recipe.ingredients||[])if(item.type==="UNIT"&&item.count>0)for(const rawcode of item.acceptedRawcodes||[item.rawcode])unitDependencies.get(recipe.resultUnitRawcode).add(rawcode);
    }
    const native=code=>!excludedSummons.has(code)&&nativeMap&&profiles[code]?.verified===true&&profiles[code]?.verificationKind==="native";
    const info=code=>{const u=units.get(code)||{};return {rawcode:code,name:u.displayName||profiles[code]?.name||u.name||code,tier:u.tier||"미확인"};};
    // 패왕의길 전용 레오가 필요한 목표는 사용자가 해당 추천을 켰을 때 포함한다.
    const kingPathDependent=(roster,template)=>roster.goals.some(goal=>goal.rawcode==="h0C8"&&goal.kind!=="retained")||(template?.sourceEvidence?.mapping||[]).some(entry=>entry.rawcode==="h0C8");
    const mysteriousDependent=template=>mysteryRows(template?.coreGoals)||mysteryRows(template?.coreRawcodes)||mysteryRows(template?.goals)||mysteryRows(template?.sourceEvidence?.mapping)||mysteryRows(template?.goalProjection?.excluded)||mysteryRows(template?.sourceEvidence?.goalProjection?.excluded)||(template?.supportSlots||[]).some(slot=>slot.required!==false&&(slot.preferredRawcodes||[]).length>0&&[...(slot.preferredRawcodes||[]),...(slot.alternatives||[])].every(code=>mysterious(typeof code==="string"?code:code?.rawcode)));
    const recommendationRecipeAllowed=recipe=>includeMysteriousDecks||!mysterious(recipe?.resultUnitRawcode);
    // SKPT는 미션·구매·조건부 특성공학으로 추가 획득할 수 있다. 미확인 획득량은 예산에 더하지 않는다.
    // 현재 SKPT 부족은 지금 조합을 막지만 미래 목표의 영구 차단 근거는 아니다.
    const fixedBlockedConditions=(recipe,conditions)=>Graph.evaluateRecipeConditions(recipe.ingredients,conditions).blockedConditions.filter(item=>item.type!=="SKPT"&&!(item.type==="NIKA"&&item.reason==="nika_enhancement_incomplete"));
    const productionByResult=new Map();for(const recipe of Array.from(recipes.values()).flatMap(recipe=>Graph.expandMaterialChoices(recipe,catalog)))if(Graph.isProductionRecipe(recipe)){if(!productionByResult.has(recipe.resultUnitRawcode))productionByResult.set(recipe.resultUnitRawcode,[]);productionByResult.get(recipe.resultUnitRawcode).push(recipe);}
    function fixedGoalBlock(roster,inventory,settings){
      let nodes=0,limited=false;
      function blocked(rawcode,count,path){
        if((inventory.get(rawcode)||0)>=count)return null;
        if(++nodes>2000){limited=true;return null;}if(path.has(rawcode))return null;
        const choices=(productionByResult.get(rawcode)||[]).filter(recommendationRecipeAllowed);if(!choices.length)return null;
        const next=new Set(path);next.add(rawcode);const failures=[];
        for(const recipe of choices){
          const direct=fixedBlockedConditions(recipe,settings.recipeConditions);if(direct.length){failures.push({rawcode,conditions:direct});continue;}
          const batches=Math.ceil(Math.max(0,count-(inventory.get(rawcode)||0))/Math.max(1,recipe.resultCount||1));
          let child=null;for(const item of recipe.ingredients||[])if(item.type==="UNIT"&&item.count>0){child=blocked(item.rawcode,item.count*batches,next);if(child)break;}
          if(!child)return null;failures.push(child);
        }
        return {rawcode,conditions:failures.flatMap(failure=>failure.conditions)};
      }
      for(const goal of roster.goals){const failure=blocked(goal.rawcode,goal.count,new Set());if(failure&&!limited)return {proven:true,rawcode:failure.rawcode,conditions:failure.conditions,nodeCount:nodes};}
      return {proven:false,searchLimited:limited,nodeCount:nodes};
    }
    const effects=code=>native(code)?(profiles[code].effects||[]).filter(e=>CombatVerifier.isRuntimeReferenceEligible(e)&&e.evidence&&["tooltip","script","objectData"].includes(e.evidence.type)):[];
    const role=code=>native(code)?profiles[code]?.damage?.reviewedAnchorRole||null:null;
    const coreMode=code=>({physicalDps:"physical",magicDps:"magic",hybridDps:"hybrid"})[role(code)]||null;
    const skillMana=code=>strategyUnits[code]?.manaDependence||{};
    const nativeMana=code=>options.combatValidation?.unitModels?.map?.sha256===hash?options.combatValidation.unitModels.units?.[code]?.mana:null;
    const reviewedManaUsage=code=>skillMana(code).usesManaSkills===true||(skillMana(code).costs||[]).length>0||nativeMana(code)?.usesManaSkills===true||(nativeMana(code)?.verifiedThresholds||[]).length>0;
    const manaUsage=code=>{const evidence=nativeManaUsage(code),reviewed=skillMana(code),model=nativeMana(code);return {...evidence,usesManaSkills:evidence.usesManaSkills===true||reviewedManaUsage(code)?true:reviewed.usesManaSkills===false||model?.usesManaSkills===false?false:null};};
    const usesMana=code=>manaUsage(code).usesManaSkills===true;
    // 마나 사용의 존재만으로 외부 마나 오라가 필수라고 가정하지 않는다.
    const needsManaSupport=code=>reviewedManaUsage(code)&&!(options.combatValidation?.unitModels?.map?.sha256===hash&&options.combatValidation.unitModels.units?.[code]?.mana?.periodicManaRelationship==="independent_additive_script_verified");
    function supports(code,kind){
      kind=({teamAttackSpeed:"attackSpeedBuff",teamAttackDamage:"attackDamageBuff",continuousSlow:"alwaysSlow",healthSupport:"healthRegeneration"})[kind]||kind;
      return effects(code).filter(e=>{
        if(kind==="areaStun")return e.kind==="stun"&&enemy.has(e.target)&&["area","all"].includes(e.scope)&&e.stunRoleEligible!==false&&e.useForStunRecommendation!==false&&e.chancePercent!==0;
        if(kind==="slow"||kind==="alwaysSlow")return e.kind==="slow"&&enemy.has(e.target)&&(!finite(e.value)||e.value>0)&&(kind!=="alwaysSlow"||["always","aura"].includes(e.mode)&&!(e.conditions||[]).length);
        if(kind==="control")return supports(code,"slow").includes(e)||supports(code,"areaStun").includes(e);
        if(kind==="manaSupport"||kind==="manaSustain")return ["manaRegeneration","manaRecovery","manaAmplification","manaCostReduction"].includes(e.kind)&&ally.has(e.target)&&(!finite(e.value)||e.value>0);
        if(kind==="damageSynergy")return ["armorReduction","magicResistanceReduction","magicAmplification","allDamageAmplification","attackSpeedBuff","attackDamageBuff"].some(k=>supports(code,k).includes(e));
        return e.kind===kind&&(["attackSpeedBuff","attackDamageBuff","healthRegeneration"].includes(kind)?ally.has(e.target):enemy.has(e.target))&&(!finite(e.value)||e.value>0);
      });
    }
    function ancestry(target,ingredient){
      if(!ancestryCache.has(target)){
        const found=new Set(),pending=Array.from(unitDependencies.get(target)||[]);
        while(pending.length){const code=pending.pop();if(found.has(code))continue;found.add(code);for(const next of unitDependencies.get(code)||[])if(!found.has(next))pending.push(next);}
        ancestryCache.set(target,found);
      }
      return ancestryCache.get(target).has(ingredient);
    }
    function nextResult(step){
      const unit=units.get(step.resultRawcode),recipe=recipes.get(String(step.recipeId));
      return (includeMysteriousDecks||!mysterious(step.resultRawcode))&&!excludedSummons.has(step.resultRawcode)&&step.type==="craft"&&unit?.playable===true&&nextTiers.has(unit.tier)&&unit.isCharacter!==false&&unit.isSummon!==true&&unit.isSummoned!==true&&
        (!unit.classificationSource||unit.classificationSource==="grade_suffix")&&recipe&&Graph.isProductionRecipe(recipe)&&recipe.resultUnitRawcode===step.resultRawcode;
    }
    function recipeDependencies(recipeId){
      if(dependencyCache.has(recipeId))return dependencyCache.get(recipeId);
      const recipe=recipes.get(String(recipeId)),found=new Set(recipe?[recipe.resultUnitRawcode]:[]);
      function visit(rawcode){if(found.has(rawcode))return;found.add(rawcode);for(const candidate of recipes.values())if(candidate.resultUnitRawcode===rawcode&&Graph.isProductionRecipe(candidate))for(const item of candidate.ingredients||[])if(item.type==="UNIT")for(const code of item.acceptedRawcodes||[item.rawcode])visit(code);}
      for(const item of recipe?.ingredients||[])if(item.type==="UNIT")for(const code of item.acceptedRawcodes||[item.rawcode])visit(code);
      dependencyCache.set(recipeId,found);return found;
    }
    function resolve(template,inventory,settings){
      const formPresence=(template.sourceEvidence?.mapping||[]).filter(entry=>entry.formUnknown).map(entry=>entry.rawcode).join("|");
      const cached=resolved.get(template.id)?.formPresence===formPresence?resolved.get(template.id):null;
      // 고정 목표의 선택은 유지하되, 자동 보유 코어는 매번 현재 재고로 다시 계산한다.
      const goals=new Map((cached?.fixedGoals||cached?.goals||[]).filter(g=>g.slots.some(slot=>slot!=="owned-core")).map(g=>[g.rawcode,{...g,slots:g.slots.filter(slot=>slot!=="owned-core")}]));
      const slots=cached?cached.slots.slice():[],coreCodes=cached?cached.coreCodes.slice():[];
      const add=(code,count,slot,kind)=>{
        if(typeof code!=="string"||excludedSummons.has(code)||!includeMysteriousDecks&&mysterious(code))return;
        count=Number.isSafeInteger(count)&&count>0?count:1;
        if(!goals.has(code))goals.set(code,{...info(code),count,slots:[],kind});
        const goal=goals.get(code);goal.count=Math.max(goal.count,count);goal.slots.push(slot);
        if(kind==="core")goal.kind="core";
      };
      if(!cached)for(const entry of template.coreGoals||template.coreRawcodes||[]){const code=typeof entry==="string"?entry:entry.rawcode;coreCodes.push(code);add(code,typeof entry==="object"?entry.count:1,"core","core");}
      if(!cached)for(const slot of template.supportSlots||[]){
        if(slot.required===false)continue;
        const alternatives=unique([...(slot.preferredRawcodes||[]),...(slot.alternatives||[])].map(x=>typeof x==="string"?x:x?.rawcode)).filter(code=>includeMysteriousDecks||!mysterious(code));
        const allowed=settings.supportedRecipeIds==null?null:new Set(settings.supportedRecipeIds);
        const automatic=code=>Array.from(recipes.values()).some(recipe=>Graph.isProductionRecipe(recipe)&&recipe.resultUnitRawcode===code&&(!allowed||allowed.has(String(recipe.id))));
        const chosen=alternatives.find(code=>inventory.get(code)>0&&units.has(code))||alternatives.find(code=>units.has(code)&&automatic(code))||alternatives.find(code=>units.has(code))||null;
        slots.push({id:slot.id,roles:slot.roles||[],required:true,count:slot.count||1,rawcode:chosen,alternatives});
        if(chosen)add(chosen,slot.count,slot.id,"support");
      }
      const fixedGoals=Array.from(goals.values()).map(g=>({...g,slots:g.slots.slice()})),prunedIntermediateLegends=[],prunedIntermediateHiddenSeeds=[];
      for(const goal of fixedGoals){
        // 실클리어 원본에 함께 등록된 보조 유닛은 상위의 재료라는 이유로 삭제하지 않는다.
        if(recordOnly&&["clear_record","record_support_adaptation"].includes(template.sourceEvidence?.kind))continue;
        const seedHidden=goal.rawcode===firstInvestmentRawcode&&units.get(goal.rawcode)?.tier==="히든";
        if(units.get(goal.rawcode)?.tier!=="전설"&&!seedHidden)continue;
        // 명시적인 native 지원 슬롯은 상위 제작분과 별개로 끝까지 남기는 추가 수량이다.
        if(template.recordPresenceGoals!==true&&goal.kind==="support"&&goal.slots.some(id=>slots.some(slot=>slot.id===id&&slot.required)))continue;
        const upper=fixedGoals.filter(other=>other.rawcode!==goal.rawcode&&advanced.has(units.get(other.rawcode)?.tier)&&units.get(other.rawcode)?.tier!=="전설"&&ancestry(other.rawcode,goal.rawcode));
        if(!upper.length)continue;
        goals.delete(goal.rawcode);(seedHidden?prunedIntermediateHiddenSeeds:prunedIntermediateLegends).push({...info(goal.rawcode),count:goal.count,usedBy:upper.map(g=>info(g.rawcode)),reason:"상위 최종 목표의 조합 재료",policy:"investment_ancestors_are_intermediate_not_additional_final_copies"});
      }
      const retained=[],replacements=[];
      for(const [code,count]of upperAnchors)if(count>0&&(includeMysteriousDecks||!mysterious(code))){
        const nikaUpgrade=["H099","H0B2"].includes(code)&&!goals.has(code)&&goals.has("H0BK")&&(inventory.get("H0BK")||0)<goals.get("H0BK").count;
        if(nikaUpgrade){replacements.push({...info(code),count:1,reason:"선택한 목표의 승급 재료"});if(count===1)continue;}
        const remaining=count-Number(nikaUpgrade);if(!recordOnly||!goals.has(code))add(code,remaining,"owned-upper","retained");retained.push({...info(code),count:remaining,reason:"현재 보유 최상위"});
      }
      for(const [code,count]of inventory)if(count>0&&(includeMysteriousDecks||!mysterious(code))&&!upperAnchors.has(code)&&!deferredUpper.has(code)&&!(ownUpperIntermediates.get(code)>=count)&&advanced.has(units.get(code)?.tier)&&role(code)){
        if(!goals.has(code)&&Array.from(goals.keys()).some(target=>ancestry(target,code)))replacements.push({...info(code),count:1,reason:"선택한 목표의 승급 재료"});
        else {if(!recordOnly||!goals.has(code))add(code,1,"owned-core","retained");retained.push({...info(code),count:1});}
      }
      const result={goals:Array.from(goals.values()),slots,coreCodes:coreCodes.filter(code=>goals.has(code)),retained,replacements,formPresence,fixedGoals,prunedIntermediateLegends,prunedIntermediateHiddenSeeds};resolved.set(template.id,result);return result;
    }
    function firstInvestmentTargets(roster){
      if(!firstInvestmentRawcode||upperAnchors.size)return [];
      const finalTiers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]);
      // 실제 최종 보조로 남는 첫 전설·히든도 같은 유닛을 유지하는 근거로 삼는다.
      return roster.goals.filter(goal=>(finalTiers.has(units.get(goal.rawcode)?.tier)||goal.rawcode===firstInvestmentRawcode&&["전설","히든"].includes(units.get(goal.rawcode)?.tier))&&native(goal.rawcode)&&units.get(goal.rawcode)?.playable===true&&(ancestry(goal.rawcode,firstInvestmentRawcode)||goal.rawcode===firstInvestmentRawcode)&&
        !roster.goals.some(other=>other.rawcode!==goal.rawcode&&ancestry(other.rawcode,goal.rawcode)));
    }
    function upperInvestment(roster){
      const sourceCodes=new Set(roster.fixedGoals.map(goal=>goal.rawcode)),needed=new Set(),anchors=[];
      for(const [code,count]of upperAnchors){
        const mode=coreMode(code);
        if(mode==="physical"||mode==="hybrid")needed.add("armorReduction");
        if(mode==="magic"||mode==="hybrid"){needed.add("magicResistanceReduction");needed.add("magicAmplification");}
        if(needsManaSupport(code))needed.add("manaSupport");
        if(strategyUnits[code]?.resourceDependence?.resource==="health")needed.add("healthSupport");
        anchors.push({...info(code),count,sourceGoal:sourceCodes.has(code)||["H099","H0B2"].includes(code)&&sourceCodes.has("H0BK"),nativeProfileKnown:native(code),reviewedDamageRole:role(code),
          supportRoles:["alwaysSlow","areaStun","manaSupport","healthSupport","armorReduction","magicResistanceReduction","magicAmplification","teamAttackSpeed","teamAttackDamage"].filter(kind=>supports(code,kind).length)});
      }
      const coverage=Array.from(needed,kind=>({role:kind,covered:roster.goals.some(goal=>supports(goal.rawcode,kind).length)}));
      return {anchors,sourceGoalMatches:anchors.filter(unit=>unit.sourceGoal).length,coverage,missingSynergyRoles:coverage.filter(row=>!row.covered).map(row=>row.role),releasedOwnIntermediateRawcodes:releasedOwnIntermediateRawcodes.slice(),
        firstInvestmentSuperseded:anchors.length>0,unknownProfileRawcodes:anchors.filter(unit=>!unit.nativeProfileKnown).map(unit=>unit.rawcode),clearOutcomeTransferred:false};
    }
    function goalProgress(goal,inventory,settings){
      if((inventory.get(goal.rawcode)||0)>=goal.count)return {percent:100,ratio:1,choiceReachablePercent:100,choiceReachableRatio:1,progressTone:"complete",choiceWispNeeded:0,choiceCoveredCount:0,canCompleteWithChoiceWisp:false,resourcesReady:true,resourceRequirements:[],missingResources:[],itemsReady:true,itemRequirements:[],missingItems:[],conditionsReady:true,conditionRequirements:[],missingConditions:[],blockedConditions:[],unknownConditions:[],quantityMode:"goal_already_owned"};
      if(typeof graph.analyze!=="function")return null;
      if(!progressCache.has(goal.rawcode)){
        const value=graph.analyze(goal.rawcode,inventory,undefined,{resources:{wood:settings.wood,gold:settings.gold},items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:settings.localPlayer});
        progressCache.set(goal.rawcode,{percent:value.percent,ratio:value.ratio,choiceReachablePercent:value.choiceReachablePercent,choiceReachableRatio:value.choiceReachableRatio,
          progressTone:value.progressTone,choiceWispAvailable:value.choiceWispAvailable,choiceWispNeeded:value.choiceWispNeeded,choiceCoveredCount:value.choiceCoveredCount,canCompleteWithChoiceWisp:value.canCompleteWithChoiceWisp,
          resourcesReady:value.resourcesReady,resourceRequirements:value.resourceRequirements,missingResources:value.missingResources,itemsReady:value.itemsReady,itemRequirements:value.itemRequirements,missingItems:value.missingItems,progressWeighting:value.progressWeighting,conditionRequirements:value.conditionRequirements,missingConditions:value.missingConditions,blockedConditions:value.blockedConditions,unknownConditions:value.unknownConditions,conditionsReady:value.conditionsReady,additionalConditionsUnverified:value.additionalConditionsUnverified,quantityMode:"one_unit_material_progress"});
      }
      return progressCache.get(goal.rawcode);
    }
    function requirement(roleName,goals,minimum,dpsCodes){
      const sources=[];
      for(const goal of goals){
        if(!native(goal.rawcode))continue;
        if(["lineDamage","bossDamage","storyDamage"].includes(roleName)){
          const purpose=strategyUnits[goal.rawcode]?.[roleName];
          if(purpose&&["main","support","finisher"].includes(purpose.assessment)&&(purpose.evidence||[]).length)sources.push({...info(goal.rawcode),count:goal.count,assessment:purpose.assessment,conditions:purpose.conditions||[],evidence:purpose.evidence});
        }else if(roleName==="reviewedDamageCore"){
          if(role(goal.rawcode)&&(!dpsCodes||dpsCodes.includes(goal.rawcode)))sources.push({...info(goal.rawcode),count:goal.count,role:role(goal.rawcode),conditions:[],evidence:profiles[goal.rawcode]?.damage?.anchorReview||null});
        }else{
          const es=supports(goal.rawcode,roleName);if(es.length)sources.push({...info(goal.rawcode),count:goal.count,effects:es,conditions:unique(es.flatMap(e=>e.conditions||[]))});
        }
      }
      const count=sources.reduce((sum,s)=>sum+s.count,0),conditional=sources.some(s=>s.conditions.length||s.effects?.some(e=>!["always","aura"].includes(e.mode)||e.value==null));
      return {role:roleName,required:minimum,count,status:count<minimum?"missing":conditional?"conditional":"covered",sources,conditions:unique(sources.flatMap(s=>s.conditions)),origin:"requested_composition_policy",nativeMandatory:false};
    }
    function manaModels(goals){
      const auraSources=[],unresolvedAuraSources=[];
      for(const goal of goals){
        for(const effect of effects(goal.rawcode))if(effect.kind==="manaRegeneration"&&effect.unit==="manaPerSecond"&&ally.has(effect.target)&&!CombatVerifier.isNumericReferenceEligible(effect))unresolvedAuraSources.push({...info(goal.rawcode),abilityRawcode:effect.abilityRawcode,declaredValue:effect.value,unit:effect.unit,evidence:effect.evidence,reason:"numeric_aggregation_unverified"});
        let manaEffects=effects(goal.rawcode).filter(e=>CombatVerifier.isNumericReferenceEligible(e)&&e.kind==="manaRegeneration"&&e.unit==="manaPerSecond"&&ally.has(e.target)&&finite(e.value)&&["always","aura"].includes(e.mode));
        if(manaEffects.some(e=>e.evidence.type==="objectData"))manaEffects=manaEffects.filter(e=>e.evidence.type==="objectData");
        const seen=new Set();for(const e of manaEffects){const key=(e.stacking?.group||e.abilityRawcode)+"|"+e.value;if(seen.has(key))continue;seen.add(key);auraSources.push({...e,...info(goal.rawcode),unitCount:goal.count});}
      }
      return goals.filter(goal=>strategyUnits[goal.rawcode]||usesMana(goal.rawcode)).map(goal=>{
        const original=skillMana(goal.rawcode),verified=nativeMana(goal.rawcode),usage=manaUsage(goal.rawcode);
        const data=verified?{...original,...verified}:original,base=finite(data.baseRegenerationPerSecond)?data.baseRegenerationPerSecond:null;
        const gain=data.gainPerAttack||{},interval=profiles[goal.rawcode]?.native?.attackCooldown;
        const attackRecovery=finite(gain.value)&&finite(interval)&&interval>0?gain.value/interval:null;
        const recoveryInputsComplete=base!==null&&attackRecovery!==null;
        const knownIntrinsic=recoveryInputsComplete?base+attackRecovery:null;
        const partialRecoveryReference={knownBaseRegenerationPerSecond:base,knownAttackRecoveryPerSecond:attackRecovery,knownComponentSumPerSecond:base!==null||attackRecovery!==null?(base??0)+(attackRecovery??0):null,recoveryInputsComplete,isTotalRecovery:false};
        const thresholdCandidates=[...(data.costs||[]),...(data.verifiedThresholds||[])].map(c=>c.thresholdMana).filter(x=>finite(x)&&x>0),threshold=thresholdCandidates.length?Math.min(...thresholdCandidates):null;
        const explicitCost=(data.costs||[]).map(c=>c.consumedMana).find(x=>finite(x)&&x>0),reset=(data.resets||[]).find(r=>finite(r.value));
        const verifiedReset=(data.verifiedThresholds||[]).find(item=>item.thresholdMana===threshold&&finite(item.resetMana));
        const consumption=explicitCost??(verifiedReset?Math.max(0,threshold-verifiedReset.resetMana):threshold!==null&&reset?Math.max(0,threshold-reset.value):null);
        function model(rate){
          const charge=finite(rate)&&rate>0&&threshold!==null?threshold/rate:null;
          const costAbilities=new Set((data.costs||[]).map(cost=>cost.abilityRawcode).filter(Boolean));
          // 다른 능력의 자동 발동 주기를 마나 주력기의 대체 주기로 사용하지 않는다.
          const periodic=(data.automaticTriggers||[]).filter(t=>costAbilities.has(t.abilityRawcode)&&finite(t.intervalSeconds)&&t.intervalSeconds>0&&t.triggerKind==="automatic_periodic"&&
            (finite(t.manaThresholdAlternative)||t.resourceConditionAlternative?.resource==="mana"||t.independentManaBranch===true));
          const attackGate=periodic.some(t=>t.requiresAttackForManaBranch||t.resourceConditionAlternative?.requiresAttackEvent);
          const aligned=charge!==null&&attackGate?(finite(interval)&&interval>0?Math.ceil(charge/interval)*interval:null):charge;
          const automatic=periodic.length?Math.min(...periodic.map(t=>t.intervalSeconds)):null;
          const relationship=verified?.periodicManaRelationship;
          const independent=relationship==="independent_additive_script_verified";
          const alternativeVerified=!verified||relationship==="alternative_branch_script_verified";
          const repeat=finite(rate)&&rate>0&&consumption!==null?consumption/rate:null;
          return {modeledRecoveryPerSecond:finite(rate)?rate:null,timeToThresholdFromZeroSeconds:charge,repeatManaCycleSeconds:finite(rate)&&rate>0&&consumption!==null?consumption/rate:null,
            attackAlignedManaTriggerSeconds:aligned,periodicManaRelationship:relationship||"unverified",independentPeriodicSeconds:independent?automatic:null,
            referenceCastsPerSecond:independent&&automatic!==null&&repeat!==null?1/automatic+1/repeat:null,automaticPeriodicReferenceSeconds:automatic,automaticAlternativeSeconds:alternativeVerified?automatic:null,
            earliestAlternativeTriggerSeconds:independent?null:alternativeVerified?automatic!==null&&aligned!==null?Math.min(automatic,aligned):automatic??aligned:aligned};
        }
        const eligible=[],excluded=[];
        for(const source of auraSources){
          if(source.rawcode===goal.rawcode&&(source.excludesSelf===true||source.includesSelf!==true)){excluded.push({source,reason:source.excludesSelf?"자기 자신 제외":"자기 대상 적용 미확인"});continue;}
          eligible.push(source);
        }
        const scenarios=eligible.map(source=>({provider:info(source.rawcode),abilityRawcode:source.abilityRawcode,effectId:source.id,extraManaPerSecond:source.value,source:source.evidence,conditions:source.conditions||[],assumesWithinRange:true,
          model:knownIntrinsic!==null?model(knownIntrinsic+source.value):model(null)}));
        const single=eligible.length===1&&eligible[0].unitCount===1;
        return {...info(goal.rawcode),...data,usesManaSkills:usage.usesManaSkills,manaUsageEvidence:usage,numericBalanceKnown:false,partialRecoveryReference,
          modeledInputs:{baseRegenerationPerSecond:base,manaPerAttackEvent:finite(gain.value)?gain.value:null,baseAttackInterval:finite(interval)?interval:null,attackRecoveryPerSecond:attackRecovery,consumedMana:consumption,recoveryInputsComplete},
          withoutSupport:model(knownIntrinsic),withDeckSupport:{aggregationKnown:single,extraManaPerSecond:single?eligible[0].value:null,model:single&&knownIntrinsic!==null?model(knownIntrinsic+eligible[0].value):null,individualAuraScenarios:scenarios,excludedSources:excluded,unresolvedSources:unresolvedAuraSources},
          assumptions:["기본 공격주기로 계속 공격한다고 가정","마나 오라 범위 안에 배치한다고 가정","14.25초 등 자동 발동 주기를 마나 회복량으로 바꾸지 않음","알 수 없는 공격 회복·중첩·소모량은 실제 0으로 확정하지 않음"],
          unresolved:unique([base===null?"기본 마나 재생 미확인":null,attackRecovery===null&&usesMana(goal.rawcode)?"공격당 회복 또는 실제 공격주기 미확인":null,eligible.length>1?"여러 마나 오라의 중첩 미확인":null,unresolvedAuraSources.length?"마나 오라 수치 미검증":null,consumption===null&&usesMana(goal.rawcode)?"반복 소모량 미확인":null])};
      });
    }
    function controlModels(goals){
      const slowSources=[],stunSources=[],penalties=[];
      for(const goal of goals)for(const effect of effects(goal.rawcode)){
        if(finite(effect.value)&&effect.value<0)penalties.push({...info(goal.rawcode),effect});
        const numericEligible=CombatVerifier.isNumericReferenceEligible(effect);
        if(effect.kind==="slow"&&enemy.has(effect.target))slowSources.push({...info(goal.rawcode),value:numericEligible?effect.value:null,declaredValue:effect.value,numericReferenceEligible:numericEligible,useForNumericAggregation:effect.useForNumericAggregation??null,mode:effect.mode,scope:effect.scope,durationSeconds:numericEligible?effect.durationSeconds:null,chancePercent:numericEligible?effect.chancePercent:null,stacking:effect.stacking,conditions:effect.conditions||[],evidence:effect.evidence});
        if(effect.kind!=="stun"||!enemy.has(effect.target)||effect.stunRoleEligible===false||effect.useForStunRecommendation===false)continue;
        const interval=profiles[goal.rawcode]?.native?.attackCooldown,p=numericEligible?effect.chancePercent:null,targetDurations=effect.targetDurations;
        const nativeSeconds=numericEligible&&targetDurations?.unit==="seconds",d=numericEligible?(nativeSeconds&&finite(targetDurations.general)?targetDurations.general:effect.durationSeconds):null;
        const resistantHeroDuration=nativeSeconds&&finite(targetDurations.resistantHero)&&targetDurations.resistantHero>=0?targetDurations.resistantHero:null;
        function procModel(duration){
          const known=finite(interval)&&interval>0&&finite(p)&&p>=0&&p<=100&&finite(duration)&&duration>=0&&effect.mode==="proc";
          let uptime=null,gap=null;
          if(known){const probability=p/100,n=Math.floor(duration/interval),fraction=duration/interval-n;uptime=p===0?0:1-Math.pow(1-probability,n)*(1-fraction*probability);gap=p===0?null:Math.pow(1-probability,n)*(interval/probability-fraction*interval);}
          return {modelKnown:known,modeledUptime:uptime,modeledMeanGapSeconds:gap};
        }
        const generalModel=procModel(d),resistantHeroModel=procModel(resistantHeroDuration);
        const periodic=numericEligible?(skillMana(goal.rawcode).automaticTriggers||[]).find(t=>t.abilityRawcode===effect.abilityRawcode&&t.triggerKind==="automatic_periodic"&&finite(t.intervalSeconds)):null;
        stunSources.push({...info(goal.rawcode),abilityRawcode:effect.abilityRawcode,scope:effect.scope,target:effect.target,mode:effect.mode,chancePercent:finite(p)?p:null,durationSeconds:finite(d)?d:null,baseAttackInterval:finite(interval)?interval:null,
          ...generalModel,generalDurationSeconds:finite(d)?d:null,resistantHeroDurationSeconds:resistantHeroDuration,generalModel,resistantHeroModel,targetDurations:numericEligible?targetDurations||null:null,declaredEffect:effect,numericReferenceEligible:numericEligible,useForNumericAggregation:effect.useForNumericAggregation??null,targetScenarioSelected:false,
          automaticIntervalSeconds:periodic?.intervalSeconds??null,automaticGapSeconds:periodic&&finite(d)?Math.max(0,periodic.intervalSeconds-d):null,
          conditions:effect.conditions||[],evidence:effect.evidence,assumptions:["기본 공격주기로 같은 대상을 계속 공격한다고 가정","공격당 발동을 독립으로 가정","일반·내성 영웅 시나리오를 분리하며 실제 대상 유형을 판정하지 않음","배치·실효 공속·보스 저항·시전 정지는 미검증"]});
      }
      return {slow:{sources:slowSources,effectiveTotal:null,aggregationKnown:false,relations:unique(slowSources.flatMap(s=>(s.conditions||[]).filter(text=>String(text).includes("중첩"))))},
        stun:{sources:stunSources,area:stunSources.filter(s=>["area","all"].includes(s.scope)),single:stunSources.filter(s=>s.scope==="single"),combinedUptime:null},penalties,
        unresolved:["이감 합계를 적 정지로 환산하지 않음","복수 스턴의 동시 발동·대상 교체·배치를 합산하지 않음"]};
    }
    function resourceModels(goals){
      return goals.filter(g=>strategyUnits[g.rawcode]?.resourceDependence?.resource==="health").map(goal=>{
        const original=strategyUnits[goal.rawcode].resourceDependence,verified=options.combatValidation?.unitModels?.map?.sha256===hash?options.combatValidation.unitModels.units?.[goal.rawcode]?.healthResource:null;
        const data=verified?{...original,...verified}:original,interval=profiles[goal.rawcode]?.native?.attackCooldown;
        const base=data.baseRegenerationUnit==="healthPerSecond"&&finite(data.baseRegenerationPerSecond)?data.baseRegenerationPerSecond:null;
        const verifiedAttackGain=verified?.gainPerAttackSourceVerified===true&&finite(verified.gainPerAttack)?verified.gainPerAttack:null;
        const gain=verified?verifiedAttackGain:data.gainPerAttack?.unit==="healthPerAttackEvent"&&finite(data.gainPerAttack.value)?data.gainPerAttack.value:null;
        const intrinsic=base!==null||gain!==null&&finite(interval)&&interval>0?(base||0)+(gain!==null&&finite(interval)&&interval>0?gain/interval:0):null;
        const reset=finite(verified?.resetValue)?verified.resetValue:(data.resets||[]).find(x=>x.unit==="health"&&finite(x.value))?.value??null;
        const required=finite(data.threshold)&&reset!==null?Math.max(0,data.threshold-reset):null;
        const sources=goals.flatMap(provider=>supports(provider.rawcode,"healthSupport").filter(e=>CombatVerifier.isNumericReferenceEligible(e)&&e.unit==="healthPerSecond"&&finite(e.value)&&["aura","always"].includes(e.mode)&&
          (provider.rawcode!==goal.rawcode||e.includesSelf===true&&e.excludesSelf!==true)).map(e=>({...info(provider.rawcode),count:provider.count,effect:e})));
        function model(rate){const refill=finite(rate)&&rate>0&&required!==null?required/rate:null;return {modeledRecoveryPerSecond:finite(rate)?rate:null,refillFromResetSeconds:refill,
          attackAlignedTriggerSeconds:verified?.gainPerAttackDamageEvent!=null?null:refill!==null&&finite(interval)&&interval>0?Math.ceil(refill/interval)*interval:null};}
        const single=sources.length===1&&sources[0].count===1;
        return {...info(goal.rawcode),...data,damageMechanics:strategyUnits[goal.rawcode].damageMechanics||[],numericBalanceKnown:false,modeledInputs:{baseRegenerationPerSecond:base,gainPerAttackEvent:gain,gainPerAttackDamageEvent:verified?.gainPerAttackDamageEvent??null,baseAttackInterval:finite(interval)?interval:null,reset,requiredRefill:required,recoveryInputCoverageComplete:base!==null&&gain!==null,baseRecoveryOnly:gain===null},withoutSupport:model(intrinsic),
          withDeckSupport:{aggregationKnown:single,model:single&&intrinsic!==null?model(intrinsic+sources[0].effect.value):null,individualAuraScenarios:sources.map(s=>({...s,model:intrinsic!==null?model(intrinsic+s.effect.value):model(null)}))},
          assumptions:["기본 공격주기로 계속 공격한다고 가정","체력 회복 오라의 범위 안에 배치한다고 가정","실효 공속·오라 중첩·실제 현재 체력은 미확인",...(verified?.gainPerAttackDamageEvent!=null?["피해 이벤트의 체력 회복을 공격 애니메이션당 회복으로 환산하지 않음"]:[])]};
      });
    }
    function encounterAssessment(template,roster,settings){
      const boss=["warcury","nasujuro","saturn"].includes(settings.boss)?settings.boss:null,record=boss&&rulesMap?combat.nightmareRules?.encounters?.[boss]:null;
      const notes=[],required=[];
      if(record){
        if(boss==="warcury"){notes.push("워큐리 선택 시나리오: 적 방어력·마법방어 증가에 맞춰 감소 효과 조건을 확인해야 합니다.");if(template.direction!=="magic")required.push("armorReduction");if(template.direction!=="physical")required.push("magicResistanceReduction");}
        if(boss==="nasujuro"){notes.push("나스쥬로 선택 시나리오: 적 이동속도 증가·아군 공속 감소를 고려해 이감·범위 스턴·팀 공속 조건을 확인해야 합니다.");required.push("alwaysSlow","areaStun","attackSpeedBuff");}
        if(boss==="saturn"){notes.push("새턴 선택 시나리오: 공격력·폭발형 피해 감소가 적용되므로 공격력에 비례하는 기술과 보조 조건을 확인해야 합니다.");required.push("attackDamageBuff");}
        for(const effect of record.effects||[])if(effect.uncertainConflict||effect.displayPolicy==="warning_only")notes.push(effect.warning||"안내와 능력값이 달라 효과 수치를 확정하지 않았습니다.");
      }
      const round=Number.isSafeInteger(settings.round)&&settings.round>0?settings.round:null,limit=rulesMap?combat.nightmareRules?.lineEnemyCountLimits:null;
      if(round!==null&&limit&&round>=limit.fromRound)notes.push(limit.fromRound+"라운드부터 라인 적 수 제한은 "+limit.after+"입니다. 아군 유닛 수 제한이 아닙니다.");
      if(round!==null&&round>=35)notes.push("와노쿠니의 35라운드 전 파괴 목표 달성 여부는 게임에서 확인해야 합니다.");
      return {boss,selectionOnly:true,activeObserved:false,conditional:true,notes:unique(notes),missingRoles:unique(required.filter(kind=>!roster.goals.some(g=>supports(g.rawcode,kind).length))),effects:record?.effects||[],additionalNightmareEffects:record?.additionalNightmareEffects||[],round};
    }
    function assessment(template,roster,settings){
      const damageCodes=unique([...roster.coreCodes,...roster.goals.filter(goal=>coreMode(goal.rawcode)).map(goal=>goal.rawcode)]);
      const demanded=new Map([["lineDamage",1],["bossDamage",1],["storyDamage",1],["alwaysSlow",1],["areaStun",1]]);
      for(const item of template.requiredCoverage||[]){const key=typeof item==="string"?item:item.role;if(key&&(key!=="manaSupport"||damageCodes.some(needsManaSupport)))demanded.set(key,typeof item==="object"?(item.minCount||item.count||1):1);}
      if(template.direction==="physical"||template.direction==="hybrid")demanded.set("armorReduction",1);
      if(template.direction==="magic"||template.direction==="hybrid"){demanded.set("magicResistanceReduction",1);demanded.set("magicAmplification",1);}
      if(damageCodes.some(needsManaSupport))demanded.set("manaSupport",1);
      if(rulesMap)for(const item of rules.completion?.structuralRequirements||[]){if(item.appliesIf&&!damageCodes.some(needsManaSupport))continue;if(item.role)demanded.set(item.role,item.role==="reviewedDamageCore"?(template.coreComposition?.minimumDpsCoreCount??item.count??1):(item.count||1));}
      const dpsCodes=template.coreComposition?.dpsRawcodes||roster.coreCodes.filter(code=>role(code)&&strategyUnits[code]?.finalAnchorEligible!==false);
      const coverage=Array.from(demanded,([key,count])=>requirement(key,roster.goals,count,dpsCodes));
      const missingSlots=roster.slots.filter(s=>!s.rawcode).map(s=>s.id);
      const mana=manaModels(roster.goals);
      return {compositionStatus:missingSlots.length?"incomplete":template.recordPresenceGoals?"recorded_presence_unverified":coverage.some(r=>r.status==="missing")?"incomplete":"conditional",roleCompositionStatus:missingSlots.length||coverage.some(r=>r.status==="missing")?"incomplete":"covered_conditionally",coverage,mana,resourceDependence:resourceModels(roster.goals),control:controlModels(roster.goals),encounter:encounterAssessment(template,roster,settings||{}),coreComposition:template.coreComposition||{dpsRawcodes:dpsCodes,actualDpsCoreCount:dpsCodes.length},missingSlots,
        currentProfileVersion:"2.323",recordBalance:template.sourceEvidence?.balanceAdjustment||null,
        numericCombatStatus:"unverified",clearStatus:"unverified",canConfirmClear:false,evidenceScope:template.evidenceScope||"native_composition_not_verified_clear",
        unresolved:unique([template.recordPresenceGoals?"공개 등록 명단은 전체 보유 유닛과 수량을 확정하지 않음":null,"실효 DPS·공격시간·실전 마나 수지 미검증","제어 중첩·보스 면역·스토리 마감시간 미검증",...(rules.completion?.unresolved||[]),...(template.limitations||[])])};
    }
    function protectedFor(roster,inventory,settings){
      const held=new Map();
      if(!includeMysteriousDecks)for(const [code,count]of inventory)if(count>0&&mysterious(code))held.set(code,count);
      for(const goal of roster.goals){const have=Math.min(goal.count,inventory.get(goal.rawcode)||0);if(have)held.set(goal.rawcode,have);}
      if(recordOnly)for(const retained of roster.retained){const have=Math.min(retained.count,inventory.get(retained.rawcode)||0);if(have)held.set(retained.rawcode,Math.max(held.get(retained.rawcode)||0,have));}
      for(const [code,count]of inventory)if(count>0&&advanced.has(units.get(code)?.tier)&&native(code)&&!roster.replacements.some(unit=>unit.rawcode===code)&&
        (role(code)||strategyUnits[code]?.primaryRole?.includes("Support"))&&
        (roster.goals.some(goal=>goal.rawcode===code)||!roster.goals.some(goal=>(inventory.get(goal.rawcode)||0)<goal.count&&ancestry(goal.rawcode,code))))held.set(code,Math.max(held.get(code)||0,1));
      for(const [code,count]of countsOf(settings.protectedCounts))if(count>0&&units.get(code)?.playable&&!excludedSummons.has(code))held.set(code,Math.max(held.get(code)||0,count));
      return held;
    }
    const checkedConditions=new Set(["UBAN","KING","IFCT","CHCT","SKPT","RMAX","NIKA"]);
    function totals(steps,settings){const result={wood:0,gold:0,items:[],conditions:[]},all=[];for(const step of steps||[])for(const item of step.nonUnitRequirements||[]){all.push(item);if(item.type==="ITEM")result.items.push(item);else if(!["WOOD","GOLD"].includes(item.type))result.conditions.push(item);}Object.assign(result,Graph.effectiveResourceCosts(all,settings?.recipeConditions));return result;}
    function resources(cost,settings){const items=Graph.evaluateItems(cost.items,settings.items),conditions=Graph.evaluateRecipeConditions(cost.conditions,settings.recipeConditions);if(items.missingItems.some(item=>item.known&&item.missing>0)||conditions.blockedConditions.length)return false;if(!items.itemsReady||conditions.unknownConditions.length||!finite(cost.wood)||!finite(cost.gold))return null;return finite(settings.wood)&&settings.wood<cost.wood||finite(settings.gold)&&settings.gold<cost.gold?false:cost.wood>0&&!finite(settings.wood)||cost.gold>0&&!finite(settings.gold)||cost.conditions.some(item=>!checkedConditions.has(item.type))?null:true;}
    function conditionalCosts(cost,settings){
      const conditionState=Graph.evaluateRecipeConditions(cost?.conditions,settings.recipeConditions);
      const groups=new Map();for(const item of [...(cost?.conditions||[]).filter(item=>!checkedConditions.has(item.type)),...conditionState.unknownConditions]){const type=String(item.type||"unknown");if(!groups.has(type))groups.set(type,{type,requirements:[],availability:"unverified"});groups.get(type).requirements.push(item);}
      const special=(groups.get("IFCT")?.requirements||[]);
      return {...conditionState,...Graph.evaluateItems(cost?.items,settings.items),nonUnitConditions:Array.from(groups.values()),specialResourceRequirements:special.length?[{type:"IFCT",requiredAtLeast:Math.max(...special.map(x=>finite(x.count)?x.count:0)),recipes:unique(special.map(x=>x.recipeId)),consumptionVerified:false,availability:"unverified"}]:[],unverifiedConditionTypes:Array.from(groups.keys())};
    }
    function sequence(steps,initial,held,settings){
      const stock=new Map(initial),frames=[],usedItems=[];let wood=0,gold=0,reason=null,recipeConditions=settings.recipeConditions;
      for(let index=0;index<steps.length;index++){
        const step=steps[index];
        if(step.type==="craft"&&!recommendationRecipeAllowed(recipes.get(String(step.recipeId)))){reason="mysterious_recommendations_disabled";break;}
        if(step.type==="craft"&&settings.supportedRecipeIds!=null&&!new Set(settings.supportedRecipeIds).has(String(step.recipeId))){reason="unsupported_recipe";break;}
        if(step.ingredients.some(i=>(stock.get(i.rawcode)||0)-i.count<(held.get(i.rawcode)||0))){reason="missing_material";break;}
        const conditionCheck=Graph.evaluateRecipeConditions(step.nonUnitRequirements,recipeConditions);if(!conditionCheck.conditionsReady){reason=conditionCheck.unknownConditions.length?"recipe_conditions_unknown":"recipe_conditions_blocked";break;}
        const cost=totals([step],{...settings,recipeConditions});const itemCheck=Graph.evaluateItems([...usedItems,...cost.items],settings.items);if(!itemCheck.itemsReady){reason=itemCheck.missingItems.some(item=>!item.known)?"items_unknown":"missing_item";break;}if(finite(settings.gold)&&gold+cost.gold>settings.gold){reason="missing_gold";break;}if(finite(settings.wood)&&wood+cost.wood>settings.wood){reason="missing_wood";break;}
        for(const i of step.ingredients)stock.set(i.rawcode,(stock.get(i.rawcode)||0)-i.count);
        stock.set(step.resultRawcode,(stock.get(step.resultRawcode)||0)+step.resultCount);wood+=cost.wood;gold+=cost.gold;usedItems.push(...cost.items);recipeConditions=Graph.projectRecipeConditions(step.nonUnitRequirements,recipeConditions);
        frames.push({index,stock:new Map(stock),wood,gold});
      }
      return {frames,reason};
    }
    function nextAction(plan,roster,inventory,held,settings){
      if(!plan?.executable)return null;
      const tested=sequence(plan.steps,inventory,held,settings),needed=roster.goals.filter(g=>(inventory.get(g.rawcode)||0)<g.count);
      const executableFrames=tested.frames.filter(frame=>frame.index<64);
      const goalFrame=executableFrames.find(frame=>{const step=plan.steps[frame.index];return nextResult(step)&&needed.some(g=>g.rawcode===step.resultRawcode&&(frame.stock.get(g.rawcode)||0)>=g.count);});
      const eligible=unique([goalFrame,...executableFrames.filter(f=>(!goalFrame||f.index<goalFrame.index)&&nextResult(plan.steps[f.index])&&(f.stock.get(plan.steps[f.index].resultRawcode)||0)>(inventory.get(plan.steps[f.index].resultRawcode)||0))].filter(Boolean));
      let frame,toward,locks;
      for(const candidate of eligible){
        const dependencies=recipeDependencies(plan.steps[candidate.index].recipeId);
        if(!plan.steps.slice(0,candidate.index+1).every(step=>dependencies.has(step.resultRawcode)))continue;
        const result=plan.steps[candidate.index].resultRawcode,target=needed.find(g=>g.rawcode===result||ancestry(g.rawcode,result))||needed[0];
        const protection=new Map(held);
        const replay=sequence(plan.steps.slice(0,candidate.index+1),inventory,protection,settings);
        if(replay.frames.length!==candidate.index+1)continue;
        frame=candidate;toward=target;locks=protection;break;
      }
      if(!frame)return null;
      const step=plan.steps[frame.index],expectedSteps=plan.steps.slice(0,frame.index+1);
      const cost=totals(expectedSteps,settings);
      return {...info(step.resultRawcode),recipeId:step.recipeId,count:(frame.stock.get(step.resultRawcode)||0)-(inventory.get(step.resultRawcode)||0),
        expectedSteps:expectedSteps.map(s=>s.type==="choice"?{type:"choice",resultRawcode:s.resultRawcode}:{type:"craft",recipeId:s.recipeId,...(s.materialChoiceRawcode?{materialChoiceRawcode:s.materialChoiceRawcode}:{})}),
        plan:expectedSteps,protectedCounts:dict(locks),expectedFinalCounts:dict(frame.stock),expectedStepCount:expectedSteps.length,
        choiceWisps:expectedSteps.filter(s=>s.type==="choice").length,wood:cost.wood,gold:cost.gold,...Graph.evaluateItems(cost.items,settings.items),...Graph.evaluateRecipeConditions(cost.conditions,settings.recipeConditions),nonUnitRequirements:expectedSteps.flatMap(s=>s.nonUnitRequirements||[]),resourcesReady:resources(cost,settings),
        towardRawcode:toward?.rawcode,towardName:toward?.name,towardTier:toward?.tier,
        isWholeGoal:Boolean(goalFrame),ready:true,requiresExpectedSteps:true,origin:"selected_deck_joint_prefix"};
    }
    function futurePlan(goals,inventory,options,virtualBudget,planner,deadline){
      planner=planner||graph;deadline=deadline??Infinity;
      const future=new Map(inventory),futureMaterials=new Map();future.set("e018",(future.get("e018")||0)+virtualBudget);
      const futureOptions={...options,supportedRecipeIds:null,allowChoiceWisps:true,maxSteps:2000};
      let forecast=planner.planDeck(goals,future,futureOptions);
      // 별도 획득 재료도 장기 목표에 남겨 둔다. 이 가상 재고는 실행 prefix 재생에는 전달하지 않는다.
      for(let attempt=0;attempt<24&&!forecast.executable&&forecast.reason==="missing_material";attempt++){
        if(Date.now()>deadline){forecast={...forecast,reason:"search_limit",missing:[]};break;}
        let changed=false;
        for(const item of forecast.missing||[]){
          if(!units.has(item.rawcode)||!includeMysteriousDecks&&mysterious(item.rawcode)||nonForecastable.has(item.rawcode)||/^h00[1-9]$/.test(item.rawcode)||!Number.isSafeInteger(item.count)||item.count<1||item.count>1000||Array.from(recipes.values()).some(r=>Graph.isProductionRecipe(r)&&r.resultUnitRawcode===item.rawcode))continue;
          const total=(futureMaterials.get(item.rawcode)||0)+item.count;if(total>1000)continue;
          futureMaterials.set(item.rawcode,total);future.set(item.rawcode,(future.get(item.rawcode)||0)+item.count);changed=true;
        }
        if(!changed)break;
        forecast=planner.planDeck(goals,future,futureOptions);
      }
      return {plan:forecast,futureMaterials:Array.from(futureMaterials,([rawcode,count])=>({...info(rawcode),count}))};
    }
    function feasibilityDiagnosis(goals,inventory,planningOptions,virtualBudget,settings){
      if(Date.now()>feasibilityDeadline)return null;
      if(!feasibilityPlanner){
        // 확정 차단 경로를 먼저 제거한다. Graph의 최초 실패 요약이 허용된 다른 경로의 부족 재료를 가릴 수 있다.
        const production=Array.from(recipes.values()).filter(Graph.isProductionRecipe),allowed=production.filter(recipe=>recommendationRecipeAllowed(recipe)&&!fixedBlockedConditions(recipe,settings.recipeConditions).length);
        const allowedResults=new Set(allowed.map(recipe=>recipe.resultUnitRawcode));
        const blockedRawcodes=new Set(production.filter(recipe=>!allowedResults.has(recipe.resultUnitRawcode)).map(recipe=>recipe.resultUnitRawcode));
        const bans=Object.fromEntries(production.flatMap(recipe=>(recipe.ingredients||[]).filter(item=>item.type==="UBAN"&&typeof item.rawcode==="string").map(item=>[item.rawcode,false])));
        feasibilityPlanner={graph:Graph.create({...catalog,recipes:allowed}),blockedRawcodes,
          recipeConditions:{status:"confirmed",localPlayer:settings.localPlayer,unitBans:bans,king:{status:"allowed"},source:{readOnly:true}},
          items:{status:"confirmed",counts:{AI00:1,AI01:1,AI02:1}}};
      }
      // 이 낙관 재고는 추천 도달성 진단에만 쓰며 실제 계획·퍼센트·실행 허가에는 전달하지 않는다.
      const diagnosed=futurePlan(goals,inventory,{...planningOptions,recipeConditions:feasibilityPlanner.recipeConditions,items:feasibilityPlanner.items,maxSearchNodes:Math.min(2000,planningOptions.maxSearchNodes||16000)},virtualBudget,feasibilityPlanner.graph,feasibilityDeadline);
      return {...diagnosed,blockedRawcodes:feasibilityPlanner.blockedRawcodes};
    }
    function minimumNecessaryMaterials(roster,inventory,materials,settings){
      const allowed=Array.from(recipes.values()).flatMap(recipe=>Graph.expandMaterialChoices(recipe,catalog)).filter(recipe=>Graph.isProductionRecipe(recipe)&&recommendationRecipeAllowed(recipe)&&!fixedBlockedConditions(recipe,settings.recipeConditions).length);
      let nodes=0;
      function minimum(target,count,material,path){
        if(++nodes>2000||path.has(target))return 0;
        const needed=Math.max(0,count-(inventory.get(target)||0));if(!needed)return 0;if(target===material)return needed;
        const choices=allowed.filter(recipe=>recipe.resultUnitRawcode===target);if(!choices.length)return 0;
        const next=new Set(path);next.add(target);
        return Math.min(...choices.map(recipe=>{const batches=needed/Math.max(1,recipe.resultCount||1);return Math.max(0,...(recipe.ingredients||[]).filter(item=>item.type==="UNIT").map(item=>minimum(item.rawcode,item.count*batches,material,next)));}));
      }
      // 목표별 합계는 배치 생성·공동 중간재료를 중복 계산할 수 있다. max는 모든 경로에 필요한 보수적 하한이다.
      const lowerBounds=materials.map(rawcode=>({...info(rawcode),count:Math.floor(Math.max(0,...roster.goals.map(goal=>minimum(goal.rawcode,goal.count,rawcode,new Set()))))})).filter(item=>item.count>0);
      return {lowerBounds,searchLimited:nodes>2000};
    }
    function unavoidableSharedChoiceDeficit(roster,inventory,template,settings){
      const nonExclusive=new Set(["h060","h05Y",...(combat.acquisitionModel?.independentlyAcquirableRequirementRawcodes||[])]);
      const materials=unique((combat.acquisitionModel?.sharedChoiceResources||[]).flatMap(source=>(source.options||[]).flatMap(option=>(option.outputs||[]).map(item=>item.rawcode))).filter(code=>!nonExclusive.has(code)));
      const minimum=minimumNecessaryMaterials(roster,inventory,materials,settings),lowerBounds=minimum.lowerBounds;
      const acquisition=acquisitionPlan(lowerBounds,inventory,template);
      return {proven:!acquisition.sharedChoiceFeasible,lowerBounds,searchLimited:minimum.searchLimited};
    }
    function recommendationFeasibility(template,roster,inventory,settings,planningOptions,virtualBudget,forecast,futureMaterials,acquisition){
      randomRoutes.set(template.id,forecast?.executable?forecast:null);
      const evidenceKey=semanticKey([feasibilityEvidenceContext,template.id,roster.goals.map(goal=>[goal.rawcode,goal.count])]);
      const remember=result=>{if(result.status==="blocked"){if(feasibilityEvidence.size>=512)feasibilityEvidence.clear();feasibilityEvidence.set(evidenceKey,result);}return result;};
      const known=feasibilityEvidence.get(evidenceKey);if(known)return {...known,evidenceReused:true};
      const fixedBlock=fixedGoalBlock(roster,inventory,settings);
      if(fixedBlock.proven)return remember({status:"blocked",grade:"C",reasons:["confirmed_recipe_condition_blocked"],diagnosed:true,blockedMaterialRawcodes:[fixedBlock.rawcode],conditionProof:{method:"all_production_alternatives_node_bounded",...fixedBlock}});
      let proof=forecast,proofMaterials=futureMaterials,proofAcquisition=acquisition,diagnosed=false,blockedRawcodes=new Set();
      if(!forecast.executable){
        const diagnosis=feasibilityDiagnosis(roster.goals.map(goal=>({rawcode:goal.rawcode,count:goal.count})),inventory,planningOptions,virtualBudget,settings);
        if(diagnosis){proof=diagnosis.plan;proofMaterials=diagnosis.futureMaterials;blockedRawcodes=diagnosis.blockedRawcodes;proofAcquisition=acquisitionPlan(proofMaterials,inventory,template);diagnosed=true;}
        else return {status:"conditional",grade:"B",reasons:["planning_limit"],diagnosed:false};
      }
      if(proof?.executable)randomRoutes.set(template.id,proof);
      const hard=[],conditional=[];
      if(proof?.reason==="king_path_craft_budget")hard.push("king_path_craft_budget");
      const limited=["search_limit","step_limit"].includes(proof.reason);
      const missingNonForecastable=(proof.missing||[]).filter(item=>nonForecastable.has(item.rawcode));
      const missingBlocked=(proof.missing||[]).filter(item=>blockedRawcodes.has(item.rawcode));
      const necessary=missingBlocked.length||missingNonForecastable.length?minimumNecessaryMaterials(roster,inventory,unique([...missingBlocked,...missingNonForecastable].map(item=>item.rawcode)),settings):{lowerBounds:[],searchLimited:false},mandatory=necessary.lowerBounds;
      if(!limited&&!necessary.searchLimited&&proof.reason==="missing_material"&&missingBlocked.some(item=>mandatory.some(required=>required.rawcode===item.rawcode)))hard.push("confirmed_recipe_condition_blocked");
      if(!limited&&!necessary.searchLimited&&proof.reason==="missing_material"&&missingNonForecastable.some(item=>mandatory.some(required=>required.rawcode===item.rawcode)))hard.push("unowned_random_only_material");
      let sharedChoiceProof=null;
      if(proof.executable&&!proofAcquisition.sharedChoiceFeasible){sharedChoiceProof=unavoidableSharedChoiceDeficit(roster,inventory,template,settings);if(sharedChoiceProof.proven)hard.push("shared_choice_budget_exhausted");else conditional.push("shared_choice_path_unresolved");}
      if(hard.length)return remember({status:"blocked",grade:"C",reasons:hard,diagnosed,missingNonForecastableMaterials:missingNonForecastable,blockedMaterialRawcodes:missingBlocked.map(item=>item.rawcode),sharedChoiceProof});
      const cost=proof.executable?totals(proof.steps,settings):null,conditions=conditionalCosts(cost,settings);
      if(conditions.missingItems.some(item=>item.known&&item.missing>0))conditional.push("random_item_missing");
      if(conditions.missingItems.some(item=>!item.known))conditional.push("item_state_unknown");
      if(conditions.unknownConditions.length)conditional.push("recipe_conditions_unknown");
      if(conditions.blockedConditions.length)conditional.push("recipe_conditions_unresolved");
      if(conditions.unverifiedConditionTypes.length)conditional.push("special_conditions_unverified");
      if(proofAcquisition.futureConditional)conditional.push("future_shared_choice_reward");
      if(proofAcquisition.remainingMaterials.length)conditional.push("future_special_material");
      if(!proof.executable)conditional.push(limited?"planning_limit":"planning_unresolved");
      if(proof.executable&&proof.steps.some(step=>step.type==="craft"&&!settings.supportedRecipeIds.has(String(step.recipeId))))conditional.push("execution_route_unsupported");
      return {status:conditional.length?"conditional":"reachable",grade:conditional.length?"B":"A",reasons:unique(conditional),diagnosed};
    }
    function budgetGraph(){
      if(budgetPlanner)return budgetPlanner;
      const unused=prefix=>Array.from({length:100},(_,index)=>prefix+String(index).padStart(2,"0")).find(rawcode=>!units.has(rawcode));
      const wood=unused("zW"),gold=unused("zG");
      const transformed=Array.from(recipes.values()).filter(recipe=>Graph.isProductionRecipe(recipe)&&recommendationRecipeAllowed(recipe)).map(recipe=>({...recipe,ingredients:[...(recipe.ingredients||[]),
        ...(recipe.ingredients||[]).filter(item=>["WOOD","GOLD"].includes(item.type)&&item.count>0).map(item=>({type:"UNIT",rawcode:item.type==="WOOD"?wood:gold,count:item.count}))]}));
      budgetPlanner={wood,gold,graph:Graph.create({...catalog,units:[...(catalog.units||[]),{rawcode:wood},{rawcode:gold}],recipes:transformed})};
      return budgetPlanner;
    }
    function* sharedBudgetStocks(stock,search){
      const sources=(combat.acquisitionModel?.sharedChoiceResources||[]).slice(0,4);let visited=0;
      function* visit(index,current,allocations){
        if(++visited>128||Date.now()>feasibilityDeadline){search.limited=true;return;}
        if(index===sources.length){yield {stock:current,allocations};return;}
        const source=sources[index],consume=Number.isSafeInteger(source.consumeCount)&&source.consumeCount>0?source.consumeCount:1;
        const count=Math.min(16,Math.floor((current.get(source.sourceRawcode)||0)/consume));
        const choices=(source.options||[]).filter(option=>option.evidence&&(option.outputs||[]).length&&option.outputs.every(output=>units.has(output.rawcode)&&(includeMysteriousDecks||!mysterious(output.rawcode))&&Number.isSafeInteger(output.count)&&output.count>0)).slice(0,12);
        function* choose(optionIndex,left,value,used){
          if(++visited>128||Date.now()>feasibilityDeadline){search.limited=true;return;}
          if(optionIndex===choices.length){yield* visit(index+1,value,[...allocations,...used]);return;}
          const option=choices[optionIndex];
          for(let amount=0;amount<=left;amount++){
            const next=new Map(value);
            if(amount){next.set(source.sourceRawcode,(next.get(source.sourceRawcode)||0)-amount*consume);for(const output of option.outputs)next.set(output.rawcode,(next.get(output.rawcode)||0)+output.count*amount);}
            yield* choose(optionIndex+1,left-amount,next,amount?[...used,{sourceRawcode:source.sourceRawcode,optionId:option.id,count:amount}]:used);
          }
        }
        yield* choose(0,count,current,[]);
      }
      yield* visit(0,new Map(stock),[]);
    }
    function rewardBudgetFeasibility(roster,inventory,settings,planningOptions,forecast){
      const budget=activeRewardBudget||{},known=["confirmed","partial"].includes(budget.status),number=value=>Number.isSafeInteger(value)&&value>=0?value:null;
      const current={choiceWisps:inventory.get("e018")||0,wood:number(settings.wood),gold:number(settings.gold)};
      const sum=(a,b)=>a===null?null:a+(b??0);
      const future={guaranteed:known?budget.guaranteed:{choiceWisps:null,wood:null,gold:null,units:{}},conditional:known?budget.conditional:{choiceWisps:null,wood:null,gold:null,units:{}}};
      const guaranteed=Object.fromEntries(["choiceWisps","wood","gold"].map(key=>[key,sum(current[key],number(future.guaranteed?.[key]))]));
      const conditional=Object.fromEntries(["choiceWisps","wood","gold"].map(key=>[key,sum(guaranteed[key],number(future.conditional?.[key]))]));
      const estimate=forecast.executable?totals(forecast.steps,settings):null;
      let required={choiceWisps:forecast.executable?forecast.choiceWispCount:null,wood:estimate?.wood??null,gold:estimate?.gold??null};
      const deficit=(need,available)=>Object.fromEntries(["choiceWisps","wood","gold"].map(key=>[key,need[key]===null||available[key]===null?null:Math.max(0,need[key]-available[key])]));
      const base={round:known?budget.round:null,storyIndex:known?budget.storyIndex:null,available:{current,guaranteed,conditional},availableIsLowerBound:!known||budget.status!=="confirmed",future,required,
        deficits:{guaranteed:deficit(required,guaranteed),conditional:deficit(required,conditional)},allConditionsKnown:false,sourceStatus:budget.status||"unknown",executionEligible:false,costQuality:"selected_path_not_minimum"};
      const randomCodes=new Set(budget.randomSourceRawcodes||["e0IX","e017","e016","e019","e015"]);
      const randomAvailable=Array.from(randomCodes).reduce((count,rawcode)=>count+(inventory.get(rawcode)||0)+(known?(future.guaranteed?.units?.[rawcode]||0)+(future.conditional?.units?.[rawcode]||0):0),0);
      base.randomWispsAvailable=randomAvailable;
      const planner=budgetGraph(),goals=roster.goals.map(goal=>({rawcode:goal.rawcode,count:goal.count}));
      function attempt(includeFuture,includeConditional){
        if(Date.now()>feasibilityDeadline)return {limited:true};
        const stock=new Map(inventory),delta=includeFuture?future.guaranteed:null,extra=includeConditional?future.conditional:null;
        for(const group of [delta,extra])if(group){stock.set("e018",(stock.get("e018")||0)+(number(group.choiceWisps)||0));for(const [rawcode,count]of Object.entries(group.units||{}))stock.set(rawcode,(stock.get(rawcode)||0)+count);}
        const money=includeConditional?conditional:includeFuture?guaranteed:current;
        stock.set(planner.wood,money.wood??current.wood??0);stock.set(planner.gold,money.gold??current.gold??0);
        let latest=null,tried=0,limited=false;const search={limited:false};
        for(const variant of sharedBudgetStocks(stock,search)){
          tried++;if(Date.now()>feasibilityDeadline){limited=true;break;}
          const plan=planner.graph.planDeck(goals,variant.stock,{...planningOptions,supportedRecipeIds:null,allowChoiceWisps:true,maxSteps:2000,maxSearchNodes:Math.min(16000,planningOptions.maxSearchNodes||16000)});
          latest=plan;if(["search_limit","step_limit"].includes(plan.reason))limited=true;
          if(plan.executable){
            const exactCost=totals(plan.steps,settings),unknownMoney=["wood","gold"].some(key=>!finite(exactCost[key])||exactCost[key]>0&&!finite(money[key]));
            if(unknownMoney){limited=true;latest={...plan,executable:false,reason:"resources_unknown"};continue;}
            const insufficient=["wood","gold"].find(key=>exactCost[key]>0&&exactCost[key]>money[key]);
            if(insufficient){latest={...plan,executable:false,reason:"missing_"+insufficient};continue;}
            return {plan,allocations:variant.allocations,limited:false};
          }
        }
        return {plan:latest,limited:limited||search.limited||!tried||Date.now()>feasibilityDeadline};
      }
      const currentOnly=attempt(false,false);
      let found=currentOnly.plan?.executable?currentOnly:null,quantityStatus=found?"within_guaranteed":null;
      let guaranteeAttempt=currentOnly,conditionalAttempt=null;
      if(!found&&known){guaranteeAttempt=attempt(true,false);if(guaranteeAttempt.plan?.executable){found=guaranteeAttempt;quantityStatus="within_guaranteed";}}
      if(!found&&known){conditionalAttempt=attempt(true,true);if(conditionalAttempt.plan?.executable){found=conditionalAttempt;quantityStatus="within_conditional";}}
      if(found){
        const cost=totals(found.plan.steps,settings),allConditionsKnown=(found.plan.nonUnitRequirements||[]).every(item=>["WOOD","GOLD","ITEM",...checkedConditions].includes(item.type));
        required={choiceWisps:found.plan.choiceWispCount,wood:cost.wood,gold:cost.gold};
        return {...base,required,deficits:{guaranteed:deficit(required,guaranteed),conditional:deficit(required,conditional)},allConditionsKnown,quantityStatus,
          status:allConditionsKnown?quantityStatus:"unknown",reason:allConditionsKnown?(quantityStatus==="within_guaranteed"?"guaranteed_budget_path":"conditional_reward_path"):"additional_conditions_unverified",
          currentOnly:found===currentOnly,sharedChoiceAllocations:found.allocations,stepCount:found.plan.stepCount};
      }
      const limited=currentOnly.limited||guaranteeAttempt?.limited||conditionalAttempt?.limited;
      const plan=conditionalAttempt?.plan||guaranteeAttempt?.plan||currentOnly.plan;
      const unknown=!known||budget.status!=="confirmed"||limited||randomAvailable>0||plan?.reason!=="missing_material"||[current.wood,current.gold,...Object.values(conditional)].some(value=>value===null);
      return {...base,status:unknown?"unknown":"over_budget",quantityStatus:unknown?"unknown":"over_budget",reason:!known?"remaining_rewards_unknown":limited?"budget_search_limit":randomAvailable>0?"random_draw_results_required":unknown?"budget_path_unconfirmed":"budget_not_sufficient",planReason:plan?.reason||null};
    }
    function randomModeStocks(inventory,settings,includeConditional){
      const known=["confirmed","partial"].includes(activeRewardBudget?.status),stock=new Map(inventory),groups=known?[activeRewardBudget.guaranteed,...(includeConditional?[activeRewardBudget.conditional]:[])]:[];
      for(const group of groups){stock.set("e018",(stock.get("e018")||0)+(group.choiceWisps||0));for(const [rawcode,count]of Object.entries(group.units||{}))stock.set(rawcode,(stock.get(rawcode)||0)+count);}
      for(const source of options.randomDrops?.sources||[])stock.delete(source.rawcode);
      const resources={wood:Number.isSafeInteger(settings.wood)&&settings.wood>=0?settings.wood:null,gold:Number.isSafeInteger(settings.gold)&&settings.gold>=0?settings.gold:null};
      for(const key of ["wood","gold"])if(resources[key]!==null)for(const group of groups)resources[key]+=Number.isSafeInteger(group[key])?group[key]:0;
      const search={limited:false},variants=Array.from(sharedBudgetStocks(stock,search)).map(value=>value.stock);
      return {variants,resources,limited:search.limited,known};
    }
    function randomWitness(context,inventory,settings,modes){
      const route=context.route;
      if(!route?.executable)return ()=>({unknown:true,reason:"production_route_unverified"});
      const chosen=new Map();
      for(const step of route.steps||[]){const canonical=recipes.get(String(step.recipeId)),recipe=canonical&&Graph.expandMaterialChoices(canonical,catalog).find(candidate=>candidate.materialChoiceRawcode===step.materialChoiceRawcode);if(step.type==="craft"&&recipe&&Graph.isProductionRecipe(recipe)&&!chosen.has(recipe.resultUnitRawcode))chosen.set(recipe.resultUnitRawcode,recipe);}
      const missing=context.ordered.map(goal=>({rawcode:goal.rawcode,count:Math.max(0,goal.count-(inventory.get(goal.rawcode)||0))})).filter(goal=>goal.count>0);
      return (additions,name)=>{
        const mode=modes[name];if(!mode||mode.limited||!mode.variants.length)return {unknown:true,reason:"shared_choice_search_limit"};
        let uncertain=false;
        for(const base of mode.variants){
          const stock=new Map(base);for(const [rawcode,count]of Object.entries(additions))stock.set(rawcode,(stock.get(rawcode)||0)+count);
          let invalid=false;for(const [rawcode,count]of context.held){if((stock.get(rawcode)||0)<count){invalid=true;break;}stock.set(rawcode,(stock.get(rawcode)||0)-count);}if(invalid)continue;
          const money={...mode.resources},items={...(settings.items?.counts||{})};let nodes=0,unknown=false,recipeConditions=settings.recipeConditions;
          function take(rawcode,count,path){
            if(++nodes>2048||path.has(rawcode)||!Number.isSafeInteger(count)||count<0){unknown=true;return false;}
            const available=stock.get(rawcode)||0,used=Math.min(available,count);stock.set(rawcode,available-used);const need=count-used;if(!need)return true;
            if(/^h00[1-9]$/.test(rawcode)){if((stock.get("e018")||0)<need)return false;stock.set("e018",stock.get("e018")-need);return true;}
            const recipe=chosen.get(rawcode);if(!recipe)return false;
            const resultCount=recipe.resultCount||1,batches=Math.ceil(need/resultCount);if(!Number.isSafeInteger(batches)||batches<1){unknown=true;return false;}
            const requirements=Graph.expandRecipeRequirements(recipe.ingredients,batches);
            if(requirements.some(item=>!["UNIT","WOOD","GOLD","ITEM",...checkedConditions].includes(item.type))){unknown=true;return false;}
            const conditions=Graph.evaluateRecipeConditions(requirements,recipeConditions);if(!conditions.conditionsReady){if(conditions.unknownConditions.length)unknown=true;return false;}
            const itemCheck=Graph.evaluateItems(requirements,{status:settings.items?.status||"unknown",counts:items});if(!itemCheck.itemsReady){if(itemCheck.missingItems.some(item=>!item.known))unknown=true;return false;}
            const resourceCost=Graph.effectiveResourceCosts(requirements,recipeConditions);
            for(const key of ["wood","gold"]){if(resourceCost[key]===null){unknown=true;return false;}if(!resourceCost[key])continue;if(money[key]===null){unknown=true;return false;}if(resourceCost[key]>money[key])return false;money[key]-=resourceCost[key];}
            for(const item of requirements)if(item.type==="ITEM")items[item.rawcode]-=item.count;
            recipeConditions=Graph.projectRecipeConditions(requirements,recipeConditions);
            const next=new Set(path);next.add(rawcode);
            for(const item of requirements)if(item.type==="UNIT"&&!take(item.rawcode,item.count,next))return false;
            stock.set(rawcode,(stock.get(rawcode)||0)+batches*resultCount-need);return true;
          }
          if(missing.every(goal=>take(goal.rawcode,goal.count,new Set())))return true;
          if(unknown)uncertain=true;
        }
        return uncertain?{unknown:true,reason:"route_conditions_unverified"}:false;
      };
    }
    function randomForecasts(candidates,inventory,settings,started){
      if(!RandomForecast||!options.randomDrops)return null;
      feasibilityDeadline=started+(settings.maxMilliseconds||900);
      const history=RandomForecast.normalizeHistory(settings.randomHistory,{mapHash:hash,localPlayer:settings.localPlayer});
      const banks=RandomForecast.buildBanks(options.randomDrops,{mapHash:hash,localPlayer:settings.localPlayer,history,currentWisps:dict(inventory),guaranteedWisps:activeRewardBudget?.guaranteed?.units,conditionalWisps:activeRewardBudget?.conditional?.units,samples:settings.randomForecastSamples||128,seed:session||hash});
      if(!["confirmed","partial"].includes(activeRewardBudget?.status))for(const mode of Object.values(banks.modes)){mode.available=false;mode.reasons.push("remaining_rewards_unknown");}
      const modeStocks={guaranteed:randomModeStocks(inventory,settings,false),conditional:randomModeStocks(inventory,settings,true)};
      const incomplete=deck=>deck.assessment.missingSlots.length>0||deck.assessment.coverage.some(row=>row.status==="missing");
      const noDraws=Object.values(banks.modes).every(mode=>!mode.remainingDraws);
      const evaluators=candidates.map(deck=>{if(incomplete(deck))return ()=>({unknown:true,reason:"role_composition_incomplete"});if(noDraws)return ()=>({unknown:true,reason:"no_remaining_random_draws"});const context=goalContexts.get(deck.id);return context?randomWitness(context,inventory,settings,modeStocks):()=>({unknown:true,reason:"production_route_unverified"});});
      const forecasts=RandomForecast.estimateMany(banks,evaluators,{deadline:started+(settings.maxMilliseconds||900),minimumSamples:32});
      for(let index=0;index<candidates.length;index++){
        const deck=candidates[index];deck.randomMaterialForecast={...forecasts[index],rewardCompleteness:activeRewardBudget?.status||"unknown"};
        if(!incomplete(deck)&&deck.budgetFeasibility.status==="within_guaranteed")deck.randomMaterialForecast={...forecasts[index],status:"known",probability:1,samples:0,successes:null,confidence:{level:1,lower:1,upper:1,method:"joint_budget_witness"},conditional:false,isEstimate:false,method:"verified_joint_budget_plan"};
      }
      return {historyStatus:history.status,nextDrawProbabilities:banks.nextDrawProbabilities,requestedSamples:banks.samples,method:"deterministic_route_witness_monte_carlo",winProbability:false};
    }
    function certainlyUses(target,material,path){
      if(target===material)return true;
      path=new Set(path||[]);if(path.has(target))return false;path.add(target);
      const candidates=Array.from(recipes.values()).flatMap(recipe=>Graph.expandMaterialChoices(recipe,catalog)).filter(r=>r.resultUnitRawcode===target&&Graph.isProductionRecipe(r));
      return candidates.length>0&&candidates.every(r=>(r.ingredients||[]).some(i=>i.type==="UNIT"&&i.count>0&&certainlyUses(i.rawcode,material,path)));
    }
    function acquisitionPlan(materials,inventory,template){
      const needs=new Map((materials||[]).map(x=>[x.rawcode,x.count])),allocations=[],sources=[],warnings=[];
      const shared=(combat.acquisitionModel?.sharedChoiceResources||[]).slice(0,4);
      // 선택 외 도박 경로가 있다는 이유로 희귀 선택 결과를 무제한 확보 가능하다고 보지 않는다.
      const nonExclusive=new Set(["h060","h05Y",...(combat.acquisitionModel?.independentlyAcquirableRequirementRawcodes||[])]);
      const exclusive=new Set(shared.flatMap(s=>(s.options||[]).flatMap(o=>(o.outputs||[]).map(x=>x.rawcode))).filter(c=>!nonExclusive.has(c)));
      const score=(stock,used,preferred)=>[Array.from(stock).filter(([c])=>exclusive.has(c)).reduce((n,[,v])=>n+v,0),Array.from(stock.values()).reduce((a,b)=>a+b,0),Array.from(stock.values()).filter(v=>v>0).length,used,-preferred];
      const better=(a,b)=>{for(let i=0;i<a.length;i++){if(a[i]!==b[i])return a[i]<b[i];}return false;};
      for(const resource of shared){
        const choices=(resource.options||[]).filter(o=>(o.outputs||[]).length&&o.evidence&&o.outputs.every(x=>units.has(x.rawcode)&&(includeMysteriousDecks||!mysterious(x.rawcode))&&Number.isSafeInteger(x.count)&&x.count>0)).slice(0,12);
        const consumed=Number.isSafeInteger(resource.consumeCount)&&resource.consumeCount>0?resource.consumeCount:1;
        const owned=Math.floor((inventory.get(resource.sourceRawcode)||0)/consumed);
        const localExclusive=unique(choices.flatMap(o=>o.outputs.map(x=>x.rawcode)).filter(c=>!nonExclusive.has(c)));
        const spent=Array.from(inventory).filter(([code,count])=>count>0&&localExclusive.some(material=>certainlyUses(code,material))).map(([code])=>code);
        const proposed=Number.isSafeInteger(resource.futureRewardBudget)?Math.max(0,resource.futureRewardBudget):0;
        // 단발 보상을 현재 위습이나 이미 소비된 상위 유닛과 다시 합산하지 않는다.
        const remainingRewardKnown=["confirmed","partial"].includes(activeRewardBudget?.status);
        const future=remainingRewardKnown?Math.floor(((activeRewardBudget.guaranteed.units?.[resource.sourceRawcode]||0)+(activeRewardBudget.conditional.units?.[resource.sourceRawcode]||0))/consumed):owned>0||spent.length?0:Math.min(1,proposed),budget=Math.min(16,owned+future),preferred=template.transcendencePlan?.preferredOptionId;
        let best={remaining:new Map(needs),counts:[],used:0,score:score(needs,0,0)};
        function visit(index,left,remaining,counts,used,preferredCount){
          if(index===choices.length){const currentScore=score(remaining,used,preferredCount);if(better(currentScore,best.score))best={remaining,counts:counts.slice(),used,score:currentScore};return;}
          const option=choices[index],wanted=Math.max(0,...option.outputs.map(x=>Math.ceil((remaining.get(x.rawcode)||0)/x.count)));
          for(let count=0;count<=Math.min(left,wanted);count++){
            const next=new Map(remaining);for(const output of option.outputs)next.set(output.rawcode,Math.max(0,(next.get(output.rawcode)||0)-count*output.count));
            counts.push(count);visit(index+1,left-count,next,counts,used+count,preferredCount+(option.id===preferred?count:0));counts.pop();
          }
        }
        visit(0,budget,new Map(needs),[],0,0);
        needs.clear();for(const [code,count]of best.remaining)if(count>0)needs.set(code,count);
        let availableOwned=owned;
        for(let i=0;i<choices.length;i++)if(best.counts[i]>0){const count=best.counts[i],option=choices[i],fromInventory=Math.min(availableOwned,count);availableOwned-=fromInventory;
          allocations.push({sourceRawcode:resource.sourceRawcode,sourceName:info(resource.sourceRawcode).name,optionId:option.id,label:({kuma:"쿠마","rayleigh-ship":"레일리·해적선",contract:"계약"})[option.id]||option.outputs.map(x=>info(x.rawcode).name).join("·"),count,fromInventory,fromFuture:count-fromInventory,
            outputs:option.outputs.map(x=>({...info(x.rawcode),count:x.count*count})),evidence:option.evidence});
        }
        sources.push({rawcode:resource.sourceRawcode,name:info(resource.sourceRawcode).name,inventoryCount:owned,futureBudget:future,configuredFutureBudget:proposed,used:best.used,fromFuture:Math.max(0,best.used-owned),spentEvidence:spent,spentEvidenceIsConservative:true,remainingRewardKnown});
        if(spent.length&&proposed)warnings.push("이미 보유한 선택 결과나 상위 유닛과 단발 스토리 보상을 중복 계산하지 않았습니다.");
      }
      const remainingMaterials=Array.from(needs,([rawcode,count])=>({...info(rawcode),count})),remainingExclusive=remainingMaterials.filter(x=>exclusive.has(x.rawcode));
      if(remainingExclusive.length)warnings.push("확인된 공유 선택 예산만으로 부족한 특수 재료를 모두 확보할 수 없습니다.");
      const futureConditional=allocations.some(x=>x.fromFuture>0),summary=(allocations.length?allocations.map(x=>x.sourceName+": "+x.label+(x.count>1?" ×"+x.count:"")).join(" / "):remainingExclusive.length?"초월 위습 추가 획득 필요":"추가 초월 위습 선택 없음")+(allocations.length&&remainingExclusive.length?" · 추가 위습 필요":"");
      return {summary,allocations,sources,remainingMaterials,remainingExclusiveMaterials:remainingExclusive,sharedChoiceFeasible:remainingExclusive.length===0,futureConditional,executionEligible:false,warnings};
    }
    function goalActions(deck,inventory,settings){
      const context=goalContexts.get(deck.id);if(!context)return [];
      const {roster,held,options}=context;
      return roster.goals.map(goal=>{
        const owned=inventory.get(goal.rawcode)||0,base={towardRawcode:goal.rawcode,towardName:goal.name,towardTier:goal.tier,goalCount:goal.count,owned,needed:Math.max(0,goal.count-owned),available:false,reason:null};
        const blocked=reason=>({...base,reason,rawcode:null,recipeId:null,expectedSteps:[],expectedStepCount:0});
        if(!includeMysteriousDecks&&mysterious(goal.rawcode))return blocked("mysterious_recommendations_disabled");
        if(owned>=goal.count)return blocked("already_owned");
        if(settings.craftRunning===true)return blocked("craft_running");
        if(goal.count-owned>1)return blocked("unsupported_quantity");
        if(deck.kingPathConstraint?.entryCompatibility?.status==="blocked")return blocked("king_path_entry_unavailable");
        if(deck.assessment.compositionStatus==="incomplete")return blocked("incomplete_composition");
        if(!deck.acquisitionPlan.sharedChoiceFeasible)return blocked("shared_choice_budget");
        if(!settings.supportedRecipeIds.size)return blocked("capabilities_unavailable");
        if(!Array.from(recipes.values()).some(r=>r.resultUnitRawcode===goal.rawcode&&Graph.isProductionRecipe(r)&&settings.supportedRecipeIds.has(String(r.id))))return blocked("unsupported_recipe");
        // 누른 목표의 실제 승급 재료는 먼저 사용한다. 무관한 코어와 명시적 보호만 유지한다.
        const locks=new Map(held);
        const explicitLocks=countsOf(settings.protectedCounts);
        for(const [code]of locks)if(code!==goal.rawcode&&ancestry(goal.rawcode,code)&&!(explicitLocks.get(code)>0))locks.delete(code);
        if(locks.size>64)return blocked("protection_limit");
        // 한 번의 클릭은 해당 목표를 한 개 추가한다. 다른 목표나 획득 선택 단계는 실행하지 않는다.
        const requested={...goal,count:goal.count},goals=[{rawcode:goal.rawcode,count:requested.count}],ownOptions={...options,protectedCounts:locks,maxSteps:64};
        const actual=graph.planDeck(goals,inventory,ownOptions);
        if(!actual.executable){
          const unprotected=actual.reason==="missing_material"&&locks.size?graph.planDeck(goals,inventory,{...ownOptions,protectedCounts:null}):null;
          return {...blocked(unprotected?.executable?"protected_material":actual.reason||"missing_material"),protectedCounts:dict(locks),missing:actual.missing||[],itemRequirements:actual.itemRequirements||[],missingItems:actual.missingItems||[],itemsReady:actual.itemsReady,conditionRequirements:actual.conditionRequirements||[],missingConditions:actual.missingConditions||[],blockedConditions:actual.blockedConditions||[],unknownConditions:actual.unknownConditions||[],conditionsReady:actual.conditionsReady,additionalConditionsUnverified:actual.additionalConditionsUnverified,missingIsEstimate:true};
        }
        const cost=totals(actual.steps,settings),resourceState=resources(cost,settings);
        if(resourceState!==true){
          const unknownMoney=cost.wood>0&&!finite(settings.wood)||cost.gold>0&&!finite(settings.gold),reason=resourceState===false?(finite(settings.wood)&&settings.wood<cost.wood?"missing_wood":"missing_gold"):unknownMoney?"resource_unknown":"conditions_unverified";
          return {...blocked(reason),protectedCounts:dict(locks),requiredStepCount:actual.steps.length,choiceWisps:actual.choiceWispCount,wood:cost.wood,gold:cost.gold,resourcesReady:resourceState,nonUnitRequirements:actual.nonUnitRequirements||[]};
        }
        const replay=sequence(actual.steps,inventory,locks,settings),last=actual.steps.at(-1),final=replay.frames.at(-1)?.stock;
        if(replay.frames.length!==actual.steps.length||!final||last?.type!=="craft"||last.resultRawcode!==goal.rawcode||(final.get(goal.rawcode)||0)<requested.count)
          return {...blocked(replay.reason||"whole_goal_unavailable"),protectedCounts:dict(locks)};
        return {...base,...info(goal.rawcode),available:true,reason:null,recipeId:last.recipeId,count:(final.get(goal.rawcode)||0)-owned,
          expectedSteps:actual.steps.map(s=>s.type==="choice"?{type:"choice",resultRawcode:s.resultRawcode}:{type:"craft",recipeId:s.recipeId,...(s.materialChoiceRawcode?{materialChoiceRawcode:s.materialChoiceRawcode}:{})}),plan:actual.steps,
          protectedCounts:dict(locks),expectedFinalCounts:dict(final),expectedStepCount:actual.steps.length,choiceWisps:actual.choiceWispCount,
          wood:cost.wood,gold:cost.gold,...Graph.evaluateItems(cost.items,settings.items),...Graph.evaluateRecipeConditions(cost.conditions,settings.recipeConditions),nonUnitRequirements:actual.nonUnitRequirements||[],resourcesReady:resources(cost,settings),isWholeGoal:true,ready:true,requiresExpectedSteps:true,completesGoal:(final.get(goal.rawcode)||0)>=goal.count,
          origin:"fixed_deck_goal_path",goalRecipeId:last.recipeId};
      });
    }
    function evaluate(template,inventory,settings){
      const roster=resolve(template,inventory,settings),held=protectedFor(roster,inventory,settings);
      const primary=roster.coreCodes.find(code=>(inventory.get(code)||0)<(roster.goals.find(g=>g.rawcode===code)?.count||1)&&strategyUnits[code]?.lineDamage?.assessment==="main")||null;
      const story=roster.goals.find(goal=>strategyUnits[goal.rawcode]?.storyDamage?.assessment==="main"&&goal.kind!=="core")?.rawcode;
      const priority=goal=>goal.kind==="retained"?0:goal.rawcode===story?1:goal.rawcode===primary?2:goal.kind==="core"?6:supports(goal.rawcode,"areaStun").length||supports(goal.rawcode,"alwaysSlow").length?3:supports(goal.rawcode,"manaSupport").length?4:5;
      const stageOrder=roster.goals.slice().sort((a,b)=>priority(a)-priority(b)||a.rawcode.localeCompare(b.rawcode));
      const options={items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:settings.localPlayer,supportedRecipeIds:settings.supportedRecipeIds,allowChoiceWisps:settings.allowChoiceWisps===true,protectedCounts:held,preserveGoalOrder:true,maxSteps:512,maxSearchNodes:settings.maxSearchNodes||16000,planningKingCraftBudget:settings.planningKingCraftBudget};
      const virtualBudget=Math.max(64,Math.min(2000,settings.futureChoiceBudget||512));
      const unfinished=stageOrder.filter(g=>(inventory.get(g.rawcode)||0)<g.count),orderings=[stageOrder];
      for(const lead of unfinished.slice(1,4))orderings.push([lead,...stageOrder.filter(g=>g!==lead)]);
      let actual,forecast,futureMaterials,action,ordered,handFit=null,orderSearchCount=0;
      const primaryGoals=roster.fixedGoals||roster.goals.filter(goal=>goal.kind!=="retained");
      const comparisonBudget={remainingNodes:(settings.maxSearchNodes||16000)*2};
      const comparisonOptions=handFitAdvisor?{...options,planningAlternativeLimit:4,planningSearchBudget:comparisonBudget}:options;
      const variants=plan=>[plan,...(plan.alternativePlans||[])];
      const pathBudgetRank=plan=>{
        if(!plan.executable)return 5;const cost=totals(plan.steps,settings),required={wood:cost.wood,gold:cost.gold,choiceWisps:plan.choiceWispCount};
        const available={wood:settings.wood,gold:settings.gold,choiceWisps:inventory.get("e018")||0};
        const fits=values=>Object.keys(required).every(key=>finite(required[key])&&(required[key]===0||finite(values[key])&&required[key]<=values[key]));
        if(fits(available))return 0;
        if(!["confirmed","partial"].includes(activeRewardBudget?.status))return 3;
        for(const [index,group]of [activeRewardBudget.guaranteed,activeRewardBudget.conditional].entries()){
          for(const key of Object.keys(available))available[key]=finite(available[key])&&finite(group?.[key])?available[key]+group[key]:null;
          if(fits(available))return index+1;
        }return Object.values(available).some(value=>!finite(value))?3:4;
      };
      const chooseProof=(plan,context)=>{let proofs=variants(plan).filter(proof=>proof.executable);if(!proofs.length)return null;
        const executionRank=proof=>rankResource(resources(totals(proof.steps,settings),settings)),readyRank=Math.max(...proofs.map(executionRank));proofs=proofs.filter(proof=>executionRank(proof)===readyRank);
        const rank=Math.min(...proofs.map(pathBudgetRank));return handFitAdvisor.choose(proofs.filter(proof=>pathBudgetRank(proof)===rank),context);};
      let selectedWitness=null;
      for(const candidateOrder of orderings){
        if(handFitAdvisor&&ordered&&comparisonBudget.remainingNodes<=0)break;
        const goals=candidateOrder.map(g=>({rawcode:g.rawcode,count:g.count}));
        let current=graph.planDeck(goals,inventory,comparisonOptions);
        if(handFitAdvisor&&current.executable)current=chooseProof(current,{goals,primaryGoals,initialCounts:inventory,protectedCounts:held,basis:"current_joint_plan"})?.plan||current;
        let candidateForecast=current,candidateFuture=[];
        if(!current.executable){const future=futurePlan(goals,inventory,comparisonOptions,virtualBudget);candidateForecast=future.plan;candidateFuture=future.futureMaterials;}
        const virtualAdditions=current.executable?{}:Object.fromEntries([["e018",virtualBudget],...candidateFuture.map(row=>[row.rawcode,row.count])]);
        let candidateFit=null;
        if(handFitAdvisor){const chosen=chooseProof(candidateForecast,{goals,primaryGoals,initialCounts:inventory,protectedCounts:held,virtualAdditions,basis:current.executable?"current_joint_plan":"synthetic_forecast"});if(chosen){candidateForecast=chosen.plan;candidateFit=chosen.fit;if(current.executable)current=chosen.plan;
            if(candidateFit.verified)candidateFuture=candidateFuture.map(row=>({...row,count:Math.max(0,row.count-(candidateFit.unusedOrigins["V:"+row.rawcode]||0))})).filter(row=>row.count>0);
          }else candidateFit=handFitAdvisor.assess({plan:candidateForecast,basis:"synthetic_forecast"});}
        const candidateAction=nextAction(current.executable?current:candidateForecast,{...roster,goals:candidateOrder},inventory,held,settings);orderSearchCount++;
        const witness={current,candidateForecast,candidateFuture,candidateAction,candidateOrder,fit:candidateFit,budgetRank:pathBudgetRank(candidateForecast)};
        const currentReady=current.executable&&resources(totals(current.steps,settings),settings)===true;
        const previousReady=selectedWitness?.current.executable&&resources(totals(selectedWitness.current.steps,settings),settings)===true;
        const better=!ordered||(handFitAdvisor?
          Number(currentReady)>Number(previousReady)||currentReady===previousReady&&
          (Number(current.executable)>Number(selectedWitness.current.executable)||current.executable===selectedWitness.current.executable&&
          (witness.budgetRank<selectedWitness.budgetRank||witness.budgetRank===selectedWitness.budgetRank&&
          (Number(Boolean(candidateAction))>Number(Boolean(selectedWitness.candidateAction))||Boolean(candidateAction)===Boolean(selectedWitness.candidateAction)&&handFitAdvisor.compare(candidateFit,selectedWitness.fit)<0))):Boolean(candidateAction));
        if(better){selectedWitness=witness;actual=current;forecast=candidateForecast;futureMaterials=candidateFuture;action=candidateAction;ordered=candidateOrder;handFit=candidateFit;}
        if(!handFitAdvisor&&candidateAction)break;
      }
      const actualCost=actual.executable?totals(actual.steps,settings):null;
      const forecastCost=forecast.executable?totals(forecast.steps,settings):null,forecastConditions=conditionalCosts(forecastCost,settings),acquisition=acquisitionPlan(futureMaterials,inventory,template);
      let feasibility=recommendationFeasibility(template,roster,inventory,settings,options,virtualBudget,forecast,futureMaterials,acquisition);
      if(handFitAdvisor&&forecast.executable)randomRoutes.set(template.id,forecast);
      const budgetFeasibility=rewardBudgetFeasibility(roster,inventory,settings,options,forecast);
      if(handFitAdvisor)budgetFeasibility.pathBasis="independent_reward_budget_joint_plan";
      if(["within_guaranteed","within_conditional"].includes(budgetFeasibility.status)&&feasibility.status==="blocked")feasibility={...feasibility,status:budgetFeasibility.status==="within_guaranteed"?"reachable":"conditional",grade:budgetFeasibility.status==="within_guaranteed"?"A":"B",reasons:[budgetFeasibility.reason],budgetPathProven:true};
      else if(feasibility.status==="reachable"&&budgetFeasibility.status!=="within_guaranteed")feasibility={...feasibility,status:"conditional",grade:"B",reasons:unique([...feasibility.reasons,budgetFeasibility.status==="within_conditional"?"conditional_reward_budget":"remaining_reward_budget_unconfirmed"])};
      const countKingSteps=plan=>(plan?.steps||[]).filter(step=>step.type==="craft"&&(recipes.get(String(step.recipeId))?.ingredients||[]).some(item=>item.type==="KING"&&item.count>0)).length;
      const modeLimit=Number.isSafeInteger(settings.planningKingCraftBudget)?settings.planningKingCraftBudget:null;
      const modePathBlocked=modeLimit!==null&&((forecast.executable&&countKingSteps(forecast)>modeLimit)||(actual.executable&&countKingSteps(actual)>modeLimit)||feasibility.reasons.includes("king_path_craft_budget")&&!feasibility.budgetPathProven);
      if(modePathBlocked){feasibility={...feasibility,status:"blocked",grade:"C",reasons:unique([...feasibility.reasons,"king_path_craft_budget"])};action=null;}
      const modeEntryBlocked=settings.kingPathEntryCompatibility?.status==="blocked";
      if(modeEntryBlocked){feasibility={...feasibility,status:"blocked",grade:"C",reasons:unique([...feasibility.reasons,"king_path_entry_unavailable"])};action=null;}
      if(!acquisition.sharedChoiceFeasible)action=null;
      const considered=assessment(template,roster,settings),missingGoals=roster.goals.filter(g=>(inventory.get(g.rawcode)||0)<g.count).map(g=>({...info(g.rawcode),count:g.count-(inventory.get(g.rawcode)||0)}));
      const combatValidation=combatVerifier?combatVerifier.assess(roster.goals,{...settings,coreRawcodes:roster.coreCodes},considered):null;
      if(considered.compositionStatus==="incomplete")action=null;
      const operatingConditions=unique([...(template.conditions||[]),roster.goals.some(g=>supports(g.rawcode,"manaSupport").length)?"마나 지원 유닛의 범위 안에 마나를 쓰는 주력 배치":null,
        roster.goals.some(g=>supports(g.rawcode,"teamAttackSpeed").length)?"팀 공속 보조의 오라 범위 안에 주력 배치":null,
        considered.control.stun.sources.some(s=>s.mode==="proc")?"확률 스턴 유닛이 라인 대상을 계속 공격하도록 배치":null,
        ...(rulesMap?(combat.nightmareRules?.objectives||[]).filter(o=>o.type==="story_deadline").map(o=>o.beforeRound+"라운드 시작 전 "+o.target+" 파괴"):[])]);
      const matching=Array.from(inventory).filter(([code,count])=>count>0&&coreMode(code)&&(template.direction==="hybrid"?coreMode(code)==="hybrid":coreMode(code)===template.direction)).reduce((sum,[,count])=>sum+count,0);
      goalContexts.set(template.id,{roster,held,options,ordered,route:randomRoutes.get(template.id),routeBasis:forecast.executable?"selected_joint_plan":"feasibility_diagnostic_only"});
      const legendTargets=firstInvestmentTargets(roster),projection={policy:"legend_and_seed_hidden_ancestors_are_intermediate_not_additional_final_copies",sourceCountsAreMinimumPresence:template.recordPresenceGoals===true,prunedIntermediateLegends:roster.prunedIntermediateLegends,prunedIntermediateHiddenSeeds:roster.prunedIntermediateHiddenSeeds};
      const investment=firstInvestmentRawcode?{...info(firstInvestmentRawcode),matchedFinalGoals:legendTargets.map(g=>({...info(g.rawcode),relation:g.rawcode===firstInvestmentRawcode?units.get(firstInvestmentRawcode)?.tier==="히든"?"retained_hidden":"retained_legend":"upgrade"})),upgradedFinalGoals:legendTargets.filter(g=>g.rawcode!==firstInvestmentRawcode).map(g=>info(g.rawcode)),upgradedCoreGoals:legendTargets.filter(g=>g.rawcode!==firstInvestmentRawcode&&roster.coreCodes.includes(g.rawcode)).map(g=>info(g.rawcode)),matched:legendTargets.length>0,supersededByOwnedUpper:upperAnchors.size>0,policy:"first_legend_or_hidden_initial_preference_not_clear_prediction"}:null;
      const ownedUpperInvestment=upperInvestment(roster);
      const sourceCounts=new Map(roster.fixedGoals.map(goal=>[goal.rawcode,goal.count])),retainedExtras=recordOnly?roster.retained.filter(goal=>goal.count>(sourceCounts.get(goal.rawcode)||0)).map(goal=>({rawcode:goal.rawcode,count:goal.count-(sourceCounts.get(goal.rawcode)||0)})):[],retainedOutsideRecord=retainedExtras.map(goal=>goal.rawcode);
      return {id:template.id,name:template.name||template.title||template.id,mode:template.direction,goals:roster.goals.map(g=>({...g,owned:inventory.get(g.rawcode)||0,needed:Math.max(0,g.count-(inventory.get(g.rawcode)||0)),progress:goalProgress(g,inventory,settings)})),
        noTranscendentUnits:template.noTranscendentUnits===true,transcendencePlan:template.transcendencePlan||null,
        coreRawcodes:roster.coreCodes,slots:roster.slots,retained:roster.retained,replacements:roster.replacements,requirements:considered.coverage,assessment:considered,combatValidation,operatingConditions,acquisitionPlan:acquisition,recommendationFeasibility:feasibility,budgetFeasibility,
        recommendationPriority:{kingPathDependent:kingPathDependent(roster,template),requiredKingPathRawcodes:kingPathDependent(roster,template)?["h0C8"]:[]},kingPathConstraint:{mode:settings.kingPathMode,maximumNewKingCrafts:modeLimit,observedHistoricalTopLowerBound:observedKingTopLowerBound,historicalCountVerified:false,selectedForecastNewKingCrafts:forecast.executable?countKingSteps(forecast):null,status:modePathBlocked||modeEntryBlocked?"blocked":modeLimit===null?"not_selected":"within_planning_limit",entryCompatibility:settings.kingPathEntryCompatibility,nativeKingGate:settings.recipeConditions?.king?.status||"unknown",planningLimitDoesNotGrantNativePermission:true},
        source:template.source||null,sourceEvidence:{...(template.sourceEvidence||{kind:"native_fallback",recordIds:[],urls:[],clearVerified:false}),projection,currentOwnedUpperProjection:{addedRawcodes:ownedUpperInvestment.anchors.filter(unit=>!unit.sourceGoal).map(unit=>unit.rawcode),clearOutcomeTransferred:false},...(recordOnly?{recommendationPolicy,compositionChanged:template.sourceEvidence?.compositionChanged===true||retainedOutsideRecord.length>0,clearOutcomeTransferred:false,currentOwnedProjection:{addedRawcodes:retainedOutsideRecord,additionalUnits:retainedExtras,clearOutcomeTransferred:false}}:{})},projection,prunedIntermediateLegends:roster.prunedIntermediateLegends,ownedUpperInvestment,
        matchesFirstInvestmentLineage:legendTargets.length>0,firstInvestment:investment,matchesFirstLegendAncestor:investment?.tier==="전설"&&legendTargets.length>0,firstLegendInvestment:investment?.tier==="전설"?investment:null,
        goalSource:template.evidenceScope||"native_composition_not_verified_clear",targetRosterStatus:template.recordPresenceGoals?"registered_presence_minimum":roster.slots.every(s=>s.rawcode)?"complete_native_composition":"incomplete",
        planning:{current:{conditionRequirements:actual.conditionRequirements||[],missingConditions:actual.missingConditions||[],blockedConditions:actual.blockedConditions||[],unknownConditions:actual.unknownConditions||[],conditionsReady:actual.conditionsReady,itemRequirements:actual.itemRequirements||[],missingItems:actual.missingItems||[],itemsReady:actual.itemsReady,unitReady:actual.executable,resourcesReady:actualCost?resources(actualCost,settings):null,choiceWisps:actual.executable?actual.choiceWispCount:null,wood:actualCost?.wood??null,gold:actualCost?.gold??null,steps:actual.executable?actual.stepCount:null,conditions:actualCost?.conditions||[],reason:actual.reason},
          forecast:{available:forecast.executable,virtualChoiceBudget:actual.executable?0:virtualBudget,futureMaterials,externalAcquisitionCount:forecast.executable?futureMaterials.reduce((sum,item)=>sum+item.count,0):null,externalAcquisitionKinds:forecast.executable?futureMaterials.length:null,...forecastConditions,conditionRequirements:forecast.conditionRequirements||forecastConditions.conditionRequirements,missingConditions:forecast.missingConditions||forecastConditions.missingConditions,blockedConditions:forecast.blockedConditions||forecastConditions.blockedConditions,unknownConditions:forecast.unknownConditions||forecastConditions.unknownConditions,conditionsReady:forecast.conditionsReady??forecastConditions.conditionsReady,itemRequirements:forecast.itemRequirements||forecastConditions.itemRequirements,missingItems:forecast.missingItems||forecastConditions.missingItems,itemsReady:forecast.itemsReady??forecastConditions.itemsReady,executionEligible:false,costQuality:"selected_path_not_minimum",choiceWisps:forecast.executable?forecast.choiceWispCount:null,choiceDeficit:forecast.executable?Math.max(0,forecast.choiceWispCount-(inventory.get("e018")||0)):null,wood:forecastCost?.wood??null,gold:forecastCost?.gold??null,steps:forecast.executable?forecast.stepCount:null,conditions:forecastCost?.conditions||[],unsupportedRecipeIds:unique((forecast.steps||[]).filter(s=>s.type==="craft"&&settings.supportedRecipeIds!=null&&!new Set(settings.supportedRecipeIds).has(String(s.recipeId))).map(s=>s.recipeId)),reason:forecast.reason},
          missingGoals,missingMaterials:actual.missing||[],missingMaterialsIsComplete:false},
        stagePolicy:["스토리 주력 우선","첫 라인 주력","제어·지원","추가 주력"],orderSearchCount,preparedPrimaryMaterials:[],handFit,
        order:ordered.map(g=>g.rawcode),craftTargets:action?[action]:[],investmentMatch:matching,ownedOverlap:roster.goals.filter(g=>(inventory.get(g.rawcode)||0)>=g.count).length,conditional:true};
    }
    function computeRecommendation(counts,settings){
      settings=settings||{};const started=Date.now(),inventory=countsOf(counts),warnings=[];
      const kingPathMode=normalizeKingPathMode(settings),includeKingPathDecks=kingPathMode!=="off";
      const requestedIncludeMysteriousDecks=settings.includeMysteriousDecks===true;
      includeMysteriousDecks=settings.craftRunning===true&&lastIncludeMysteriousDecks!==null&&(!(settings.sessionIdentity||settings.sessionId)||(settings.sessionIdentity||settings.sessionId)===session)?lastIncludeMysteriousDecks:requestedIncludeMysteriousDecks;
      const mysteriousPreferenceChanged=lastIncludeMysteriousDecks!==null&&lastIncludeMysteriousDecks!==includeMysteriousDecks;
      if(mysteriousPreferenceChanged){resolved.clear();goalContexts.clear();supportAdaptationCache=null;lastSelectedSupportBase=null;earlyStorySnapshot=null;budgetPlanner=null;selectedId=null;lastSelectedTemplate=null;suppressedPreferredId=null;}
      lastIncludeMysteriousDecks=includeMysteriousDecks;
      settings={...settings,kingPathMode,includeKingPathDecks,includeMysteriousDecks,recipeConditions:{...(settings.recipeConditions||{}),special:Graph.normalizeRecipeSpecial(settings.recipeConditions?.special,settings.localPlayer,hash)}};
      progressCache=new Map();releasedOwnIntermediateRawcodes=[];
      feasibilityPlanner=null;feasibilityDeadline=started+(settings.maxMilliseconds||900);randomRoutes.clear();
      activeRewardBudget=RewardBudget?.normalize(settings.rewardBudget,{mapHash:hash,localPlayer:settings.localPlayer})||{status:"unknown"};
      const hasRandomDraws=(options.randomDrops?.sources||[]).some(source=>(inventory.get(source.rawcode)||0)+(activeRewardBudget.guaranteed?.units?.[source.rawcode]||0)+(activeRewardBudget.conditional?.units?.[source.rawcode]||0)>0);
      const planningMilliseconds=RandomForecast&&options.randomDrops?.map?.sha256===hash&&hasRandomDraws?Math.max(80,Math.floor((settings.maxMilliseconds||900)*.65)):(settings.maxMilliseconds||900);
      feasibilityDeadline=started+planningMilliseconds;
      const targetedIds=new Set(Array.from(recipes.values()).filter(r=>(r.ingredients||[]).some(i=>i.type==="PICK")).map(r=>String(r.id)));
      for(const up of curated.ownedSourceUpgrades||[])if(up.targetedInputRequired||up.additionalRandomUnitRequired)targetedIds.add(String(up.recipeId));
      const readableItems=new Set(settings.readableItemRawcodes||[]),readableConditions=new Set(settings.readableConditionTypes||[]);
      const conditionInputSupported=recipe=>(recipe?.ingredients||[]).filter(item=>checkedConditions.has(item.type)).every(item=>settings.recipeConditionsSupported===true&&readableConditions.has(item.type)&&(!["IFCT","CHCT","SKPT","RMAX","NIKA"].includes(item.type)||settings.specialRecipeConditionsSupported===true));
      const itemInputSupported=recipe=>(recipe?.ingredients||[]).filter(item=>item.type==="ITEM").every(item=>settings.itemRequirementsSupported===true&&readableItems.has(item.rawcode));
      settings={...settings,supportedRecipeIds:new Set(Array.from(settings.supportedRecipeIds||[]).filter(id=>recommendationRecipeAllowed(recipes.get(String(id)))&&!targetedIds.has(String(id))&&itemInputSupported(recipes.get(String(id)))&&conditionInputSupported(recipes.get(String(id)))))};
      const evidenceSettings={...settings};for(const key of ["wood","gold","preferredDeckId","manualDeckChoiceRevision","maxMilliseconds"])delete evidenceSettings[key];
      feasibilityEvidenceContext=semanticKey([inventory,evidenceSettings]);
      const identity=settings.sessionIdentity||settings.sessionId||null;
      if(identity&&identity!==session){earlyStorySnapshot=null;earlyStoryAdvancedSeen=false;}
      if(identity&&identity!==session){supportAdaptationCache=null;lastSelectedSupportBase=null;}
      if(identity&&identity!==session){lastConfirmedCombatScenario=null;pendingCombatScenarioReplan=false;lastIncludeKingPathDecks=null;lastKingPathMode=null;pendingKingPathPreferenceReplan=false;pendingKingPathTargetSelection=false;observedKingTopLowerBound=0;}
      if(identity&&identity!==session){lastConfirmedItems=null;pendingItemReplan=false;pendingFirstInvestmentReplan=false;previousPermanentStock=null;pendingStockRecompare=false;lastConfirmedRewardBudget=null;pendingRewardBudgetReplan=false;lastRandomHistoryFacts=null;pendingRandomHistoryReplan=false;lastKnownRecipeConditions=new Map();pendingRecipeConditionReplan=false;resolved.clear();goalContexts.clear();selectedId=null;session=identity;firstInvestmentRawcode=null;previousUpper=null;upperAnchors=new Map();ownUpperIntermediates=new Map();deferredUpper=new Map();wasCraftRunning=false;lastCraftTarget=null;ownExpectedResults=new Set();lastManualRevision=null;lastPreferredId=null;suppressedPreferredId=null;rebaseRevision=0;rebaseInfo=null;lastSelectedTemplate=null;lastIdleDirection=null;}
      const permanentStock=counts==null?null:Array.from(inventory).filter(([code,count])=>count>0&&(units.get(code)?.playable===true||inventoryWispRawcodes.has(code))&&units.get(code)?.isSummon!==true&&!excludedSummons.has(code)).sort(([a],[b])=>a<b?-1:a>b?1:0).map(([code,count])=>code+":"+count).join("|");
      const stockChanged=permanentStock!==null&&previousPermanentStock!==null&&permanentStock!==previousPermanentStock;
      if(stockChanged)pendingStockRecompare=true;
      if(permanentStock!==null)previousPermanentStock=permanentStock;
      const manualRevision=Number.isSafeInteger(settings.manualDeckChoiceRevision)?settings.manualDeckChoiceRevision:null;
      const freshManual=Boolean(settings.preferredDeckId&&settings.manualDeckChoice!==false&&(
        manualRevision!==null&&manualRevision!==lastManualRevision||
        settings.preferredDeckId!==lastPreferredId&&settings.preferredDeckId!==selectedId));
      if(freshManual)suppressedPreferredId=null;
      const currentUpper=new Map(Array.from(inventory).filter(([code,count])=>count>0&&(includeMysteriousDecks||!mysterious(code))&&upperTiers.has(units.get(code)?.tier)&&units.get(code)?.playable===true&&!excludedSummons.has(code)&&!investmentMaterialProxies.has(code)));
      const initialUpper=previousUpper===null,craftRunning=settings.craftRunning===true,previousSelectedId=selectedId;
      const kingPathPreferenceChanged=lastKingPathMode!==null&&lastKingPathMode!==kingPathMode;
      if(kingPathPreferenceChanged)pendingKingPathPreferenceReplan=true;
      if(includeKingPathDecks&&(kingPathPreferenceChanged||lastKingPathMode===null&&(settings.manualDeckChoice===false||!settings.preferredDeckId)))pendingKingPathTargetSelection=true;
      if(!includeKingPathDecks)pendingKingPathTargetSelection=false;
      lastKingPathMode=kingPathMode;lastIncludeKingPathDecks=includeKingPathDecks;
      const directTopMarkers=new Set(["A905","A906","A907","A909"]);
      const presentTopLowerBound=Array.from(inventory).filter(([code,count])=>count>0&&!["H0BK","h057","h04S","H08T"].includes(code)&&native(code)&&((profiles[code].abilities||[]).some(ability=>directTopMarkers.has(ability.rawcode))||transcendenceCapacity.slotCosts[code]===1&&(profiles[code].abilities||[]).some(ability=>ability.rawcode==="A07O"))).reduce((sum,[,count])=>sum+count,0);
      observedKingTopLowerBound=Math.max(observedKingTopLowerBound,presentTopLowerBound);
      settings.planningKingCraftBudget=kingPathMode==="off"?undefined:kingPathMode==="martial_law"||observedKingTopLowerBound>0||settings.recipeConditions.king?.status==="blocked"?0:1;
      const nativeConditions=settings.recipeConditions,modeEvidenceValid=["confirmed","partial"].includes(nativeConditions.status)&&Number.isInteger(settings.localPlayer)&&settings.localPlayer>=0&&settings.localPlayer<=3&&nativeConditions.localPlayer===settings.localPlayer&&nativeConditions.source?.readOnly===true&&nativeConditions.source?.mapSha256===hash;
      const nativeModeReason=modeEvidenceValid?nativeConditions.king?.reason:null;
      const actualKingPathMode=nativeModeReason==="martial_law_forbids_top_crafts"?"martial_law":["king_navigation_and_owned_top_marker","historical_top_counter_not_directly_read"].includes(nativeModeReason)?"standard":null;
      const entryKnownBlocked=includeKingPathDecks&&actualKingPathMode!==kingPathMode&&(actualKingPathMode!==null||observedKingTopLowerBound>=(kingPathMode==="martial_law"?1:2));
      settings.kingPathEntryCompatibility={status:!includeKingPathDecks?"not_selected":actualKingPathMode===kingPathMode?"confirmed":entryKnownBlocked?"blocked":"conditional",actualMode:actualKingPathMode,nativeReason:nativeModeReason,observedHistoricalTopLowerBound:observedKingTopLowerBound,historicalCountVerified:false,selectionLimit:kingPathMode==="martial_law"?0:kingPathMode==="standard"?1:null,modeEvidenceMapMatched:modeEvidenceValid};
      let recordsChanged=false;
      if(pendingClearRecords&&!craftRunning){
        recordProvider=pendingClearRecords.provider;recordProviderMysteriousPolicy=pendingClearRecords.includeMysteriousDecks;clearRecords=pendingClearRecords.data;recordDataRevision=pendingClearRecords.revision;pendingClearRecords=null;recordsChanged=true;
        for(const cache of [resolved,goalContexts,randomRoutes])for(const id of cache.keys())if(String(id).startsWith("record-"))cache.delete(id);
      }
      if(recordProviderMysteriousPolicy!==includeMysteriousDecks){recordProvider=makeRecordProvider(clearRecords);recordProviderMysteriousPolicy=includeMysteriousDecks;kingRecordProviderData=null;}
      const recordsRecompare=recordsChanged&&(settings.manualDeckChoice===false||!settings.preferredDeckId);
      const randomHistory=RandomForecast?.normalizeHistory(settings.randomHistory,{mapHash:hash,localPlayer:settings.localPlayer});
      const randomFacts=randomHistory?.status==="confirmed"?JSON.stringify({drawCount:randomHistory.drawCount,correctionInterval:randomHistory.correctionInterval,commonAcquiredCounts:randomHistory.commonAcquiredCounts}):null;
      const randomHistoryChanged=randomFacts!==null&&(lastRandomHistoryFacts!==null?randomFacts!==lastRandomHistoryFacts:Boolean(selectedId));
      if(randomHistoryChanged)pendingRandomHistoryReplan=true;
      if(randomFacts!==null)lastRandomHistoryFacts=randomFacts;
      const randomRecompare=pendingRandomHistoryReplan&&!craftRunning&&(settings.manualDeckChoice===false||!settings.preferredDeckId);
      if(randomRecompare)pendingRandomHistoryReplan=false;
      if(craftRunning){
        if(typeof settings.activeCraftTarget==="string")lastCraftTarget=settings.activeCraftTarget;
        for(const code of settings.activeCraftExpectedResults||[])if(typeof code==="string")ownExpectedResults.add(code);
      }
      const ownResult=code=>(craftRunning||wasCraftRunning)&&(code===lastCraftTarget||ownExpectedResults.has(code)||lastCraftTarget&&ancestry(lastCraftTarget,code));
      const externalAdded=[];
      if(!initialUpper)for(const [code,count]of currentUpper){
        const added=count-(previousUpper.get(code)||0);if(added<=0)continue;
        if(ownResult(code)){
          if(code!==lastCraftTarget)ownUpperIntermediates.set(code,(ownUpperIntermediates.get(code)||0)+added);
        }else if(craftRunning)deferredUpper.set(code,(deferredUpper.get(code)||0)+added);
        else externalAdded.push(code);
      }
      for(const [code,count]of ownUpperIntermediates){const remaining=Math.min(count,currentUpper.get(code)||0);if(remaining)ownUpperIntermediates.set(code,remaining);else ownUpperIntermediates.delete(code);}
      if(!craftRunning){for(const code of deferredUpper.keys())if((currentUpper.get(code)||0)>0)externalAdded.push(code);deferredUpper.clear();}
      const direction=settings.direction||"all",manualTargetChanged=settings.manualDeckChoice!==false&&settings.preferredDeckId&&settings.preferredDeckId!==selectedId&&settings.preferredDeckId!==suppressedPreferredId;
      if(!craftRunning&&(externalAdded.length||manualTargetChanged||lastIdleDirection!==null&&lastIdleDirection!==direction)){
        // 같은 목표 재개에는 중간 재료를 쓰되, 사용자가 방향을 바꾸면 남은 실제 상위도 새 보유 기준으로 돌린다.
        releasedOwnIntermediateRawcodes=Array.from(ownUpperIntermediates.keys());ownUpperIntermediates.clear();
      }
      if(!craftRunning)lastIdleDirection=direction;
      upperAnchors=new Map(Array.from(currentUpper,([code,count])=>[code,Math.max(0,count-(ownUpperIntermediates.get(code)||0)-(craftRunning?(deferredUpper.get(code)||0):0))]).filter(([,count])=>count>0));
      const upperRebase=!craftRunning&&!freshManual&&(externalAdded.length>0||initialUpper&&upperAnchors.size>0&&(!settings.preferredDeckId||settings.manualDeckChoice===false));
      if(upperRebase){
        selectedId=null;resolved.clear();goalContexts.clear();suppressedPreferredId=settings.preferredDeckId||null;rebaseRevision++;
        rebaseInfo={revision:rebaseRevision,reason:externalAdded.length?"external_upper_added":"initial_owned_upper",newUpperRawcodes:unique(externalAdded.length?externalAdded:Array.from(upperAnchors.keys())),previousSelectedDeckId:previousSelectedId};
      }
      const confirmedItems=settings.items?.status==="confirmed"&&["AI00","AI01","AI02"].every(code=>settings.items.counts?.[code]===0||settings.items.counts?.[code]===1)?["AI00","AI01","AI02"].map(code=>settings.items.counts[code]).join(""):null;
      const itemsChanged=confirmedItems!==null&&(lastConfirmedItems!==null?confirmedItems!==lastConfirmedItems:selectedId!==null&&confirmedItems.includes("1"));
      if(itemsChanged)pendingItemReplan=true;
      if(confirmedItems!==null)lastConfirmedItems=confirmedItems;
      let itemReplanned=false;
      if(pendingItemReplan&&confirmedItems!==null&&!craftRunning){
        if(settings.manualDeckChoice===false||!settings.preferredDeckId){selectedId=null;goalContexts.clear();suppressedPreferredId=settings.preferredDeckId||null;itemReplanned=true;}
        pendingItemReplan=false;
      }
      let confirmedConditionsChanged=false;
      if(["confirmed","partial"].includes(settings.recipeConditions?.status)){
        const facts=Object.entries(settings.recipeConditions.unitBans||{}).filter(([,value])=>typeof value==="boolean").map(([code,value])=>["UBAN:"+code,value]);
        if(["allowed","blocked"].includes(settings.recipeConditions.king?.status))facts.push(["KING",settings.recipeConditions.king.status]);
        for(const [key,value]of facts){if(lastKnownRecipeConditions.has(key)?lastKnownRecipeConditions.get(key)!==value:selectedId!==null)confirmedConditionsChanged=true;lastKnownRecipeConditions.set(key,value);}
      }
      const specialFacts=Graph.normalizeRecipeSpecial(settings.recipeConditions?.special,settings.localPlayer,hash);
      for(const key of ["distortionWoodSurcharge","distortionWoodIncrement","transformationCount","skillPoints","round"]){const value=specialFacts[key];if(value===null)continue;const fact="SPECIAL:"+key;if(lastKnownRecipeConditions.has(fact)?lastKnownRecipeConditions.get(fact)!==value:selectedId!==null)confirmedConditionsChanged=true;lastKnownRecipeConditions.set(fact,value);}
      if(specialFacts.nikaEnhancement.status==="confirmed"){const fact="SPECIAL:NIKA",value=specialFacts.nikaEnhancement.stage;if(lastKnownRecipeConditions.has(fact)?lastKnownRecipeConditions.get(fact)!==value:selectedId!==null)confirmedConditionsChanged=true;lastKnownRecipeConditions.set(fact,value);}
      // 저장한 사실은 재비교 신호에만 쓴다. 현재 unknown을 과거 허용값으로 복원하지 않는다.
      if(confirmedConditionsChanged)pendingRecipeConditionReplan=true;
      const conditionRecompare=pendingRecipeConditionReplan&&!craftRunning&&(settings.manualDeckChoice===false||!settings.preferredDeckId);
      if(pendingRecipeConditionReplan&&!craftRunning)pendingRecipeConditionReplan=false;
      const rewardKnown=["confirmed","partial"].includes(activeRewardBudget.status);
      const rewardFacts=rewardKnown?JSON.stringify({status:activeRewardBudget.status,guaranteed:activeRewardBudget.guaranteed,conditional:activeRewardBudget.conditional,unknownEvents:activeRewardBudget.unknownEvents,wood:settings.wood,gold:settings.gold}):lastConfirmedRewardBudget?.known?null:JSON.stringify({wood:settings.wood,gold:settings.gold});
      const rewardBudgetChanged=rewardFacts!==null&&lastConfirmedRewardBudget!==null&&lastConfirmedRewardBudget.facts!==rewardFacts;
      if(rewardBudgetChanged)pendingRewardBudgetReplan=true;
      if(rewardFacts!==null)lastConfirmedRewardBudget={facts:rewardFacts,known:rewardKnown};
      const budgetRecompare=pendingRewardBudgetReplan&&!craftRunning&&(settings.manualDeckChoice===false||!settings.preferredDeckId);
      if(pendingRewardBudgetReplan&&!craftRunning)pendingRewardBudgetReplan=false;
      const combatFacts={};
      if(["warcury","nasujuro","saturn","none"].includes(settings.boss))combatFacts.boss=settings.boss;
      if(Number.isSafeInteger(settings.round)&&settings.round>0)combatFacts.round=settings.round;
      const combatScenarioChanged=Boolean(combatVerifier&&lastConfirmedCombatScenario&&Object.entries(combatFacts).some(([key,value])=>lastConfirmedCombatScenario[key]!==value));
      if(combatScenarioChanged)pendingCombatScenarioReplan=true;
      if(Object.keys(combatFacts).length)lastConfirmedCombatScenario={...(lastConfirmedCombatScenario||{}),...combatFacts};
      const combatRecompare=pendingCombatScenarioReplan&&!craftRunning&&(settings.manualDeckChoice===false||!settings.preferredDeckId);
      if(pendingCombatScenarioReplan&&!craftRunning)pendingCombatScenarioReplan=false;
      previousUpper=currentUpper;wasCraftRunning=craftRunning;if(!craftRunning){lastCraftTarget=null;ownExpectedResults.clear();}
      lastManualRevision=manualRevision;lastPreferredId=settings.preferredDeckId||null;
      const incomingLegend=settings.firstInvestmentRawcode||settings.firstLegendRawcode,legendChanged=typeof incomingLegend==="string"&&incomingLegend!=="h0C8"&&incomingLegend!==firstInvestmentRawcode&&["전설","히든"].includes(units.get(incomingLegend)?.tier)&&units.get(incomingLegend)?.playable===true&&!excludedSummons.has(incomingLegend);
      if(legendChanged){firstInvestmentRawcode=incomingLegend;pendingFirstInvestmentReplan=true;}
      let firstInvestmentReplanned=false,firstInvestmentReplanReason=pendingFirstInvestmentReplan?"waiting_for_craft_idle":null;
      if(pendingFirstInvestmentReplan&&!craftRunning){
        if(upperAnchors.size)firstInvestmentReplanReason="owned_upper_priority";
        else if(settings.manualDeckChoice!==false&&settings.preferredDeckId)firstInvestmentReplanReason="manual_choice_retained";
        else{
          selectedId=null;resolved.clear();goalContexts.clear();suppressedPreferredId=settings.preferredDeckId||null;
          firstInvestmentReplanned=true;firstInvestmentReplanReason="first_investment_changed";
        }
        pendingFirstInvestmentReplan=false;
      }
      const stockRecompare=pendingStockRecompare&&!craftRunning&&(settings.manualDeckChoice===false||!settings.preferredDeckId);
      if(pendingStockRecompare&&!craftRunning){
        if(stockRecompare)suppressedPreferredId=settings.preferredDeckId||null;
        pendingStockRecompare=false;
      }
      if(earlyStoryAdvisor&&nativeMap&&counts!=null){
        const manualFinal=Boolean(settings.preferredDeckId)&&settings.manualDeckChoice!==false;
        const ownedAdvanced=Array.from(inventory).some(([code,count])=>count>0&&(includeMysteriousDecks||!mysterious(code))&&code!=="h0C8"&&!investmentMaterialProxies.has(code)&&advanced.has(units.get(code)?.tier)&&units.get(code)?.playable===true&&!excludedSummons.has(code));
        if(ownedAdvanced&&!(craftRunning&&earlyStorySnapshot))earlyStoryAdvancedSeen=true;
        if(craftRunning&&earlyStorySnapshot&&!manualFinal)return{...earlyStorySnapshot,earlyStory:{...earlyStorySnapshot.earlyStory,craftRunning:true},craftTargets:[],goalCraftTargets:earlyStorySnapshot.goalCraftTargets.map(target=>({...target,available:false,reason:"craft_running",expectedSteps:[],expectedStepCount:0})),planningMs:Date.now()-started};
        if(!craftRunning&&!manualFinal&&!earlyStoryAdvancedSeen&&settings.firstInvestmentAmbiguous!==true&&!firstInvestmentRawcode&&settings.kingPathEntryCompatibility.status!=="blocked"){
          const rows=earlyStoryAdvisor.candidates(inventory,settings),actions=new Map(),held=countsOf(settings.protectedCounts),ids=[];
          const planOptions={items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:settings.localPlayer,supportedRecipeIds:settings.supportedRecipeIds,allowChoiceWisps:settings.allowChoiceWisps===true,protectedCounts:held,preserveGoalOrder:true,maxSteps:64,maxSearchNodes:settings.maxSearchNodes||16000,planningKingCraftBudget:settings.planningKingCraftBudget};
          for(const row of rows){
            const id="early-story:"+row.rawcode,goal={...info(row.rawcode),count:1};ids.push(id);
            // 후보 하나의 실제 재고 경로만 계획한다. 미래 단발 보상이나 다른 후보의 재료를 예약하지 않는다.
            goalContexts.set(id,{roster:{goals:[goal]},held,options:planOptions});
            const action=goalActions({id,assessment:{compositionStatus:"early_story_single_unit"},acquisitionPlan:{sharedChoiceFeasible:true},kingPathConstraint:{entryCompatibility:settings.kingPathEntryCompatibility}},inventory,settings)[0];
            row.ready=action.available===true;actions.set(row.rawcode,{...action,origin:"early_story_whole_goal"});
          }
          const unitGap=row=>(row.progress?.missingNonChoiceLeaves||[]).reduce((sum,item)=>sum+(item.count||0),0)+Math.max(0,(row.progress?.choiceWispNeeded||0)-(row.progress?.choiceWispAvailable||0)),woodGap=row=>(row.progress?.missingResources||[]).filter(item=>item.type==="WOOD").reduce((sum,item)=>sum+(item.missing??Infinity),0);
          const legalRows=rows.filter(row=>!["recipe_conditions_blocked","unsupported_recipe"].includes(actions.get(row.rawcode)?.reason));
          const percent=row=>finite(row.progress?.percent)?row.progress.percent:-1,ratio=row=>finite(row.progress?.ratio)?row.progress.ratio:-1;
          legalRows.sort((a,b)=>percent(b)-percent(a)||ratio(b)-ratio(a)||Number(b.ready)-Number(a.ready)||unitGap(a)-unitGap(b)||((actions.get(a.rawcode)?.choiceWisps??a.progress?.choiceWispNeeded??Infinity)-(actions.get(b.rawcode)?.choiceWisps??b.progress?.choiceWispNeeded??Infinity))||woodGap(a)-woodGap(b)||(a.storyRank??Infinity)-(b.storyRank??Infinity)||a.rawcode.localeCompare(b.rawcode));
          const top=legalRows.slice(0,3),topCodes=new Set(top.map(row=>row.rawcode));for(const id of ids)if(!topCodes.has(id.slice("early-story:".length)))goalContexts.delete(id);
          if(top.length||earlyStoryAdvisor.candidateCount>0){const targets=top.map(row=>actions.get(row.rawcode)),best=targets.find(target=>target.available===true);selectedId=null;lastSelectedTemplate=null;lastSelectedSupportBase=null;
            earlyStorySnapshot={schemaVersion:1,phase:"early_story",targetDeck:null,selectedDeckId:null,alternatives:[],earlyStory:{candidates:top,selectedRawcode:top[0]?.rawcode||null,policy:earlyStoryAdvisor.policy,storyMeasurementVersion:earlyStoryAdvisor.storyMeasurementVersion,sourceExperimentVersion:earlyStoryAdvisor.sourceExperimentVersion,relativeQualityFraction:earlyStoryAdvisor.relativeQualityFraction,sourceCandidateCount:earlyStoryAdvisor.sourceCandidateCount,evaluatedCandidateCount:rows.length,craftRunning:false},goalCraftTargets:targets,craftTargets:best?[best]:[],order:top.map(row=>row.rawcode),recommendationPolicy,canConfirmClear:false,searchComplete:true,evaluatedDeckCount:0,eligibleDeckCount:0,planningMs:Date.now()-started};return earlyStorySnapshot;}
          for(const id of ids)goalContexts.delete(id);
        }
        earlyStorySnapshot=null;
      }
      if(!nativeMap)warnings.push("전투 프로필과 현재 맵의 일치를 확인하지 못했습니다.");
      if(!rulesMap)warnings.push("난이도 규칙과 현재 맵의 일치를 확인하지 못했습니다.");
      const requestedBase=supportAdapter&&typeof settings.preferredDeckId==="string"&&settings.preferredDeckId.includes("--support-")?settings.preferredDeckId.split("--support-")[0]:null;
      const sourceSelection=requestedBase?{...settings,preferredDeckId:requestedBase}:lastSelectedTemplate?.sourceEvidence?.kind==="record_support_adaptation"&&(!settings.preferredDeckId||settings.preferredDeckId===lastSelectedTemplate.id)?{...settings,preferredDeckId:lastSelectedTemplate.sourceEvidence.baseTemplateId}:settings;
      let recordSet=recordProvider?.templates(inventory,sourceSelection);
      if(includeKingPathDecks&&recordProvider&&clearRecords&&(pendingKingPathTargetSelection||settings.manualDeckChoice===false||!settings.preferredDeckId)){
        if(kingRecordProviderData!==clearRecords){kingRecordProviderData=clearRecords;const data=recommendationRecordData(clearRecords);kingRecordProvider=Records.create(catalog,combat,{...data,records:(data.records||[]).filter(record=>[...(record.goals||[]),...(record.entries||[])].some(entry=>entry.rawcode==="h0C8"))},{recipeGraph:graph,recordOnly,minRecordVersion,generateAlternatives:!recordOnly,handFitAdvisor});}
        const supplement=kingRecordProvider.templates(inventory,{...sourceSelection,preferredDeckId:null}),seen=new Set((recordSet?.templates||[]).map(template=>template.id)),extra=supplement.templates.filter(template=>!seen.has(template.id));
        if(extra.length)recordSet={...recordSet,templates:[...(recordSet?.templates||[]),...extra],preselection:{...recordSet?.preselection,kingPathSupplementCount:extra.length,kingPathSupplementTruncated:supplement.preselection?.truncated===true}};
      }
      const usingRecords=Boolean(recordSet?.templates.some(t=>(settings.direction==null||settings.direction==="all"||settings.direction===t.direction)&&(includeKingPathDecks||!kingPathDependent(resolve(t,inventory,settings),t))));
      let templates=recordOnly?(recordSet?.templates||[]).filter(template=>Records?.originalClearTemplate?.(template)===true):usingRecords?[...recordSet.templates,...(recordSet.alternatives||[])]:fallbackTemplates;
      if(!includeKingPathDecks)templates=templates.filter(template=>!kingPathDependent(resolve(template,inventory,settings),template));
      if(!includeMysteriousDecks)templates=templates.filter(template=>!mysteriousDependent(template));
      const completeFallbackIds=new Set();
      if(!recordOnly&&usingRecords){const ids=new Set(templates.map(template=>template.id));for(const template of fallbackTemplates)if((includeMysteriousDecks||!mysteriousDependent(template))&&!ids.has(template.id)&&assessment(template,resolve(template,inventory,settings),settings).roleCompositionStatus!=="incomplete"){templates.push(template);completeFallbackIds.add(template.id);}}
      if(!recordOnly&&firstInvestmentRawcode&&!upperAnchors.size&&!templates.some(template=>firstInvestmentTargets(resolve(template,inventory,settings)).length)){
        const finalTiers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]),hiddenSeed=units.get(firstInvestmentRawcode)?.tier==="히든",seedLabel=hiddenSeed?"히든":"전설";
        const descendants=Array.from(units.values()).filter(unit=>(includeMysteriousDecks||!mysterious(unit.rawcode))&&unit.playable===true&&(finalTiers.has(unit.tier)||hiddenSeed&&unit.rawcode===firstInvestmentRawcode)&&native(unit.rawcode)&&
          (!nonForecastable.has(unit.rawcode)||hiddenSeed&&unit.rawcode===firstInvestmentRawcode&&(inventory.get(unit.rawcode)||0)>0)&&(ancestry(unit.rawcode,firstInvestmentRawcode)||hiddenSeed&&unit.rawcode===firstInvestmentRawcode)&&!effects(unit.rawcode).some(effect=>effect.isPenalty===true||finite(effect.value)&&effect.value<0)&&
          ((inventory.get(unit.rawcode)||0)>0||Array.from(recipes.values()).some(recipe=>recipe.resultUnitRawcode===unit.rawcode&&Graph.isProductionRecipe(recipe)&&settings.supportedRecipeIds.has(String(recipe.id)))));
        const variants=[];
        for(const base of templates.filter(template=>template.sourceEvidence?.kind!=="role_similar_alternative").slice(0,12)){
          const roster=resolve(base,inventory,settings),codes=new Set(roster.goals.map(goal=>goal.rawcode));
          const compatible=unit=>{
            if(codes.has(unit.rawcode)||roster.goals.some(goal=>ancestry(goal.rawcode,unit.rawcode)))return false;
            const types=profiles[unit.rawcode]?.damage?.explicitTypes||[];
            if(coreMode(unit.rawcode)===base.direction||types.includes(base.direction)||base.direction==="hybrid"&&(types.length||coreMode(unit.rawcode)))return true;
            return supports(unit.rawcode,"alwaysSlow").length>0||supports(unit.rawcode,"areaStun").length>0||supports(unit.rawcode,"manaSupport").length>0||hiddenSeed&&unit.rawcode===firstInvestmentRawcode&&["teamAttackSpeed","teamAttackDamage","healthSupport","damageSynergy"].some(kind=>supports(unit.rawcode,kind).length>0);
          };
          const choices=descendants.filter(compatible).map(unit=>({unit,analysis:graph.analyze(unit.rawcode,inventory,undefined,{resources:{wood:settings.wood,gold:settings.gold},items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:settings.localPlayer})}))
            .sort((a,b)=>Number(b.analysis.resourcesReady)-Number(a.analysis.resourcesReady)||b.analysis.ratio-a.analysis.ratio||a.unit.rawcode.localeCompare(b.unit.rawcode));
          for(const {unit}of choices.slice(0,2)){
            const inherited=base.sourceEvidence||{},balance=inherited.balanceAdjustment;
            const sourceEvidence={...inherited,kind:hiddenSeed?"first_hidden_variant":"first_legend_variant",baseTemplateId:base.id,baseSourceKind:inherited.kind||"native_fallback",compositionChanged:true,clearOutcomeTransferred:false,
              firstInvestmentRawcode,addedFinalRawcode:unit.rawcode,performanceEquivalenceVerified:false,
              balanceAdjustment:balance?{...balance,riskRank:Math.max(3,balance.riskRank||0),clearEquivalenceVerified:false,risks:[...(balance.risks||[]),{kind:"composition_changed",message:"첫 "+seedLabel+"을 활용하도록 목표를 추가한 구성입니다."}]}:null};
            variants.push({...base,id:base.id+(hiddenSeed?"--hidden-":"--legend-")+unit.rawcode,name:"[첫 "+seedLabel+" 활용] "+info(unit.rawcode).name+" · "+(base.name||base.id),
              source:sourceEvidence,sourceEvidence,evidenceScope:"first_investment_added_to_existing_composition_not_replayed",supportSlots:[...(base.supportSlots||[]),{id:"first-investment-final",roles:[],preferredRawcodes:[unit.rawcode],alternatives:[],count:1,required:true}]});
          }
        }
        templates=[...templates,...variants];
      }
      if(!includeMysteriousDecks)templates=templates.filter(template=>!mysteriousDependent(template));
      if(!nativeMap||counts==null||!templates.length||typeof graph.planDeck!=="function"){
        const reinforcements=nativeMap&&counts!=null&&typeof graph.planDeck==="function"?reinforcementAdvisor?.recommend(inventory,{...settings,rewardBudget:activeRewardBudget},{}):null;
        return {schemaVersion:1,recommendationPolicy,targetDeck:null,craftTargets:[],order:[],selectedDeckId:null,alternatives:[],...(reinforcements?{reinforcements}:{}),warnings:[...warnings,"전체 목표 구성 자료를 확인하지 못했습니다."],canConfirmClear:false,searchComplete:false,evaluatedDeckCount:0,eligibleDeckCount:0};
      }
      const ownedOpportunities=(recordOnly?[]:curated.ownedSourceUpgrades||[]).filter(up=>(includeMysteriousDecks||!mysterious(up.resultRawcode))&&!excludedSummons.has(up.sourceRawcode)&&!excludedSummons.has(up.resultRawcode)&&((inventory.get(up.sourceRawcode)||0)>0||(inventory.get(up.resultRawcode)||0)>0)).map(up=>({...up,...info(up.resultRawcode),sourceName:info(up.sourceRawcode).name,
        ownedSource:inventory.get(up.sourceRawcode)||0,ownedResult:inventory.get(up.resultRawcode)||0,templateIds:[],automaticCraftSupported:settings.supportedRecipeIds!=null&&new Set(settings.supportedRecipeIds).has(String(up.recipeId))&&!up.targetedInputRequired&&!up.additionalRandomUnitRequired,
        finalAnchorEligible:strategyUnits[up.resultRawcode]?.finalAnchorEligible===true}));
      const ownedTemplates=[];
      const canonicalRole=x=>({teamAttackSpeed:"attackSpeedBuff",teamAttackDamage:"attackDamageBuff",continuousSlow:"alwaysSlow",manaSustain:"manaSupport"})[x]||x;
      for(const opportunity of ownedOpportunities){
        if(opportunity.reviewStatus==="unverified"||!native(opportunity.resultRawcode)||!strategyUnits[opportunity.resultRawcode])continue;
        const contributionModes=strategyUnits[opportunity.resultRawcode].reviewedDamageModes||profiles[opportunity.resultRawcode]?.damage?.explicitTypes||[];
        for(const base of templates){
          if(["physicalDps","magicDps","hybridDps"].includes(opportunity.primaryRole)&&base.direction!=="hybrid"&&contributionModes.length&&!contributionModes.includes(base.direction))continue;
          const roles=(opportunity.coverageRoles||[]).map(canonicalRole),baseline=resolve(base,inventory,settings);
          const compatible=s=>{
            if(!(s.roles||[]).length||!(s.roles||[]).every(role=>roles.includes(canonicalRole(role))))return false;
            const original=baseline.slots.find(x=>x.id===s.id)?.rawcode,candidate=opportunity.resultRawcode;
            for(const role of s.roles||[]){
              if(["lineDamage","bossDamage","storyDamage"].includes(role)){
                const previous=strategyUnits[original]?.[role]?.assessment,next=strategyUnits[candidate]?.[role]?.assessment;
                if(previous==="main"&&next!=="main"||!["main","support","finisher"].includes(next))return false;
              }else if(!supports(candidate,canonicalRole(role)).length)return false;
            }
            // 포괄적인 control/story 태그로 원래의 범위 제어·상시 이감·스토리 주력을 없애지 않는다.
            if(original&&supports(original,"areaStun").length&&!supports(candidate,"areaStun").length)return false;
            if(original&&supports(original,"alwaysSlow").length&&!supports(candidate,"alwaysSlow").length)return false;
            for(const purpose of ["lineDamage","bossDamage","storyDamage"])if(strategyUnits[original]?.[purpose]?.assessment==="main"&&strategyUnits[candidate]?.[purpose]?.assessment!=="main")return false;
            return true;
          };
          const slot=(base.supportSlots||[]).find(compatible);
          const id=base.id+"--owned-"+opportunity.id,variant={...base,id,name:(base.name||base.id)+" · "+opportunity.name+" 활용",ownedUpgradeId:opportunity.id,
            availableWhen:{ownedAnyRawcodes:[opportunity.sourceRawcode,opportunity.resultRawcode],minimumCount:1},requiredOwnedMaterials:[],
            supportSlots:(base.supportSlots||[]).map(s=>slot&&s.id===slot.id?{...s,preferredRawcodes:[opportunity.resultRawcode],alternatives:[]}:s),
            conditions:unique([...(base.conditions||[]),...(opportunity.conditions||[])])};
          if(!slot)variant.supportSlots.push({id:"owned-"+opportunity.id,roles:opportunity.coverageRoles||[],preferredRawcodes:[opportunity.resultRawcode],alternatives:[],count:1,required:true});
          ownedTemplates.push(variant);opportunity.templateIds.push(id);
        }
      }
      const adaptationBases=new Map(templates.map(template=>[template.id,template]));
      const handPreselectionCache=new Map(),handPreselection=template=>{if(!handFitAdvisor)return null;if(!handPreselectionCache.has(template.id)){const roster=resolve(template,inventory,settings);handPreselectionCache.set(template.id,handFitAdvisor.preselectionScore({...template,goals:roster.fixedGoals||roster.goals},inventory,settings));}const score=handPreselectionCache.get(template.id);return finite(score)&&score>=0?score:null;};
      const supportSearch={enabled:Boolean(supportAdapter),bounded:Boolean(supportAdapter),evaluatedBases:0,generated:0,truncated:false};
      let adaptedTemplates=[];
      if(supportAdapter&&!craftRunning){
        const supportFit=template=>{const roster=resolve(template,inventory,settings);return {upper:upperInvestment(roster).sourceGoalMatches,first:firstInvestmentTargets(roster).length,owned:roster.fixedGoals.filter(goal=>(inventory.get(goal.rawcode)||0)>=goal.count).length};};
        const fits=new Map(templates.map(template=>[template.id,supportFit(template)]));
        const supportPrefix=(a,b)=>fits.get(b.id).upper-fits.get(a.id).upper||Number(fits.get(b.id).first>0)-Number(fits.get(a.id).first>0);
        let originals=templates.slice().sort((a,b)=>supportPrefix(a,b)||fits.get(b.id).owned-fits.get(a.id).owned||a.id.localeCompare(b.id));
        if(handFitAdvisor)originals=rerankKnown(originals,supportPrefix,handPreselection,(a,b)=>b-a);
        const preferredBase=requestedBase||lastSelectedTemplate?.sourceEvidence?.baseTemplateId||settings.preferredDeckId;
        const bases=unique([adaptationBases.get(preferredBase),...originals].filter(Boolean)).slice(0,12);
        const conditionFacts={status:settings.recipeConditions.status,localPlayer:settings.recipeConditions.localPlayer,unitBans:Object.entries(settings.recipeConditions.unitBans||{}).sort(([a],[b])=>a.localeCompare(b)),king:settings.recipeConditions.king?.status,nikaEnhancement:specialFacts.nikaEnhancement,special:Object.fromEntries(["status","localPlayer","distortionWoodSurcharge","distortionWoodIncrement","transformationCount","skillPoints","round"].map(key=>[key,settings.recipeConditions.special?.[key]]))};
        const itemFacts={status:settings.items?.status,localPlayer:settings.items?.localPlayer,counts:settings.items?.counts,readOnly:settings.items?.source?.readOnly};
        const signature=JSON.stringify([identity,recordDataRevision,bases.map(template=>template.id),permanentStock,settings.wood,settings.gold,Array.from(settings.supportedRecipeIds).sort(),itemFacts,conditionFacts,settings.localPlayer,includeKingPathDecks,includeMysteriousDecks,settings.supportCandidateRawcodes,settings.supportCandidateScores]);
        if(supportAdaptationCache?.signature===signature){adaptedTemplates=supportAdaptationCache.templates;Object.assign(supportSearch,supportAdaptationCache.search,{cached:true});}
        else{
          for(const base of bases){
            if(adaptedTemplates.length>=24){supportSearch.truncated=true;break;}
            const adapted=supportAdapter.adapt(base,inventory,{...settings,supportMaxVariants:Math.min(4,24-adaptedTemplates.length)});
            supportSearch.evaluatedBases++;supportSearch.truncated||=adapted.search?.limitReached===true||adapted.search?.bounded===true;
            adaptedTemplates.push(...(adapted.templates||[]).filter(template=>Records.originalClearTemplate(base)&&supportAdapter.isAdaptation(template,base)).slice(0,24-adaptedTemplates.length));
          }
          supportSearch.generated=adaptedTemplates.length;supportSearch.truncated||=originals.length>bases.length;
          supportAdaptationCache={signature,templates:adaptedTemplates,search:{...supportSearch}};
        }
      }
      const allTemplates=[...templates,...adaptedTemplates,...ownedTemplates];
      const kingPathPreferenceRecompare=pendingKingPathPreferenceReplan&&!craftRunning&&(settings.manualDeckChoice===false||!settings.preferredDeckId||pendingKingPathTargetSelection);
      if(!craftRunning)pendingKingPathPreferenceReplan=false;
      const busyTemplate=craftRunning&&lastSelectedTemplate?.id===selectedId?lastSelectedTemplate:null;
      if(busyTemplate&&!allTemplates.some(template=>template.id===busyTemplate.id))allTemplates.push(busyTemplate);
      const eligible=t=>(t.requiredOwnedMaterials||[]).every(x=>(inventory.get(x.rawcode)||0)>=(x.count||1))&&
        (!(t.availableWhen?.ownedAnyRawcodes||[]).length||t.availableWhen.ownedAnyRawcodes.some(c=>(inventory.get(c)||0)>=(t.availableWhen.minimumCount||1)));
      const capacityAllowed=t=>transcendenceState(resolve(t,inventory,settings).goals,inventory).valid;
      const recordSourceAllowed=template=>{
        if(!recordOnly||Records?.originalClearTemplate?.(template)===true)return true;
        const original=adaptationBases.get(template.sourceEvidence?.baseTemplateId)||(template===busyTemplate?lastSelectedSupportBase:null);
        return Boolean(supportAdapter&&original&&Records.originalClearTemplate(original)&&supportAdapter.isAdaptation(template,original));
      };
      const allowed=allTemplates.filter(t=>(includeMysteriousDecks||!mysteriousDependent(t))&&recordSourceAllowed(t)&&capacityAllowed(t)&&(includeKingPathDecks||!kingPathDependent(resolve(t,inventory,settings),t))&&(t.id===busyTemplate?.id||eligible(t)&&["physical","magic","hybrid","unknown"].includes(t.direction)&&(settings.direction==null||settings.direction==="all"||settings.direction===t.direction)));
      if(settings.preferredDeckId&&!upperRebase&&settings.preferredDeckId!==suppressedPreferredId&&(settings.manualDeckChoice!==false||!selectedId)&&(!craftRunning||!selectedId)&&!(legendChanged&&!upperAnchors.size&&settings.manualDeckChoice===false)&&allowed.some(t=>t.id===settings.preferredDeckId)){
        if(selectedId&&selectedId!==settings.preferredDeckId)resolved.delete(settings.preferredDeckId);
        selectedId=settings.preferredDeckId;
      }
      let candidates=[];
      const modeInventory=upperAnchors.size?upperAnchors:inventory;
      const ownedModes=new Set(Array.from(modeInventory).filter(([,n])=>n>0).map(([code])=>coreMode(code)).filter(Boolean));
      const preferredMode=ownedModes.has("hybrid")||ownedModes.has("physical")&&ownedModes.has("magic")?"hybrid":ownedModes.has("magic")?"magic":ownedModes.has("physical")?"physical":null;
      const expectedTranscendence=t=>finite(t.transcendenceMaterialCount)?t.transcendenceMaterialCount:(t.coreRawcodes||[]).filter(code=>units.get(code)?.tier==="초월").length;
      // 수동 목표와 실행 중인 목표는 유지한다. 자동 목표는 유닛 재고가 바뀐 뒤 기존 목표와 다시 비교한다.
      const selected=allowed.find(t=>t.id===selectedId);
      const conditionalOwned=t=>(t.requiredOwnedMaterials||[]).length+(t.availableWhen?.ownedAnyRawcodes||[]).length>0;
      const overlap=t=>resolve(t,inventory,settings).goals.filter(g=>(inventory.get(g.rawcode)||0)>=g.count).length;
      // 과거 기록의 수치 변화를 총 DPS나 승률로 바꾸지 않고, 같은 재고 적합도에서는 변경 위험이 적은 근거를 우선한다.
      const balanceRisk=t=>finite(t.sourceEvidence?.balanceAdjustment?.riskRank)?t.sourceEvidence.balanceAdjustment.riskRank:0;
      const balanceDownsides=t=>finite(t.sourceEvidence?.balanceAdjustment?.downsideCount)?t.sourceEvidence.balanceAdjustment.downsideCount:0;
      const legendRank=t=>{const matches=firstInvestmentTargets(resolve(t,inventory,settings));return matches.some(goal=>goal.rawcode!==firstInvestmentRawcode)?2:matches.length?1:0;},legendMatch=t=>legendRank(t)>0;
      const upperFitCache=new Map(),upperFit=t=>{if(!upperFitCache.has(t.id))upperFitCache.set(t.id,upperInvestment(resolve(t,inventory,settings)));return upperFitCache.get(t.id);};
      const queuePrefix=(a,b)=>upperFit(b).sourceGoalMatches-upperFit(a).sourceGoalMatches||upperFit(a).missingSynergyRoles.length-upperFit(b).missingSynergyRoles.length||legendRank(b)-legendRank(a)||Number(conditionalOwned(b))-Number(conditionalOwned(a));
      let rankedQueue=allowed.slice().sort((a,b)=>queuePrefix(a,b)||overlap(b)-overlap(a)||balanceRisk(a)-balanceRisk(b)||balanceDownsides(a)-balanceDownsides(b)||Number(b.direction===preferredMode)-Number(a.direction===preferredMode)||expectedTranscendence(a)-expectedTranscendence(b));
      if(handFitAdvisor)rankedQueue=rerankKnown(rankedQueue,queuePrefix,handPreselection,(a,b)=>b-a);
      const firstPerUpgrade=unique(ownedOpportunities.map(o=>rankedQueue.find(t=>t.ownedUpgradeId===o.id)));
      const baseQueue=rankedQueue.filter(t=>!t.ownedUpgradeId&&t.sourceEvidence?.kind!=="role_similar_alternative"),baseFirst=selected&&!selected.ownedUpgradeId&&selected.sourceEvidence?.kind!=="role_similar_alternative"?selected:baseQueue[0];
      const historical=t=>{const version=t?.sourceEvidence?.balanceAdjustment?.evaluationSourceVersion||t?.sourceEvidence?.balanceAdjustment?.sourceVersion;return Boolean(version&&version!=="2.323");};
      const completeBaselines=unique([...['physical','magic','hybrid'].map(mode=>baseQueue.find(template=>completeFallbackIds.has(template.id)&&template.direction===mode)),...baseQueue.filter(template=>completeFallbackIds.has(template.id))]).slice(0,4);
      const preferActualLeo=includeKingPathDecks&&(pendingKingPathTargetSelection||settings.manualDeckChoice===false||!settings.preferredDeckId);
      const leoQueue=preferActualLeo?rankedQueue.filter(template=>resolve(template,inventory,settings).goals.some(goal=>goal.rawcode==="h0C8")):[];
      const kingOnlyResults=new Set(Array.from(units.keys()).filter(code=>{const production=Array.from(recipes.values()).filter(recipe=>recipe.resultUnitRawcode===code&&Graph.isProductionRecipe(recipe));return production.length>0&&production.every(recipe=>(recipe.ingredients||[]).some(item=>item.type==="KING"&&item.count>0));}));
      const modeCompatibleLeoQueue=leoQueue.filter(template=>resolve(template,inventory,settings).goals.reduce((sum,goal)=>sum+(kingOnlyResults.has(goal.rawcode)?Math.max(0,goal.count-(inventory.get(goal.rawcode)||0)):0),0)<=(settings.planningKingCraftBudget??Infinity));
      const baselines=unique([baseFirst,modeCompatibleLeoQueue[0],leoQueue[0],baseQueue.find(t=>legendRank(t)===2),baseQueue.find(legendMatch),baseQueue.find(t=>t.direction!==baseFirst?.direction),baseQueue.find(historical),baseQueue.find(t=>!historical(t)),...completeBaselines]);
      const adaptedBaselines=unique(baselines.map(base=>rankedQueue.find(template=>template.sourceEvidence?.baseTemplateId===base.id)));
      const queue=unique([selected,...baselines,...modeCompatibleLeoQueue,...leoQueue,...adaptedBaselines,...firstPerUpgrade,...rankedQueue].filter(Boolean));
      for(const template of queue){const comparedBaselines=baselines.every(t=>candidates.some(c=>c.id===t.id));if(candidates.length>=64||Date.now()-started>planningMilliseconds&&candidates.length>0&&comparedBaselines){warnings.push("일부 목표 구성만 현재 재고와 대조했습니다.");break;}candidates.push(evaluate(template,inventory,settings));}
      const randomForecast=randomForecasts(candidates,inventory,settings,started);
      const feasibilityRank=deck=>({reachable:0,conditional:1,blocked:2})[deck.recommendationFeasibility.status];
      const budgetRank=deck=>({within_guaranteed:0,within_conditional:1,unknown:2,over_budget:3})[deck.budgetFeasibility.status];
      const budgetGap=deck=>{const gap=deck.budgetFeasibility.deficits.conditional;return [Object.values(deck.budgetFeasibility.required).filter(value=>value===null).length,Object.values(gap).filter(value=>value===null).length,Object.values(gap).filter(value=>value>0).length,gap.choiceWisps??Infinity,gap.wood??Infinity,gap.gold??Infinity];};
      const compareGap=(a,b)=>{if(budgetRank(a)<2||budgetRank(b)<2||a.budgetFeasibility.sourceStatus==="unknown"&&b.budgetFeasibility.sourceStatus==="unknown")return 0;const left=budgetGap(a),right=budgetGap(b);for(let index=0;index<left.length;index++)if(left[index]!==right[index])return left[index]-right[index];return 0;};
      const incomplete=deck=>deck.assessment.compositionStatus==="incomplete"||deck.assessment.missingSlots.length>0||deck.assessment.coverage.some(row=>row.status==="missing");
      const compareRandom=(a,b)=>{for(const mode of ["guaranteed","conditionalRewards"]){const x=a.randomMaterialForecast?.[mode],y=b.randomMaterialForecast?.[mode];if(x?.status==="estimated"&&y?.status==="estimated"&&Math.abs(y.confidence.lower-x.confidence.lower)>1e-12)return y.confidence.lower-x.confidence.lower;}return 0;};
      const comparePrefix=(a,b)=>Number(a.recommendationFeasibility.status==="blocked")-Number(b.recommendationFeasibility.status==="blocked")||Number(incomplete(a))-Number(incomplete(b))||((a.combatValidation?.provenFailureCount||0)-(b.combatValidation?.provenFailureCount||0))||(combatVerifier?.compare(a.combatValidation,b.combatValidation)||0)||Number(b.budgetFeasibility.status==="within_guaranteed")-Number(a.budgetFeasibility.status==="within_guaranteed")||compareRandom(a,b)||budgetRank(a)-budgetRank(b)||compareGap(a,b)||feasibilityRank(a)-feasibilityRank(b)||(upperAnchors.size?(Number(b.acquisitionPlan.sharedChoiceFeasible)-Number(a.acquisitionPlan.sharedChoiceFeasible)||b.ownedUpperInvestment.sourceGoalMatches-a.ownedUpperInvestment.sourceGoalMatches||a.ownedUpperInvestment.missingSynergyRoles.length-b.ownedUpperInvestment.missingSynergyRoles.length):0)||(b.firstInvestment?.upgradedFinalGoals?.length?2:Number(b.matchesFirstInvestmentLineage))-(a.firstInvestment?.upgradedFinalGoals?.length?2:Number(a.matchesFirstInvestmentLineage))||Number(b.acquisitionPlan.sharedChoiceFeasible)-Number(a.acquisitionPlan.sharedChoiceFeasible)||Number(b.planning.current.unitReady)-Number(a.planning.current.unitReady)||rankResource(b.planning.current.itemsReady)-rankResource(a.planning.current.itemsReady)||rankResource(b.planning.current.resourcesReady)-rankResource(a.planning.current.resourcesReady);
      const compareTail=(a,b)=>b.ownedOverlap-a.ownedOverlap||balanceRisk(a)-balanceRisk(b)||balanceDownsides(a)-balanceDownsides(b)||Number(b.mode===preferredMode)-Number(a.mode===preferredMode)||b.investmentMatch-a.investmentMatch||a.assessment.coverage.filter(r=>r.status==="missing").length-b.assessment.coverage.filter(r=>r.status==="missing").length||
        a.acquisitionPlan.remainingMaterials.reduce((n,x)=>n+x.count,0)-b.acquisitionPlan.remainingMaterials.reduce((n,x)=>n+x.count,0)||
        (a.planning.forecast.externalAcquisitionCount??Infinity)-(b.planning.forecast.externalAcquisitionCount??Infinity)||(a.planning.forecast.externalAcquisitionKinds??Infinity)-(b.planning.forecast.externalAcquisitionKinds??Infinity)||a.planning.forecast.specialResourceRequirements.length-b.planning.forecast.specialResourceRequirements.length||a.planning.forecast.unverifiedConditionTypes.length-b.planning.forecast.unverifiedConditionTypes.length||
        (a.planning.forecast.choiceDeficit??Infinity)-(b.planning.forecast.choiceDeficit??Infinity)||(combatVerifier?.compareEvidence(a.combatValidation,b.combatValidation)||0);
      const legacyCompareFit=(a,b)=>comparePrefix(a,b)||compareTail(a,b);
      candidates.sort((a,b)=>legacyCompareFit(a,b)||a.id.localeCompare(b.id));
      const rankedCandidates=handFitAdvisor?rerankKnown(candidates,comparePrefix,deck=>deck.handFit?.verified===true?deck.handFit:null,handFitAdvisor.compare):candidates;
      candidates.splice(0,candidates.length,...rankedCandidates);
      const finalRanks=new Map();let tieRank=0;
      candidates.forEach((deck,index)=>{const previous=candidates[index-1],sameEvidence=previous&&legacyCompareFit(previous,deck)===0&&(previous.handFit?.verified===true&&deck.handFit?.verified===true?handFitAdvisor.compare(previous.handFit,deck.handFit)===0:previous.handFit?.verified!==true&&deck.handFit?.verified!==true);if(!sameEvidence)tieRank++;finalRanks.set(deck.id,tieRank);});
      const compareFit=(a,b)=>handFitAdvisor?(finalRanks.get(a.id)-finalRanks.get(b.id)):legacyCompareFit(a,b);
      const automatic=settings.manualDeckChoice===false||!settings.preferredDeckId;
      const incumbent=candidates.find(t=>t.id===selectedId),best=(automatic&&!craftRunning?candidates.find(deck=>deck.recommendationFeasibility.status!=="blocked"):candidates[0])||null;
      let target=incumbent||best,stockReplanned=false,retainedTie=false;
      const blockedAutomatic=automatic&&!craftRunning&&incumbent?.recommendationFeasibility.status==="blocked";
      if(blockedAutomatic)target=best;
      const priorityAutomatic=automatic&&!craftRunning&&!includeKingPathDecks&&incumbent?.recommendationPriority.kingPathDependent===true&&best?.recommendationPriority.kingPathDependent===false&&compareFit(best,incumbent)<0;
      if(priorityAutomatic)target=best;
      if((stockRecompare||conditionRecompare||budgetRecompare||randomRecompare||combatRecompare||kingPathPreferenceRecompare||recordsRecompare)&&incumbent&&best&&!blockedAutomatic){
        if(compareFit(best,incumbent)<0){target=best;stockReplanned=best.id!==incumbent.id;}
        else retainedTie=candidates.some(candidate=>candidate.id!==incumbent.id&&compareFit(candidate,incumbent)===0);
      }
      const bestLeo=preferActualLeo&&!craftRunning?candidates.find(deck=>deck.goals.some(goal=>goal.rawcode==="h0C8")&&deck.recommendationFeasibility.status!=="blocked"&&!incomplete(deck)):null;
      const kingPathTargetSelected=Boolean(bestLeo);
      if(bestLeo){target=bestLeo;if(pendingKingPathTargetSelection)suppressedPreferredId=settings.preferredDeckId||null;}
      if(!craftRunning&&settings.kingPathEntryCompatibility.status==="blocked")target=null;
      if(!craftRunning)pendingKingPathTargetSelection=false;
      selectedId=target?.id||null;
      if(target){target.goalCraftTargets=goalActions(target,inventory,settings);if(craftRunning)target.craftTargets=[];warnings.push(...target.acquisitionPlan.warnings);lastSelectedTemplate=allTemplates.find(template=>template.id===target.id)||null;if(lastSelectedTemplate?.sourceEvidence?.kind==="record_support_adaptation")lastSelectedSupportBase=adaptationBases.get(lastSelectedTemplate.sourceEvidence.baseTemplateId)||lastSelectedSupportBase;else lastSelectedSupportBase=null;}
      else if(!craftRunning){lastSelectedTemplate=null;lastSelectedSupportBase=null;}
      const publicRecommendations=deduplicateAlternatives(candidates.filter(deck=>deck.kingPathConstraint?.entryCompatibility?.status!=="blocked"),target,catalog);
      const reinforcementTargetGoals=target?goalContexts.get(target.id)?.roster.fixedGoals:undefined;
      const reinforcements=reinforcementAdvisor?.recommend(inventory,{...settings,rewardBudget:activeRewardBudget},{mode:target?.mode,targetGoals:reinforcementTargetGoals,targetBasis:target?{deckId:target.id,kind:target.sourceEvidence?.kind||null,recordIds:target.sourceEvidence?.recordIds||[],mapSha256:hash,clearSufficiencyEstablished:false}:null,activeCoreRawcodes:target?.coreRawcodes||[],requiredSupportCounts:Object.fromEntries((target?.goals||[]).filter(goal=>goal.kind==="support"&&(inventory.get(goal.rawcode)||0)>0&&["areaStun","alwaysSlow","armorReduction","magicResistanceReduction","manaSupport"].some(kind=>supports(goal.rawcode,kind).length)).map(goal=>[goal.rawcode,Math.min(goal.count,inventory.get(goal.rawcode))]))});
      return {schemaVersion:1,targetDeck:target,transcendenceLimit:{...transcendenceState(target?.goals,inventory),excludedDeckCount:allTemplates.length-allowed.length},recordDataReevaluation:{revision:recordDataRevision,changed:recordsChanged,performed:recordsRecompare,pending:Boolean(pendingClearRecords)},kingPathPreferenceReevaluation:{mode:kingPathMode,included:includeKingPathDecks,changed:kingPathPreferenceChanged,performed:kingPathPreferenceRecompare,selectedActualLeo:kingPathTargetSelected,pending:pendingKingPathPreferenceReplan||pendingKingPathTargetSelection,entryCompatibility:settings.kingPathEntryCompatibility},randomForecast,randomHistoryReevaluation:{confirmedChange:randomHistoryChanged,performed:randomRecompare,pending:pendingRandomHistoryReplan},rewardBudget:activeRewardBudget,rewardBudgetReevaluation:{confirmedChange:rewardBudgetChanged,performed:budgetRecompare,pending:pendingRewardBudgetReplan},recipeConditionReevaluation:{confirmedChange:confirmedConditionsChanged,performed:conditionRecompare,pending:pendingRecipeConditionReplan,avoidedBlockedAutomatic:blockedAutomatic},feasibilitySelection:{automatic,blockedDeckCount:candidates.filter(deck=>deck.recommendationFeasibility.status==="blocked").length,conditionalDeckCount:candidates.filter(deck=>deck.recommendationFeasibility.status==="conditional").length,noEligibleAutomaticTarget:automatic&&!target},stockInventoryReevaluation:{changed:stockChanged,performed:stockRecompare,changedTarget:stockReplanned,retainedTie,pending:pendingStockRecompare,incumbentCompared:Boolean(incumbent),comparedIncumbentId:incumbent?.id||null},firstInvestmentReevaluation:{pending:pendingFirstInvestmentReplan,performedThisUpdate:firstInvestmentReplanned,reason:firstInvestmentReplanReason},itemInventoryReevaluation:{confirmedChange:itemsChanged,performedThisUpdate:itemReplanned,pending:pendingItemReplan,manualChoiceRespected:itemsChanged&&Boolean(settings.preferredDeckId)&&settings.manualDeckChoice!==false},craftTargets:target?.craftTargets||[],goalCraftTargets:target?.goalCraftTargets||[],order:target?.order||[],selectedDeckId:selectedId,alternatives:publicRecommendations.alternatives,recommendationDeduplication:{policy:"direction_and_original_final_composition",groupCount:publicRecommendations.groupCount,hiddenDuplicateCount:publicRecommendations.duplicateCount},ownedOpportunities,
        mysteriousPreferenceReevaluation:{requested:requestedIncludeMysteriousDecks,included:includeMysteriousDecks,changed:mysteriousPreferenceChanged,pending:requestedIncludeMysteriousDecks!==includeMysteriousDecks},
        recommendationRebase:rebaseInfo?{...rebaseInfo,selectedDeckId:selectedId,performedThisUpdate:upperRebase,deferredNewUpperRawcodes:Array.from(deferredUpper.keys())}:null,
        ownedUpperInvestment:target?.ownedUpperInvestment||null,
        combatScenarioReevaluation:{confirmedChange:combatScenarioChanged,performed:combatRecompare,pending:pendingCombatScenarioReplan},
        recommendationPriorityReevaluation:{performed:priorityAutomatic||kingPathTargetSelected,policy:includeKingPathDecks?"selected_king_path_mode_prefers_actual_leo_goals":"leo_king_path_decks_excluded"},
        recommendationPolicy,supportAdaptationSearch:supportSearch,...(reinforcements?{reinforcements}:{}),recordSelection:recordSet?{compatible:recordSet.compatible,preselection:recordSet.preselection||null,usingRecords,eligibleRecords:recordSet.templates.length,currentEligibleRecords:recordSet.templates.filter(t=>!historical(t)).length,historicalEligibleRecords:recordSet.templates.filter(historical).length,balanceTargetVersion:"2.323",roleSimilarAlternatives:(recordSet.alternatives||[]).length,lockedRecords:recordSet.lockedRecords}:null,warnings,canConfirmClear:false,searchComplete:recordSet?.preselection?.truncated!==true&&(!supportAdapter||!supportSearch.truncated)&&queue.every(template=>candidates.some(c=>c.id===template.id)),evaluatedDeckCount:candidates.length,eligibleDeckCount:allowed.length,planningMs:Date.now()-started};
    }
    function recommendationInputKey(counts,settings){
      const normalized={...(settings||{})};normalized.kingPathMode=normalizeKingPathMode(normalized);normalized.includeKingPathDecks=normalized.kingPathMode!=="off";normalized.includeMysteriousDecks=normalized.includeMysteriousDecks===true;
      if(normalized.manualDeckChoice===false){delete normalized.preferredDeckId;delete normalized.manualDeckChoiceRevision;}
      return semanticKey([counts==null,countsOf(counts),normalized,recordDataRevision,pendingClearRecords?.revision||null]);
    }
    function recommend(counts,settings){
      const key=recommendationInputKey(counts,settings);
      if(lastRecommendation&&key===lastRecommendationKey){
        const replay=semanticValue(lastRecommendation);replay.planningMs=0;replay.recommendationReuse={sameSemanticInput:true};
        if(replay.supportAdaptationSearch?.enabled===true)replay.supportAdaptationSearch.cached=true;
        for(const field of ["recordDataReevaluation","kingPathPreferenceReevaluation","randomHistoryReevaluation","rewardBudgetReevaluation","recipeConditionReevaluation","stockInventoryReevaluation","firstInvestmentReevaluation","itemInventoryReevaluation","mysteriousPreferenceReevaluation","combatScenarioReevaluation","recommendationPriorityReevaluation","recommendationRebase"]){const signal=replay[field];if(signal)for(const name of ["changed","confirmedChange","performed","performedThisUpdate","changedTarget","retainedTie","selectedActualLeo","manualChoiceRespected"])if(typeof signal[name]==="boolean")signal[name]=false;}
        return replay;
      }
      const result=computeRecommendation(counts,settings);lastRecommendation=semanticValue(result);lastRecommendationKey=recommendationInputKey(counts,settings);return semanticValue(result);
    }
    function updateClearRecords(data,revision){
      if(!Records||!validateClearRecords(data,catalog)||typeof revision!=="string"||!/^[a-f0-9]{64}$/.test(revision))return {accepted:false};
      if(revision===recordDataRevision||revision===pendingClearRecords?.revision)return {accepted:true,revision,unchanged:true};
      try{const snapshot=JSON.parse(JSON.stringify(data)),provider=makeRecordProvider(snapshot);pendingClearRecords={data:snapshot,provider,revision,includeMysteriousDecks};return {accepted:true,revision,pending:true};}catch{return {accepted:false};}
    }
    return {recommend,updateClearRecords};
  }
  return {create,validateClearRecords,classifyTranscendence,recommendationGroupKey,deduplicateAlternatives,normalizeKingPathMode};
});






