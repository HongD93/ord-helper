(function(root,factory){const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph,typeof module==="object"&&module.exports?require("./nightmare-records.js"):root.OrdNightmareRecords,typeof module==="object"&&module.exports?require("./nightmare-combat-verifier.js"):root.OrdNightmareCombatVerifier);if(typeof module==="object"&&module.exports)module.exports=api;root.OrdNightmareSupportAdaptation=api;})(typeof globalThis!=="undefined"?globalThis:this,function(Graph,Records,CombatVerifier){
 "use strict";
 const list=x=>Array.isArray(x)?x:[],finite=x=>typeof x==="number"&&Number.isFinite(x),clone=x=>JSON.parse(JSON.stringify(x)),unique=x=>Array.from(new Set(x));
 const allies=new Set(["ally","allies","allied","all_allies","team"]),enemies=new Set(["enemy","enemies","all_enemies","boss","boss_enemies","line_enemies"]);
 const kinds=new Set(["slow","stun","manaRegeneration","manaRecovery","manaAmplification","manaCostReduction","healthRegeneration","attackSpeedBuff","attackDamageBuff","armorReduction","magicResistanceReduction","magicAmplification","allDamageAmplification","explosionAmplification"]);
 const temporary=new Set(["h076","h04T","h04Y","h05D","h0CA"]),damageRoles=new Set(["physicalDps","magicDps","hybridDps"]),purposes=["lineDamage","bossDamage","storyDamage"];
 const preservedFinalTiers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]),finalMaterialProxies=new Set(["h04S","H08T"]);
 const clamp=(x,min,max,fallback)=>Number.isSafeInteger(x)?Math.max(min,Math.min(max,x)):fallback;
 function goalsOf(template){const found=new Map(),add=(rawcode,count)=>{if(typeof rawcode==="string"&&Number.isSafeInteger(count)&&count>0)found.set(rawcode,Math.max(count,found.get(rawcode)||0));};for(const entry of list(template.coreGoals).length?template.coreGoals:list(template.coreRawcodes).map(rawcode=>({rawcode,count:1})))add(entry.rawcode,entry.count);for(const slot of list(template.supportSlots))if(slot.required!==false&&list(slot.preferredRawcodes).length===1)add(slot.preferredRawcodes[0],slot.count||1);return Array.from(found,([rawcode,count])=>({rawcode,count}));}
 function sourceValid(template){const e=template?.sourceEvidence;return e?.kind==="clear_record"&&typeof template.id==="string"&&list(e.recordIds).length>0&&list(e.urls).length>0&&e.compositionChanged!==true&&(!e.goalProjection||Records?.originalClearTemplate?.(template)===true);}
 function create(catalog,combat,options={}){
  const finalGoalPolicyAvailable=typeof Records?.isResidualGoalTier==="function"&&typeof Records?.originalClearTemplate==="function";
  const mapHash=catalog?.map?.sha256,valid=Boolean(mapHash&&combat?.map?.sha256===mapHash&&typeof CombatVerifier?.createNativeManaUsage==="function"),units=new Map(list(catalog?.units).map(u=>[u.rawcode,u]));
  const profiles=valid?combat.units||{}:{},curated=valid?combat.curatedStrategyProfiles?.units||{}:{},modelData=options.unitModels?.map?.sha256===mapHash?options.unitModels.units||{}:{};
  const graph=options.recipeGraph||null,recipes=list(catalog?.recipes),summaries=new Map(),candidateCache=new Map();
  const nativeManaUsage=CombatVerifier?.createNativeManaUsage?.(catalog,combat)||(()=>({usesManaSkills:null,status:"unknown",declarations:[]}));
  const native=code=>!temporary.has(code)&&units.get(code)?.playable===true&&units.get(code)?.isSummon!==true&&profiles[code]?.verified===true&&profiles[code]?.verificationKind==="native";
  const finalCore=code=>preservedFinalTiers.has(units.get(code)?.tier)&&units.get(code)?.playable===true&&units.get(code)?.isSummon!==true&&!temporary.has(code)&&!finalMaterialProxies.has(code);
  const purpose=code=>curated[code]||modelData[code]?.roles||{},main=code=>damageRoles.has(profiles[code]?.damage?.reviewedAnchorRole)||purposes.some(kind=>purpose(code)[kind]?.assessment==="main");
  const referenceEligible=e=>CombatVerifier.isRuntimeReferenceEligible(e);
  const knownEffect=e=>referenceEligible(e)&&kinds.has(e.kind)&&e.evidence&&["tooltip","objectData","script"].includes(e.evidence.type)&&(!finite(e.value)||e.value>=0)&&e.isPenalty!==true;
  const effects=code=>{
   if(!native(code))return [];
   const source=list(profiles[code].effects).filter(e=>knownEffect(e)&&!e.duplicateOf&&(e.kind!=="stun"||e.stunRoleEligible!==false&&e.useForStunRecommendation!==false));
   source.sort((a,b)=>Number(b.evidence.type==="objectData")-Number(a.evidence.type==="objectData"));
   const kept=[],seen=new Set(),values=e=>[e.value,e.durationSeconds,e.chancePercent,e.unit].join("|"),branch=e=>list(e.conditions).some(text=>/강화|특성|레벨|마나|체력|개체.*1회|스택/.test(String(text)));
   for(const e of source){const trigger=[e.abilityRawcode||e.id,e.kind,e.target,e.mode].join("|"),scope=e.scope||"unknown",conditions=list(e.conditions).slice().sort().join("|");
    const duplicateBash=e.kind==="stun"&&e.evidence.type==="tooltip"&&!branch(e)&&kept.some(k=>k.evidence.type==="objectData"&&!branch(k)&&[k.abilityRawcode||k.id,k.kind,k.target,k.mode].join("|")===trigger&&values(k)===values(e)&&(k.scope===scope||scope==="unknown"));
    const key=[trigger,scope,values(e),conditions,e.evidence.field||""].join("|");if(duplicateBash||seen.has(key))continue;seen.add(key);kept.push(e);
   }
   const groups=new Map();for(const e of kept){const key=[e.abilityRawcode||e.id,e.kind,e.target,e.scope,e.mode].join("|");if(!groups.has(key))groups.set(key,[]);groups.get(key).push(e);}
   return kept.map(e=>{const group=groups.get([e.abilityRawcode||e.id,e.kind,e.target,e.scope,e.mode].join("|"));return {...e,supportBranchAmbiguous:group.length>1};});
  };
  function protectedCodes(template){return unique([...list(template.coreRawcodes),...list(template.coreGoals).map(g=>g.rawcode),...goalsOf(template).filter(g=>main(g.rawcode)||finalCore(g.rawcode)).map(g=>g.rawcode)]);}
  function stunReference(sources,field){const groups=new Map();for(const source of sources)if(finite(source[field])){const key=source.branchAmbiguous?[source.rawcode,source.abilityRawcode,source.scope,source.mode].join("|"):source.id;groups.set(key,Math.max(groups.get(key)||0,source[field]*source.count));}return groups.size?Array.from(groups.values()).reduce((sum,value)=>sum+value,0):null;}
  function normalizedGoals(goals){return goals.map(g=>({rawcode:g.rawcode,count:g.count})).sort((a,b)=>a.rawcode.localeCompare(b.rawcode));}
  function assess(goals){
   const normalized=normalizedGoals(goals),key=JSON.stringify(normalized);if(summaries.has(key))return summaries.get(key);
   const utility={},stuns=[],unknown=[],penalties=[],roleRanks={},manaConsumers=[];
   const put=(metric,source)=>{if(!utility[metric])utility[metric]={value:null,sources:[]};const row=utility[metric];row.sources.push(source);if(finite(source.value))row.value=row.value===null?source.value:Math.max(row.value,source.value);};
   for(const goal of normalized){
    const code=goal.rawcode,p=profiles[code],known=native(code),roles=purpose(code);
    if(!known){unknown.push({rawcode:code,kind:"native_profile_unknown"});continue;}
    for(const kind of purposes){const value=roles[kind],rank={main:3,support:2,finisher:1}[value?.assessment];if(rank)roleRanks[kind]=Math.max(roleRanks[kind]||0,rank);}
    const mana=modelData[code]?.mana||curated[code]?.manaDependence;
    const declaredMana=nativeManaUsage(code);
    if(mana?.usesManaSkills===true||list(mana?.verifiedThresholds).length||list(mana?.costs).length||declaredMana.usesManaSkills===true)manaConsumers.push(code);else if(mana?.usesManaSkills!==false)unknown.push({rawcode:code,kind:"mana_usage_unknown"});
    for(const e of list(p.effects)){if(!referenceEligible(e)){unknown.push({rawcode:code,kind:"effect_runtime_condition_unverified",effectId:e.id||e.abilityRawcode,requiredAbilityLevels:e.requiredAbilityLevels||[e.requiredAbilityLevel].filter(finite)});continue;}if(e.isPenalty===true||finite(e.value)&&e.value<0)penalties.push({rawcode:code,id:e.id||e.abilityRawcode,kind:e.kind,value:e.value});}
    for(const e of effects(code)){
     const enemy=enemies.has(e.target),ally=allies.has(e.target);if(!enemy&&!ally)continue;
     const scope=["all","area"].includes(e.scope)?"area":e.scope||"unknown",conditions=list(e.conditions),id=code+":"+(e.id||[e.abilityRawcode,e.kind,scope,e.mode].join(":"));
     const continuous=["always","aura"].includes(e.mode)&&!(finite(e.durationSeconds)&&scope==="single")&&!conditions.some(text=>/발동조건|명중|공격(?:시|할|당)|스킬.*(?:시|이후)|마나\s*\d|체력.*(?:이상|이하)|특성.*(?:강화|이후)/.test(String(text)));
     const numericEligible=CombatVerifier.isNumericReferenceEligible(e);
     const source={id,rawcode:code,abilityRawcode:e.abilityRawcode||null,kind:e.kind,value:numericEligible&&finite(e.value)?e.value:null,declaredValue:finite(e.value)?e.value:null,numericReferenceEligible:numericEligible,useForNumericAggregation:e.useForNumericAggregation??null,scope,mode:e.mode,continuous,conditions,stackingGroup:e.stacking?.group||null,stackingVerified:e.stacking?.verified===true,additiveVerified:e.stacking?.rule==="verified_additive",crossGroupAdditiveVerified:e.stacking?.crossGroupRule==="verified_additive",branchAmbiguous:e.supportBranchAmbiguous===true,stackingRule:e.stacking?.rule||null,count:goal.count,sameUnitCopiesStack:e.stacking?.sameUnitCopiesStack===true,evidenceType:e.evidence.type};
     if(e.supportBranchAmbiguous)unknown.push({rawcode:code,kind:"ability_branch_or_field_interpretation_unverified",abilityRawcode:e.abilityRawcode,effectId:id});
     if(e.kind==="stun"){
      if(scope==="unknown")unknown.push({rawcode:code,kind:"stun_scope_unknown",effectId:id});
      const once=conditions.some(text=>/개체.*1회|개체당.*한\s*번|최초.*1회/.test(String(text))),target=e.targetDurations;
      const declared=modelData[code]?.control?.stuns?.find(s=>CombatVerifier.isNumericReferenceEligible(s)&&s.abilityRawcode===e.abilityRawcode&&s.scope===e.scope&&(s.sourceVerified===true||s.nativeObjectValueVerified===true));
      const general=numericEligible?(target?.unit==="seconds"&&finite(target.general)?target.general:finite(e.durationSeconds)?e.durationSeconds:e.unit==="seconds"&&finite(e.value)?e.value:null):null;
      const hero=numericEligible?(target?.unit==="seconds"&&finite(target.resistantHero)?target.resistantHero:finite(declared?.heroDurationSeconds)?declared.heroDurationSeconds:null):null;
      const interval=finite(modelData[code]?.attackReference?.unmodifiedCooldownSeconds)?modelData[code].attackReference.unmodifiedCooldownSeconds:finite(p.native?.attackCooldown)?p.native.attackCooldown:null;
      const chance=numericEligible&&finite(e.chancePercent)?e.chancePercent:null,periodic=numericEligible?list(curated[code]?.manaDependence?.automaticTriggers).find(t=>t.abilityRawcode===e.abilityRawcode&&t.triggerKind==="automatic_periodic"&&finite(t.intervalSeconds)&&t.intervalSeconds>0):null;
      const frequency=!once&&e.mode==="proc"&&interval>0&&chance!==null&&chance>=0&&chance<=100?chance/100/interval:!once&&periodic?1/periodic.intervalSeconds:null;
      stuns.push({...source,count:goal.count,oncePerTarget:once,chancePercent:chance,baseAttackInterval:interval,generalDuration:general,resistantHeroDuration:hero,referenceActivationsPerSecond:frequency,generalOffered:frequency!==null&&general!==null?frequency*general:null,heroOffered:frequency!==null&&hero!==null?frequency*hero:null});continue;
     }
     const lane=ally?"ally":"enemy",metric=[e.kind,lane,scope,continuous?"continuous":"conditional",e.unit||"unknown"].join("|");
     put(metric,source);
    }
   }
   const control={};for(const scope of ["area","single"]){const sources=stuns.filter(s=>s.scope===scope),metrics={};for(const scenario of ["general","hero"]){const field=scenario==="general"?"generalOffered":"heroOffered",known=sources.filter(s=>finite(s[field])),unresolved=sources.filter(s=>!s.oncePerTarget&&(!finite(s[field])||s.branchAmbiguous));metrics[scenario]={knownOfferedSecondsPerReferenceSecond:stunReference(known,field),strongestSourceReference:known.length?Math.max(...known.map(s=>s[field])):null,unknownSourceIds:unresolved.map(s=>s.id),complete:unresolved.length===0,actualCombinedUptime:null};}control[scope]={sources,metrics};}
   for(const row of Object.values(utility)){for(const source of row.sources)if(source.count>1&&!source.sameUnitCopiesStack)unknown.push({rawcode:source.rawcode,kind:"same_unit_copies_stacking_unverified",effectId:source.id,count:source.count});const groups={};for(const source of row.sources)if(source.additiveVerified&&source.stackingGroup&&source.value!==null)groups[source.stackingGroup]=(groups[source.stackingGroup]||0)+source.value*(source.sameUnitCopiesStack?source.count:1);const cross=row.sources.filter(source=>source.crossGroupAdditiveVerified&&source.value!==null),crossGroups={};for(const source of cross){const key=source.stackingGroup||source.id;crossGroups[key]=source.additiveVerified?(crossGroups[key]||0)+source.value*(source.sameUnitCopiesStack?source.count:1):Math.max(crossGroups[key]||0,source.value);}const references=[row.value,...Object.values(groups),...(cross.length?[Object.values(crossGroups).reduce((a,b)=>a+b,0)]:[])].filter(finite);row.verifiedAdditiveGroups=groups;row.crossGroupAdditiveVerified=cross.length>0;row.knownReferenceLowerBound=references.length?Math.max(...references):null;}
   const result={utility,control,roleRanks,manaConsumers,unknown,penalties,actualEffectiveDps:null,actualRequiredStun:null,actualCombinedSlow:null};if(summaries.size>=512)summaries.clear();summaries.set(key,result);return result;
  }
  function compare(originalGoals,candidateGoals){
   if(!valid)return {accepted:false,status:"native_map_unverified",losses:[{kind:"native_map_unverified"}],metrics:[],unresolved:[],clearOutcomeTransferred:false,performanceEquivalenceVerified:false};
   const before=assess(originalGoals),after=assess(candidateGoals),losses=[],unresolved=[{kind:"effective_damage_not_established"},{kind:"runtime_control_sufficiency_not_established"}],metrics=[];
   for(const [kind,rank]of Object.entries(before.roleRanks))if((after.roleRanks[kind]||0)<rank)losses.push({kind:"damage_role_loss",role:kind,before:rank,after:after.roleRanks[kind]??null});
   for(const [metric,left]of Object.entries(before.utility)){
    const right=after.utility[metric],leftIds=new Set(left.sources.map(s=>s.id));
    if(!right){losses.push({kind:"support_role_loss",metric});continue;}
    const a=left.knownReferenceLowerBound,b=right.knownReferenceLowerBound,comparable=a!==null&&b!==null;metrics.push({metric,before:a,after:b,strongestSourceBefore:left.value,strongestSourceAfter:right.value,verifiedAdditiveGroupsBefore:left.verifiedAdditiveGroups,verifiedAdditiveGroupsAfter:right.verifiedAdditiveGroups,meaning:"verified_lower_reference_or_strongest_source_not_unknown_stacked_total"});
    if(a!==null&&(b===null||b+1e-10<a))losses.push({kind:b===null?"known_support_became_unknown":"support_reference_decreased",metric,before:a,after:b});
    if(right.sources.some(source=>source.branchAmbiguous&&!leftIds.has(source.id))){const safe=right.sources.filter(source=>!source.branchAmbiguous||leftIds.has(source.id)).map(source=>source.value).filter(finite);if(a!==null&&(!safe.length||Math.max(...safe)+1e-10<a))losses.push({kind:"conditional_branch_preservation_unverified",metric});}
    if(!comparable)unresolved.push({kind:"support_value_unknown",metric});
    const removed=left.sources.filter(s=>!right.sources.some(t=>t.id===s.id));if(removed.length)unresolved.push({kind:"removed_source_stacking_or_conditions_unverified",metric,sourceIds:removed.map(s=>s.id),groups:unique(removed.map(s=>s.stackingGroup).filter(Boolean))});
    if(left.sources.length>1||right.sources.length>1)unresolved.push({kind:"support_stacking_not_aggregated",metric});
   }
   for(const scope of ["area","single"]){
    const left=before.control[scope],right=after.control[scope];if(left.sources.length&&!right.sources.length){losses.push({kind:"stun_scope_loss",scope});continue;}
    for(const scenario of ["general","hero"]){const a=left.metrics[scenario],b=right.metrics[scenario],metric="stun|"+scope+"|"+scenario;
     metrics.push({metric,before:a.knownOfferedSecondsPerReferenceSecond,after:b.knownOfferedSecondsPerReferenceSecond,baselineComplete:a.complete,candidateComplete:b.complete,meaning:"partial_offered_seconds_per_reference_second_not_uptime"});
     if(a.knownOfferedSecondsPerReferenceSecond!==null&&(b.knownOfferedSecondsPerReferenceSecond===null||b.knownOfferedSecondsPerReferenceSecond+1e-10<a.knownOfferedSecondsPerReferenceSecond))losses.push({kind:b.complete?"known_stun_reference_decreased":"known_stun_reference_preservation_unverified",scope,scenario,before:a.knownOfferedSecondsPerReferenceSecond,after:b.knownOfferedSecondsPerReferenceSecond});
     const oldIds=new Set(left.sources.map(source=>source.id)),field=scenario==="general"?"generalOffered":"heroOffered",safe=stunReference(right.sources.filter(source=>!source.branchAmbiguous||oldIds.has(source.id)),field);if(right.sources.some(source=>source.branchAmbiguous&&!oldIds.has(source.id))&&a.knownOfferedSecondsPerReferenceSecond!==null&&(safe===null||safe+1e-10<a.knownOfferedSecondsPerReferenceSecond))losses.push({kind:"stun_branch_preservation_unverified",scope,scenario});
     if(!a.complete||!b.complete)unresolved.push({kind:"stun_reference_incomplete",scope,scenario,beforeUnknown:a.unknownSourceIds,afterUnknown:b.unknownSourceIds});
    }
    const removedOnce=left.sources.filter(s=>s.oncePerTarget&&!right.sources.some(t=>t.id===s.id));if(removedOnce.length)unresolved.push({kind:"removed_once_per_target_stun",scope,sourceIds:removedOnce.map(s=>s.id)});
   }
   for(const penalty of after.penalties)if(!before.penalties.some(p=>p.id===penalty.id&&p.rawcode===penalty.rawcode&&p.value===penalty.value))losses.push({kind:"new_native_penalty",...penalty});
   const consumers=new Set([...before.manaConsumers,...after.manaConsumers]);for(const rawcode of consumers){const relevant=summary=>Object.values(summary.utility).flatMap(row=>row.sources).filter(s=>s.kind==="manaRegeneration"&&s.continuous&&s.scope==="area"&&!(s.rawcode===rawcode));const left=relevant(before),right=relevant(after),a=left.some(s=>s.value!==null)?Math.max(...left.filter(s=>s.value!==null).map(s=>s.value)):null,b=right.some(s=>s.value!==null)?Math.max(...right.filter(s=>s.value!==null).map(s=>s.value)):null;if(a!==null&&(b===null||b+1e-10<a))losses.push({kind:"mana_recipient_supply_loss",rawcode,before:a,after:b});}
   unresolved.push(...before.unknown,...after.unknown);return {accepted:losses.length===0,status:losses.length?"known_function_loss":"conditional_support_comparison",losses,metrics,unresolved,actualEffectiveDps:null,actualRequiredStun:null,clearOutcomeTransferred:false,performanceEquivalenceVerified:false};
  }
  function stockOf(counts){return counts instanceof Map?counts:new Map(Object.entries(counts||{}));}
  function pool(counts,settings){
   const stock=stockOf(counts),scores=settings.supportCandidateScores||{},supported=settings.supportedRecipeIds==null?null:new Set(settings.supportedRecipeIds),explicit=list(settings.supportCandidateRawcodes),limit=clamp(settings.supportMaxCandidates,2,16,12);
   const signature=JSON.stringify([Array.from(stock).sort(([a],[b])=>a.localeCompare(b)),settings.wood,settings.gold,explicit,settings.supportCandidateScores||null,supported?Array.from(supported).sort():null,settings.items?.counts||null,settings.recipeConditions?.unitBans||null,settings.recipeConditions?.king?.status||null,settings.recipeConditions?.special?.distortionWoodSurcharge,settings.recipeConditions?.special?.transformationCount,settings.recipeConditions?.special?.skillPoints,settings.recipeConditions?.special?.round,Graph.normalizeRecipeSpecial(settings.recipeConditions?.special,settings.localPlayer,catalog.map?.sha256).nikaEnhancement,limit,settings.includeKingPathDecks===true,settings.includeMysteriousDecks===true]);
   if(candidateCache.has(signature))return candidateCache.get(signature);
   const analysisLimit=clamp(settings.supportCandidateAnalysisLimit,0,32,16);let analyzed=0;const candidateCodes=(explicit.length?explicit:Array.from(units.keys())).slice().sort((a,b)=>Number((stock.get(b)||0)>0)-Number((stock.get(a)||0)>0)||Number(finite(scores[b]?.progressRatio)||finite(scores[b]?.completionProbability))-Number(finite(scores[a]?.progressRatio)||finite(scores[a]?.completionProbability))||a.localeCompare(b));
   const candidates=[];for(const code of candidateCodes){
    if(code==="h0C8"&&settings.includeKingPathDecks!==true||settings.includeMysteriousDecks!==true&&units.get(code)?.tier==="신비"||!native(code)||Records?.isResidualGoalTier?.(units.get(code))===true||main(code)||finalCore(code)||!effects(code).length||scores[code]?.eligible===false)continue;
    const owned=stock.get(code)||0,upgrade=list(combat.curatedStrategyProfiles?.ownedSourceUpgrades).find(u=>u.resultRawcode===code);
    if(!owned&&(list(combat.acquisitionModel?.nonForecastableRawcodes).includes(code)||list(combat.acquisitionModel?.randomOnlyRawcodes).includes(code)||upgrade&&!(stock.get(upgrade.sourceRawcode)>0)))continue;
    const production=recipes.some(r=>r.resultUnitRawcode===code&&Graph?.isProductionRecipe(r)&&(!supported||supported.has(String(r.id))));if(!owned&&!production)continue;
    let progress=finite(scores[code]?.progressRatio)?Math.max(0,Math.min(1,scores[code].progressRatio)):owned?1:null;
    if(progress===null&&graph?.analyze&&analyzed<analysisLimit){analyzed++;try{const result=graph.analyze(code,stock,undefined,{resources:{wood:settings.wood,gold:settings.gold},items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:settings.localPlayer});progress=finite(result?.ratio)?Math.max(0,Math.min(1,result.ratio)):null;}catch{}}
    const probability=finite(scores[code]?.completionProbability)&&scores[code].completionProbability>=0&&scores[code].completionProbability<=1?scores[code].completionProbability:null;
    if(!owned&&!(progress>0)&&probability===null)continue;
    candidates.push({rawcode:code,owned,progressRatio:progress,completionProbability:probability,roleKeys:new Set(Object.keys(assess([{rawcode:code,count:1}]).utility))});
   }
   candidates.sort((a,b)=>Number(b.owned>0)-Number(a.owned>0)||(a.completionProbability!==null&&b.completionProbability!==null?b.completionProbability-a.completionProbability:0)||(a.progressRatio!==null&&b.progressRatio!==null?b.progressRatio-a.progressRatio:0)||a.rawcode.localeCompare(b.rawcode));const result=candidates.slice(0,limit);if(candidateCache.size>=8)candidateCache.clear();candidateCache.set(signature,result);return result;
  }
  function structural(candidate,original){
   const evidence=candidate?.sourceEvidence,changes=list(evidence?.supportReplacements),protectedSet=new Set(protectedCodes(original));
   if(!finalGoalPolicyAvailable||!sourceValid(original)||evidence?.kind!=="record_support_adaptation"||evidence.baseTemplateId!==original.id||!candidate.id?.startsWith(original.id+"--support-")||changes.length<1||changes.length>2||evidence.clearOutcomeTransferred!==false||evidence.performanceEquivalenceVerified!==false||evidence.compositionChanged!==true||JSON.stringify(candidate.source)!==JSON.stringify(evidence)||candidate.id!==original.id+"--support-"+changes.map(r=>r.originalRawcode+"x"+r.count+"-"+r.replacementRawcode+"x"+r.count).join("-"))return false;
   for(const field of ["coreRawcodes","coreGoals","coreComposition","requiredCoverage","recordId","direction","recordPresenceGoals","availableWhen","requiredOwnedMaterials","conditions","noTranscendentUnits","transcendencePlan"])if(JSON.stringify(candidate[field])!==JSON.stringify(original[field]))return false;
   for(const field of ["recordIds","urls","mapping","goalProjection"])if(JSON.stringify(evidence[field])!==JSON.stringify(original.sourceEvidence[field]))return false;
   const expected=clone(list(original.supportSlots)),changed=new Set(),originalCodes=new Set(goalsOf(original).map(g=>g.rawcode)),replacements=new Set();for(const change of changes){const slot=expected.find(s=>s.id===change.slotId);if(!slot||changed.has(slot.id)||slot.required===false||list(slot.preferredRawcodes).length!==1||slot.preferredRawcodes[0]!==change.originalRawcode||protectedSet.has(change.originalRawcode)||change.originalRawcode===change.replacementRawcode||originalCodes.has(change.replacementRawcode)||replacements.has(change.replacementRawcode)||!native(change.replacementRawcode)||main(change.replacementRawcode)||(slot.count||1)!==change.count)return false;slot.preferredRawcodes=[change.replacementRawcode];slot.alternatives=[];changed.add(slot.id);replacements.add(change.replacementRawcode);}
   if(goalsOf(original).some(goal=>Records?.isResidualGoalTier?.(units.get(goal.rawcode))===true)||goalsOf(candidate).some(goal=>Records?.isResidualGoalTier?.(units.get(goal.rawcode))===true))return false;
   if(JSON.stringify(normalizedGoals(goalsOf(candidate).filter(goal=>finalCore(goal.rawcode))))!==JSON.stringify(normalizedGoals(goalsOf(original).filter(goal=>finalCore(goal.rawcode)))))return false;
   return JSON.stringify(candidate.supportSlots)===JSON.stringify(expected);
  }
  function isAdaptation(candidate,original){return valid&&structural(candidate,original)&&compare(goalsOf(original),goalsOf(candidate)).accepted;}
  function adapt(template,counts,settings={}){
   if(settings.includeMysteriousDecks!==true&&[...goalsOf(template||{}),...list(template?.sourceEvidence?.mapping),...list(template?.goalProjection?.excluded),...list(template?.sourceEvidence?.goalProjection?.excluded)].some(row=>[row?.rawcode,...list(row?.candidateRawcodes)].some(code=>units.get(code)?.tier==="신비")))return {templates:[],comparisons:[],protectedRawcodes:protectedCodes(template||{}),search:{evaluated:0,limitReached:false}};
   const protectedRawcodes=protectedCodes(template||{}),empty={templates:[],comparisons:[],protectedRawcodes,search:{evaluated:0,limitReached:false}};if(!valid||!finalGoalPolicyAvailable||!sourceValid(template)||goalsOf(template).some(goal=>Records?.isResidualGoalTier?.(units.get(goal.rawcode))===true)||settings.includeKingPathDecks!==true&&(goalsOf(template).some(goal=>goal.rawcode==="h0C8")||list(template.sourceEvidence?.mapping).some(entry=>entry.rawcode==="h0C8")))return empty;
   const stock=stockOf(counts),protectedSet=new Set(protectedRawcodes),originalGoals=goalsOf(template),originalCodes=new Set(originalGoals.map(g=>g.rawcode));
   const slots=list(template.supportSlots).filter(slot=>slot.required!==false&&list(slot.preferredRawcodes).length===1&&!protectedSet.has(slot.preferredRawcodes[0])&&native(slot.preferredRawcodes[0])&&effects(slot.preferredRawcodes[0]).length).sort((a,b)=>Number((stock.get(a.preferredRawcodes[0])||0)>0)-Number((stock.get(b.preferredRawcodes[0])||0)>0)||String(a.id).localeCompare(String(b.id)));
   const candidates=pool(stock,settings).filter(c=>!originalCodes.has(c.rawcode)),maximum=clamp(settings.supportMaxComparisons,4,96,64),variantLimit=clamp(settings.supportMaxVariants,1,6,4),singles=[],comparisons=[],variants=[],seen=new Set();let evaluated=0;
   const choices=slots.map(slot=>{const reference=assess([{rawcode:slot.preferredRawcodes[0],count:slot.count||1}]),keys=Object.keys(reference.utility);return {slot,candidates:candidates.slice().sort((a,b)=>keys.filter(k=>b.roleKeys.has(k)).length-keys.filter(k=>a.roleKeys.has(k)).length||Number(b.owned>0)-Number(a.owned>0)||a.rawcode.localeCompare(b.rawcode))};});
   const tryChanges=changes=>{
    if(evaluated>=maximum)return;const key=changes.map(c=>c.slot.id+":"+c.candidate.rawcode).sort().join("|");if(seen.has(key))return;seen.add(key);evaluated++;
    const result=clone(template),replacements=changes.map(({slot,candidate})=>({slotId:slot.id,originalRawcode:slot.preferredRawcodes[0],replacementRawcode:candidate.rawcode,count:slot.count||1}));
    for(const change of replacements){const slot=result.supportSlots.find(s=>s.id===change.slotId);slot.preferredRawcodes=[change.replacementRawcode];slot.alternatives=[];}
    const comparison=compare(originalGoals,goalsOf(result));comparisons.push({replacements,accepted:comparison.accepted,losses:comparison.losses});if(!comparison.accepted)return;
    const evidence={...clone(template.sourceEvidence),kind:"record_support_adaptation",baseTemplateId:template.id,baseSourceKind:"clear_record",compositionChanged:true,clearOutcomeTransferred:false,performanceEquivalenceVerified:false,supportReplacements:replacements,supportComparison:comparison};
    result.id=template.id+"--support-"+replacements.map(r=>r.originalRawcode+"x"+r.count+"-"+r.replacementRawcode+"x"+r.count).join("-");result.name="[패 반영] "+String(template.name||template.id).replace(/^\[[^\]]+\]\s*/,"");result.sourceEvidence=evidence;result.source=evidence;result.evidenceScope="record_core_preserved_support_adaptation_not_replayed";
    result.supportAdaptationRank={ownedReplacementCount:changes.filter(c=>(stock.get(c.candidate.rawcode)||0)>=(c.slot.count||1)).length,progressRatio:changes.every(c=>c.candidate.progressRatio!==null)?changes.reduce((sum,c)=>sum+c.candidate.progressRatio,0)/changes.length:null,completionProbability:changes.every(c=>c.candidate.completionProbability!==null)?Math.min(...changes.map(c=>c.candidate.completionProbability)):null,unresolvedCount:comparison.unresolved.length};
    variants.push(result);
   };
   for(let i=0;i<choices.length;i++)for(const candidate of choices[i].candidates){singles.push({slot:choices[i].slot,candidate});}
   const singleBudget=Math.min(maximum,Math.ceil(maximum*.55));for(const change of singles.slice(0,singleBudget))tryChanges([change]);
   outer:for(let i=0;i<choices.length;i++)for(let j=i+1;j<choices.length;j++)for(const a of choices[i].candidates.slice(0,4))for(const b of choices[j].candidates.slice(0,4)){if(evaluated>=maximum)break outer;if(a.rawcode===b.rawcode)continue;tryChanges([{slot:choices[i].slot,candidate:a},{slot:choices[j].slot,candidate:b}]);}
   variants.sort((a,b)=>b.supportAdaptationRank.ownedReplacementCount-a.supportAdaptationRank.ownedReplacementCount||(a.supportAdaptationRank.completionProbability!==null&&b.supportAdaptationRank.completionProbability!==null?b.supportAdaptationRank.completionProbability-a.supportAdaptationRank.completionProbability:0)||(a.supportAdaptationRank.progressRatio!==null&&b.supportAdaptationRank.progressRatio!==null?b.supportAdaptationRank.progressRatio-a.supportAdaptationRank.progressRatio:0)||a.supportAdaptationRank.unresolvedCount-b.supportAdaptationRank.unresolvedCount||a.id.localeCompare(b.id));
   return {templates:variants.slice(0,variantLimit),comparisons,protectedRawcodes,search:{evaluated,maxComparisons:maximum,maxCandidates:clamp(settings.supportMaxCandidates,2,16,12),limitReached:evaluated>=maximum,bounded:true}};
  }
  return Object.freeze({adapt,isAdaptation,assess,compare,protectedRawcodes:protectedCodes});
 }
 return Object.freeze({create,goalsOf});
});
