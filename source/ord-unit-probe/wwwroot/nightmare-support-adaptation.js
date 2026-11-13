(function(root,factory){const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph,typeof module==="object"&&module.exports?require("./nightmare-records.js"):root.OrdNightmareRecords,typeof module==="object"&&module.exports?require("./nightmare-combat-verifier.js"):root.OrdNightmareCombatVerifier);if(typeof module==="object"&&module.exports)module.exports=api;root.OrdNightmareSupportAdaptation=api;})(typeof globalThis!=="undefined"?globalThis:this,function(Graph,Records,CombatVerifier){
 "use strict";
 const list=x=>Array.isArray(x)?x:[],finite=x=>typeof x==="number"&&Number.isFinite(x),clone=x=>JSON.parse(JSON.stringify(x)),unique=x=>Array.from(new Set(x));
 const allies=new Set(["ally","allies","allied","all_allies","team"]),enemies=new Set(["enemy","enemies","all_enemies","boss","boss_enemies","line_enemies"]);
 const kinds=new Set(["slow","stun","manaRegeneration","manaRecovery","manaAmplification","manaCostReduction","healthRegeneration","attackSpeedBuff","attackDamageBuff","armorReduction","magicResistanceReduction","magicAmplification","allDamageAmplification","explosionAmplification"]);
 const temporary=new Set(["h076","h04T","h04Y","h05D","h0CA"]),damageRoles=new Set(["physicalDps","magicDps","hybridDps"]),purposes=["lineDamage","bossDamage","storyDamage"];
 const preservedFinalTiers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]),finalMaterialProxies=new Set(["h04S","H08T"]);
 const clamp=(x,min,max,fallback)=>Number.isSafeInteger(x)?Math.max(min,Math.min(max,x)):fallback;
 function targetExclusionsPreserved(required,candidate){const read=source=>source?.targetExclusions===undefined?[]:Array.isArray(source.targetExclusions)&&source.targetExclusions.every(value=>typeof value==="string"&&value.trim())?source.targetExclusions:null,before=read(required),after=read(candidate);return before!==null&&after!==null&&after.every(value=>before.includes(value));}
 function goalsOf(template){const found=new Map(),add=(rawcode,count)=>{if(typeof rawcode==="string"&&Number.isSafeInteger(count)&&count>0)found.set(rawcode,Math.max(count,found.get(rawcode)||0));};for(const entry of list(template.coreGoals).length?template.coreGoals:list(template.coreRawcodes).map(rawcode=>({rawcode,count:1})))add(entry.rawcode,entry.count);for(const slot of list(template.supportSlots))if(slot.required!==false&&list(slot.preferredRawcodes).length===1)add(slot.preferredRawcodes[0],slot.count||1);return Array.from(found,([rawcode,count])=>({rawcode,count}));}
 function sourceValid(template,catalog){const e=template?.sourceEvidence,local=e?.sourceProvider==="local-native"&&Records?.originalClearTemplate?.(template,catalog)===true;return e?.kind==="clear_record"&&typeof template.id==="string"&&list(e.recordIds).length>0&&(list(e.urls).length>0||local)&&e.compositionChanged!==true&&(!e.goalProjection||Records?.originalClearTemplate?.(template,catalog)===true);}
 function create(catalog,combat,options={}){
  const finalGoalPolicyAvailable=typeof Records?.isResidualGoalTier==="function"&&typeof Records?.originalClearTemplate==="function";
  const mapHash=catalog?.map?.sha256,valid=Boolean(mapHash&&combat?.map?.sha256===mapHash&&typeof CombatVerifier?.createNativeManaUsage==="function"),units=new Map(list(catalog?.units).map(u=>[u.rawcode,u]));
  const profiles=valid?combat.units||{}:{},curated=valid?combat.curatedStrategyProfiles?.units||{}:{},modelData=options.unitModels?.map?.sha256===mapHash?options.unitModels.units||{}:{};
  const graph=options.recipeGraph||null,recipes=list(catalog?.recipes),summaries=new Map(),candidateCache=new Map();
  const nativeManaUsage=CombatVerifier?.createNativeManaUsage?.(catalog,combat)||(()=>({usesManaSkills:null,status:"unknown",declarations:[]}));
  const randomCodes=new Set([...list(combat?.acquisitionModel?.randomOnlyRawcodes),...list(catalog?.units).filter(unit=>unit.tier==="랜덤전용").map(unit=>unit.rawcode)]);
  const randomOnly=(code,template)=>randomCodes.has(code)||list(template?.sourceEvidence?.randomOnlyRawcodes).includes(code);
  const native=code=>!temporary.has(code)&&units.get(code)?.playable===true&&units.get(code)?.isSummon!==true&&units.get(code)?.isSummoned!==true&&profiles[code]?.verified===true&&profiles[code]?.verificationKind==="native";
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
  function protectedCodes(template){return unique([...list(template.coreRawcodes),...list(template.coreGoals).map(g=>g.rawcode),...goalsOf(template).filter(g=>!randomOnly(g.rawcode,template)&&main(g.rawcode)||finalCore(g.rawcode)).map(g=>g.rawcode)]);}
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
     const source={id,rawcode:code,abilityRawcode:e.abilityRawcode||null,kind:e.kind,value:numericEligible&&finite(e.value)?e.value:null,declaredValue:finite(e.value)?e.value:null,numericReferenceEligible:numericEligible,useForNumericAggregation:e.useForNumericAggregation??null,scope,target:e.target,targetExclusions:e.targetExclusions,range:finite(e.range)&&e.range>=0?e.range:null,unit:e.unit||"unknown",durationSeconds:finite(e.durationSeconds)?e.durationSeconds:null,chancePercent:finite(e.chancePercent)?e.chancePercent:null,mode:e.mode,continuous,conditions,stackingGroup:e.stacking?.group||null,stackingVerified:e.stacking?.verified===true,additiveVerified:e.stacking?.rule==="verified_additive",crossGroupAdditiveVerified:e.stacking?.crossGroupRule==="verified_additive",branchAmbiguous:e.supportBranchAmbiguous===true,stackingRule:e.stacking?.rule||null,count:goal.count,sameUnitCopiesStack:e.stacking?.sameUnitCopiesStack===true,evidenceType:e.evidence.type};
     if(e.supportBranchAmbiguous)unknown.push({rawcode:code,kind:"ability_branch_or_field_interpretation_unverified",abilityRawcode:e.abilityRawcode,effectId:id});
     if(e.kind==="stun"){
      if(scope==="unknown")unknown.push({rawcode:code,kind:"stun_scope_unknown",effectId:id});
      const once=conditions.some(text=>/개체.*1회|개체당.*한\s*번|최초.*1회/.test(String(text))),target=e.targetDurations;
      const declared=modelData[code]?.control?.stuns?.find(s=>CombatVerifier.isNumericReferenceEligible(s)&&s.abilityRawcode===e.abilityRawcode&&s.scope===e.scope&&(s.sourceVerified===true||s.nativeObjectValueVerified===true));
      const general=numericEligible?(target?.unit==="seconds"&&finite(target.general)?target.general:finite(e.durationSeconds)?e.durationSeconds:e.unit==="seconds"&&finite(e.value)?e.value:null):null;
      const hero=numericEligible?(target?.unit==="seconds"&&finite(target.resistantHero)?target.resistantHero:finite(declared?.heroDurationSeconds)?declared.heroDurationSeconds:null):null;
      const intervalReference=CombatVerifier.attackIntervalReference?.(modelData[code]?.attackReference),interval=finite(intervalReference)?intervalReference:finite(modelData[code]?.attackReference?.unmodifiedCooldownSeconds)?modelData[code].attackReference.unmodifiedCooldownSeconds:finite(p.native?.attackCooldown)?p.native.attackCooldown:null;
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
   const result={utility,control,roleRanks,manaConsumers,unknown,penalties,damageSources:normalized.filter(goal=>native(goal.rawcode)).map(goal=>damageReferences(goal.rawcode,goal.count)),actualEffectiveDps:null,actualRequiredStun:null,actualCombinedSlow:null};if(summaries.size>=512)summaries.clear();summaries.set(key,result);return result;
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
  const sortedConditions=source=>list(source.conditions).map(String).sort();
  const targetOf=source=>allies.has(source.target)?"ally":["enemy","enemies","all_enemies"].includes(source.target)?"enemy":source.target||"unknown";
  const activationOf=source=>JSON.stringify([source.continuous?"continuous":source.mode,source.continuous?null:source.chancePercent,source.continuous?null:source.durationSeconds,sortedConditions(source)]);
  function referenceValue(sources){
   const known=sources.filter(source=>source.numericReferenceEligible&&!source.branchAmbiguous&&finite(source.value)),groups=new Map(),cross=new Map();
   for(const source of known){const value=source.value*(source.sameUnitCopiesStack?source.count:1);if(source.additiveVerified&&source.stackingGroup)groups.set(source.stackingGroup,(groups.get(source.stackingGroup)||0)+value);if(source.crossGroupAdditiveVerified){const key=source.stackingGroup||source.id;cross.set(key,source.additiveVerified?(cross.get(key)||0)+value:Math.max(cross.get(key)||0,value));}}
   return known.length?Math.max(...known.map(source=>source.value),...groups.values(),...(cross.size?[Array.from(cross.values()).reduce((a,b)=>a+b,0)]:[])):null;
  }
  function damageReferences(code,count){
   const model=modelData[code],damage=model?.damage,attack=model?.attackReference;
   const components=list(damage?.components).filter(component=>component.sourceVerified===true&&component.numericFactsVerified!==false&&referenceEligible(component)).map(component=>{
    const reference=CombatVerifier.componentDamageReference?.(component,attack);
    return {...(reference||component),rawcode:code,count,referenceAvailable:Boolean(reference),actualEffectiveDps:null};
   });
   const interval=CombatVerifier.attackIntervalReference?.(attack),basicKnown=attack?.referenceOnly===true&&list(attack.sourceRefs).length>0&&finite(attack.meanDamage)&&attack.meanDamage>=0&&finite(interval)&&interval>0&&typeof attack.attackType==="string"&&attack.targetCount===1;
   if(basicKnown&&attack.meanDamage>0)components.unshift({rawcode:code,count,abilityRawcode:"native-basic-attack",isBaseAttack:true,sourceVerified:true,numericFactsVerified:true,referenceAvailable:true,referenceFlatDps:attack.meanDamage/interval,referenceMaxHealthFractionPerSecond:null,referenceCurrentHealthFractionPerSecond:null,referenceCooldownFlatDps:null,damageType:attack.attackType==="magic"?"magic":"physical",attackType:attack.attackType,scope:"single",target:"enemy",radius:finite(attack.attackRange)?attack.attackRange:null,conditions:[],trigger:{kind:"native_basic_attack"},actualEffectiveDps:null});
   return {rawcode:code,count,known:Boolean(model),basicAttackKnown:basicKnown,hasBasicAttack:finite(attack?.meanDamage)&&attack.meanDamage>0,complete:damage?.complete===true,unknownMajorDamage:damage?.unknownMajorDamage!==false,components};
  }
  // 원본 랜덤 유닛을 지울 때는 역할명만 맞추지 않고 검증된 대상·범위·발동 조건을 보존한다.
  function randomRequirements(originalGoals,candidateGoals,removedRawcodes){
   const removed=new Set(removedRawcodes),before=assess(originalGoals.filter(goal=>removed.has(goal.rawcode))),after=assess(candidateGoals),requirements=[],unresolved=[],losses=[];
   const offered=(sources,field)=>sources.length?stunReference(sources,field):null;
   const comparable=(required,source,stun=false)=>source.numericReferenceEligible&&!source.branchAmbiguous&&source.kind===required.kind&&targetOf(source)===targetOf(required)&&targetExclusionsPreserved(required,source)&&source.scope===required.scope&&source.scope!=="unknown"&&
    JSON.stringify(sortedConditions(source))===JSON.stringify(sortedConditions(required))&&
    (stun?source.mode===required.mode:source.unit===required.unit&&activationOf(source)===activationOf(required))&&
    (required.scope==="single"||finite(required.range)&&finite(source.range)&&source.range>=required.range);
   for(const row of Object.values(before.utility))for(const required of row.sources){
    const retained=Object.values(after.utility).flatMap(value=>value.sources).some(source=>source.id===required.id&&source.count>=required.count);
    if(retained)continue;
    const candidates=Object.values(after.utility).flatMap(value=>value.sources).filter(source=>comparable(required,source));
    const known=required.numericReferenceEligible&&!required.branchAmbiguous&&finite(required.value)&&required.scope!=="unknown"&&(required.scope==="single"||finite(required.range))&&(required.continuous||finite(required.durationSeconds));
    const need=required.value*(required.sameUnitCopiesStack?required.count:1),have=referenceValue(candidates),ready=known&&have!==null&&have+1e-10>=need;
    const result={kind:required.kind,rawcode:required.rawcode,effectId:required.id,scope:required.scope,target:required.target,range:required.range,conditions:required.conditions,requiredReference:known?need:null,candidateReference:have,status:ready?"reference_preserved":known?"reference_missing":"reference_unknown",referenceOnly:true};requirements.push(result);if(!ready)(known?losses:unresolved).push(result);
   }
   for(const scope of ["area","single"])for(const required of before.control[scope].sources){
    if(after.control[scope].sources.some(source=>source.id===required.id&&source.count>=required.count))continue;
    const sources=after.control[scope].sources.filter(source=>comparable(required,source,true));
    for(const scenario of ["general","hero"]){const field=scenario==="general"?"generalOffered":"heroOffered",known=required.numericReferenceEligible&&!required.branchAmbiguous&&!required.oncePerTarget&&finite(required[field])&&(scope==="single"||finite(required.range)),need=known?required[field]*required.count:null,have=offered(sources,field),ready=known&&have!==null&&have+1e-10>=need;
     const result={kind:"stun",scenario,rawcode:required.rawcode,effectId:required.id,scope,target:required.target,range:required.range,conditions:required.conditions,requiredReference:need,candidateReference:have,status:ready?"reference_preserved":known?"reference_missing":"reference_unknown",referenceOnly:true};requirements.push(result);if(!ready)(known?losses:unresolved).push(result);}
   }
   // 유지한 주력의 기존 기여를 빠진 랜덤의 대체분으로 다시 쓰지 않는다.
   const originalCounts=new Map(originalGoals.map(goal=>[goal.rawcode,goal.count]));
   const candidateDamage=candidateGoals.flatMap(goal=>{const added=Math.max(0,goal.count-(originalCounts.get(goal.rawcode)||0));return added?damageReferences(goal.rawcode,added).components:[];});
   const spentDamage=new Map();
   for(const goal of originalGoals.filter(goal=>removed.has(goal.rawcode))){
    if(!native(goal.rawcode)){unresolved.push({kind:"random_native_profile_unknown",rawcode:goal.rawcode});continue;}
    const reference=damageReferences(goal.rawcode,goal.count),hasDamageDeclaration=reference.hasBasicAttack||reference.components.length>0||list(modelData[goal.rawcode]?.damage?.declaredExpressions).length>0||damageRoles.has(profiles[goal.rawcode]?.damage?.reviewedAnchorRole);
    if(hasDamageDeclaration&&(!reference.known||reference.unknownMajorDamage))unresolved.push({kind:"random_damage_components_incomplete",rawcode:goal.rawcode,actualEffectiveDps:null});
    if(reference.hasBasicAttack&&!reference.basicAttackKnown)unresolved.push({kind:"random_basic_attack_reference_unknown",rawcode:goal.rawcode,actualEffectiveDps:null});
    for(const component of reference.components){
     const range=component.radius,conditions=JSON.stringify(sortedConditions(component)),key=source=>[source.damageType,source.attackType||"",source.scope,targetOf(source),source.trigger?.kind].join("|");
     const candidates=candidateDamage.map((source,index)=>({source,index})).filter(({source})=>source.referenceAvailable&&key(source)===key(component)&&targetExclusionsPreserved(component,source)&&JSON.stringify(sortedConditions(source))===conditions&&(finite(range)?finite(source.radius)&&source.radius>=range:component.scope==="single"&&!component.isBaseAttack));
     for(const field of ["referenceFlatDps","referenceMaxHealthFractionPerSecond","referenceCurrentHealthFractionPerSecond","referenceCooldownFlatDps"]){
      if(!finite(component[field])||component[field]<=0)continue;
      const need=component[field]*goal.count,available=candidates.filter(({source})=>finite(source[field])).map(row=>({...row,key:row.index+"|"+field,remaining:Math.max(0,row.source[field]*row.source.count-(spentDamage.get(row.index+"|"+field)||0))})),have=available.reduce((sum,row)=>sum+row.remaining,0),ready=have+1e-10>=need;let remaining=need;
      for(const row of available){const used=Math.min(remaining,row.remaining);spentDamage.set(row.key,(spentDamage.get(row.key)||0)+used);remaining-=used;if(remaining<=1e-10)break;}
      const result={kind:"support_damage",rawcode:goal.rawcode,abilityRawcode:component.abilityRawcode||null,damageType:component.damageType,scope:component.scope,target:component.target,range,conditions:component.conditions,metric:field,requiredReference:need,candidateReference:have,status:ready?"reference_preserved":"reference_missing",referenceOnly:true,actualEffectiveDps:null};requirements.push(result);if(!ready)losses.push(result);
     }
     if(!component.referenceAvailable||!["referenceFlatDps","referenceMaxHealthFractionPerSecond","referenceCurrentHealthFractionPerSecond","referenceCooldownFlatDps"].some(field=>finite(component[field])&&component[field]>0))unresolved.push({kind:"random_damage_activation_unknown",rawcode:goal.rawcode,abilityRawcode:component.abilityRawcode||null});
    }
    if(!requirements.some(row=>row.rawcode===goal.rawcode)&&!hasDamageDeclaration)unresolved.push({kind:"random_support_role_unknown",rawcode:goal.rawcode});
    for(const unknown of before.unknown.filter(row=>row.rawcode===goal.rawcode&&!['mana_usage_unknown'].includes(row.kind)))unresolved.push({...unknown,kind:"random_"+unknown.kind});
   }
   const damageUnknown=new Set(["random_damage_components_incomplete","random_damage_activation_unknown"]),accepted=losses.length===0&&requirements.length>0&&unresolved.every(row=>damageUnknown.has(row.kind));
   return {accepted,status:losses.length?"known_random_function_loss":accepted&&unresolved.length?"conditional_native_reference_preserved":unresolved.length?"random_function_unverified":"native_reference_preserved",coverageComplete:accepted&&unresolved.length===0,unresolvedOriginalRandomRawcodes:unique(unresolved.map(row=>row.rawcode).filter(Boolean)),requirements,losses,unresolved,actualEffectiveDps:null,clearOutcomeTransferred:false,performanceEquivalenceVerified:false};
  }
  function stockOf(counts){return counts instanceof Map?counts:new Map(Object.entries(counts||{}));}
  function pool(counts,settings){
   const stock=stockOf(counts),scores=settings.supportCandidateScores||{},supported=settings.supportedRecipeIds==null?null:new Set(settings.supportedRecipeIds),explicit=list(settings.supportCandidateRawcodes),limit=clamp(settings.supportMaxCandidates,2,16,12);
   const signature=JSON.stringify([Array.from(stock).sort(([a],[b])=>a.localeCompare(b)),settings.wood,settings.gold,explicit,settings.supportCandidateScores||null,supported?Array.from(supported).sort():null,settings.items?.counts||null,settings.recipeConditions?.unitBans||null,settings.recipeConditions?.king?.status||null,settings.recipeConditions?.special?.distortionWoodSurcharge,settings.recipeConditions?.special?.transformationCount,settings.recipeConditions?.special?.skillPoints,settings.recipeConditions?.special?.round,Graph.normalizeRecipeSpecial(settings.recipeConditions?.special,settings.localPlayer,catalog.map?.sha256).nikaEnhancement,limit,settings.includeKingPathDecks===true,settings.includeMysteriousDecks===true,settings.allowResidualSupportCandidates===true]);
   if(candidateCache.has(signature))return candidateCache.get(signature);
   const analysisLimit=clamp(settings.supportCandidateAnalysisLimit,0,32,16);let analyzed=0;const candidateCodes=(explicit.length?explicit:Array.from(units.keys())).slice().sort((a,b)=>Number((stock.get(b)||0)>0)-Number((stock.get(a)||0)>0)||Number(finite(scores[b]?.progressRatio)||finite(scores[b]?.completionProbability))-Number(finite(scores[a]?.progressRatio)||finite(scores[a]?.completionProbability))||a.localeCompare(b));
   const candidates=[];for(const code of candidateCodes){
    if(code==="h0C8"&&settings.includeKingPathDecks!==true||settings.includeMysteriousDecks!==true&&units.get(code)?.tier==="신비"||!native(code)||Records?.isResidualGoalTier?.(units.get(code))===true&&settings.allowResidualSupportCandidates!==true||main(code)&&!randomOnly(code)||finalCore(code)||!effects(code).length&&!damageReferences(code,1).components.length||scores[code]?.eligible===false)continue;
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
  function replacementGoals(change){return Array.isArray(change.replacementGoals)?change.replacementGoals:[{rawcode:change.replacementRawcode,count:change.count}];}
  function replacementId(base,changes){return base+"--support-"+changes.map(change=>change.originalRawcode+"x"+change.count+"-"+replacementGoals(change).map(goal=>goal.rawcode+"x"+goal.count).join("-and-")).join("-");}
  function replaceSlots(slots,changes){
   const result=clone(list(slots));for(const change of changes){const at=result.findIndex(slot=>slot.id===change.slotId),slot=result[at];if(!slot)return null;
    result.splice(at,1,...replacementGoals(change).map((goal,index)=>({...slot,id:index?slot.id+"--replacement-"+(index+1):slot.id,count:goal.count,preferredRawcodes:[goal.rawcode],alternatives:[]})));}
   return result;
  }
  function missingRandomSupport(template,counts){const stock=stockOf(counts);return goalsOf(template||{}).filter(goal=>randomOnly(goal.rawcode,template)&&(stock.get(goal.rawcode)||0)<goal.count).map(goal=>({...goal,owned:stock.get(goal.rawcode)||0,missing:goal.count-(stock.get(goal.rawcode)||0),status:"original_random_unit_missing",randomAcquisitionAssumed:false,clearOutcomeTransferred:false}));}
  function currentReplacementProof(goals,stock,protectedRawcodes,settings){
   if(goals.every(goal=>(stock.get(goal.rawcode)||0)>=goal.count))return {executable:true,steps:[],stepCount:0,ownedOnly:true,randomAcquisitionAssumed:false};
   if(!graph?.planDeck)return {executable:false,reason:"replacement_plan_unavailable"};
   if(!Array.isArray(settings.supportedRecipeIds)&&!(settings.supportedRecipeIds instanceof Set))return {executable:false,reason:"replacement_commands_unverified"};
   const locks=new Map();for(const code of protectedRawcodes)if((stock.get(code)||0)>0)locks.set(code,stock.get(code));
   for(const [code,count]of stockOf(settings.protectedCounts))if(Number.isSafeInteger(count)&&count>0)locks.set(code,Math.max(count,locks.get(code)||0));
   let plan;try{plan=graph.planDeck(goals,stock,{supportedRecipeIds:settings.supportedRecipeIds,allowChoiceWisps:settings.allowChoiceWisps===true,protectedCounts:locks,items:settings.items,recipeConditions:settings.recipeConditions,maxSteps:64,maxSearchNodes:clamp(settings.supportReplacementMaxSearchNodes,100,12000,4000)});}catch{return {executable:false,reason:"replacement_plan_failed"};}
   if(!plan?.executable||list(plan.steps).some(step=>randomOnly(step.resultRawcode)))return {executable:false,reason:plan?.reason||"random_acquisition_unavailable"};
   const requirements=list(plan.nonUnitRequirements),money=Graph.effectiveResourceCosts(requirements,settings.recipeConditions),unknown=requirements.some(row=>!["WOOD","GOLD","ITEM","UBAN","KING","IFCT","CHCT","SKPT","RMAX","NIKA"].includes(String(row.type).toUpperCase()));
   if(unknown||["wood","gold"].some(key=>!finite(money[key])||money[key]>0&&(!finite(settings[key])||settings[key]<money[key])))return {executable:false,reason:unknown?"replacement_condition_unknown":"replacement_resources_unavailable"};
   return {executable:true,steps:plan.steps,stepCount:plan.stepCount,choiceWispCount:plan.choiceWispCount||0,wood:money.wood,gold:money.gold,randomAcquisitionAssumed:false};
  }
  function structural(candidate,original){
   const evidence=candidate?.sourceEvidence,changes=list(evidence?.supportReplacements),protectedSet=new Set(protectedCodes(original));
   if(!finalGoalPolicyAvailable||!sourceValid(original,catalog)||evidence?.kind!=="record_support_adaptation"||evidence.baseTemplateId!==original.id||!candidate.id?.startsWith(original.id+"--support-")||changes.length<1||changes.length>2||evidence.clearOutcomeTransferred!==false||evidence.performanceEquivalenceVerified!==false||evidence.compositionChanged!==true||JSON.stringify(candidate.source)!==JSON.stringify(evidence)||candidate.id!==replacementId(original.id,changes))return false;
   for(const field of ["coreRawcodes","coreGoals","coreComposition","requiredCoverage","recordId","direction","recordPresenceGoals","availableWhen","requiredOwnedMaterials","conditions","noTranscendentUnits","transcendencePlan"])if(JSON.stringify(candidate[field])!==JSON.stringify(original[field]))return false;
   for(const field of ["recordIds","urls","mapping","goalProjection","sourceProvider","localNativeProof","randomOnlyRawcodes"])if(JSON.stringify(evidence[field])!==JSON.stringify(original.sourceEvidence[field]))return false;
   const changed=new Set(),originalCodes=new Set(goalsOf(original).map(g=>g.rawcode)),replacements=new Set();for(const change of changes){const slot=list(original.supportSlots).find(s=>s.id===change.slotId),goals=replacementGoals(change);if(!slot||changed.has(slot.id)||slot.required===false||list(slot.preferredRawcodes).length!==1||slot.preferredRawcodes[0]!==change.originalRawcode||protectedSet.has(change.originalRawcode)||(slot.count||1)!==change.count||!goals.length||goals.length>3||goals.some(goal=>typeof goal?.rawcode!=="string"||!Number.isSafeInteger(goal.count)||goal.count<1||goal.count>1000||goal.rawcode===change.originalRawcode||originalCodes.has(goal.rawcode)||replacements.has(goal.rawcode)||!native(goal.rawcode)||main(goal.rawcode)&&!randomOnly(goal.rawcode)||finalCore(goal.rawcode))||new Set(goals.map(goal=>goal.rawcode)).size!==goals.length)return false;
    if(goals.length>1&&!randomOnly(change.originalRawcode,original))return false;
    for(const goal of goals)replacements.add(goal.rawcode);changed.add(slot.id);}
   const expected=replaceSlots(original.supportSlots,changes);
   if(goalsOf(original).some(goal=>Records?.isResidualGoalTier?.(units.get(goal.rawcode))===true)||goalsOf(candidate).some(goal=>Records?.isResidualGoalTier?.(units.get(goal.rawcode))===true&&!changes.some(change=>randomOnly(change.originalRawcode,original)&&replacementGoals(change).some(row=>row.rawcode===goal.rawcode))))return false;
   if(JSON.stringify(normalizedGoals(goalsOf(candidate).filter(goal=>finalCore(goal.rawcode))))!==JSON.stringify(normalizedGoals(goalsOf(original).filter(goal=>finalCore(goal.rawcode)))))return false;
   return JSON.stringify(candidate.supportSlots)===JSON.stringify(expected);
  }
  function isAdaptation(candidate,original){if(!valid||!structural(candidate,original)||!compare(goalsOf(original),goalsOf(candidate)).accepted)return false;const removed=list(candidate.sourceEvidence.supportReplacements).filter(change=>randomOnly(change.originalRawcode,original)).map(change=>change.originalRawcode);return !removed.length||randomRequirements(goalsOf(original),goalsOf(candidate),removed).accepted;}
  function adapt(template,counts,settings={}){
   if(settings.includeMysteriousDecks!==true&&[...goalsOf(template||{}),...list(template?.sourceEvidence?.mapping),...list(template?.goalProjection?.excluded),...list(template?.sourceEvidence?.goalProjection?.excluded)].some(row=>[row?.rawcode,...list(row?.candidateRawcodes)].some(code=>units.get(code)?.tier==="신비")))return {templates:[],comparisons:[],protectedRawcodes:protectedCodes(template||{}),search:{evaluated:0,limitReached:false}};
   const protectedRawcodes=protectedCodes(template||{}),randomSupportNeeds=missingRandomSupport(template||{},counts),empty={templates:[],comparisons:[],protectedRawcodes,randomSupportNeeds,randomSupportSuggestions:[],search:{evaluated:0,limitReached:false}};if(!valid||!finalGoalPolicyAvailable||!sourceValid(template,catalog)||goalsOf(template).some(goal=>Records?.isResidualGoalTier?.(units.get(goal.rawcode))===true)||settings.includeKingPathDecks!==true&&(goalsOf(template).some(goal=>goal.rawcode==="h0C8")||list(template.sourceEvidence?.mapping).some(entry=>entry.rawcode==="h0C8")))return empty;
   const stock=stockOf(counts),protectedSet=new Set(protectedRawcodes),originalGoals=goalsOf(template),originalCodes=new Set(originalGoals.map(g=>g.rawcode));
   const slots=list(template.supportSlots).filter(slot=>slot.required!==false&&list(slot.preferredRawcodes).length===1&&!protectedSet.has(slot.preferredRawcodes[0])&&native(slot.preferredRawcodes[0])&&(!randomOnly(slot.preferredRawcodes[0],template)||(stock.get(slot.preferredRawcodes[0])||0)===0)&&(effects(slot.preferredRawcodes[0]).length||damageReferences(slot.preferredRawcodes[0],slot.count||1).components.length)).sort((a,b)=>Number((stock.get(a.preferredRawcodes[0])||0)>0)-Number((stock.get(b.preferredRawcodes[0])||0)>0)||String(a.id).localeCompare(String(b.id)));
   const candidates=pool(stock,{...settings,allowResidualSupportCandidates:randomSupportNeeds.length>0}).filter(c=>!originalCodes.has(c.rawcode)),maximum=clamp(settings.supportMaxComparisons,4,96,64),variantLimit=clamp(settings.supportMaxVariants,1,6,4),singles=[],comparisons=[],variants=[],randomSupportSuggestions=[],seen=new Set();let evaluated=0;
   const choices=slots.map(slot=>{const reference=assess([{rawcode:slot.preferredRawcodes[0],count:slot.count||1}]),keys=Object.keys(reference.utility);return {slot,candidates:candidates.slice().sort((a,b)=>keys.filter(k=>b.roleKeys.has(k)).length-keys.filter(k=>a.roleKeys.has(k)).length||Number(b.owned>0)-Number(a.owned>0)||a.rawcode.localeCompare(b.rawcode))};});
   const tryChanges=changes=>{
    if(evaluated>=maximum)return;const selected=c=>c.candidates||[c.candidate],key=changes.map(c=>c.slot.id+":"+selected(c).map(candidate=>candidate.rawcode).sort().join("+")).sort().join("|");if(seen.has(key))return;seen.add(key);evaluated++;
    const result=clone(template),replacements=changes.map(change=>{const {slot}=change,candidates=selected(change),row={slotId:slot.id,originalRawcode:slot.preferredRawcodes[0],replacementRawcode:candidates[0].rawcode,count:slot.count||1};if(candidates.length>1)row.replacementGoals=candidates.map(candidate=>({rawcode:candidate.rawcode,count:slot.count||1}));return row;});
    result.supportSlots=replaceSlots(result.supportSlots,replacements);
    const randomRemoved=replacements.filter(change=>randomOnly(change.originalRawcode,template)).map(change=>change.originalRawcode),replacementGoalsNeeded=replacements.filter(change=>randomOnly(change.originalRawcode,template)).flatMap(replacementGoals),production=randomRemoved.length?currentReplacementProof(replacementGoalsNeeded,stock,protectedRawcodes,settings):null;
    const comparison=compare(originalGoals,goalsOf(result)),randomComparison=randomRemoved.length?randomRequirements(originalGoals,goalsOf(result),randomRemoved):null;
    if(randomComparison){comparison.randomReplacementRequirements=randomComparison;comparison.accepted=comparison.accepted&&randomComparison.accepted&&production.executable;comparison.losses.push(...randomComparison.losses,...(production.executable?[]:[{kind:"random_replacement_not_currently_craftable",reason:production.reason}]));comparison.unresolved.push(...randomComparison.unresolved);}
    comparisons.push({replacements,accepted:comparison.accepted,losses:comparison.losses,...(randomComparison?{randomReplacementRequirements:randomComparison,replacementProduction:production}: {})});
    if(!comparison.accepted){if(randomComparison&&production.executable&&randomComparison.requirements.some(row=>row.status==="reference_preserved"))randomSupportSuggestions.push({replacements,replacementGoals:replacementGoalsNeeded,requirements:randomComparison,production,originalRandomUnitsRetained:true,compositionComplete:false,clearOutcomeTransferred:false});return;}
    const evidence={...clone(template.sourceEvidence),kind:"record_support_adaptation",baseTemplateId:template.id,baseSourceKind:"clear_record",compositionChanged:true,clearOutcomeTransferred:false,performanceEquivalenceVerified:false,supportReplacements:replacements,supportComparison:comparison};
    result.id=replacementId(template.id,replacements);result.name="[패 반영] "+String(template.name||template.id).replace(/^\[[^\]]+\]\s*/,"");result.sourceEvidence=evidence;result.source=evidence;result.evidenceScope="record_core_preserved_support_adaptation_not_replayed";
    if(Array.isArray(template.missingRandomRequirements))result.missingRandomRequirements=template.missingRandomRequirements.filter(row=>!randomRemoved.includes(row.rawcode));
    const ranked=changes.flatMap(change=>selected(change).map(candidate=>({candidate,count:change.slot.count||1})));
    result.supportAdaptationRank={ownedReplacementCount:ranked.filter(row=>(stock.get(row.candidate.rawcode)||0)>=row.count).length,progressRatio:ranked.every(row=>row.candidate.progressRatio!==null)?ranked.reduce((sum,row)=>sum+row.candidate.progressRatio,0)/ranked.length:null,completionProbability:ranked.every(row=>row.candidate.completionProbability!==null)?Math.min(...ranked.map(row=>row.candidate.completionProbability)):null,unresolvedCount:comparison.unresolved.length};
    variants.push(result);
   };
   for(let i=0;i<choices.length;i++)for(const candidate of choices[i].candidates){singles.push({slot:choices[i].slot,candidate});}
   const singleBudget=Math.min(maximum,Math.ceil(maximum*.55));for(const change of singles.slice(0,singleBudget))tryChanges([change]);
   for(const choice of choices.filter(row=>randomOnly(row.slot.preferredRawcodes[0],template)))for(let i=0;i<Math.min(6,choice.candidates.length);i++)for(let j=i+1;j<Math.min(6,choice.candidates.length);j++)tryChanges([{slot:choice.slot,candidates:[choice.candidates[i],choice.candidates[j]]}]);
   outer:for(let i=0;i<choices.length;i++)for(let j=i+1;j<choices.length;j++)for(const a of choices[i].candidates.slice(0,4))for(const b of choices[j].candidates.slice(0,4)){if(evaluated>=maximum)break outer;if(a.rawcode===b.rawcode)continue;tryChanges([{slot:choices[i].slot,candidate:a},{slot:choices[j].slot,candidate:b}]);}
   variants.sort((a,b)=>b.supportAdaptationRank.ownedReplacementCount-a.supportAdaptationRank.ownedReplacementCount||(a.supportAdaptationRank.completionProbability!==null&&b.supportAdaptationRank.completionProbability!==null?b.supportAdaptationRank.completionProbability-a.supportAdaptationRank.completionProbability:0)||(a.supportAdaptationRank.progressRatio!==null&&b.supportAdaptationRank.progressRatio!==null?b.supportAdaptationRank.progressRatio-a.supportAdaptationRank.progressRatio:0)||a.supportAdaptationRank.unresolvedCount-b.supportAdaptationRank.unresolvedCount||a.id.localeCompare(b.id));
   return {templates:variants.slice(0,variantLimit),comparisons,protectedRawcodes,randomSupportNeeds,randomSupportSuggestions:randomSupportSuggestions.slice(0,6),search:{evaluated,maxComparisons:maximum,maxCandidates:clamp(settings.supportMaxCandidates,2,16,12),limitReached:evaluated>=maximum,bounded:true}};
  }
  return Object.freeze({adapt,isAdaptation,assess,compare,randomReplacementRequirements:randomRequirements,missingRandomSupport,protectedRawcodes:protectedCodes});
 }
 return Object.freeze({create,goalsOf,targetExclusionsPreserved});
});
