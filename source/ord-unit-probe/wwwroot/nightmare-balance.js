(function(root,factory){const api=factory();if(typeof module==="object"&&module.exports)module.exports=api;root.OrdNightmareBalance=api;})(typeof globalThis!=="undefined"?globalThis:this,function(){
"use strict";
const list=value=>Array.isArray(value)?value:[],unique=values=>Array.from(new Set(values.filter(Boolean)));
const finite=value=>typeof value==="number"&&Number.isFinite(value);
function version(value){const match=String(value||"").match(/^(\d+)\.(\d{3})$/);return match?[Number(match[1]),Number(match[2])]:null;}
function compare(a,b){return a[0]-b[0]||a[1]-b[1];}
function create(history,options={}){
 const targetVersion=options.targetVersion||history?.targetVersion||"2.323",target=version(targetVersion),earliest=version(history?.earliestSupportedVersion);
 const compatible=Boolean(history&&options.catalogMapHash&&history.catalogMapHash===options.catalogMapHash&&history.targetVersion===targetVersion);
 const releases=new Map();for(const release of list(history?.releases)){const key=String(release.version);if(!releases.has(key))releases.set(key,[]);releases.get(key).push(release);}
 function assess(sourceVersion,rawcodes,settings={}){
  const initial={sourceVersion:String(sourceVersion||""),targetVersion,currentProfileReevaluation:true,clearEquivalenceVerified:false,changes:[],risks:[],riskRank:0,downsideCount:0,numericComparisons:[],sourceKind:"unofficial_transcription",chain:[],aggregateDamageRatio:null,winProbability:null};
  const locked=(reason,message)=>({...initial,status:"locked",lockReason:reason,riskRank:9,risks:[{kind:reason,message}]});
  const source=version(sourceVersion);
  if(!source||!target)return locked("unverified_version","기록 버전의 숫자 표기를 확인하지 못했습니다.");
  if(compare(source,target)===0)return {...initial,status:"current",lockReason:null};
  if(!compatible)return locked("history_map_mismatch","패치 이력과 대상 맵의 일치를 확인하지 못했습니다.");
  if(!earliest||compare(source,earliest)<0||compare(source,target)>0)return locked("unsupported_source_version","연결된 패치 이력 범위를 벗어난 기록입니다.");
  const chain=[],seen=new Set();let cursor=targetVersion;
  while(cursor!==String(sourceVersion)){
   if(seen.has(cursor)||chain.length>128)return locked("invalid_patch_chain","패치 이력 연결이 순환하거나 상한을 넘었습니다.");
   seen.add(cursor);const choices=releases.get(cursor)||[];
   if(choices.length!==1)return locked(choices.length?"ambiguous_patch_chain":"missing_patch_chain","기록 버전부터 현재 버전까지의 패치 연결이 빠져 있습니다.");
   const release=choices[0],previous=version(release.previousVersion),current=version(release.version);
   if(release.reviewed!==true||!previous||!current||compare(previous,current)>=0||compare(previous,source)<0)return locked("missing_patch_chain","검토된 연속 패치 이력을 확인하지 못했습니다.");
   chain.push(release);cursor=String(release.previousVersion);
  }
  chain.reverse();const ownedNames=new Set(list(rawcodes)),uncertainForms=new Set(list(settings.formUnknownRawcodes)),changes=[],risks=[],numericComparisons=[];
  let downsideCount=0,riskRank=1;
  for(const release of chain)for(const original of list(release.changes)){
   const mapped=unique([...list(original.rawcodes),...list(original.familyRawcodes),...list(original.candidateRawcodes)]),mapping=original.mappingStatus||(mapped.length?"exact":original.category==="environment"?"global":"unmapped");
   const unresolved=["ambiguous","unmapped"].includes(mapping)||original.subtype==="unresolved_unit_mapping",global=mapping==="global"||original.category==="environment";
   const matched=mapped.filter(code=>ownedNames.has(code)),formUnknown=original.formApplicabilityUnknown===true||matched.some(code=>uncertainForms.has(code));
   if(!global&&!unresolved&&!matched.length)continue;
   // 표시·행정 원문은 history 자산에 남기고 각 덱의 실행시 평가에는 복제하지 않는다.
   if(original.affectsGameplay===false)continue;
   // 명칭 매핑이 안 된 유닛 항목은 전역 효과로 적용하지 않고 별도의 불확실성으로 남긴다.
   const change={...original,releaseVersion:release.version,releaseSourceUrl:release.sourceUrl,sourceKind:release.sourceKind||"unofficial_transcription",mappingStatus:mapping,
    applicability:unresolved?"unresolved_unit_mapping":global?"global_environment":formUnknown?"family_form_unknown":"matched_unit",appliesAsGlobalEffect:global&&!unresolved,matchedRawcodes:matched};
   changes.push(change);
   const risk=(kind,message,rank)=>{risks.push({kind,message,changeId:original.id||null,releaseVersion:release.version,rawcodes:mapped,evidence:original.evidence||{url:release.sourceUrl}});riskRank=Math.max(riskRank,rank);};
   if(unresolved){risk("unresolved_unit_mapping","패치 대상 유닛의 명칭 매핑을 확인하지 못했습니다.",3);continue;}
   if(formUnknown){risk("unknown_form_applicability","기록의 실제 형태가 불명확해 변형별 패치 적용을 확정하지 않았습니다.",3);continue;}
   if(original.category!=="numeric"){
    if(["mechanic","new_unit"].includes(original.category))risk("mechanic_changed",original.summary||"동작 또는 유닛 구성이 변경됐습니다.",3);
    else if(original.category==="bugfix")risk("bugfix_changed",original.summary||"버그 수정의 실제 영향은 수치 비율로 환산하지 않았습니다.",2);
    else if(original.category==="environment")risk("environment_changed",original.summary||"전투 환경이 달라졌습니다.",2);
    else if(original.category==="tooltip")risk("tooltip_only",original.summary||"문구 변경을 실제 성능 변경으로 확정하지 않았습니다.",2);
    else risk("unclassified_change",original.summary||"변경의 성능 영향을 확인하지 못했습니다.",3);
    continue;
   }
   if(!finite(original.from)||!finite(original.to)||!original.metric||!original.unit||!original.evidence?.text||original.numericComparisonVerified!==true){risk("unresolved_numeric_change",original.summary||"개별 변경 수치나 단위를 확인하지 못했습니다.",3);continue;}
   const delta=original.to-original.from,direction=original.benefitDirection,conditionalComparison=original.conditionalComparison===true||original.runtimeApplicabilityVerified===false;
   const nominalOutcome=delta===0?"unchanged":direction==="higher"?(delta>0?"improved":"weakened"):direction==="lower"?(delta<0?"improved":"weakened"):"unknown",outcome=conditionalComparison?"unknown":nominalOutcome;
   numericComparisons.push({changeId:original.id||null,releaseVersion:release.version,rawcodes:matched,scope:original.scope||null,metric:original.metric,from:original.from,to:original.to,unit:original.unit,delta,
    ratio:original.from!==0?original.to/original.from:null,relativeChangePercent:original.from!==0?delta/Math.abs(original.from)*100:null,benefitDirection:direction||"unknown",outcome,
    conditions:original.conditions||[],abilityRawcode:original.abilityRawcode||null,requiredAbilityLevel:original.requiredAbilityLevel??original.abilityLevel??null,target:original.target||null,conditionalComparison,nominalOutcome,runtimeApplicabilityVerified:original.runtimeApplicabilityVerified===true,evidence:original.evidence||{url:release.sourceUrl},comparisonLevel:"reviewed_transcription_metric_not_runtime",meaning:"individual_metric_only_not_total_dps_or_clear_probability"});
   if(outcome==="weakened"){downsideCount++;risk("numeric_downside",original.summary||"확인된 개별 수치가 약화 방향으로 바뀌었습니다.",2);}
   if(conditionalComparison)risk("conditional_numeric_applicability",original.summary||"능력 레벨·특성 조건의 실제 적용 여부를 확인하지 않았습니다.",3);
   else if(outcome==="unknown")risk("unknown_benefit_direction",original.summary||"수치 변경의 유불리 방향을 확정하지 않았습니다.",2);
  }
  return {...initial,status:risks.length?"historical_conditional":"historical_adjusted",lockReason:null,changes,risks,riskRank,downsideCount,numericComparisons,
   chain:chain.map(r=>({version:r.version,previousVersion:r.previousVersion,sourceUrl:r.sourceUrl,sourceKind:r.sourceKind||"unofficial_transcription",reviewed:r.reviewed,rawSectionCount:r.rawSectionCount??null,coveredSourceBulletCount:r.coveredSourceBulletCount??null}))};
 }
 return {assess,compatible,targetVersion};
}
return {create};
});
