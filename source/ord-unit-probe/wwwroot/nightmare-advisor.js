(function(root,factory){
  const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph);
  if(typeof module==="object"&&module.exports)module.exports=api;
  root.OrdNightmareAdvisor=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(graphApi){
  "use strict";
  const finite=value=>typeof value==="number"&&Number.isFinite(value);
  const unique=values=>Array.from(new Set(values.filter(Boolean)));
  const resourceRank=value=>value===true?2:value===false?0:1;
  const roleNames={physicalDps:"물리 주력",magicDps:"마법 주력",hybridDps:"하이브리드 주력",slow:"이동속도 감소",stun:"스턴",armorReduction:"방어력 감소",magicResistanceReduction:"마법 방어력 감소",magicAmplification:"마법 증폭",attackSpeedBuff:"공격속도 보조",attackDamageBuff:"공격력 보조"};
  const roleKeys=Object.keys(roleNames),supportKinds=roleKeys.filter(key=>!key.endsWith("Dps"));
  const advanced=new Set(["전설","히든","초월","불멸","영원","제한","신비","왜곡","변화","랜덤전용","세라핌"]);
  const finalTiers=new Set(["초월","불멸","영원","제한","신비"]);
  const kinds={armorReduction:"armorReduction",armorBreak:"armorReduction",magicResistanceReduction:"magicResistanceReduction",magicAmplification:"magicAmplification",slow:"slow",stun:"stun",attackSpeedBuff:"attackSpeedBuff",attackDamageBuff:"attackDamageBuff"};
  function countsOf(value){
    const pairs=value instanceof Map?value.entries():Array.isArray(value)?value.map(item=>[item.rawcode,item.count]):Object.entries(value||{});
    return new Map(Array.from(pairs,([code,count])=>[code,finite(count)?Math.max(0,Math.floor(count)):0]));
  }
  function create(catalog,combatProfiles,options){
    options=options||{};combatProfiles=combatProfiles||{};
    const units=new Map((catalog.units||[]).map(unit=>[unit.rawcode,unit]));
    const profiles=combatProfiles.units||{},recipes=new Map();
    for(const recipe of catalog.recipes||[]){if(!recipes.has(recipe.resultUnitRawcode))recipes.set(recipe.resultUnitRawcode,[]);recipes.get(recipe.resultUnitRawcode).push(recipe);}
    for(const list of recipes.values())list.sort((a,b)=>String(a.id)<String(b.id)?-1:1);
    const graph=options.recipeGraph||graphApi.create(catalog);
    const ancestorCache=new Map();
    function hasMaterialAncestor(target,ancestor,path){
      if(target===ancestor)return false;
      const key=target+"\0"+ancestor;if(!path&&ancestorCache.has(key))return ancestorCache.get(key);
      path=new Set(path||[]);if(path.has(target))return false;path.add(target);
      const found=(recipes.get(target)||[]).some(recipe=>(recipe.ingredients||[]).some(item=>String(item.type).toUpperCase()==="UNIT"&&(item.rawcode===ancestor||hasMaterialAncestor(item.rawcode,ancestor,path))));
      if(path.size===1)ancestorCache.set(key,found);return found;
    }
    const expectedHash=catalog.map?.sha256||catalog.source_sha256;
    const mapCompatible=Boolean(expectedHash&&combatProfiles.map?.sha256&&expectedHash.toLowerCase()===combatProfiles.map.sha256.toLowerCase());
    const native=code=>mapCompatible&&profiles[code]?.verified===true&&profiles[code]?.verificationKind==="native"&&(!profiles[code].mapSha256||profiles[code].mapSha256===expectedHash);
    function encounter(settings){
      const rules=combatProfiles.nightmareRules,notes=[],warnings=[],effects=[];
      const labels={armor:"방어력",magicResistance:"마법 방어력",maxHealth:"최대 체력",healthRegeneration:"체력 재생",movementSpeedBuff:"이동속도",attackDamageBuff:"공격력",explosionAmplification:"폭발형 피해",attackSpeedBuff:"공격속도"};
      const targets={all_enemies:"모든 적",boss_enemies:"보스",line_enemies:"라인 적",all_allies:"아군"};
      const bossNames={warcury:"워큐리",saturn:"새턴",nasujuro:"나스쥬로"};
      const sourced=value=>value?.source?.file==="war3map.j"&&Array.isArray(value.source.lines)&&value.source.lines.length>0;
      function add(list,prefix){for(const effect of list||[])if(sourced(effect)){
        if(effect.displayPolicy==="warning_only"||effect.uncertainConflict){effects.push({...effect,value:null,context:prefix});notes.push(effect.warning||"원본 수치 불일치: 확정 수치 표시 보류");warnings.push(effect.warning||"원본 수치 불일치");continue;}
        if(!finite(effect.value))continue;
        if(settings.boss==="saturn"&&effect.kind==="healthRegeneration"){
          effects.push({...effect,value:null,conflict:true,context:prefix});notes.push("새턴 체력 재생 증가: 안내·능력값 불일치");warnings.push("새턴의 체력 재생은 안내와 능력 원본값이 달라 수치를 단정하지 않습니다.");continue;
        }
        effects.push({...effect,context:prefix});
        const amount=effect.value>0?"+"+effect.value.toLocaleString("ko-KR"):String(effect.value);
        const unit=effect.unit==="percent"?"%":effect.unit==="health"?" HP":effect.unit==="health_regeneration"?" (원문 재생값)":effect.unit==="armor"?"":" "+effect.unit;
        notes.push(prefix+": "+(targets[effect.target]||effect.target||"")+" "+(labels[effect.kind]||effect.kind)+" "+amount+unit+" (원본 "+effect.source.lines.join(", ")+"행)");
      }}
      let lineEnemyCountLimit=null,lateRound=false;
      if(mapCompatible&&rules){
        const selected=rules.encounters?.[settings.boss];
        if(selected){add(selected.effects,"선택한 "+bossNames[settings.boss]);add(selected.additionalNightmareEffects,"선택한 원로의 악몽 추가 효과");
          warnings.push(settings.boss==="nasujuro"?"나스쥬로의 이속·공속 조건으로 실제 제어 공백이 기본 주기 모델보다 커질 수 있습니다.":settings.boss==="warcury"?"워큐리는 물리·마법 방어 양쪽의 보강 근거를 함께 확인해야 합니다.":"새턴은 공격력 의존 피해와 지속 피해 조건을 함께 확인해야 합니다.");
        }else if(!["none","unknown"].includes(settings.boss))warnings.push("선택한 원로의 추가 패널티 원본을 확인하지 못했습니다.");
        add(rules.base?.effects,"악몽 기본");
        const limits=rules.lineEnemyCountLimits;
        if(limits&&sourced(limits)){
          if(settings.round!==null){lateRound=settings.round>=limits.fromRound;lineEnemyCountLimit=lateRound?limits.after:limits.initial;notes.unshift("현재 입력 "+settings.round+"라운드: 적 라인 카운트 제한 "+lineEnemyCountLimit+" (아군 보유 유닛 제한 아님)");}
          else notes.unshift("라운드 미확인: 적 라인 카운트 제한 "+limits.initial+", "+limits.fromRound+"라운드부터 "+limits.after);
        }
        for(const objective of rules.objectives||[])if(sourced(objective)&&objective.type==="story_deadline")notes.push(objective.beforeRound+"라운드 이전 "+objective.target+" 진행 조건 확인 필요 (스토리 진행은 자동으로 읽지 않음)");
      }else warnings.push("현재 맵의 악몽·원로 조건 자료 미확인");
      if(settings.boss==="unknown")notes.unshift("현재 원로 미확인: 원로별 추가 조건은 적용하지 않음");
      else notes.unshift("원로 설정은 사용자 입력이며 현재 원로를 자동 판독한 결과가 아님");
      return {boss:settings.boss,round:settings.round,automaticallyDetected:false,effects,lineEnemyCountLimit,lateRound,notes:unique(notes),warnings:unique(warnings)};
    }
    const effectCache=new Map(),roleCache=new Map();
    const evidence=effect=>Boolean(effect?.evidence&&["tooltip","script","objectData"].includes(effect.evidence.type)&&
      (effect.evidence.quote||effect.evidence.source||effect.evidence.lines?.length||effect.evidence.scriptLines?.length));
    const typeTags=code=>{
      const damage=profiles[code]?.damage||{},hint=Array.isArray(damage.roleHint)?damage.roleHint:[damage.roleHint];
      return unique([...(damage.explicitTypes||[]),...hint].filter(value=>value==="physical"||value==="magic"));
    };
    function anchorType(code){
      if(!native(code))return null;
      const damage=profiles[code]?.damage||{},role=damage.reviewedAnchorRole;
      if(role==="physicalDps"&&damage.primaryType==="physical"&&typeTags(code).includes("physical"))return "physical";
      if(role==="magicDps"&&damage.primaryType==="magic"&&typeTags(code).includes("magic"))return "magic";
      if(role==="hybridDps"&&damage.primaryType==="hybrid"&&["physical","magic"].every(type=>typeTags(code).includes(type)))return "hybrid";
      return null;
    }
    function beneficial(effect){
      if(effect.kind==="stun")return (!finite(effect.durationSeconds)||effect.durationSeconds>0)&&effect.chancePercent!==0;
      return !finite(effect.value)||effect.value>0;
    }
    const teamTargets=new Set(["ally","allies","allied","all_allies","team","friendly"]),enemyTargets=new Set(["enemy","enemies","all_enemies","boss","boss_enemies","line_enemies"]);
    const targetEligible=effect=>["attackSpeedBuff","attackDamageBuff"].includes(effect.kind)?teamTargets.has(effect.target):enemyTargets.has(effect.target);
    const roleEligible=effect=>effect.useForRoleInference!==false&&targetEligible(effect)&&(effect.kind!=="stun"||effect.stunRoleEligible!==false&&effect.useForStunRecommendation!==false);
    function conditionNotes(code){return unique(effectsOf(code).filter(effect=>roleEligible(effect)&&beneficial(effect)&&(!["always","aura"].includes(effect.mode)||(effect.conditions||[]).length)).map(effect=>(units.get(code)?.displayName||code)+" 보조효과 조건: "+((effect.conditions||[]).join(" / ")||effect.mode))).slice(0,3);}
    function effectsOf(code){
      if(effectCache.has(code))return effectCache.get(code);
      if(!native(code))return [];
      const result=(profiles[code]?.effects||[]).filter(effect=>kinds[effect.kind]&&evidence(effect)).map(effect=>({...effect,kind:kinds[effect.kind],rawcode:code,name:units.get(code)?.displayName||profiles[code]?.name||code}));
      effectCache.set(code,result);return result;
    }
    function rolesOf(code){
      if(roleCache.has(code))return roleCache.get(code);
      const result=[];
      const mainType=anchorType(code);if(mainType)result.push(mainType+"Dps");
      for(const effect of effectsOf(code))if(roleEligible(effect)&&beneficial(effect))result.push(effect.kind);
      const roles=unique(result);roleCache.set(code,roles);return roles;
    }
    function ref(code,count){const unit=units.get(code)||{};return {rawcode:code,name:unit.displayName||profiles[code]?.name||unit.name||code,tier:unit.tier||"기타",count:count||1,verified:native(code),roles:rolesOf(code)};}
    function metric(sources){
      const result={value:null,known:false,unit:unique(sources.map(item=>item.unit)).length===1?sources[0]?.unit||null:null,sources,unknownReasons:[]};
      if(!sources.length){result.unknownReasons.push("확인된 효과 없음");return result;}
      if(sources.some(item=>!finite(item.value))){result.unknownReasons.push("효과 수치 미확인");return result;}
      if(sources.some(item=>item.useForNumericAggregation===false)){result.unknownReasons.push("원본 자료에서 실제 수치 합산을 허용하지 않음");return result;}
      if(sources.some(item=>String(item.stacking?.crossGroupRule||"").includes("display_only"))){result.unknownReasons.push("표시용 중첩 규칙은 실제 효과 합산에 사용하지 않음");return result;}
      const groups=new Map();
      for(const source of sources){const group=source.stacking?.group;if(!group){result.unknownReasons.push("중첩 그룹 미확인");continue;}if(!groups.has(group))groups.set(group,[]);groups.get(group).push(source);}
      const totals=[];
      for(const list of groups.values()){
        const rules=unique(list.map(item=>item.stacking?.rule));
        if(rules.length!==1||!["max","min","sum"].includes(rules[0])){result.unknownReasons.push("중첩 규칙 미확인");continue;}
        const rule=rules[0],values=list.map(item=>item.value);
        if(rule==="sum"&&(new Set(list.map(item=>item.abilityRawcode)).size!==list.length||list.some(item=>item.count>1))){result.unknownReasons.push("동일 능력의 중복 합산 미확인");continue;}
        totals.push({rule,value:rule==="max"?Math.max(...values):rule==="min"?Math.min(...values):values.reduce((sum,value)=>sum+value,0)});
      }
      if(new Set(sources.map(item=>item.unit)).size>1)result.unknownReasons.push("서로 다른 단위의 효과");
      if(totals.length>1&&!totals.every(item=>item.rule==="sum"))result.unknownReasons.push("서로 다른 능력 그룹의 합산 미확인");
      if(totals.length>1&&totals.every(item=>item.rule==="sum")&&new Set(sources.map(item=>item.abilityRawcode)).size!==sources.length)result.unknownReasons.push("서로 다른 능력임을 확인하지 못해 합산 보류");
      result.unknownReasons=unique(result.unknownReasons);
      if(!result.unknownReasons.length&&totals.length){result.known=true;result.value=totals.reduce((sum,item)=>sum+item.value,0);}
      return result;
    }
    function sourceList(inventory){
      const result=[];
      for(const [code,count]of inventory)if(count>0)for(const effect of effectsOf(code))result.push({...effect,count});
      return result;
    }
    function modeSources(sources,kind,mode){return sources.filter(item=>item.kind===kind&&targetEligible(item)&&(mode==="always"?["always","aura"].includes(item.mode)&&!(item.conditions||[]).length:mode==="proc"?item.mode==="proc":!["always","aura","proc"].includes(item.mode)||(item.conditions||[]).length));}
    function stunIndicator(effect,speed){
      const base=profiles[effect.rawcode]?.native?.attackCooldown,duration=effect.durationSeconds,chance=effect.chancePercent;
      const target=effect.target||"unknown",scope=["single","area","all","boss"].includes(effect.scope)?effect.scope:"unknown";
      const item={rawcode:effect.rawcode,name:effect.name,abilityRawcode:effect.abilityRawcode||null,scope,target,count:effect.count,mode:effect.mode,
        durationSeconds:finite(duration)?duration:null,chancePercent:finite(chance)?chance:null,baseAttackInterval:finite(base)?base:null,
        intervalSeconds:finite(effect.intervalSeconds)?effect.intervalSeconds:null,attackSpeedBuff:speed.known?speed.value:null,
        modeledAttackInterval:null,modeledUptime:null,modeledMeanGapSeconds:null,known:false,evidence:effect.evidence,unknownReasons:[],
        recommendationEligible:roleEligible(effect),
        assumptions:["사거리 안에서 같은 대상을 계속 공격한다고 가정","각 공격의 발동이 독립이라고 가정","기본 공격주기는 실효 공격속도와 다를 수 있음"]};
      const simpleAttackCondition=text=>/^(?:◎\s*발동조건\s*:\s*)?(?:기본\s*)?공격\s*(?:시|적중\s*시)(?:\s*\d+(?:\.\d+)?%\s*확률)?$/.test(String(text).trim());
      const otherConditions=(effect.conditions||[]).filter(condition=>!simpleAttackCondition(condition));
      item.conditions=effect.conditions||[];
      if(!finite(duration)||duration<0)item.unknownReasons.push("스턴 지속시간 미확인");
      if(!finite(chance)||chance<0||chance>100)item.unknownReasons.push("발동 확률 미확인");
      if(!finite(base)||base<=0)item.unknownReasons.push("기본 공격주기 미확인");
      if(effect.mode!=="proc"&&effect.mode!=="always")item.unknownReasons.push("공격당 발동 방식 미확인");
      if(otherConditions.length)item.unknownReasons.push("추가 발동 조건 미확인");
      if(finite(effect.intervalSeconds)&&effect.intervalSeconds!==base)item.unknownReasons.push("공격주기와 별도의 발동 간격 존재");
      if(item.unknownReasons.length)return item;
      const speedApplies=speed.known&&speed.sources.every(source=>source.scope==="all"||(source.rawcode===effect.rawcode&&source.target==="self"));
      let interval=base;
      if(speedApplies&&speed.value>-100){interval=base/(1+speed.value/100);item.assumptions.push("확인된 공격속도 보조만 단순 배율로 적용");}
      else item.assumptions.push("확인되지 않은 공속·이동·타깃 전환은 계산에 반영하지 않음");
      const probability=chance/100,windows=duration/interval,whole=Math.floor(windows),fraction=windows-whole;
      item.modeledAttackInterval=interval;item.known=true;
      item.modeledUptime=probability===0?0:1-Math.pow(1-probability,whole)*(1-fraction*probability);
      item.modeledMeanGapSeconds=probability===0?null:Math.pow(1-probability,whole)*(interval/probability-fraction*interval);
      return item;
    }
    function diagnose(inventory,settings){
      const roles=Object.fromEntries(roleKeys.map(key=>[key,[]])),sources=sourceList(inventory),unknownConditions=[];
      for(const [code,count]of inventory)if(count>0&&units.get(code)?.playable){
        const unit=ref(code,count);for(const role of unit.roles)roles[role].push(unit);
        if(!unit.verified)unknownConditions.push(unit.name+"의 현재 맵 전투 정보 미확인");
        else if(advanced.has(unit.tier)&&typeTags(code).length&&!anchorType(code))unknownConditions.push(unit.name+"의 주력 물리·마법 분류 미확인");
        unknownConditions.push(...conditionNotes(code));
      }
      const total=(kind,mode="always")=>metric(modeSources(sources,kind,mode));
      const speed=total("attackSpeedBuff"),stuns=sources.filter(item=>item.kind==="stun").map(effect=>stunIndicator(effect,speed));
      const slow={always:total("slow"),proc:total("slow","proc"),conditional:total("slow","conditional")};
      const eligibleStuns=stuns.filter(item=>item.recommendationEligible);
      const stun={sources:stuns,ignoredSources:stuns.filter(item=>!item.recommendationEligible),single:eligibleStuns.filter(item=>item.scope==="single"),area:eligibleStuns.filter(item=>item.scope==="area"||item.scope==="all"),boss:eligibleStuns.filter(item=>item.scope==="boss"||item.target==="boss"),unknown:eligibleStuns.filter(item=>item.scope==="unknown"),hasConfirmedArea:eligibleStuns.some(item=>(item.scope==="area"||item.scope==="all")&&item.durationSeconds>0&&item.chancePercent!==0)};
      const hasSlow=slow.always.sources.some(item=>finite(item.value)&&item.value>0);
      const modeledArea=stun.area.filter(item=>item.known&&finite(item.modeledMeanGapSeconds));
      const longGaps=modeledArea.filter(item=>item.modeledMeanGapSeconds>item.durationSeconds);
      const controlReasons=[];
      if(!hasSlow)controlReasons.push("확인된 상시 이감 근거가 없음");
      if(!stun.hasConfirmedArea)controlReasons.push("추천 가능한 광역 스턴 근거가 없음");
      if(!slow.always.known)controlReasons.push("상시 이감의 실제 합계 미확인");
      if(!modeledArea.length)controlReasons.push("광역 스턴의 확률·지속시간·공격주기 모델 미확인");
      if(longGaps.length)controlReasons.push("일부 광역 스턴의 모델상 평균 공백이 지속시간보다 김");
      if(stun.area.length>1)controlReasons.push("여러 광역 스턴의 동시 발동·대상 관계 미확인");
      const control={slow,stun,needsControl:controlReasons.length>0,assessment:!hasSlow||!stun.hasConfirmedArea||longGaps.length?"보강 우선":"판단 보류",
        reasons:controlReasons,modeledAreaGapSeconds:modeledArea.length?Math.min(...modeledArea.map(item=>item.modeledMeanGapSeconds)):null,
        notes:["이감 합계를 적 정지나 스턴 유지율로 환산하지 않음","기본 공격주기 기반 스턴 수치는 실전 유지율을 보장하지 않음","여러 스턴의 개별 모델 중 짧은 공백을 비교할 뿐 공동 유지율을 뜻하지 않음"]};
      if(sources.some(item=>item.kind==="slow"&&(!item.stacking?.group||item.stacking?.rule==="unknown")))unknownConditions.push("이동속도 감소 효과의 중첩 규칙 미확인");
      const synergy={physical:{armorReduction:total("armorReduction"),attackSpeedBuff:speed,attackDamageBuff:total("attackDamageBuff")},magic:{magicResistanceReduction:total("magicResistanceReduction"),magicAmplification:total("magicAmplification"),attackSpeedBuff:speed},hybrid:{armorReduction:total("armorReduction"),magicResistanceReduction:total("magicResistanceReduction"),magicAmplification:total("magicAmplification"),attackSpeedBuff:speed,attackDamageBuff:total("attackDamageBuff")}};
      function lane(mode){
        const anchors=roles[mode==="physical"?"physicalDps":"magicDps"].filter(unit=>advanced.has(unit.tier));
        const missingRoles=[];
        if(!anchors.length)missingRoles.push("주력 딜러 근거");
        if(!hasSlow)missingRoles.push("상시 이감 근거");
        if(!stun.hasConfirmedArea)missingRoles.push("광역 스턴 근거");
        if(mode==="physical"&&!roles.armorReduction.length)missingRoles.push("방어력 감소 보조");
        if(mode==="magic"&&!roles.magicResistanceReduction.length&&!roles.magicAmplification.length)missingRoles.push("마법 피해 보조");
        return {anchors,missingRoles};
      }
      if(settings.boss!=="none")unknownConditions.push("보스별 면역·대상 제한 미확인");
      const physical=lane("physical"),magic=lane("magic"),hybridAnchors=roles.hybridDps.filter(unit=>advanced.has(unit.tier));
      const hybrid={anchors:hybridAnchors,physicalAnchors:physical.anchors,magicAnchors:magic.anchors,dualCoreReady:Boolean(physical.anchors.length&&magic.anchors.length),missingRoles:unique([!hybridAnchors.length&&!(physical.anchors.length&&magic.anchors.length)?"검토된 하이브리드 또는 물리·마법 두 코어":null,...physical.missingRoles.filter(role=>role!=="주력 딜러 근거"),...magic.missingRoles.filter(role=>role!=="주력 딜러 근거")])};
      return {physical,magic,hybrid,roles,control,synergy,nonSharedEffects:{self:sources.filter(effect=>!targetEligible(effect)&&effect.target==="self"),unknown:sources.filter(effect=>!targetEligible(effect)&&!['self','enemy','ally'].includes(effect.target)),opposing:sources.filter(effect=>!targetEligible(effect)&&['enemy','ally'].includes(effect.target))},unknownConditions:unique(unknownConditions),uncertainDamageUnits:Array.from(inventory).filter(([code,count])=>count>0&&advanced.has(units.get(code)?.tier)&&typeTags(code).length&&!anchorType(code)).map(([code,count])=>ref(code,count))};
    }
    function recommend(counts,settings){
      settings={...(settings||{})};
      settings.boss=["unknown","none","nasujuro","warcury","saturn"].includes(settings.boss)?settings.boss:"unknown";
      settings.round=Number.isSafeInteger(settings.round)&&settings.round>0?settings.round:null;
      if(!finite(settings.wood)||settings.wood<0)settings.wood=null;
      const start=Date.now(),inventory=countsOf(counts),current=diagnose(inventory,settings);
      const encounterContext=encounter(settings);current.encounterContext=encounterContext;current.encounterNotes=encounterContext.notes;
      if(encounterContext.lateRound)current.control.notes.push("현재 라운드의 적 카운트 제한을 고려해 광역 제어 보강을 먼저 검토");
      current.resources={wood:{known:finite(settings.wood),value:settings.wood}};
      const resourcesReady=(wood,gold)=>wood>0&&finite(settings.wood)&&settings.wood<wood?false:wood>0&&!finite(settings.wood)||gold>0?null:true;
      const warnings=[],assumptions=["추천은 역할 구성과 조합 재료를 대조한 후보이며 악몽 클리어를 보장하지 않음","수치가 없는 발동 확률·중첩·공격주기는 미확인으로 유지","스턴 지표는 상시 공격·독립 발동 가정의 보조 모델"];
      warnings.push(...encounterContext.warnings);
      if(!mapCompatible)warnings.push("전투 자료와 현재 맵의 버전 일치를 확인하지 못했습니다.");
      const pendingAdvice=(label,reason)=>({mode:null,label,reason,basis:[],conditional:true,firstPriorities:[]});
      if(counts==null)return {schemaVersion:1,connected:false,mapCompatible,current,recommendations:{physical:[],magic:[],hybrid:[]},immediateSupports:[],nextPriorities:[],directionAdvice:pendingAdvice("보유 정보 대기","실시간 보유 유닛을 확인한 뒤 투자 방향을 비교합니다."),warnings:[...warnings,"보유 유닛 정보가 연결되지 않았습니다."],assumptions};
      if(typeof graph.planDeck!=="function")return {schemaVersion:1,mapCompatible,current,recommendations:{physical:[],magic:[],hybrid:[]},nextPriorities:[],directionAdvice:pendingAdvice("판단 보류","공동 재료 계획을 확인하지 못했습니다."),warnings:[...warnings,"공동 재료 계획을 사용할 수 없습니다."],assumptions};
      const allowed=settings.supportedRecipeIds==null?null:new Set(settings.supportedRecipeIds),all=Array.from(units.values()).filter(unit=>unit.playable&&profiles[unit.rawcode]);
      const byOwned=(a,b)=>(inventory.get(b.rawcode)||0)-(inventory.get(a.rawcode)||0)||Number(native(b.rawcode))-Number(native(a.rawcode))||a.rawcode.localeCompare(b.rawcode);
      const anchorEstimates=new Map();
      function anchorEstimate(code){if(anchorEstimates.has(code))return anchorEstimates.get(code);
        let estimate={owned:inventory.get(code)||0,ratio:0,materialsReady:false,choiceWisps:null,steps:null,wood:null,gold:null,resourcesReady:null,estimateOnly:true};
        if(estimate.owned>0)estimate={...estimate,ratio:1,materialsReady:true,choiceWisps:0,steps:0,wood:0,gold:0,resourcesReady:true};
        else {const proposed=typeof graph.plan==="function"?graph.plan(code,inventory,{supportedRecipeIds:allowed,allowChoiceWisps:settings.allowChoiceWisps===true,maxSteps:settings.maxSteps||128,maxSearchNodes:Math.min(settings.maxSearchNodes||2000,300)}):null;
          const progress=typeof graph.analyze==="function"?graph.analyze(code,inventory):null;
          const wood=(proposed?.nonUnitRequirements||[]).filter(item=>String(item.type).toUpperCase()==="WOOD").reduce((sum,item)=>sum+(finite(item.count)?item.count:0),0),gold=(proposed?.nonUnitRequirements||[]).filter(item=>String(item.type).toUpperCase()==="GOLD").reduce((sum,item)=>sum+(finite(item.count)?item.count:0),0);
          estimate={...estimate,ratio:progress?.ratio||0,materialsReady:proposed?.executable===true,choiceWisps:proposed?.executable?proposed.choiceWispCount:null,steps:proposed?.executable?proposed.stepCount:null,wood:proposed?.executable?wood:null,gold:proposed?.executable?gold:null,resourcesReady:proposed?.executable?resourcesReady(wood,gold):null};}
        anchorEstimates.set(code,estimate);return estimate;}
      const byAnchorProximity=(a,b)=>{const x=anchorEstimate(a.rawcode),y=anchorEstimate(b.rawcode);return y.owned-x.owned||Number(y.materialsReady)-Number(x.materialsReady)||(x.choiceWisps??Infinity)-(y.choiceWisps??Infinity)||y.ratio-x.ratio||(x.steps??Infinity)-(y.steps??Infinity)||a.rawcode.localeCompare(b.rawcode);};
      const limit=Math.max(3,Math.min(30,settings.maxCandidatePlans||12)),maxMilliseconds=Math.max(10,Math.min(500,settings.maxMilliseconds||200));
      const recommendations={physical:[],magic:[],hybrid:[]};let attempted=0,searchLimited=false;
      const currentAnchorCodes=unique([...current.physical.anchors,...current.magic.anchors,...current.hybrid.anchors,...current.uncertainDamageUnits].map(unit=>unit.rawcode));
      const currentSupportCodes=Array.from(inventory).filter(([code,count])=>count>0&&advanced.has(units.get(code)?.tier)&&rolesOf(code).some(kind=>supportKinds.includes(kind))).map(([code])=>code);
      function coreFor(newCodes,anchor,mode){
        return unique([...currentAnchorCodes,...currentSupportCodes]).filter(code=>{
          if(newCodes.includes(code))return true;
          const type=anchorType(code);
          if(!type||!newCodes.some(target=>hasMaterialAncestor(target,code)))return true;
          // 검토된 코어가 재료인 승급·방향 전환에서는 새 목표가 대체한다.
          // 부가 제어·보조까지 사라지는 경우에는 원래 유닛도 보존한다.
          const oldRoles=rolesOf(code).filter(kind=>supportKinds.includes(kind));
          if(oldRoles.some(kind=>!newCodes.some(target=>rolesOf(target).includes(kind))))return true;
          const oldScopes=effectsOf(code).filter(effect=>effect.kind==="stun"&&beneficial(effect)).map(effect=>effect.scope);
          if(oldScopes.some(scope=>["area","all"].includes(scope)&&!newCodes.some(target=>effectsOf(target).some(effect=>effect.kind==="stun"&&beneficial(effect)&&["area","all"].includes(effect.scope)))))return true;
          return false;
        });
      }
      function nextFor(plan,goals){
        const step=plan.steps?.[0];
        if(step){const toward=goals.find(goal=>(inventory.get(goal.rawcode)||0)<goal.count&&(goal.rawcode===step.resultRawcode||hasMaterialAncestor(goal.rawcode,step.resultRawcode)))||goals.find(goal=>(inventory.get(goal.rawcode)||0)<goal.count)||goals[0];return [{rawcode:step.resultRawcode,name:ref(step.resultRawcode).name,tier:ref(step.resultRawcode).tier,recipeId:step.recipeId||null,count:step.resultCount||1,action:step.type==="choice"?"openUnit":"recipe",sourceRawcode:step.type==="choice"?"e018":undefined,towardRawcode:toward?.rawcode,towardName:toward?ref(toward.rawcode).name:null,towardTier:toward?ref(toward.rawcode).tier:null,reason:step.type==="choice"?"부족한 흔함을 선택 위습으로 확보":"덱 목표의 하위 재료부터 제작",ready:true}];}
        const goal=goals.find(item=>(inventory.get(item.rawcode)||0)<item.count);
        if(!goal)return [];
        const recipe=(recipes.get(goal.rawcode)||[]).find(item=>!allowed||allowed.has(String(item.id)));
        return [{...ref(goal.rawcode),recipeId:recipe?.id||null,action:"openUnit",reason:"부족한 재료와 조합 조건 확인",ready:false}];
      }
      function roleDelta(before,after){
        const gained=[],lost=[],retained=[],lostKinds=[];
        const present=(state,kind)=>state.roles[kind].length>0||["physicalDps","magicDps"].includes(kind)&&state.roles.hybridDps.length>0;
        for(const kind of roleKeys){const a=present(before,kind),b=present(after,kind);if(a&&b)retained.push(roleNames[kind]);else if(b)gained.push(roleNames[kind]);else if(a){lost.push(roleNames[kind]);lostKinds.push(kind);}}
        const consumed=[];for(const [code,count]of inventory)if(count>0&&rolesOf(code).length&&(after.inventory?.get(code)||0)<count)consumed.push(ref(code).name);
        return {gained,lost,retained,lostKinds,consumed};
      }
      for(const mode of ["physical","magic","hybrid"]){
        const modeStart=Date.now();
        if(settings.direction&&settings.direction!=="all"&&settings.direction!==mode)continue;
        let anchors=all.filter(unit=>finalTiers.has(unit.tier)&&anchorType(unit.rawcode)===mode).sort(byAnchorProximity).slice(0,4).map(unit=>({...unit,coreCodes:[unit.rawcode],hybridKind:mode==="hybrid"?"reviewed-hybrid":null}));
        if(mode==="hybrid"){
          const physical=all.filter(unit=>anchorType(unit.rawcode)==="physical"&&(finalTiers.has(unit.tier)||inventory.get(unit.rawcode)>0)).sort(byAnchorProximity).slice(0,3);
          const magical=all.filter(unit=>anchorType(unit.rawcode)==="magic"&&(finalTiers.has(unit.tier)||inventory.get(unit.rawcode)>0)).sort(byAnchorProximity).slice(0,3);
          const pairs=[];for(const a of physical)for(const b of magical)pairs.push({...a,coreCodes:[a.rawcode,b.rawcode],secondaryRawcode:b.rawcode,hybridKind:"dual-core"});
          pairs.sort((a,b)=>b.coreCodes.filter(code=>inventory.get(code)>0).length-a.coreCodes.filter(code=>inventory.get(code)>0).length||a.coreCodes.join().localeCompare(b.coreCodes.join()));
          anchors=[...anchors,...pairs.slice(0,Math.max(1,4-anchors.length))];
        }
        const controls=all.filter(unit=>advanced.has(unit.tier)&&rolesOf(unit.rawcode).includes("slow")).sort(byOwned).slice(0,4);
        const areaStuns=all.filter(unit=>advanced.has(unit.tier)&&effectsOf(unit.rawcode).some(effect=>effect.kind==="stun"&&roleEligible(effect)&&["area","all"].includes(effect.scope)&&beneficial(effect))).sort(byOwned).slice(0,4);
        const synergyKinds=mode==="physical"?["armorReduction","attackSpeedBuff","attackDamageBuff"]:mode==="magic"?["magicResistanceReduction","magicAmplification","attackSpeedBuff"]:["armorReduction"];
        const supports=all.filter(unit=>advanced.has(unit.tier)&&rolesOf(unit.rawcode).some(role=>synergyKinds.includes(role))).sort(byOwned).slice(0,4);
        const buffs=all.filter(unit=>advanced.has(unit.tier)&&rolesOf(unit.rawcode).some(role=>["attackSpeedBuff","attackDamageBuff"].includes(role))).sort(byOwned).slice(0,3);
        const hybridMagicSupports=mode==="hybrid"?all.filter(unit=>advanced.has(unit.tier)&&rolesOf(unit.rawcode).some(role=>["magicResistanceReduction","magicAmplification"].includes(role))).sort(byOwned).slice(0,4):[];
        if(!anchors.length){warnings.push((mode==="physical"?"물리":mode==="magic"?"마법":"하이브리드")+" 주력 후보의 맵 근거가 부족합니다.");continue;}
        const combinations=[];
        for(let variant=0;variant<4;variant++)for(const anchor of anchors){
          const control=controls.length?controls[variant%controls.length]:null,support=supports.length?supports[variant%supports.length]:null;
          const areaStun=areaStuns.length?areaStuns[(variant+1)%areaStuns.length]:null,buff=buffs.length?buffs[variant%buffs.length]:null;
          const hybridMagic=hybridMagicSupports.length?hybridMagicSupports[variant%hybridMagicSupports.length]:null;
          const selected=unique([...anchor.coreCodes,control?.rawcode,areaStun?.rawcode,support?.rawcode,hybridMagic?.rawcode,buff?.rawcode]);
          const codes=unique([...coreFor(selected,anchor,mode),...selected]);
          const key=codes.slice().sort().join("|");if(!combinations.some(item=>item.key===key&&item.anchor.coreCodes.join()===anchor.coreCodes.join()))combinations.push({anchor,control,areaStun,support,hybridMagic,buff,codes,key});
        }
        // 현재 보유한 주력과 추가 유닛 수가 적은 구성을 먼저 살펴본다.
        let count=0;
        for(const combination of combinations){
          if(count>=limit){searchLimited=true;break;}
          if(count>=Math.min(3,anchors.length)&&Date.now()-modeStart>maxMilliseconds/4){searchLimited=true;break;}
          count++;attempted++;
          const goals=combination.codes.map(rawcode=>({rawcode,count:1}));
          const plan=graph.planDeck(goals,inventory,{supportedRecipeIds:allowed,allowChoiceWisps:settings.allowChoiceWisps===true,protectedCounts:settings.protectedCounts,maxSteps:settings.maxSteps||128,maxSearchNodes:settings.maxSearchNodes||2000});
          const finalInventory=plan.executable?countsOf(plan.finalCounts):new Map(inventory),after=diagnose(finalInventory,settings);after.inventory=finalInventory;
          const changes=roleDelta(current,after),conditions=(plan.nonUnitRequirements||[]),wood=conditions.filter(item=>String(item.type).toUpperCase()==="WOOD").reduce((sum,item)=>sum+(finite(item.count)?item.count:0),0),gold=conditions.filter(item=>String(item.type).toUpperCase()==="GOLD").reduce((sum,item)=>sum+(finite(item.count)?item.count:0),0);
          const unknownConditions=[];
          for(const goal of goals)if(!native(goal.rawcode))unknownConditions.push(ref(goal.rawcode).name+"의 전투 역할 미확인");
          for(const goal of goals)unknownConditions.push(...conditionNotes(goal.rawcode));
          if(!combination.control)unknownConditions.push("제어 보조 후보 미확인");
          if(!combination.areaStun)unknownConditions.push("광역 스턴 보조 후보 미확인");
          if(!combination.support)unknownConditions.push("시너지 보조 후보 미확인");
          if(mode==="hybrid"&&!combination.hybridMagic)unknownConditions.push("마법 피해 시너지 보조 후보 미확인");
          if(conditions.some(item=>!["WOOD","GOLD"].includes(String(item.type).toUpperCase())))unknownConditions.push("밴·항법·추가 조합 조건 확인 필요");
          if(wood>0&&!finite(settings.wood))unknownConditions.push("보유 목재 미확인");
          if(wood>0&&finite(settings.wood)&&settings.wood<wood)unknownConditions.push("목재 "+(wood-settings.wood)+"개 부족");
          if(gold>0)unknownConditions.push("보유 골드 미확인");
          if(!plan.executable)unknownConditions.push(plan.reason==="search_limit"||plan.reason==="step_limit"?"계획 탐색 범위 내에서 제작 가능성을 확인하지 못함":"재료가 부족하거나 하위 자동 조합을 지원하지 않음");
          if(settings.boss!=="none")unknownConditions.push("보스별 면역·대상 제한 미확인");
          if(changes.lost.length)unknownConditions.push("교체 후 사라지는 역할: "+changes.lost.join(", "));
          const goalRows=goals.map(goal=>({...ref(goal.rawcode),count:goal.count,owned:inventory.get(goal.rawcode)||0,needed:Math.max(0,goal.count-(inventory.get(goal.rawcode)||0))}));
          const anchorRefs=combination.anchor.coreCodes.map(code=>ref(code));
          const candidate={id:mode+":"+combination.anchor.coreCodes.join("+")+":"+combination.key,mode,title:anchorRefs.map(unit=>unit.name).join(" + ")+" 목표 빌드 후보",stage:"final-target",anchor:anchorRefs[0],anchors:anchorRefs,hybridKind:combination.anchor.hybridKind,target:anchorRefs.find(unit=>!(inventory.get(unit.rawcode)>0))||anchorRefs[0],goals:goalRows,
            rawcode:combination.anchor.rawcode,name:ref(combination.anchor.rawcode).name,tier:combination.anchor.tier,
            materialStatus:plan.executable?(plan.stepCount?"craftable":"owned"):["step_limit","search_limit"].includes(plan.reason)?"limited":"missing",
            materialsReady:plan.executable,stock:plan.executable&&!plan.stepCount,craftable:plan.executable&&plan.stepCount>0,owned:inventory.get(combination.anchor.rawcode)||0,
            missing:(plan.missing||[]).map(item=>({...item,name:ref(item.rawcode).name})),missingIsEstimate:!plan.executable,
            choiceWisps:plan.executable?(plan.choiceWispCount||0):null,wood:plan.executable?wood:null,gold:plan.executable?gold:null,woodRequired:plan.executable?wood:null,woodKnown:finite(settings.wood),resourcesReady:!plan.executable?null:resourcesReady(wood,gold),
            steps:plan.stepCount||0,planSteps:plan.steps||[],finalCounts:plan.executable?plan.finalCounts:null,
            control:after.control,synergy:after.synergy,roleChanges:changes,nextPriorities:nextFor(plan,goals),unknownConditions:unique(unknownConditions),
            reasons:unique([mode==="hybrid"?(combination.anchor.hybridKind==="dual-core"?"검토된 물리·마법 두 코어와 양쪽 시너지 재료를 공동 계산":"원본 검토된 하이브리드 코어와 양쪽 시너지 재료를 공동 계산"):null,combination.control?"제어 보조를 함께 보유":"제어 보조 보강 필요",combination.support?"피해 시너지 보조를 함께 보유":"피해 시너지 보강 필요",currentAnchorCodes.length?"현재 주력 투자를 보존하거나 검토된 승급·교체 경로를 사용":null,changes.lost.length?"제작 후 사라지는 역할 확인 필요":null]),
            roleCoverage:roleKeys.filter(kind=>after.roles[kind].length>0).length,reason:plan.reason||null,projectionVerified:plan.executable,optimality:"first-feasible",conditional:unknownConditions.length>0};
          recommendations[mode].push(candidate);
        }
        recommendations[mode].sort((a,b)=>Number(b.materialsReady)-Number(a.materialsReady)||resourceRank(b.resourcesReady)-resourceRank(a.resourcesReady)||a.roleChanges.lost.length-b.roleChanges.lost.length||b.roleCoverage-a.roleCoverage||Number(b.anchor.verified)-Number(a.anchor.verified)||(a.choiceWisps??Infinity)-(b.choiceWisps??Infinity)||(a.wood??Infinity)-(b.wood??Infinity)||a.steps-b.steps||a.id.localeCompare(b.id));
        const diverse=[],seenAnchors=new Set();for(const item of recommendations[mode]){const key=item.anchors.map(unit=>unit.rawcode).join("+");if(!seenAnchors.has(key)){diverse.push(item);seenAnchors.add(key);}}
        recommendations[mode]=[...diverse,...recommendations[mode].filter(item=>!diverse.includes(item))].slice(0,3);
      }
      const protectedCore=new Map();for(const [code,count]of inventory)if(count>0&&advanced.has(units.get(code)?.tier)&&(rolesOf(code).length||typeTags(code).length))protectedCore.set(code,1);
      for(const [code,count]of countsOf(settings.protectedCounts))protectedCore.set(code,Math.max(protectedCore.get(code)||0,count));
      const immediateSupports=[];
      const supportStart=Date.now();let supportAttempts=0;
      const reinforces=all.filter(unit=>advanced.has(unit.tier)&&!(inventory.get(unit.rawcode)>0)&&rolesOf(unit.rawcode).some(role=>supportKinds.includes(role)));
      const roleGain=unit=>rolesOf(unit.rawcode).filter(kind=>supportKinds.includes(kind)&&!current.roles[kind].length).length;
      const reviewedSupport=unit=>Boolean(profiles[unit.rawcode]?.damage?.reviewedCombatRole&&!String(profiles[unit.rawcode].damage.reviewedCombatRole).endsWith("Dps"));
      const controlGain=unit=>current.control.needsControl&&effectsOf(unit.rawcode).some(effect=>roleEligible(effect)&&beneficial(effect)&&(effect.kind==="slow"||effect.kind==="stun"&&["area","all"].includes(effect.scope)));
      const isAreaControl=unit=>effectsOf(unit.rawcode).some(effect=>effect.kind==="stun"&&roleEligible(effect)&&["area","all"].includes(effect.scope));
      reinforces.sort((a,b)=>encounterContext.lateRound?(Number(isAreaControl(b))-Number(isAreaControl(a))||Number(controlGain(b))-Number(controlGain(a))||Number(reviewedSupport(b))-Number(reviewedSupport(a))||roleGain(b)-roleGain(a)||a.rawcode.localeCompare(b.rawcode)):(Number(controlGain(b))-Number(controlGain(a))||Number(reviewedSupport(b))-Number(reviewedSupport(a))||roleGain(b)-roleGain(a)||a.rawcode.localeCompare(b.rawcode)));
      for(const unit of reinforces.slice(0,20)){
        if(supportAttempts>=Math.min(12,reinforces.length)&&Date.now()-supportStart>maxMilliseconds/4){searchLimited=true;break;}
        attempted++;supportAttempts++;
        const plan=graph.planDeck([{rawcode:unit.rawcode,count:1}],inventory,{supportedRecipeIds:allowed,allowChoiceWisps:settings.allowChoiceWisps===true,protectedCounts:protectedCore,maxSteps:settings.maxSteps||128,maxSearchNodes:settings.maxSearchNodes||2000});
        if(!plan.executable||!plan.steps.length)continue;
        const after=diagnose(countsOf(plan.finalCounts),settings);after.inventory=countsOf(plan.finalCounts);
        const wood=plan.nonUnitRequirements.filter(item=>String(item.type).toUpperCase()==="WOOD").reduce((sum,item)=>sum+(finite(item.count)?item.count:0),0),gold=plan.nonUnitRequirements.filter(item=>String(item.type).toUpperCase()==="GOLD").reduce((sum,item)=>sum+(finite(item.count)?item.count:0),0),changes=roleDelta(current,after);
        const purpose=effectsOf(unit.rawcode).some(effect=>effect.kind==="stun"&&roleEligible(effect)&&["area","all"].includes(effect.scope))?"광역 스턴":rolesOf(unit.rawcode).includes("slow")?"이감":rolesOf(unit.rawcode).includes("armorReduction")?"방어력 감소":"피해 시너지";
        const priorities=nextFor(plan,[{rawcode:unit.rawcode,count:1}]).map(item=>({...item,targetRawcode:unit.rawcode,targetName:ref(unit.rawcode).name,targetTier:ref(unit.rawcode).tier,towardRawcode:unit.rawcode,towardName:ref(unit.rawcode).name,towardTier:ref(unit.rawcode).tier,jointlyProtected:true,kind:"reinforcement",reason:ref(unit.rawcode).name+" "+purpose+" 보강을 위한 "+item.name+(item.action==="recipe"?" 제작":" 확보")}));
        const ownArea=after.control.stun.area.filter(item=>item.rawcode===unit.rawcode&&item.known&&finite(item.modeledMeanGapSeconds));
        const modeledAreaGapSeconds=ownArea.length?Math.min(...ownArea.map(item=>item.modeledMeanGapSeconds)):null;
        immediateSupports.push({...ref(unit.rawcode),stage:"reinforcement",protectedCounts:Object.fromEntries(protectedCore),materialsReady:true,choiceWisps:plan.choiceWispCount,wood,gold,woodKnown:finite(settings.wood),resourcesReady:resourcesReady(wood,gold),steps:plan.stepCount,roles:rolesOf(unit.rawcode),control:after.control,synergy:after.synergy,roleChanges:changes,nextPriorities:priorities,
          newRoleCount:changes.gained.length,addsAreaStun:!current.control.stun.hasConfirmedArea&&after.control.stun.hasConfirmedArea,
          addsAlwaysSlow:!current.control.slow.always.sources.some(item=>finite(item.value)&&item.value>0)&&after.control.slow.always.sources.some(item=>finite(item.value)&&item.value>0),
          modeledAreaGapSeconds,gapComparison:{current:current.control.modeledAreaGapSeconds,candidate:modeledAreaGapSeconds,assumedContinuousAttacks:true,combinedUptimeKnown:false},
          evidence:unique([ownArea.length?"광역 스턴의 기본 공격주기 모델을 비교할 수 있음":null,ownArea.some(item=>item.modeledMeanGapSeconds>item.durationSeconds)?"모델상 평균 공백이 지속시간보다 길어 추가 제어 검토 필요":null,modeledAreaGapSeconds===null&&rolesOf(unit.rawcode).includes("stun")?"스턴 공백 수치 미확인":null]),
          unknownConditions:unique([...conditionNotes(unit.rawcode),wood&&!finite(settings.wood)?"보유 목재 미확인":null,wood&&finite(settings.wood)&&settings.wood<wood?"목재 부족":null,gold?"보유 골드 미확인":null,plan.nonUnitRequirements.some(item=>!['WOOD','GOLD'].includes(String(item.type).toUpperCase()))?"추가 조합 조건 확인 필요":null]),finalCounts:plan.finalCounts});
      }
      immediateSupports.sort((a,b)=>resourceRank(b.resourcesReady)-resourceRank(a.resourcesReady)||Number(b.addsAreaStun)-Number(a.addsAreaStun)||Number(b.addsAlwaysSlow)-Number(a.addsAlwaysSlow)||(a.modeledAreaGapSeconds??Infinity)-(b.modeledAreaGapSeconds??Infinity)||b.newRoleCount-a.newRoleCount||a.choiceWisps-b.choiceWisps||a.wood-b.wood||a.steps-b.steps||a.rawcode.localeCompare(b.rawcode));
      function directionAdvice(){
        const physical=current.physical.anchors,magic=current.magic.anchors,hybrid=current.hybrid.anchors;
        const names=list=>list.map(unit=>unit.name+" "+unit.count+"기").join(", ");
        const basis=["현재 보유 투자와 검토된 역할 기준이며 클리어 승률 비교가 아님"];
        let mode=null,label="판단 보류",reason="검토된 주력이 없고 현재 재료·자원으로 우선할 코어를 확인하지 못했습니다.";
        if(hybrid.length){mode="hybrid";label="현재 투자 기준 하이브리드 우선";reason=names(hybrid)+"를 보유해 혼합 주력 투자를 이어갈 수 있습니다. 양쪽 시너지 비용은 별도로 확인해야 합니다.";basis.push("원본 검토된 하이브리드 주력 보유: "+names(hybrid));}
        else if(physical.length&&magic.length){mode="hybrid";label="현재 투자 기준 혼합 구성 검토";reason=names(physical)+"와 "+names(magic)+"를 함께 보유합니다. 기존 두 축을 활용할 수 있지만 어느 방향이 최적인지는 조건부입니다.";basis.push("물리·마법 두 코어의 기존 투자를 보유");}
        else if(physical.length||magic.length){mode=physical.length?"physical":"magic";const list=physical.length?physical:magic;label="현재 투자 기준 "+(mode==="physical"?"물딜":"마딜")+" 우선";reason=names(list)+"를 이미 보유합니다. 반대 방향은 새 주력 코어를 추가로 만들어야 하므로 현재 투자 기준으로 이 방향을 먼저 보강합니다.";basis.push("검토된 주력 보유: "+names(list));basis.push("반대 방향 검토 주력 보유 0기");}
        else{
          const available=all.filter(unit=>finalTiers.has(unit.tier)&&anchorType(unit.rawcode)).map(unit=>({unit,estimate:anchorEstimate(unit.rawcode)})).filter(item=>item.estimate.materialsReady&&item.estimate.resourcesReady!==false);
          available.sort((a,b)=>resourceRank(b.estimate.resourcesReady)-resourceRank(a.estimate.resourcesReady)||a.estimate.choiceWisps-b.estimate.choiceWisps||a.estimate.wood-b.estimate.wood||a.estimate.steps-b.estimate.steps||a.unit.rawcode.localeCompare(b.unit.rawcode));
          if(available.length){const closest=available[0];mode=anchorType(closest.unit.rawcode);label="재료 기준 "+(mode==="physical"?"물딜":mode==="magic"?"마딜":"하이브리드")+" 검토";reason=ref(closest.unit.rawcode).name+" 코어는 현재 유닛·선위로 제작 경로를 확인했습니다. 완성 빌드 전체와 보스·추가 조건의 우위는 별도 확인이 필요합니다.";basis.push("가까운 검토 코어: "+ref(closest.unit.rawcode).name+", 선위 "+closest.estimate.choiceWisps+"개, 목재 "+closest.estimate.wood+"개");if(closest.estimate.resourcesReady===null)basis.push("보유 목재·골드 등 자원 조건 미확인");}
        }
        const relevant=mode==="physical"?["slow","stun","armorReduction","attackSpeedBuff","attackDamageBuff"]:mode==="magic"?["slow","stun","magicResistanceReduction","magicAmplification","attackSpeedBuff"]:supportKinds;
        const selected=immediateSupports.filter(item=>item.roles.some(role=>relevant.includes(role)));
        selected.sort((a,b)=>resourceRank(b.resourcesReady)-resourceRank(a.resourcesReady)||Number(b.roles.some(role=>role==="slow"||role==="stun"))-Number(a.roles.some(role=>role==="slow"||role==="stun")));
        const firstPriorities=mode?uniquePriorities([...selected.slice(0,3),...(recommendations[mode]||[]).filter(item=>item.materialsReady)].flatMap(item=>item.nextPriorities)):[];
        if(mode&&firstPriorities.length)basis.push("첫 보강 목표: "+(firstPriorities[0].towardName||firstPriorities[0].name));
        return {mode,label,reason,basis,conditional:true,firstPriorities};
      }
      const advice=directionAdvice();
      if(searchLimited)warnings.push("일부 후보만 살펴본 결과입니다.");
      return {schemaVersion:1,connected:counts!=null,mapCompatible,current,recommendations,directionAdvice:advice,immediateSupports:immediateSupports.slice(0,6),nextPriorities:advice.mode?advice.firstPriorities:uniquePriorities([...immediateSupports.slice(0,3),...[...recommendations.physical,...recommendations.magic,...recommendations.hybrid].filter(item=>item.materialsReady)].flatMap(item=>item.nextPriorities)),warnings:unique(warnings),assumptions,planningMs:Date.now()-start,attemptedCandidates:attempted,searchLimited};
    }
    return {recommend};
  }
  function uniquePriorities(items){const seen=new Set();return items.filter(item=>{const key=item.rawcode+"|"+item.recipeId;if(seen.has(key))return false;seen.add(key);return true;}).slice(0,6);}
  return {create};
});
