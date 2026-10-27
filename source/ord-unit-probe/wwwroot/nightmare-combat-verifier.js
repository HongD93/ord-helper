(function(root,factory){
  const api=factory();
  if(typeof module==="object"&&module.exports)module.exports=api;
  root.OrdNightmareCombatVerifier=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(){
  "use strict";
  const finite=value=>typeof value==="number"&&Number.isFinite(value);
  const unique=values=>Array.from(new Set(values.filter(Boolean)));
  const array=value=>Array.isArray(value)?value:[];
  function isRuntimeReferenceEligible(source){
    if(!source||source.useForRoleInference===false)return false;
    const levels=[source.requiredAbilityLevel,...array(source.requiredAbilityLevels)].filter(finite);
    return !levels.some(level=>level>1)||source.runtimeAbilityLevelObserved===true;
  }
  // 합산 불허는 개별 참고값의 부재를 뜻하지 않는다. 명시적으로 검증 실패한 숫자와 미관측 레벨만 제외한다.
  const isNumericReferenceEligible=source=>isRuntimeReferenceEligible(source)&&source.numericFactsVerified!==false;
  const roles={lineDamage:"라인 딜 보강",bossDamage:"보스 딜 보강",storyDamage:"스토리 딜 보강",reviewedDamageCore:"주력 딜러 보강",alwaysSlow:"상시 이감 보강",areaStun:"범위 스턴 보강",manaSupport:"마나 보조 보강",armorReduction:"방어력 감소 보강",magicResistanceReduction:"마법 저항 감소 보강",magicAmplification:"마법 피해 증폭 보강"};
  function normalize(goals){
    const result=new Map();
    for(const goal of array(goals)){const code=typeof goal==="string"?goal:goal.rawcode,count=typeof goal==="string"?1:goal.count;if(code&&finite(count)&&count>0)result.set(code,(result.get(code)||0)+Math.floor(count));}
    return Array.from(result,([rawcode,count])=>({rawcode,count}));
  }
  function proc(duration,interval,chance){
    if(!finite(duration)||duration<0||!finite(interval)||interval<=0||!finite(chance)||chance<0||chance>100)return null;
    const p=chance/100,n=Math.floor(duration/interval),fraction=duration/interval-n;
    return {modeledUptime:p===0?0:1-Math.pow(1-p,n)*(1-fraction*p),modeledMeanGapSeconds:p===0?null:Math.pow(1-p,n)*(interval/p-fraction*interval)};
  }
  function createNativeManaUsage(catalog,combat){
    const hash=catalog?.map?.sha256,valid=typeof hash==="string"&&/^[a-f0-9]{64}$/i.test(hash)&&combat?.map?.sha256===hash;
    const catalogUnits=new Map(array(catalog?.units).map(unit=>[unit.rawcode,unit])),cache=new Map();
    const excluded=new Set(["h04S","H08T","h076","h04T","h04Y","h05D","e018","e0IX","e017","e016","e019","e015"]);
    const plain=text=>typeof text==="string"?text.replace(/\|c[\da-f]{8}/gi,"").replace(/\|r/gi,"").replace(/\|n/gi,"\n").replace(/\r\n?/g,"\n").replace(/^"([\s\S]*)"$/,"$1").trim():"";
    return function lookup(rawcode){
      if(cache.has(rawcode))return cache.get(rawcode);
      const unit=catalogUnits.get(rawcode),profile=combat?.units?.[rawcode],declarations=[];
      const usable=valid&&unit?.playable===true&&unit.isSummon!==true&&unit.isSummoned!==true&&!excluded.has(rawcode)&&profile?.rawcode===rawcode&&profile.verified===true&&profile.verificationKind==="native"&&profile.native&&typeof profile.native==="object"&&profile.mapSha256===hash;
      if(usable)for(const ability of array(profile.abilities)){
        const level=ability?.tooltipByLevel?.["1"],source=level?.source;
        if(!/^[A-Za-z0-9]{4}$/.test(ability?.rawcode||"")||level?.levelResolved!==true||level.requiredAbilityLevel!==1||plain(level.tooltip)!==plain(source?.quote)||!plain(level.tooltip))continue;
        const textSource=source?.type==="tooltip"&&source.source==="Units\\CampaignAbilityStrings.txt"&&source.field==="Ubertip"&&array(source.lines).length>0&&source.lines.every(line=>Number.isSafeInteger(line)&&line>0);
        const objectSource=source?.type==="objectData"&&source.source==="war3map.w3a"&&source.field==="aub1"&&source.level===1&&Number.isSafeInteger(source.byteOffset)&&source.byteOffset>=0;
        if(!textSource&&!objectSource)continue;
        for(const line of plain(level.tooltip).split("\n")){
          // 직접 마나 발동/소모 선언만 사용한다. 회복·최대마나·대상 유닛 설명은 사용 근거가 아니다.
          const activation=line.match(/^\s*[◎◈●■◆▶※★]?\s*발동조건\d*\s*:\s*(.+)$/u),condition=activation?.[1]||"";
          const threshold=/(?:대상(?:의)?|적의|적군|아군|유닛의)\s*마나/u.test(condition)?null:condition.match(/(?:^|\s)(?:자신(?:의)?\s*)?마나\s*(\d+(?:\.\d+)?)(?!\d)(?!\s*(?:회복|재생|증가))/u);
          const cost=line.match(/^\s*[◎◈●■◆▶※★]?\s*(?:마나소모\s*:\s*|소모마나\s*:\s*|코스트\s*:\s*마나\s*)(\d+(?:\.\d+)?)/u)||line.match(/^\s*[◎◈●■◆▶※★]?\s*마나\s*(\d+(?:\.\d+)?)\s*소모/u);
          const value=Number((threshold||cost)?.[1]);if(!finite(value)||value<=0||value>1000000)continue;
          declarations.push({abilityRawcode:ability.rawcode,abilityName:ability.tip||ability.name||ability.rawcode,level:1,kind:threshold?"mana_activation_condition":"mana_consumption_declaration",thresholdMana:threshold?value:null,declaredConsumedMana:cost?value:null,consumedMana:null,activationCondition:threshold?condition:null,conditionalBranch:threshold?condition.slice(0,threshold.index).trim().length>0:null,activationConditionObserved:false,line:line.trim(),mapSha256:hash,source:{file:source.source,field:source.field,...(textSource?{lines:source.lines.slice()}:{byteOffset:source.byteOffset}),levelResolved:true}});
        }
      }
      const result={rawcode,usesManaSkills:declarations.length?true:null,status:declarations.length?"native_condition_declared":"unknown",declarations,sourceVerified:declarations.length>0,catalogMapHash:valid?hash:null,actualCycleSeconds:null,externalManaSupportRequired:null,consumptionAmountVerified:false};
      cache.set(rawcode,result);return result;
    };
  }
  function create(catalog,options){
    options=options||{};
    const models=options.unitModels||{},scenarios=options.scenarios||{},records=options.recordEvidence||{};
    const mapHash=catalog?.map?.sha256||catalog?.source_sha256;
    const hashOf=data=>data?.map?.sha256||data?.map?.hash||data?.target?.mapHash;
    const validModels=Boolean(mapHash&&hashOf(models)===mapHash),validScenarios=Boolean(mapHash&&hashOf(scenarios)===mapHash),validRecords=Boolean(mapHash&&hashOf(records)===mapHash);
    const units=validModels?models.units||{}:{},encounters=validScenarios?array(scenarios.scenarios):[];
    const nativeManaUsage=createNativeManaUsage(catalog,options.nativeCombat);
    const names=new Map(array(catalog?.units).map(unit=>[unit.rawcode,unit.name||unit.properName||unit.rawcode]));
    function sourceName(rawcode){return names.get(rawcode)||rawcode;}
    function scenarioReferences(settings){
      const elder=["warcury","nasujuro","saturn","none"].includes(settings.boss)?settings.boss:null;
      return encounters.filter(scenario=>scenario.kind==="boss"||scenario.id==="boss65").map(scenario=>{
        const selected=elder?scenario.researchByElder?.[elder]:null;
        const research=selected?.normalProgressionReachable===false?null:selected;
        const alternatives=Object.entries(scenario.researchByElder||{}).filter(([,profile])=>profile.normalProgressionReachable!==false).map(([id,profile])=>({elder:id,conditionalHp:profile.conditionalHp||null,postMitigationDpsLowerBound:profile.bossDpsLowerBound?.value??null}));
        const limit=scenario.time?.maximumSecondsAfterFirstSpawn??scenario.time?.maxSecondsAfterFirstSpawn;
        const explicit=scenario.requirement?.sourceVerified===true&&finite(scenario.requirement.minimumEffectiveDps)?scenario.requirement.minimumEffectiveDps:null;
        const conditional=research?.bossDpsLowerBound;
        const hp=scenario.effective?.sourceVerified===true&&finite(scenario.effective.hp)?scenario.effective.hp:null;
        const requirement=explicit??(hp!==null&&finite(limit)&&limit>0?hp/limit:null);
        return {scenarioId:scenario.id,round:scenario.round,elder,maximumAttackSeconds:finite(limit)?limit:null,actualAttackSeconds:null,
          conditionalHp:research?.conditionalHp||null,elderUnknown:elder===null,counterfactualExcluded:selected?.normalProgressionReachable===false,elderAlternatives:elder===null||selected?.normalProgressionReachable===false?alternatives:[],postMitigationDpsLowerBound:requirement??(finite(conditional?.value)?conditional.value:null),
          requirementVerified:requirement!==null,requirementIsConditional:explicit===null,isSufficiencyThreshold:explicit!==null&&scenario.requirement.isSufficiencyThreshold===true,healingExcluded:scenario.requirement?.healingIncluded!==true,
          interpretation:"continuous_attack_health_only_lower_bound",sourceRefs:[...(conditional?.sourceRefs||[]),...(scenario.sourceRefs||[]),...(scenario.time?.sources||[]),...(scenarios.engineMechanics?.hpResearch?.evidenceArtifact?[scenarios.engineMechanics.hpResearch.evidenceArtifact]:[])],
          assumptions:unique([...(research?.conditionalHp?.assumptions||[]),...(scenarios.engineMechanics?.hpResearch?.conditions||[]),"공격 가능한 최대 시간 내내 같은 보스를 공격하는 조건","체력 재생·공격 중단·다른 적 처리는 별도"])};
      });
    }
    function damageAssessment(goals,settings){
      const sources=goals.map(goal=>{
        const unit=units[goal.rawcode],attack=unit?.attackReference;
        const components=array(unit?.damage?.components).filter(component=>component.sourceVerified===true&&isNumericReferenceEligible(component)).map(component=>{
          const trigger=component.trigger||{},interval=trigger.intervalSeconds??attack?.unmodifiedCooldownSeconds;
          const chance=trigger.chancePercent;
          const periodic=["automatic_periodic","periodic"].includes(trigger.kind),attackProc=trigger.requiresAttackEvent===true&&["attack_proc","native_bash_attack_proc"].includes(trigger.kind);
          const noCooldown=!finite(trigger.cooldownSeconds)||trigger.cooldownSeconds===0;
          const rate=finite(interval)&&interval>0&&(periodic||attackProc&&finite(chance)&&noCooldown)?(periodic?1:chance/100)/interval:null;
          const hits=finite(component.hitsPerActivation)&&component.hitsPerActivation>0?component.hitsPerActivation:1;
          return {...component,referenceActivationsPerSecond:rate,referenceFlatDps:finite(rate)&&finite(component.amountFlat)?component.amountFlat*rate*hits:null,
            referenceMaxHealthFractionPerSecond:finite(rate)&&finite(component.amountMaxHealthPercent)?component.amountMaxHealthPercent/100*rate*hits:null,
            actualEffectiveDps:null};
        });
        return {rawcode:goal.rawcode,name:sourceName(goal.rawcode),count:goal.count,modelKnown:Boolean(unit),attackReference:attack||null,
          rawBaseAttackDps:finite(attack?.rawSingleTargetDps)?attack.rawSingleTargetDps:null,components,unresolvedComponents:array(unit?.damage?.components).filter(component=>!isNumericReferenceEligible(component)),totalDps:null,complete:unit?.damage?.complete===true,
          unknownMajorDamage:unit?.damage?.unknownMajorDamage!==false};
      });
      const references=scenarioReferences(settings);
      const results=references.map(reference=>{
        const bounds=goals.map(goal=>{
          const bound=units[goal.rawcode]?.damage?.bounds;
          return bound?.sourceVerified===true&&bound.useForNumericSufficiency!==false&&isNumericReferenceEligible(bound)&&bound.complete===true&&bound.scope==="scenario_effective_dps"&&bound.scenarioId===reference.scenarioId?{...bound,count:goal.count}:null;
        });
        const complete=bounds.length>0&&bounds.every(Boolean),lower=complete&&bounds.every(bound=>finite(bound.lowerDps))?bounds.reduce((sum,bound)=>sum+bound.lowerDps*bound.count,0):null;
        const upper=complete&&bounds.every(bound=>finite(bound.upperDps))?bounds.reduce((sum,bound)=>sum+bound.upperDps*bound.count,0):null;
        const threshold=reference.requirementVerified?reference.postMitigationDpsLowerBound:null;
        const satisfied=reference.isSufficiencyThreshold&&threshold!==null&&lower!==null&&lower>=threshold;
        const insufficient=threshold!==null&&upper!==null&&upper<threshold;
        return {...reference,status:insufficient?"insufficient_model":satisfied?"conditional_model":"reference_only",completeDamageBounds:complete,lowerEffectiveDps:lower,upperEffectiveDps:upper,modelRequirementMet:satisfied,
          // 필요한 하한을 넘었다는 사실만으로는 실제 클리어 충분조건이 되지 않는다.
          provesClearSufficiency:false};
      });
      const known=sources.filter(source=>source.rawBaseAttackDps!==null),rawSum=known.length?known.reduce((sum,source)=>sum+source.rawBaseAttackDps*source.count,0):null;
      return {status:results.some(result=>result.status==="insufficient_model")?"insufficient_model":results.some(result=>result.status==="conditional_model")?"conditional_model":known.length||sources.some(source=>source.components.length)?"reference_only":"unknown",
        sources,rawBaseAttackDpsReference:rawSum,referenceCoverage:{knownUnits:known.length,totalUnits:sources.length},totalEffectiveDps:null,totalDpsComplete:false,
        scenarioResults:results,damageTypeMitigationApplied:false,synergyAggregationApplied:false,
        assumptions:["기본 평타와 확인된 기술 성분은 참고값이며 실전 총 DPS가 아님","범위 피해를 라인 전체 피해로 단순 곱하지 않음","기술 누락을 0으로 두거나 부족 확정에 사용하지 않음"]};
    }
    function controlAssessment(goals,existing){
      const fromExisting=existing?.control;
      const stuns=goals.flatMap(goal=>array(units[goal.rawcode]?.control?.stuns).filter(source=>(source.sourceVerified===true||source.nativeObjectValueVerified===true)&&isNumericReferenceEligible(source)).map(source=>{
        const interval=units[goal.rawcode]?.attackReference?.unmodifiedCooldownSeconds;
        const attackProc=["proc","attack_proc","native_bash_attack_proc"].includes(source.triggerKind);
        const general=attackProc?proc(source.durationSeconds,interval,source.chancePercent):null;
        const hero=attackProc?proc(source.heroDurationSeconds,interval,source.chancePercent):null;
        return {...source,rawcode:goal.rawcode,count:goal.count,generalModel:general,resistantHeroModel:hero,baseAttackInterval:interval,targetScenarioSelected:false};
      }));
      const slows=goals.flatMap(goal=>array(units[goal.rawcode]?.control?.slows).filter(source=>(source.sourceVerified===true||source.nativeObjectValueVerified===true)&&isNumericReferenceEligible(source)).map(source=>({...source,rawcode:goal.rawcode,count:goal.count,value:source.valuePercent})));
      const unresolvedSources=goals.flatMap(goal=>[...array(units[goal.rawcode]?.control?.stuns),...array(units[goal.rawcode]?.control?.slows)].filter(source=>!isNumericReferenceEligible(source)).map(source=>({...source,rawcode:goal.rawcode,count:goal.count})));
      const modeled=stuns.filter(source=>finite(source.generalModel?.modeledUptime)),values=modeled.map(source=>source.generalModel.modeledUptime);
      const bound=values.length?{lowerBound:Math.max(...values),upperBound:Math.min(1,modeled.reduce((sum,source)=>sum+source.generalModel.modeledUptime*source.count,0)),conditional:true,targetScenarioSelected:false}:null;
      const areaModeled=modeled.filter(source=>["area","all"].includes(source.scope));
      const areaBound=areaModeled.length?{lowerBound:Math.max(...areaModeled.map(source=>source.generalModel.modeledUptime)),upperBound:Math.min(1,areaModeled.reduce((sum,source)=>sum+source.generalModel.modeledUptime*source.count,0)),conditional:true,targetScenarioSelected:false}:null;
      const gapValues=modeled.map(source=>source.generalModel.modeledMeanGapSeconds).filter(finite);
      const slowValues=slows.map(source=>source.value??source.valuePercent).filter(value=>finite(value)&&value>0);
      const missing=new Set(array(existing?.coverage).filter(item=>item.status==="missing").map(item=>item.role));
      return {stun:{status:missing.has("areaStun")?"role_incomplete":stuns.length?"reference_only":"unknown",sources:stuns,modeledSourceCount:modeled.length,
          commonTargetUptimeBounds:bound,areaCommonTargetUptimeBounds:areaBound,sourceScopeCounts:{area:stuns.filter(source=>["area","all"].includes(source.scope)).length,single:stuns.filter(source=>source.scope==="single").length},bestSingleSourceMeanGapSeconds:gapValues.length?Math.min(...gapValues):null,actualCombinedUptime:null,actualMaximumGapSeconds:null,requiredUptime:null,
          assumptions:["동일한 일반 대상을 기본 공격주기로 계속 공격하는 조건","개별 확률 스턴의 범위는 참고값이며 실제 중첩·보스 내성을 확정하지 않음"]},
        slow:{status:missing.has("alwaysSlow")?"role_incomplete":slows.length?"reference_only":"unknown",sources:slows,strongestSingleSourcePercent:slowValues.length?Math.max(...slowValues):null,effectiveTotalPercent:null,stackingVerified:false,requiredSlowPercent:null,
          assumptions:["이감 합계와 스턴 시간을 더하지 않음","최저 이동속도·배치·효과별 중첩 확인 전 실제 정지 비율을 확정하지 않음"]},
        unresolvedSources,penalties:array(fromExisting?.penalties)};
    }
    function manaAssessment(goals,existing,references){
      const consumers=array(existing?.mana).filter(consumer=>consumer.usesManaSkills).map(consumer=>{
        const supported=consumer.withDeckSupport?.aggregationKnown===true?consumer.withDeckSupport.model:null;
        const model=supported||consumer.withoutSupport||{},inputs=consumer.modeledInputs||{};
        const recoveryComplete=finite(inputs.baseRegenerationPerSecond)&&finite(inputs.manaPerAttackEvent)&&finite(inputs.baseAttackInterval)&&inputs.baseAttackInterval>0;
        const relationship=units[consumer.rawcode]?.mana?.periodicManaRelationship||"unverified";
        const independent=relationship==="independent_additive_script_verified";
        const automatic=independent?consumer.withoutSupport?.independentPeriodicSeconds:consumer.withoutSupport?.automaticAlternativeSeconds;
        const periodicUnresolved=relationship==="unverified"&&finite(consumer.withoutSupport?.automaticPeriodicReferenceSeconds);
        const verifiedCost=array(units[consumer.rawcode]?.mana?.costs).some(cost=>cost.consumptionSourceVerified===true&&finite(cost.consumedMana)&&cost.consumedMana>0);
        const zeroSupply=verifiedCost&&recoveryComplete&&inputs.baseRegenerationPerSecond===0&&inputs.manaPerAttackEvent===0&&!(finite(model.modeledRecoveryPerSecond)&&model.modeledRecoveryPerSecond>0)&&!finite(automatic)&&!periodicUnresolved;
        const cycle=independent?model.repeatManaCycleSeconds:model.earliestAlternativeTriggerSeconds??model.repeatManaCycleSeconds;
        return {rawcode:consumer.rawcode,name:sourceName(consumer.rawcode),status:zeroSupply?"risk_detected":finite(cycle)?"conditional_model":"unknown",usesManaSkills:true,
          modeledRecoveryPerSecond:model.modeledRecoveryPerSecond??null,repeatManaCycleSeconds:model.repeatManaCycleSeconds??null,timeToThresholdFromZeroSeconds:model.timeToThresholdFromZeroSeconds??null,
          automaticAlternativeSeconds:independent?null:finite(automatic)?automatic:null,independentPeriodicSeconds:independent&&finite(automatic)?automatic:null,
          periodicManaRelationship:relationship,earliestAlternativeTriggerSeconds:independent?null:model.earliestAlternativeTriggerSeconds??null,
          referenceCastsPerSecond:independent&&finite(automatic)&&automatic>0&&finite(model.repeatManaCycleSeconds)&&model.repeatManaCycleSeconds>0?1/automatic+1/model.repeatManaCycleSeconds:null,withDeckSupport:supported!==null,
          recoveryInputCoverageComplete:recoveryComplete,supplySufficientEstablished:false,numericBalanceKnown:false,zeroSupplyUnderModel:zeroSupply,
          manaUsageEvidence:consumer.manaUsageEvidence||nativeManaUsage(consumer.rawcode),partialRecoveryReference:consumer.partialRecoveryReference||null,
          assumptions:array(consumer.assumptions),unresolved:array(consumer.unresolved)};
      });
      // 새 native 자료를 직접 사용할 때에도 공급이 미확인인 마나 기술을 비사용자로 처리하지 않는다.
      for(const goal of goals)if((units[goal.rawcode]?.mana?.usesManaSkills===true||array(units[goal.rawcode]?.mana?.verifiedThresholds).length>0||nativeManaUsage(goal.rawcode).usesManaSkills===true)&&!consumers.some(consumer=>consumer.rawcode===goal.rawcode))consumers.push({rawcode:goal.rawcode,name:sourceName(goal.rawcode),status:"unknown",usesManaSkills:true,numericBalanceKnown:false,supplySufficientEstablished:false,manaUsageEvidence:nativeManaUsage(goal.rawcode),modeledRecoveryPerSecond:null,repeatManaCycleSeconds:null,unresolved:["공격·오라를 포함한 마나 순환 미확인"]});
      const usageUnknownUnits=goals.filter(goal=>!consumers.some(consumer=>consumer.rawcode===goal.rawcode)&&units[goal.rawcode]?.mana?.usesManaSkills!==false&&!array(existing?.mana).some(consumer=>consumer.rawcode===goal.rawcode&&consumer.usesManaSkills===false)).map(goal=>({rawcode:goal.rawcode,name:sourceName(goal.rawcode),usesManaSkills:null,status:"unknown"}));
      const resourceCycles=array(existing?.resourceDependence).map(consumer=>{
        const supported=consumer.withDeckSupport?.aggregationKnown===true?consumer.withDeckSupport.model:null,model=supported||consumer.withoutSupport||{};
        return {rawcode:consumer.rawcode,name:sourceName(consumer.rawcode),resource:consumer.resource||"health",status:finite(model.refillFromResetSeconds)?"conditional_model":"unknown",
          threshold:finite(consumer.threshold)?consumer.threshold:null,reset:consumer.modeledInputs?.reset??null,requiredRefill:consumer.modeledInputs?.requiredRefill??null,
          modeledRecoveryPerSecond:model.modeledRecoveryPerSecond??null,refillFromResetSeconds:model.refillFromResetSeconds??null,attackAlignedTriggerSeconds:model.attackAlignedTriggerSeconds??null,
          withDeckSupport:supported!==null,actualBalanceKnown:false,recoveryInputCoverageComplete:consumer.modeledInputs?.recoveryInputCoverageComplete===true,
          baseRecoveryOnly:consumer.modeledInputs?.baseRecoveryOnly===true,gainPerAttackDamageEvent:consumer.modeledInputs?.gainPerAttackDamageEvent??null,
          thresholdSourceVerified:finite(units[consumer.rawcode]?.healthResource?.recoveryRequiredAfterReset),
          unresolved:unique([...array(consumer.unresolved),consumer.modeledInputs?.baseRecoveryOnly?"피해 이벤트 회복의 빈도 미확인":null]),assumptions:array(consumer.assumptions)};
      });
      for(const goal of goals){
        const health=units[goal.rawcode]?.healthResource;
        if(health&&!resourceCycles.some(consumer=>consumer.rawcode===goal.rawcode))resourceCycles.push({rawcode:goal.rawcode,name:sourceName(goal.rawcode),resource:"health",status:"unknown",threshold:health.threshold??null,reset:health.resetValue??null,requiredRefill:health.recoveryRequiredAfterReset??null,
          thresholdSourceVerified:finite(health.recoveryRequiredAfterReset),actualBalanceKnown:false,recoveryInputCoverageComplete:false,unresolved:["체력 회복과 발동 이벤트의 실전 순환 미확인"]});
      }
      const missing=array(existing?.coverage).some(item=>item.role==="manaSupport"&&item.status==="missing");
      return {status:consumers.some(consumer=>consumer.status==="risk_detected")?"risk_detected":missing?"role_incomplete":consumers.some(consumer=>consumer.status==="conditional_model")||resourceCycles.some(consumer=>consumer.status==="conditional_model")?"conditional_model":consumers.length||resourceCycles.length||usageUnknownUnits.length?"unknown":"not_required",
        consumers,usageUnknownUnits,resourceCycles,supportRoleMissing:missing,totalSupply:null,actualBalanceKnown:false,automaticAlternativesAreManaSupply:false,
        assumptions:["마나 회복과 자동 주기 발동을 별도 경로로 계산","마나 소모·중첩이 불명인 값을 0으로 확정하지 않음","충전 시간은 마나 0부터 시작하는 참고 조건"]};
    }
    function recordAssociation(goals){
      const stock=new Map(goals.map(goal=>[goal.rawcode,goal.count]));
      const associations=validRecords?array(records.records).filter(record=>record.safeComparators?.registeredPresenceAssociation===true&&record.cohort?.eligibleForRegularAssociation===true&&record.version?.number==="2.323").map(record=>{
        let exact=0,family=0;const matched=[],missing=[];
        for(const entry of array(record.roster?.entries)){
          const candidates=entry.kind==="family_form_unverified"?array(entry.candidateRawcodes):[entry.canonicalRawcode];
          const code=candidates.find(rawcode=>(stock.get(rawcode)||0)>=(entry.minimumCount||1));
          if(code){matched.push(code);if(entry.exactFormVerified)exact++;else family++;}else missing.push(entry.sourceName);
        }
        const minimumContained=missing.length===0&&matched.length>0;
        return {recordId:record.id,sourceUrl:record.sourceUrl,associationTier:record.eligibility.associationTier,sourceVariant:record.version.variant,matchedExactIdentities:exact,matchedUnverifiedFamilies:family,
          registeredEntryCount:record.roster.entries.length,matchedRawcodes:matched,missingRegisteredNames:missing,minimumPresenceContained:minimumContained,
          unknownSupportCounts:true,completeRosterMatch:false,numericCalibrationEligible:false,clearSufficiencyEstablished:false,variantPerformanceTransfer:false};
      }).filter(association=>association.matchedRawcodes.length):[];
      const same=associations.filter(association=>association.associationTier==="same_variant_partial"),related=associations.filter(association=>association.associationTier==="related_variant_partial");
      const order=(a,b)=>Number(b.minimumPresenceContained)-Number(a.minimumPresenceContained)||b.matchedExactIdentities-a.matchedExactIdentities||b.matchedUnverifiedFamilies-a.matchedUnverifiedFamilies||a.recordId.localeCompare(b.recordId);
      same.sort(order);related.sort(order);
      return {status:same.length?"same_variant_partial":related.length?"related_variant_partial":"none",sameVariantMatches:same,relatedVariantMatches:related,
        bestSameVariant:same[0]||null,bestRelatedVariant:related[0]||null,numericCalibrationRecordCount:0,fullRosterVerifiedCount:0,clearProbability:null,
        interpretation:"registered_minimum_presence_association",remainingEnemyCountUsed:false,variantEquivalenceVerified:false};
    }
    function assess(inputGoals,settings,existing){
      const goals=normalize(inputGoals);settings=settings||{};existing=existing||{};
      const damage=damageAssessment(goals,settings),control=controlAssessment(goals,existing),mana=manaAssessment(goals,existing,damage.scenarioResults),recordSupport=recordAssociation(goals);
      const missing=array(existing.coverage).filter(item=>item.status==="missing").map(item=>item.role),weaknesses=[];
      for(const role of missing)weaknesses.push({dimension:role==="manaSupport"?"mana":["areaStun","alwaysSlow"].includes(role)?"control":"damage",code:"missing_role",role,status:"role_incomplete",label:roles[role]||"보조 역할 보강"});
      for(const result of damage.scenarioResults)if(result.status==="insufficient_model")weaknesses.push({dimension:"damage",code:"verified_upper_bound_below_required_lower_bound",scenarioId:result.scenarioId,status:"insufficient_model",label:"보스 딜 부족"});
      for(const consumer of mana.consumers)if(consumer.zeroSupplyUnderModel)weaknesses.push({dimension:"mana",code:"no_modeled_recovery_or_periodic_alternative",rawcode:consumer.rawcode,status:"conditional_risk",label:consumer.name+" 마나 공급 보강"});
      const failureCount=weaknesses.filter(weakness=>weakness.status==="insufficient_model").length,conditionalRiskCount=weaknesses.filter(weakness=>weakness.status==="conditional_risk").length;
      return {schemaVersion:1,status:failureCount||conditionalRiskCount?"risk_detected":missing.length||array(existing.missingSlots).length?"role_incomplete":[damage.status,mana.status].includes("conditional_model")?"conditional_model":"evidence_limited",
        canConfirmClear:false,damage,control,mana,recordSupport,weaknesses,missingRoles:unique(missing),provenFailureCount:failureCount,conditionalRiskCount,
        coreSignature:array(settings.coreRawcodes||existing.coreComposition?.dpsRawcodes).map(code=>code+":"+(goals.find(goal=>goal.rawcode===code)?.count||0)).sort().join("|"),
        inputCoverage:{unitModelsMapMatched:validModels,scenariosMapMatched:validScenarios,recordEvidenceMapMatched:validRecords},
        evidence:{mapHash,unitModels:models.provenance||models.sources||null,scenarios:scenarios.sources||null,numericRecordCalibrationCount:0},
        assumptions:unique(["목표 유닛이 모두 완성된 구성을 평가","실제 배치·강화·공격 시간에 따라 결과가 달라짐",...damage.assumptions]),
        comparisonPolicy:{unknownIsNotZero:true,unknownIsNotAutomaticFailure:true,remainingEnemyCountNotRanked:true,recordAssociationNotClearProof:true}};
    }
    function comparableMetrics(validation){
      const metrics=new Map();
      for(const consumer of validation.mana?.consumers||[]){const cycle=consumer.earliestAlternativeTriggerSeconds??consumer.repeatManaCycleSeconds;if(finite(cycle)&&cycle>0)metrics.set("mana:"+consumer.rawcode,-cycle);}
      for(const consumer of validation.mana?.consumers||[])if(finite(consumer.modeledRecoveryPerSecond))metrics.set("manaRecovery:"+consumer.rawcode,consumer.modeledRecoveryPerSecond);
      for(const consumer of validation.mana?.resourceCycles||[]){const cycle=consumer.attackAlignedTriggerSeconds??consumer.refillFromResetSeconds;if(finite(cycle)&&cycle>0)metrics.set(consumer.resource+":"+consumer.rawcode,-cycle);}
      const bounds=validation.control?.stun?.areaCommonTargetUptimeBounds;
      if(bounds&&finite(bounds.lowerBound))metrics.set("stunReferenceLower",bounds.lowerBound);
      return metrics;
    }
    function compare(a,b){
      if(!a||!b)return 0;
      const failure=(a.provenFailureCount||0)-(b.provenFailureCount||0);if(failure)return failure;
      const missing=Number(a.status==="role_incomplete"||array(a.missingRoles).length>0)-Number(b.status==="role_incomplete"||array(b.missingRoles).length>0);if(missing)return missing;
      if(!a.coreSignature||a.coreSignature!==b.coreSignature||a.control?.penalties?.length||b.control?.penalties?.length)return 0;
      const am=comparableMetrics(a),bm=comparableMetrics(b),common=Array.from(am.keys()).filter(key=>bm.has(key));
      // 같은 주력의 알려진 순환·제어 참고값만 비교한다. 불명인 차원은 열세 점수가 아니다.
      let better=false,worse=false;
      for(const key of common){const delta=am.get(key)-bm.get(key);if(delta>1e-9)better=true;if(delta< -1e-9)worse=true;}
      return better&&!worse?-1:worse&&!better?1:0;
    }
    function compareEvidence(a,b){
      if(!a||!b||a.provenFailureCount!==b.provenFailureCount||a.status!==b.status)return 0;
      const matched=value=>value.recordSupport?.bestSameVariant?.minimumPresenceContained===true?1:0;
      return matched(b)-matched(a);
    }
    return {assess,compare,compareEvidence};
  }
  return {create,createNativeManaUsage,isRuntimeReferenceEligible,isNumericReferenceEligible};
});
