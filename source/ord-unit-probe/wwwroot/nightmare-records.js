(function(root,factory){const api=factory(typeof module==="object"&&module.exports?require("./nightmare-balance.js"):root.OrdNightmareBalance);if(typeof module==="object"&&module.exports)module.exports=api;root.OrdNightmareRecords=api;})(typeof globalThis!=="undefined"?globalThis:this,function(Balance){
"use strict";
const norm=value=>String(value||"").toLowerCase().replace(/[^a-z0-9가-힣]/g,"");
const list=value=>Array.isArray(value)?value:[];
const unique=values=>Array.from(new Set(values.filter(Boolean)));
const temporaryUnits=new Set(["h076","h04T","h04Y","h05D","h0CA"]);
const preservedFinalTiers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]),finalMaterialProxies=new Set(["h04S","H08T"]);
const residualGoalTiers=new Set(["흔함","안흔함","특별","희귀"]),goalProjectionPolicy="exclude_low_tier_residual_final_goals";
const goalTier=value=>{const tier=typeof value==="string"?value:value?.catalogTier??value?.tier??value?.sourceTier;return ({"특별함":"특별","희귀함":"희귀"})[tier]||tier;};
const isResidualGoalTier=value=>residualGoalTiers.has(goalTier(value));
const verifiedCount=entry=>entry?.countVerified===true&&entry.countMeaning==="verified_api_quantity"&&(entry.quantityEvidence||entry.evidence)?.sourceProvider==="tmo"&&(entry.quantityEvidence||entry.evidence)?.sourceCountVerified===true&&Number.isSafeInteger(entry.count)&&entry.count>0&&entry.count<=1000000;
const entryCount=entry=>verifiedCount(entry)?entry.count:1;
function finalGoalProjection(mapping){const excluded=new Map();for(const entry of list(mapping))if(isResidualGoalTier(entry)){const previous=excluded.get(entry.rawcode),count=entryCount(entry);if(!previous||count>previous.count)excluded.set(entry.rawcode,{rawcode:entry.rawcode,count,tier:goalTier(entry),reason:"lower_tier_residual"});}return {schemaVersion:1,policy:goalProjectionPolicy,excluded:Array.from(excluded.values()).sort((a,b)=>String(a.rawcode).localeCompare(String(b.rawcode))),sourceMappingPreserved:true,clearOutcomeTransferred:false};}
const versionKey=value=>typeof value==="string"&&/^\d{1,4}\.\d{3}$/.test(value)?Number(value.split(".")[0])*1000+Number(value.split(".")[1]):null;
const supportedSourceVersions=["2.321","2.322","2.323"],versionDeadlines=["2026-09-16T23:00:00+09:00","2026-09-24T23:00:00+09:00","2026-09-25T23:00:00+09:00"];
function recordVersionFacts(record,settings={}){
 const value=record?.version||{},evidence=record?.versionEvidence||{},key=versionKey(value.number),tmo=record?.sourceProvider==="tmo"||record?.sourceOutcome==="tmo_public_api_clear_flag_not_replayed";
 const exact=value.numericVerified===true&&key!==null&&(!settings.strictSource||value.sourceNumericVersionVerified!==false&&value.assumedByUser!==true&&value.source!=="user_default"&&value.datePolicyApplied!==true&&(!tmo||value.sourceNumericVersionVerified===true));
 if(exact)return {sourceVersion:value.number,evaluationSourceVersion:value.number,sourceNumericVersionVerified:true,datePolicyApplied:false,minimumPossibleVersion:value.number,possibleSourceVersions:[value.number]};
 if(value.number!==null||value.numericVerified!==false||value.sourceNumericVersionVerified!==false||value.datePolicyApplied!==true||value.source!=="user_date_policy_after_legacy_deadline"||value.cohortBasis!=="date_based_version_cohort"||evidence.method!==value.source||evidence.sourceNumericVersionVerified!==false||evidence.cohortBasis!==value.cohortBasis)return null;
 const time=typeof evidence.normalizedCreatedAt==="string"&&/(?:Z|[+-]\d{2}:\d{2})$/.test(evidence.normalizedCreatedAt)?Date.parse(evidence.normalizedCreatedAt):NaN;
 const index=versionDeadlines.reduce((found,deadline,current)=>time>Date.parse(deadline)?current:found,-1),minimum=supportedSourceVersions[index],possible=supportedSourceVersions.slice(index);
 const policy=evidence.datePolicy;
 if(index<0||value.minimumPossibleVersion!==minimum||evidence.minimumPossibleVersion!==minimum||!Array.isArray(value.possibleSourceVersions)||!Array.isArray(evidence.possibleSourceVersions)||JSON.stringify(value.possibleSourceVersions)!==JSON.stringify(possible)||JSON.stringify(evidence.possibleSourceVersions)!==JSON.stringify(possible)||policy?.comparison!=="strictly_after"||policy.minimumSupportedVersion!=="2.321"||policy.targetVersion!=="2.323")return null;
 return {sourceVersion:null,evaluationSourceVersion:minimum,sourceNumericVersionVerified:false,datePolicyApplied:true,minimumPossibleVersion:minimum,possibleSourceVersions:possible};
}
const validRecordIds=value=>list(value).length>0&&value.every(id=>typeof id==="string"&&id.trim().length>0&&id.length<=120);
const validRecordUrls=value=>list(value).length>0&&value.every(url=>typeof url==="string"&&url.length<=4096&&/^https?:\/\/[^/\s?#]+(?:[/?#][^\s]*)?$/i.test(url));
function originalClearTemplate(template){
 const evidence=template?.sourceEvidence;
 const provenance=template?.recordPresenceGoals===true&&typeof template.recordId==="string"&&template.id==="record-"+template.recordId&&
  !template.ownedUpgradeId&&evidence?.kind==="clear_record"&&evidence.compositionChanged!==true&&!list(evidence.replacements).length&&
  validRecordIds(evidence.recordIds)&&validRecordUrls(evidence.urls);
 if(!provenance||!list(evidence.mapping).length)return false;
 const expected=new Map(),actual=new Map(),add=(map,rawcode,count)=>{if(typeof rawcode!=="string"||!Number.isSafeInteger(count)||count<1)return false;map.set(rawcode,Math.max(map.get(rawcode)||0,count));return true;};
 const projection=finalGoalProjection(evidence.mapping);
 if((projection.excluded.length||evidence.goalProjection!==undefined)&&JSON.stringify(evidence.goalProjection)!==JSON.stringify(projection))return false;
 for(const entry of evidence.mapping){if(typeof entry.rawcode!=="string"||!Number.isSafeInteger(entryCount(entry))||entryCount(entry)<1)return false;if(!isResidualGoalTier(entry)&&!add(expected,entry.rawcode,entryCount(entry)))return false;}
 if(!expected.size)return false;
 const cores=list(template.coreGoals),coreCodes=list(template.coreRawcodes);
 if(cores.length!==coreCodes.length||!cores.every(entry=>coreCodes.includes(entry.rawcode)&&add(actual,entry.rawcode,entry.count)))return false;
 for(const slot of list(template.supportSlots)){if(slot.required===false||list(slot.preferredRawcodes).length!==1||list(slot.alternatives).length||!add(actual,slot.preferredRawcodes[0],slot.count||1))return false;}
 return actual.size===expected.size&&Array.from(expected).every(([rawcode,count])=>actual.get(rawcode)===count);
}
const aliases=[
 ["전설","나미","나미 도둑고양이"],["전설","상디","상디 슈트코스"],["전설","롤로노아 조로","조로 죽음의경지"],["전설","조로","조로 죽음의경지"],
 ["전설","마르코","마르코 환수종"],["전설","쵸파","쵸파 유력강화"],["전설","몽키.D.루피","루피 나이트메어"],["전설","루피","루피 나이트메어"],
 ["전설","에드워드 뉴게이트","흰수염"],["불멸","에드워드 뉴게이트","흰수염"],["전설","골.D.에이스","포트거스.D.에이스"],
 ["왜곡","에이스","포트거스.D.에이스"],["전설","에이스","포트거스.D.에이스"],["변화","비비","네펠타리 비비"],
 ["히든","쿠잔","아오키지"],["히든","귀죽의 베르고","베르고"],["히든","사보","사보 용조권"],["히든","키쿠","키쿠노죠"],["제한","에넬","갓 에넬"],
 ["초월","대령 타시기","타시기"],["초월","키드","유스타스 키드"],["초월","아라마키","료쿠규"],["초월","쵸파","토니토니 쵸파"],
 ["전설","드래곤","몽키.D.드래곤"],["불멸","드래곤","몽키.D.드래곤"],["영원","핸콕","보아 핸콕"],["전설","핸콕","보아 핸콕"]
];
function normalize(source,catalog,formAudit){
 const units=new Map(list(catalog.units).map(u=>[u.rawcode,u])),families=list(formAudit?.families).filter(f=>f.canonicalCraftVerified&&f.canUseBaseAsGenericCraftTarget&&units.has(f.canonicalCraftRawcode));
 const mapEntry=row=>{
  const base={sourceName:row.name,sourceTier:row.tier,sourceRawcode:row.rawcode??null,reportedCount:row.count??null,count:entryCount(row),countMeaning:verifiedCount(row)?"verified_api_quantity":"minimum_registered_presence",...(verifiedCount(row)?{countVerified:true,quantityEvidence:row.quantityEvidence||row.evidence}:{}),externalCharacterId:row.externalCharacterId??null,sourceUrl:row.sourceUrl||null};
  if(row.tier==="오로성")return {...base,status:"context_only",rawcode:null,candidateRawcodes:[],reason:"not_a_playable_catalog_tier"};
  if(temporaryUnits.has(row.rawcode))return {...base,status:"excluded_summon",rawcode:null,candidateRawcodes:[row.rawcode],reason:row.rawcode==="h0CA"?"native_trait_summon":"native_temporary_unit"};
  const original=units.get(row.rawcode);
  if(original?.playable&&original.tier===row.tier)return {...base,status:"exact_native_identity",rawcode:original.rawcode,candidateRawcodes:[original.rawcode],evidence:{kind:"source_rawcode_and_native_tier",catalogName:original.name,catalogDisplayName:original.displayName}};
  if(row.rawcode)return {...base,status:"unmapped",rawcode:null,candidateRawcodes:[row.rawcode],reason:"source_identity_absent_or_tier_changed"};
  const alias=aliases.find(([tier,name])=>tier===row.tier&&norm(name)===norm(row.name)),canonical=alias?.[2]||row.name;
  const fromName=list(catalog.units).filter(u=>u.playable&&u.tier===row.tier&&norm(u.displayName)===norm(canonical)).map(u=>u.rawcode);
  const candidates=unique([...list(row.candidateRawcodes),...fromName]).filter(code=>units.get(code)?.playable&&units.get(code).tier===row.tier);
  const family=families.find(f=>candidates.length>1&&candidates.every(code=>list(f.candidateRawcodes).includes(code)));
  if(family)return {...base,status:"canonical_craft_form_unknown",rawcode:family.canonicalCraftRawcode,candidateRawcodes:candidates,formUnknown:true,performanceEquivalenceVerified:false,
   evidence:{kind:"native_craft_family",familyId:family.id,canonicalRecipeId:family.canonicalRecipeId,relation:family.relation,nativeEvidenceIds:family.evidence,recordExactFormVerified:false,recordVariantPerformanceVerified:false}};
  if(candidates.length===1){const u=units.get(candidates[0]);return {...base,status:alias?"explicit_native_alias":"normalized_native_identity",rawcode:u.rawcode,candidateRawcodes:candidates,
   evidence:{kind:alias?"explicit_name_alias_and_exact_tier":"normalized_name_and_exact_tier",sourceName:row.name,canonicalName:canonical,catalogName:u.name,catalogDisplayName:u.displayName}};}
  return {...base,status:candidates.length?"ambiguous":"unmapped",rawcode:null,candidateRawcodes:candidates,evidence:alias?{kind:"explicit_alias_but_multiple_native_forms",canonicalName:canonical}:null};
 };
 return {schemaVersion:1,catalogMapHash:catalog.map?.sha256||null,numericVersion:"2.323",defaultCohort:"nightmare-regular",
  policy:{rosterComplete:false,counts:"minimum_presence_1",sourceMapHashKnown:false,remainingEnemyCountUsedForRanking:false,performanceEquivalenceClaimed:false},
  nativeFormFamilies:families.map(f=>({id:f.id,candidateRawcodes:f.candidateRawcodes,canonicalCraftRawcode:f.canonicalCraftRawcode,canonicalRecipeId:f.canonicalRecipeId,relation:f.relation,formUnknown:true,performanceEquivalenceVerified:false,evidence:f.evidence})),
  records:list(source.records).map(r=>{
   const entries=list(r.roster).map(mapEntry),goals=entries.filter(e=>e.rawcode),unresolved=entries.filter(e=>["ambiguous","unmapped"].includes(e.status));
   const modeTags=[r.mode,...list(r.difficulty?.modeTags)].filter(Boolean),normal=r.difficulty?.raw==="악몽"&&r.difficulty?.solo===false&&!modeTags.some(tag=>/변질|초고속|솔로|mutated|solo|(?:super|ultra)[_ -]?fast/i.test(String(tag)));
   const listQueryUrls=unique(list(r.sourceRefs).map(ref=>ref.queryKey).filter(url=>typeof url==="string"));
   return {id:String(r.id),sourceUrl:r.sourceUrl,imageUrl:r.imageUrl,createdAt:r.createdAt,version:{number:r.version?.number,variant:r.version?.variant||null,numericVerified:r.numericVersionVerified===true,variantVerified:r.variantVerified===true},
    versionEvidence:{method:r.versionEvidence?.method,sourceSha256:r.versionEvidence?.sourceSha256,visibleText:r.versionEvidence?.visibleText},
    cohort:normal?"nightmare-regular":r.mode==="변질"?"nightmare-mutated":"nightmare-mode-unknown",cohortEvidence:{difficultyRaw:r.difficulty?.raw||null,solo:r.difficulty?.solo??null,reportedMode:r.mode??null,modeTags:list(r.difficulty?.modeTags),siteDifficultyId:listQueryUrls.some(url=>/[?&]difficulty=13(?:&|$)/.test(url))?13:null,listQueryUrls,classificationRule:"nightmare_non_solo_excluding_mutated_speed_solo_tags"},difficulty:r.difficulty?.raw,solo:r.difficulty?.solo??null,mode:r.mode??null,otherworldMode:r.otherworldMode??null,
    environment:{elder:r.elder||null,navigation:r.navigation||null,navigationNode:r.navigationNode||null,enhancements:r.enhancements??null,items:r.items??null},
    reportedRemainingEnemyCount:r.reportedRemainingEnemyCount??null,imageObservedRemainingEnemyCount:r.imageObservedRemainingEnemyCount??null,
    rosterComplete:false,rosterCompleteness:r.rosterCompleteness||"reported_roster_not_verified",entries,goals,mappingComplete:unresolved.length===0,unresolved,sourceOutcome:"public_clear_submission_not_replayed"};
  })};
}
function create(catalog,combat,data,options={}){
 const recordOnly=options.recordOnly===true,generateAlternatives=!recordOnly&&options.generateAlternatives!==false;
 const requestedMinimum=versionKey(options.minRecordVersion),minimumRecordVersion=recordOnly?Math.max(versionKey("2.321"),requestedMinimum||0):requestedMinimum;
 const compatible=Boolean(data&&data.catalogMapHash===catalog.map?.sha256),units=new Map(list(catalog.units).map(u=>[u.rawcode,u])),profiles=combat.units||{},curated=combat.curatedStrategyProfiles?.units||{};
 const forbidden=new Set([...temporaryUnits,...list(combat.acquisitionModel?.nonForecastableRawcodes)]),upgrades=list(combat.curatedStrategyProfiles?.ownedSourceUpgrades);
 const families=new Map(list(data?.nativeFormFamilies).map(f=>[f.id,f]));
 const balance=Balance?.create(data?.balanceHistory,{catalogMapHash:catalog.map?.sha256,targetVersion:"2.323"});
 const stockOf=counts=>counts instanceof Map?new Map(counts):new Map(Object.entries(counts||{}));
 const mainRole=code=>curated[code]?.finalAnchorEligible===true&&["lineDamage","bossDamage"].some(k=>curated[code]?.[k]?.assessment==="main")?profiles[code]?.damage?.reviewedAnchorRole:null;
 const ally=new Set(["ally","allies","allied","all_allies","team"]),enemy=new Set(["enemy","enemies","all_enemies","boss","boss_enemies","line_enemies"]);
 const native=code=>!temporaryUnits.has(code)&&profiles[code]?.verified===true&&profiles[code]?.verificationKind==="native";
 const eligible= (code,stock)=>!temporaryUnits.has(code)&&units.get(code)?.playable===true&&(!forbidden.has(code)||(stock.get(code)||0)>0)&&
  !upgrades.some(u=>u.resultRawcode===code&&(stock.get(code)||0)<1&&(stock.get(u.sourceRawcode)||0)<1);
 function traits(code){
  const found=new Set();
  if(!native(code))return found;
  for(const e of profiles[code].effects||[]){
   if(e.useForRoleInference===false||!e.evidence||e.isPenalty||typeof e.value==="number"&&e.value<=0)continue;
   if(e.kind==="stun"&&enemy.has(e.target)&&e.stunRoleEligible!==false&&e.useForStunRecommendation!==false&&e.chancePercent!==0)found.add(["area","all"].includes(e.scope)?"areaStun":"singleStun");
   if(e.kind==="slow"&&enemy.has(e.target)){found.add("slow");if(["always","aura"].includes(e.mode)&&!(e.conditions||[]).length)found.add("alwaysSlow");}
   if(["attackSpeedBuff","attackDamageBuff","healthRegeneration","manaRegeneration"].includes(e.kind)&&ally.has(e.target))found.add(e.kind);
   if(["armorReduction","magicResistanceReduction","magicAmplification","allDamageAmplification"].includes(e.kind)&&enemy.has(e.target))found.add(e.kind);
  }
  return found;
 }
 const purposeRank={unverified:0,finisher:1,support:2,main:3};
 const comparisonKinds=new Set(["slow","stun","attackSpeedBuff","attackDamageBuff","healthRegeneration","manaRegeneration","armorReduction","magicResistanceReduction","magicAmplification","allDamageAmplification"]);
 const finite=value=>typeof value==="number"&&Number.isFinite(value),minimumRatio=0.8;
 const usedEffects=code=>(profiles[code]?.effects||[]).filter(e=>e.useForRoleInference!==false&&e.evidence&&!e.isPenalty&&comparisonKinds.has(e.kind)&&
  (ally.has(e.target)||enemy.has(e.target))&&(!finite(e.value)||e.value>0)&&!(e.kind==="stun"&&(e.stunRoleEligible===false||e.useForStunRecommendation===false)));
 function numericalComparison(original,candidate){
  const comparisons=[],sources=usedEffects(original),targets=usedEffects(candidate);
  if(!sources.length)return null;
  for(const source of sources){
   const same=targets.filter(target=>target.kind===source.kind&&target.target===source.target&&target.scope===source.scope&&target.mode===source.mode&&target.unit===source.unit&&
    JSON.stringify((target.conditions||[]).slice().sort())===JSON.stringify((source.conditions||[]).slice().sort()));
   let best=null;
   for(const target of same){
    const metrics=[],check=(name,from,to,inverse=false)=>{
     if(!finite(from)||!finite(to)||from<=0||to<=0)return false;
     const ratio=inverse?from/to:to/from;metrics.push({name,original:from,replacement:to,ratio,minimumRatio});return ratio>=minimumRatio;
    };
    let allowed=true;
    if(source.kind==="stun"){
     if(source.mode!=="proc")continue;
     const sd=source.targetDurations,td=target.targetDurations;
     allowed=check("chancePercent",source.chancePercent,target.chancePercent)&&
      check("baseAttackInterval",profiles[original]?.native?.attackCooldown,profiles[candidate]?.native?.attackCooldown,true)&&
      check("generalDurationSeconds",sd?.unit==="seconds"?sd.general:source.durationSeconds,td?.unit==="seconds"?td.general:target.durationSeconds)&&
      check("resistantHeroDurationSeconds",sd?.unit==="seconds"?sd.resistantHero:null,td?.unit==="seconds"?td.resistantHero:null);
    }else allowed=Boolean(source.unit)&&check("value",source.value,target.value);
    if(["area","all"].includes(source.scope)||source.mode==="aura")allowed=check("range",source.range,target.range)&&allowed;
    if(source.mode==="proc")allowed=check("chancePercent",source.chancePercent,target.chancePercent)&&allowed;
    if(source.mode==="interval")allowed=check("intervalSeconds",source.intervalSeconds,target.intervalSeconds,true)&&allowed;
    if(allowed){best={kind:source.kind,target:source.target,scope:source.scope,mode:source.mode,unit:source.unit,conditions:source.conditions||[],metrics,
     originalEffectId:source.id||null,replacementEffectId:target.id||null,originalEvidence:source.evidence,replacementEvidence:target.evidence};break;}
   }
   if(!best)return null;
   comparisons.push(best);
  }
  return comparisons;
 }
 function similar(original,candidate){
  if(original===candidate||!native(original)||!native(candidate)||!curated[original]||!curated[candidate])return false;
  const a=mainRole(original),b=mainRole(candidate),required=traits(original),available=traits(candidate);
  // 서로 다른 주력기의 실측·명목 딜 수지를 비교할 모델이 없으므로 주력 교체는 만들지 않는다.
  if(a||b)return false;
  if(a?a!==b:!required.size||b)return false;
  for(const tag of required)if(!available.has(tag))return false;
  for(const kind of ["lineDamage","bossDamage","storyDamage"]){
   const source=curated[original][kind],target=curated[candidate][kind];
   if((source?.evidence||[]).length&&(purposeRank[target?.assessment]||0)<(purposeRank[source.assessment]||0))return false;
  }
  const sourceMana=curated[original].manaDependence?.usesManaSkills===true,targetMana=curated[candidate].manaDependence?.usesManaSkills===true;
  if(!sourceMana&&targetMana)return false;
  return required.size>0&&numericalComparison(original,candidate)!==null;
 }
 function templates(counts,settings={}){
  const stock=stockOf(counts),result=[],locked=[];
  if(!compatible)return {templates:[],lockedRecords:[],compatible:false};
  const matching=list(data.records).filter(r=>r.cohort===(settings.recordCohort||data.defaultCohort)),handFitAdvisor=options.handFitAdvisor,usesHandFit=typeof handFitAdvisor?.preselectionScore==="function",affinities=new Map();
  const selectable=usesHandFit?matching.filter(record=>{let value=null;try{value=handFitAdvisor.preselectionScore(record,stock,settings);}catch{}if(value===-1)return false;affinities.set(record,typeof value==="number"&&Number.isFinite(value)&&value>=0&&value<=1?value:null);return true;}):matching;
  const legacy=selectable.filter(r=>r.sourceProvider!=="tmo"),live=selectable.filter(r=>r.sourceProvider==="tmo");
  const limit=Number.isSafeInteger(options.liveTemplateLimit)?Math.max(16,Math.min(256,options.liveTemplateLimit)):96;
  const score=record=>list(record.goals).reduce((sum,entry)=>sum+Math.min(stock.get(entry.rawcode)||0,entryCount(entry))*(new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]).has(units.get(entry.rawcode)?.tier)?100:1),0);
  const finalGoal=entry=>{const unit=units.get(entry.rawcode);return unit?.playable===true&&unit.isSummon!==true&&!temporaryUnits.has(entry.rawcode)&&!isResidualGoalTier(unit);};
  const actualUpper=rawcode=>{const unit=units.get(rawcode);return unit?.playable===true&&unit.isSummon!==true&&preservedFinalTiers.has(unit.tier)&&!temporaryUnits.has(rawcode)&&!finalMaterialProxies.has(rawcode);};
  const overlap=(record,upperOnly)=>list(record.goals).filter(entry=>finalGoal(entry)&&(!upperOnly||actualUpper(entry.rawcode))).reduce((sum,entry)=>sum+Math.min(stock.get(entry.rawcode)||0,entryCount(entry)),0);
  const previousOrder=(a,b)=>b.score-a.score||String(b.record.createdAt||"").localeCompare(String(a.record.createdAt||""))||a.record.id.localeCompare(b.record.id);
  const liveRanked=live.map(record=>({record,score:usesHandFit?overlap(record,false):score(record),upperScore:usesHandFit?overlap(record,true):0,affinity:affinities.get(record)??null})).sort((a,b)=>usesHandFit?b.upperScore-a.upperScore||previousOrder(a,b):previousOrder(a,b));
  if(usesHandFit){
   // 미확인 점수를 0으로 취급하지 않고 기존 자리를 보존한다. 확인된 점수만 같은 최상위 보유 그룹 안에서 비교한다.
   for(let start=0;start<liveRanked.length;){let end=start+1;while(end<liveRanked.length&&liveRanked[end].upperScore===liveRanked[start].upperScore)end++;const known=liveRanked.slice(start,end).filter(item=>item.affinity!==null).sort((a,b)=>b.affinity-a.affinity||previousOrder(a,b));let cursor=0;for(let index=start;index<end;index++)if(liveRanked[index].affinity!==null)liveRanked[index]=known[cursor++];start=end;}
  }
  const buckets=new Map();for(const item of liveRanked){const roles=list(item.record.goals).map(e=>mainRole(e.rawcode)),mode=roles.includes("hybridDps")||roles.includes("physicalDps")&&roles.includes("magicDps")?"hybrid":roles.includes("physicalDps")?"physical":roles.includes("magicDps")?"magic":"unknown";if(!buckets.has(mode))buckets.set(mode,[]);buckets.get(mode).push(item.record);}
  const selectedLive=[],selectedIds=new Set(),keep=record=>{if(record&&!selectedIds.has(record.id)){selectedIds.add(record.id);selectedLive.push(record);}};
  for(const {record}of liveRanked)if(settings.preferredDeckId==="record-"+record.id||String(settings.preferredDeckId||"").startsWith("record-"+record.id+"--"))keep(record);
  for(const [rawcode,count]of stock)if(count>0&&(usesHandFit?actualUpper(rawcode):preservedFinalTiers.has(units.get(rawcode)?.tier)))keep(liveRanked.find(item=>list(item.record.goals).some(entry=>entry.rawcode===rawcode))?.record);
  let cursor=0;while(selectedLive.length<limit&&cursor<liveRanked.length){for(const mode of ["physical","magic","hybrid","unknown"]){if(selectedLive.length>=limit)break;keep(buckets.get(mode)?.[cursor]);}cursor++;}
  const preselection={policy:"owned_upper_overlap_and_direction_balanced_recent_records",eligibleRecords:matching.length,legacyRecordsPreserved:legacy.length,totalTmoRecords:live.length,selectedTmoRecords:selectedLive.length,liveLimit:limit,truncated:selectedLive.length<live.length,remainingEnemyCountUsed:false};
  if(usesHandFit)Object.assign(preselection,{policy:"owned_final_upper_then_material_affinity_and_direction_balanced_records",policyExcludedRecords:matching.length-selectable.length,materialScoredRecords:liveRanked.filter(item=>item.affinity!==null).length,materialUnknownRecords:liveRanked.filter(item=>item.affinity===null).length,unknownMaterialPolicy:"preserve_previous_rank_slots"});
  for(const record of [...legacy,...selectedLive]){
   if(recordOnly&&(!validRecordIds(record.sourceRecordIds||[record.id])||!validRecordUrls(record.sourceUrls||[record.sourceUrl])||!["public_clear_submission_not_replayed","tmo_public_api_clear_flag_not_replayed"].includes(record.sourceOutcome))){locked.push({id:record.id,reasons:[{reason:"clear_record_provenance_missing"}]});continue;}
   const versionFacts=recordVersionFacts(record,{strictSource:recordOnly||minimumRecordVersion!==null}),evaluationVersion=versionFacts?.evaluationSourceVersion;
   if(!versionFacts){locked.push({id:record.id,sourceUrl:record.sourceUrl,reasons:[{reason:"unverified_version"}]});continue;}
   if(minimumRecordVersion!==null&&versionKey(evaluationVersion)<minimumRecordVersion||recordOnly&&versionKey(evaluationVersion)>versionKey("2.323")){locked.push({id:record.id,sourceUrl:record.sourceUrl,reasons:[{reason:"record_version_outside_supported_range"}]});continue;}
   const resolved=[],unresolved=[];
   for(const entry of record.entries){
    if(["context_only","excluded_summon"].includes(entry.status))continue;
    let rawcode=entry.rawcode,status=entry.status;
    const family=status==="canonical_craft_form_unknown"&&entry.evidence?.kind==="native_craft_family"?families.get(entry.evidence.familyId):null;
    if(family&&family.canonicalCraftRawcode===rawcode&&list(entry.candidateRawcodes).every(c=>list(family.candidateRawcodes).includes(c))){
     const owned=list(entry.candidateRawcodes).filter(c=>!temporaryUnits.has(c)&&(stock.get(c)||0)>0);
     const observed=(stock.get(rawcode)||0)>0?rawcode:owned.length===1?owned[0]:null;
     if(observed){rawcode=observed;status="owned_native_family_presence";}
    }
    if(!rawcode){const owned=list(entry.candidateRawcodes).filter(c=>!temporaryUnits.has(c)&&(stock.get(c)||0)>0);if(owned.length===1){rawcode=owned[0];status="owned_candidate_form_unknown";}else{unresolved.push(entry);continue;}}
    if(temporaryUnits.has(rawcode))continue;
    if(!units.has(rawcode)){unresolved.push(entry);continue;}
    if(forbidden.has(rawcode)&&(stock.get(rawcode)||0)<1){unresolved.push({...entry,reason:"unowned_random_source"});continue;}
    const upgrade=upgrades.find(u=>u.resultRawcode===rawcode);
    if(upgrade&&(stock.get(rawcode)||0)<1&&(stock.get(upgrade.sourceRawcode)||0)<1){unresolved.push({...entry,reason:"unowned_otherworld_source"});continue;}
    resolved.push({...entry,rawcode,status,catalogTier:units.get(rawcode).tier,formUnknown:entry.formUnknown||status==="owned_candidate_form_unknown"});
   }
   if(unresolved.length){locked.push({id:record.id,sourceUrl:record.sourceUrl,reasons:unresolved});continue;}
   const finalEntries=resolved.filter(entry=>!isResidualGoalTier(entry)),goalProjection=finalGoalProjection(resolved);
   const goalCodes=unique(finalEntries.map(e=>e.rawcode)),goalCounts=new Map(goalCodes.map(code=>[code,Math.max(...finalEntries.filter(entry=>entry.rawcode===code).map(entryCount))])),unknownFormCodes=unique(finalEntries.filter(e=>e.formUnknown).flatMap(e=>[e.rawcode,...list(e.candidateRawcodes)]));
   if(recordOnly&&!goalCodes.length){locked.push({id:record.id,sourceUrl:record.sourceUrl,reasons:[{reason:"no_original_registered_goals"}]});continue;}
   const adjustment=balance?.assess(evaluationVersion,unique([...goalCodes,...unknownFormCodes]),{formUnknownRawcodes:unknownFormCodes})||{sourceVersion:evaluationVersion,targetVersion:"2.323",status:evaluationVersion==="2.323"?"current":"locked",lockReason:"history_unavailable",currentProfileReevaluation:true,clearEquivalenceVerified:false,changes:[],risks:[],riskRank:evaluationVersion==="2.323"?0:9,downsideCount:0,numericComparisons:[]};
   if(adjustment.status==="locked"){locked.push({id:record.id,sourceUrl:record.sourceUrl,reasons:[{reason:adjustment.lockReason||"history_unavailable"}],balanceAdjustment:adjustment});continue;}
   const targetVariant=String(catalog.map?.version||catalog.map?.title||"").match(/\[([RC])\]/)?.[1]||null;
   adjustment.variantCompatibility={sourceVariant:record.version.variant||null,targetVariant,verified:false};
   if(evaluationVersion!=="2.323"&&(!record.version.variant||!targetVariant||record.version.variant!==targetVariant)){
    adjustment.risks.push({kind:"variant_equivalence_unverified",message:"원본 기록과 현재 맵의 R/C 성능 동등성을 확인하지 않았습니다.",sourceVariant:record.version.variant||null,targetVariant});
    adjustment.riskRank=Math.max(2,adjustment.riskRank);adjustment.status="historical_conditional";
   }
   if(versionFacts.datePolicyApplied){Object.assign(adjustment,{sourceVersion:null,evaluationSourceVersion:evaluationVersion,sourceNumericVersionVerified:false,datePolicyApplied:true,minimumPossibleVersion:versionFacts.minimumPossibleVersion,possibleSourceVersions:versionFacts.possibleSourceVersions,status:"historical_conditional",riskRank:Math.max(2,adjustment.riskRank)});adjustment.risks.push({kind:"date_based_source_version_range",minimumPossibleVersion:versionFacts.minimumPossibleVersion,possibleSourceVersions:versionFacts.possibleSourceVersions,exactSourceVersionVerified:false});}
   const physical=goalCodes.filter(c=>mainRole(c)==="physicalDps"),magic=goalCodes.filter(c=>mainRole(c)==="magicDps"),hybrid=goalCodes.filter(c=>mainRole(c)==="hybridDps");
   const mode=hybrid.length||physical.length&&magic.length?"hybrid":physical.length?"physical":magic.length?"magic":"unknown";
   const dpsCodes=unique([...physical,...magic,...hybrid]);
   const preservedFinalCodes=goalCodes.filter(code=>preservedFinalTiers.has(units.get(code)?.tier)&&units.get(code)?.playable===true&&units.get(code)?.isSummon!==true&&!temporaryUnits.has(code)&&!finalMaterialProxies.has(code));
   const coreCodes=unique([...dpsCodes,...preservedFinalCodes]),title=goalCodes.slice(0,2).map(c=>units.get(c).displayName||units.get(c).name).join("·");
   const evidence={kind:"clear_record",recordIds:record.sourceRecordIds||[record.id],urls:record.sourceUrls||[record.sourceUrl],versions:[record.version],cohort:record.cohort,sourceOutcome:record.sourceOutcome,sourceMapHashKnown:false,
    rosterComplete:false,countMeaning:resolved.every(verifiedCount)?"verified_listed_api_quantities_not_full_roster":"minimum_registered_presence",exactCountsVerified:resolved.every(verifiedCount),fullRosterCountsVerified:false,recordVariantCompatibilityVerified:false,cohortEvidence:record.cohortEvidence||null,balanceAdjustment:adjustment,formUnknown:resolved.filter(e=>e.formUnknown).map(e=>({sourceName:e.sourceName,rawcode:e.rawcode,evidence:e.evidence})),mapping:resolved,goalProjection,remainingEnemyCountUsedForRanking:false};
   const historical=versionFacts.sourceNumericVersionVerified&&evaluationVersion!=="2.323",displayId=record.displayId||record.id,recordName=historical?"[2.323 보정] "+title+" #"+displayId+" · 원본"+record.version.number+(record.version.variant?"["+record.version.variant+"]":"[R/C미확인]"):(evidence.formUnknown.length?"[기록기반] ":"[클리어기록] ")+title+" #"+displayId+(versionFacts.sourceNumericVersionVerified?(record.version.variant?"["+record.version.variant+"]":"[R/C미확인]"):"");
   result.push({id:"record-"+record.id,name:recordName,direction:mode,
    coreRawcodes:coreCodes,coreGoals:coreCodes.map(rawcode=>({rawcode,count:goalCounts.get(rawcode)})),coreComposition:{kind:"registered_record_presence",dpsRawcodes:dpsCodes,actualDpsCoreCount:dpsCodes.length,minimumDpsCoreCount:0},
    supportSlots:goalCodes.filter(c=>!coreCodes.includes(c)).map((c,i)=>({id:"registered-"+i,roles:[],preferredRawcodes:[c],alternatives:[],count:goalCounts.get(c),required:true})),
    requiredCoverage:[],recordPresenceGoals:true,sourceEvidence:evidence,source:evidence,evidenceScope:"registered_roster_presence_not_full_clear_validation",conditions:[],recordId:record.id});
  }
  const alternatives=[];
  if(generateAlternatives)for(const base of result){
   const goalCodes=[...base.coreRawcodes,...base.supportSlots.flatMap(s=>s.preferredRawcodes)],choices=[];
   for(const original of goalCodes)for(const candidate of Object.keys(curated)){
    if(goalCodes.includes(candidate)||!eligible(candidate,stock)||!similar(original,candidate))continue;
    const coverage=Array.from(traits(original)),progress=options.recipeGraph?.analyze(candidate,stock)?.ratio??0;
    choices.push({original,candidate,owned:(stock.get(candidate)||0)>0,progress,coverage,comparisons:numericalComparison(original,candidate)});
   }
   choices.sort((a,b)=>Number(b.owned)-Number(a.owned)||b.progress-a.progress||a.candidate.localeCompare(b.candidate));
   for(const change of choices.slice(0,2)){
    const evidence={...base.sourceEvidence,kind:"role_similar_alternative",baseTemplateId:base.id,performanceEquivalenceVerified:false,replacements:[{
     originalRawcode:change.original,resultRawcode:change.candidate,sharedRoles:change.coverage,damageLane:mainRole(change.original),comparisons:change.comparisons,
     originalProfile:curated[change.original],replacementProfile:curated[change.candidate],comparisonMeaning:"native_role_similarity_not_equal_damage_or_clear_verification"}],similarityPolicy:{minimumRatio,origin:"requested_similarity_policy",numericCombatEquivalence:false,unknownMetricsAllowed:false,addEffectsForComparison:false}};
    alternatives.push({...base,id:base.id+"--similar-"+change.original+"-"+change.candidate,name:"[대체안] "+(units.get(change.candidate)?.displayName||change.candidate)+" · #"+base.recordId,
     coreRawcodes:base.coreRawcodes.map(c=>c===change.original?change.candidate:c),coreComposition:{...base.coreComposition,dpsRawcodes:base.coreComposition.dpsRawcodes.map(c=>c===change.original?change.candidate:c)},
     supportSlots:base.supportSlots.map(s=>({...s,preferredRawcodes:s.preferredRawcodes.map(c=>c===change.original?change.candidate:c)})),source:evidence,sourceEvidence:evidence});
   }
  }
  return {templates:result,alternatives,lockedRecords:locked,compatible:true,preselection};
 }
 return {templates,compatible};
}
return {normalize,create,aliases,originalClearTemplate,recordVersionFacts,isResidualGoalTier,finalGoalProjection};
});
