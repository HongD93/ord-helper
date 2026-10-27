(function(root){
"use strict";
const lanes={physical:"물딜",magic:"마딜",hybrid:"하이브리드"};
const elders={warcury:"워큐리",saturn:"새턴",nasujuro:"나스쥬로"};
const itemNames={AI00:"우타의 헤드셋",AI01:"태양신의 흔적",AI02:"불사조의 깃털"};
const nextTiers=new Set(["희귀","전설","히든","초월","불멸","제한","영원","신비","특수","변화","왜곡","세라핌"]);
const finalTiers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]);
const list=value=>Array.isArray(value)?value:[];
const text=value=>typeof value==="string"?value.trim():"";
const code=value=>typeof value==="string"&&/^[A-Za-z0-9]{4}$/.test(value);
const finite=value=>typeof value==="number"&&Number.isFinite(value);
const kingPathModes=new Set(["off","standard","martial_law"]);
const reinforcementRoles=new Set(["auto","stun","physical_shred","magic_shred"]);
function normalize(value){
 const optional=(n,min)=>n===null||n===undefined||n===""?null:Number.isFinite(Number(n))&&Number(n)>=min?Math.floor(Number(n)):null;
 const kingPathMode=Object.hasOwn(value,"kingPathMode")?(kingPathModes.has(value.kingPathMode)?value.kingPathMode:"off"):value.includeKingPathDecks===true?"standard":"off";
 return{boss:Object.hasOwn(elders,value.boss)||value.boss==="none"?value.boss:"unknown",round:optional(value.round,1),wood:optional(value.wood,0),direction:Object.hasOwn(lanes,value.direction)?value.direction:"all",preferredDeckId:text(value.preferredDeckId).slice(0,160)||null,kingPathMode,includeKingPathDecks:kingPathMode!=="off",includeMysteriousDecks:value.includeMysteriousDecks===true,reinforcementRole:reinforcementRoles.has(value.reinforcementRole)?value.reinforcementRole:"auto"};
}
function create(options){
 const host=options?.host;if(!host||typeof host.append!=="function")throw new TypeError("A panel host is required.");
 const doc=host.ownerDocument||root.document;
 const el=(tag,cls,value)=>{const node=doc.createElement(tag);if(cls)node.className=cls;if(value!==undefined)node.textContent=String(value);return node;};
 if(!doc.getElementById("ord-nightmare-panel-style")){
  const style=el("style");style.id="ord-nightmare-panel-style";style.textContent=`.ord-na{margin:4px 3px 6px;padding:7px 8px;border:1px solid #ded6ed;border-radius:4px;background:#fff;color:#64596f;font-size:10px}.ord-na-head{display:flex;align-items:center;gap:7px;flex-wrap:wrap;margin-bottom:6px}.ord-na-title{margin:0;font-size:11px;color:#6753ac}.ord-na-chip{padding:2px 6px;border-radius:3px;background:#eee8fb;color:#7c65a7;font-size:9px}.ord-na-select{margin-left:auto;max-width:350px;min-height:24px;padding:3px 6px;border:1px solid #ded5ea;background:#fff;color:#6f5d88;font:inherit}.ord-na-roster{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:5px}.ord-na-unit{display:grid;grid-template-columns:18px minmax(0,1fr) auto;align-items:center;gap:3px 5px;padding:5px 6px;min-width:0;border:1px solid #e7e0ef;border-radius:3px;background:#faf8ff}.ord-na-number{grid-row:1 / span 2;color:#a697b4;font-variant-numeric:tabular-nums}.ord-na-name{padding:0;min-width:0;border:0;background:none;color:#665086;text-align:left;font:inherit;cursor:pointer;overflow-wrap:anywhere}.ord-na-name:disabled{cursor:default;color:#84788f}.ord-na-count{grid-column:2;font-size:9px;color:#a193ad}.ord-na-action{grid-column:3;grid-row:1 / span 2;padding:5px 8px;border:0;border-radius:3px;background:#6b55bd;color:#fff;font:inherit;cursor:pointer}.ord-na-action:disabled{background:#e9e3f0;color:#aa9cb7;cursor:default}.ord-na-owned{background:#f5faf6;border-color:#dce9df}.ord-na-owned .ord-na-count{color:#7d9a86}.ord-na-next{display:flex;align-items:center;flex-wrap:wrap;gap:6px;margin-top:6px;padding-top:6px;border-top:1px solid #ece5f2}.ord-na-next-label{color:#8c7a9e}.ord-na-next-name{padding:0;border:0;background:none;color:#65479a;font:inherit;cursor:pointer}.ord-na-next-cost{color:#a18fae;font-size:9px}.ord-na-condition{margin-top:5px;color:#a18891;font-size:9px}.ord-na-condition summary{cursor:pointer}.ord-na-condition ul{margin:4px 0;padding-left:15px;line-height:1.5}.ord-na-status{margin-top:5px;color:#96859e;line-height:1.5}.ord-na-status[data-tone=error]{color:#af566a}.ord-na-empty{padding:4px 0;color:#a093ad}.ord-na button:focus-visible,.ord-na select:focus-visible,.ord-na summary:focus-visible{outline:2px solid #9f89da;outline-offset:2px}@media(max-width:1000px){.ord-na-roster{grid-template-columns:repeat(3,minmax(0,1fr))}}@media(max-width:700px){.ord-na-roster{grid-template-columns:repeat(2,minmax(0,1fr))}.ord-na-select{max-width:100%;margin-left:0}}@media(max-width:420px){.ord-na-roster{grid-template-columns:1fr}}`;
  style.textContent+=".ord-na-unit{grid-template-columns:18px minmax(0,1fr) auto auto}.ord-na-unit-state{grid-column:4;grid-row:1 / span 2;padding:5px 7px;border-radius:3px;background:#e9e3f0;color:#aa9cb7}.ord-na-owned .ord-na-unit-state{background:#e5f1e8;color:#7d9a86}";
  style.textContent+=".ord-na-name{position:relative;isolation:isolate;min-height:19px;border-radius:2px;padding:2px 3px}.ord-na-progress{position:absolute;inset:0;z-index:-1;overflow:hidden;border-radius:2px;background:#eeeaf5}.ord-na-progress-current,.ord-na-progress-choice{position:absolute;inset:0 auto 0 0;opacity:.28;background:#2fa467}.ord-na-progress-choice{background:#f4ca43}.ord-na-progress-choice[data-ready=true]{background:#e75c66}";
  style.textContent+=".ord-na-controls{display:flex;align-items:center;flex-wrap:wrap;gap:5px;margin-left:auto;max-width:100%}.ord-na-controls .ord-na-select{margin-left:0}.ord-na-toggle{min-height:24px;padding:3px 7px;border:1px solid #ded5ea;border-radius:3px;background:#fff;color:#8d7d9e;font:inherit;cursor:pointer}.ord-na-toggle[aria-pressed=true]{border-color:#7d67bd;background:#eee8fb;color:#6246a2}";
  style.textContent+=".ord-na-reinforcements{margin-top:6px;padding-top:6px;border-top:1px solid #ece5f2}.ord-na-reinforcement-head{display:flex;align-items:center;flex-wrap:wrap;gap:5px;margin-bottom:5px}.ord-na-reinforcement-role{padding:3px 6px;border:1px solid #ded5ea;border-radius:3px;background:#fff;color:#8d7d9e;font:inherit;cursor:pointer}.ord-na-reinforcement-role[aria-pressed=true]{background:#eee8fb;color:#6246a2}.ord-na-reinforcement-unit{grid-template-columns:minmax(0,1fr) auto}.ord-na-reinforcement-unit .ord-na-action{grid-column:2;grid-row:auto}.ord-na-reinforcement-owned{color:#7d9a86;font-size:9px}";
  (doc.head||doc.documentElement).append(style);
 }
 const kingPathStorageKey="ord-nightmare-king-path-mode-v2",legacyKingPathStorageKey="ord-nightmare-include-king-path-v1",mysteriousStorageKey="ord-nightmare-include-mysterious-v1";
 let storedKingPathMode="off";try{const stored=root.localStorage?.getItem(kingPathStorageKey);if(stored!==null&&stored!==undefined){let value;try{value=JSON.parse(stored);}catch{value=stored;}storedKingPathMode=kingPathModes.has(value)?value:"off";}else if(JSON.parse(root.localStorage?.getItem(legacyKingPathStorageKey)||"false")===true)storedKingPathMode="standard";}catch{}
 const initialSettings={...(options.settings||{})};if(!Object.hasOwn(initialSettings,"kingPathMode")&&!Object.hasOwn(initialSettings,"includeKingPathDecks"))initialSettings.kingPathMode=storedKingPathMode;
 if(!Object.hasOwn(initialSettings,"includeMysteriousDecks")){try{initialSettings.includeMysteriousDecks=JSON.parse(root.localStorage?.getItem(mysteriousStorageKey)||"false")===true;}catch{initialSettings.includeMysteriousDecks=false;}}
 let settings=normalize(initialSettings),state={connected:false,result:null,calculating:false,error:null,craftAvailable:false,craftRunning:false,craftMessage:"",craftTone:"",elder:null},destroyed=false,pending=false,localError="";
 let deckSelectionActive=false,deckSelectionNode=null,queuedRender=false;
 let labelCatalog=null,labelUnits=new Map();
 function deckLabel(deck){
  if(state.catalog!==labelCatalog){labelCatalog=state.catalog;labelUnits=new Map(list(labelCatalog?.units).filter(unit=>code(unit?.rawcode)).map(unit=>[unit.rawcode,unit]));}
  const goals=list(deck?.goals),goalCodes=new Set(goals.map(goal=>goal.rawcode)),ordered=[...list(deck?.coreRawcodes).filter(rawcode=>goalCodes.has(rawcode)),...goals.map(goal=>goal.rawcode)],seen=new Set(),native=[];
  for(const rawcode of ordered){const unit=labelUnits.get(rawcode);if(seen.has(rawcode)||unit?.playable!==true)continue;seen.add(rawcode);const name=text(unit.displayName||unit.properName||unit.name).replace(/\|c[0-9a-f]{8}|\|r/gi,"").replace(/\|n/gi," ").replace(/\s*-\s*(?:흔함|안흔함|특별|희귀|전설|히든|초월|불멸|영원|제한|신비|왜곡|변화|랜덤전용|특수|세라핌)\s*$/,"").trim();if(name)native.push({rawcode,name,tier:unit.tier});}
  const upper=native.filter(unit=>finalTiers.has(unit.tier));if(upper.length)return upper.map(unit=>unit.name+"("+unit.tier+")").join(" · ");
  const cores=new Set(list(deck?.coreRawcodes)),coreNames=native.filter(unit=>cores.has(unit.rawcode));return(coreNames.length?coreNames:native).map(unit=>unit.name).join(" · ")||"목표 덱";
 }
 const panel=el("section","ord-na");panel.setAttribute("aria-label","악몽 목표 덱");host.append(panel);
 const busy=()=>pending||state.craftRunning===true;
 const targetFor=rawcode=>list(state.result?.craftTargets).find(target=>target.rawcode===rawcode);
 const goalTargetFor=rawcode=>{const target=Array.isArray(state.result?.goalCraftTargets)?state.result.goalCraftTargets.find(item=>item.towardRawcode===rawcode):targetFor(rawcode);return target?.rawcode===rawcode&&target.isWholeGoal!==false?target:null;};
 const itemReady=target=>{const requirements=list(target?.itemRequirements).length?target.itemRequirements:list(target?.nonUnitRequirements).filter(item=>item.type==="ITEM");return !requirements.length||state.itemCraftAvailable===true&&requirements.every(item=>list(state.readableItemRawcodes).includes(item.rawcode));};
 const conditionsReady=target=>{const requirements=list(target?.conditionRequirements).length?target.conditionRequirements:list(target?.nonUnitRequirements).filter(item=>["UBAN","KING","IFCT","CHCT","SKPT","RMAX","NIKA"].includes(item.type));return !requirements.length||state.recipeConditionsAvailable===true&&requirements.every(item=>list(state.readableConditionTypes).includes(item.type)&&(!["IFCT","CHCT","SKPT","RMAX","NIKA"].includes(item.type)||state.specialRecipeConditionsAvailable===true)&&item.known===true&&item.allowed===true);};
 function reinforcementTargetFor(rawcode){
  if(state.result?.phase==="early_story"||state.result?.mapCompatible===false)return null;
  const target=list(state.result?.reinforcements?.craftTargets).find(action=>action.rawcode===rawcode&&action.towardRawcode===rawcode&&action.origin==="reinforcement_whole_goal_path"&&action.isWholeGoal===true&&action.available===true),steps=list(target?.expectedSteps),last=steps.at(-1),recipes=new Map(list(state.catalog?.recipes).map(recipe=>[String(recipe.id),recipe]));
  if(!target||target.resourcesReady!==true||!Number.isSafeInteger(target.wood)||target.wood<0||!Number.isSafeInteger(target.gold)||target.gold<0||target.itemsReady===false||target.conditionsReady===false||target.additionalConditionsUnverified===true||!target.protectedCounts||Object.prototype.toString.call(target.protectedCounts)!=="[object Object]"||Object.keys(target.protectedCounts).length>64||!Object.entries(target.protectedCounts).every(([raw,count])=>code(raw)&&Number.isSafeInteger(count)&&count>=0)||!steps.length||steps.length>64||last?.type!=="craft"||String(target.recipeId)!==String(last.recipeId)||recipes.get(String(last.recipeId))?.resultUnitRawcode!==rawcode)return null;
  if(!steps.every(step=>step?.type==="craft"&&typeof step.recipeId==="string"&&recipes.has(step.recipeId)||step?.type==="choice"&&["h001","h002","h003","h004","h005","h006","h007","h008","h009"].includes(step.resultRawcode)))return null;
  if(list(target.nonUnitRequirements).some(item=>!["WOOD","GOLD","ITEM","UBAN","KING","IFCT","CHCT","SKPT","RMAX","NIKA"].includes(item.type)))return null;
  const items=list(target.itemRequirements).length?target.itemRequirements:list(target.nonUnitRequirements).filter(item=>item.type==="ITEM"),itemState=state.items;if(items.length&&(itemState?.status!=="confirmed"||!Number.isInteger(state.localPlayer)||itemState.localPlayer!==state.localPlayer||itemState.source?.readOnly!==true||items.some(item=>item.known!==true||item.missing!==0||!Number.isSafeInteger(item.count)||item.count<1||!Number.isSafeInteger(itemState.counts?.[item.rawcode])||itemState.counts[item.rawcode]<item.count)))return null;
  return target;
 }
 const enabled=(target,scope)=>{const independent=scope==="reinforcement"&&reinforcementTargetFor(target?.rawcode)===target;return Boolean(target&&(scope!=="reinforcement"||independent)&&itemReady(target)&&conditionsReady(target)&&target.available!==false&&code(target.rawcode)&&text(target.recipeId)&&list(target.expectedSteps).length&&target.expectedSteps.length<=64&&target.resourcesReady!==false&&(independent||state.result?.targetDeck?.acquisitionPlan?.sharedChoiceFeasible!==false&&state.result?.targetDeck?.assessment?.compositionStatus!=="incomplete")&&state.connected&&state.craftAvailable&&!state.calculating&&!state.error&&!busy()&&typeof options.onCraftTarget==="function");};
 function request(target,goalRawcode,scope){
  if(destroyed||!enabled(target,scope)||(goalRawcode&&(target.rawcode!==goalRawcode||target.isWholeGoal===false)))return;pending=true;localError="";render();
  try{Promise.resolve(scope==="reinforcement"?options.onCraftTarget(target.rawcode,goalRawcode,"reinforcement"):options.onCraftTarget(target.rawcode,goalRawcode)).catch(()=>{localError="조합하지 못했습니다.";}).finally(()=>{pending=false;render();});}
  catch{pending=false;localError="조합하지 못했습니다.";render();}
 }
 function cost(target){const result=[];if(finite(target?.choiceWisps)&&target.choiceWisps>0)result.push("선위 "+target.choiceWisps);if(finite(target?.wood)&&target.wood>0)result.push("목재 "+target.wood);if(target?.resourcesReady===false)result.push("자원 부족");return result.join(" · ");}
 function probabilityRange(value){
  const interval=value?.confidence;
  if(value?.status!=="estimated"||!Number.isSafeInteger(value.samples)||value.samples<1||!finite(interval?.lower)||!finite(interval?.upper)||interval.lower<0||interval.upper>1||interval.lower>interval.upper)return null;
  return Math.floor(interval.lower*100)+"~"+Math.ceil(interval.upper*100)+"%";
 }
 function combatState(value){return {conditional_model:"조건부",insufficient_model:"모델 부족",reference_only:"참고값",risk_detected:"보강 권장",unknown:"미확인",role_incomplete:"누락",unavailable:"미확인",not_required:"해당 없음"}[value?.status]||"미확인";}
 function combatTooltip(value,deck){
  const recordLabels={same_variant_partial:"동일 태그의 부분 등록 명단",related_variant_partial:"다른 변형의 부분 등록 명단",none:"대조되는 명단 없음",unknown_variant:"변형 미확인 명단"};
  const lines=["목표 덱 완성 기준 · 기록 연관: "+(recordLabels[value.recordSupport?.tier||value.recordSupport?.status]||"확인 필요")];
  const boss=list(value.damage?.scenarioResults).find(item=>item.round===65&&finite(item.postMitigationDpsLowerBound));
  if(boss)lines.push("65라운드 보스 체력 처리 하한 약 "+Math.round(boss.postMitigationDpsLowerBound).toLocaleString("ko-KR")+"/초 (조건부·최대 시간 공격 가정)");
  const bounds=value.control?.stun?.commonTargetUptimeBounds;
  if(finite(bounds?.lowerBound)&&finite(bounds?.upperBound)&&bounds.lowerBound>=0&&bounds.upperBound<=1&&bounds.lowerBound<=bounds.upperBound)lines.push("스턴 참고 구간 "+Math.floor(bounds.lowerBound*100)+"~"+Math.ceil(bounds.upperBound*100)+"% (동일 일반 대상·기본 공격 주기)");
  const slow=value.control?.slow?.strongestSingleSourcePercent;
  if(finite(slow))lines.push("확인된 개별 이감 값 최대 "+Math.round(slow)+"% (실효 합계 미확인)");
  for(const resource of list(value.mana?.resourceCycles).slice(0,2)){
   const name=text(list(deck.goals).find(goal=>goal.rawcode===resource.rawcode)?.name);
   if(name&&finite(resource.requiredRefill))lines.push(name+" 회복 필요량 "+resource.requiredRefill+" (배치·피해 이벤트에 따라 주기 변동)");
  }
  return lines.join("\n");
 }
  function appendPathControls(controls){for(const [mode,label]of [["standard","패왕의길"],["martial_law","계엄령"]]){const button=el("button","ord-na-toggle",label);button.type="button";button.setAttribute("aria-label",label+" 덱 포함");button.setAttribute("aria-pressed",String(settings.kingPathMode===mode));button.title="레오 포함 덱 추천";button.addEventListener("click",()=>setSettings({kingPathMode:settings.kingPathMode===mode?"off":mode,preferredDeckId:null},{notify:true}));controls.append(button);}const button=el("button","ord-na-toggle","신비함");button.type="button";button.setAttribute("aria-label","신비함 덱 포함");button.setAttribute("aria-pressed",String(settings.includeMysteriousDecks));button.addEventListener("click",()=>setSettings({includeMysteriousDecks:!settings.includeMysteriousDecks,preferredDeckId:null},{notify:true}));controls.append(button);}
 function appendStop(head){if(state.craftRunning&&state.craftCanStop&&typeof options.onStopCraft==="function"){const stop=el("button","ord-na-action",state.craftStopRequested?"중지 요청 중":"중지");stop.type="button";stop.disabled=state.craftStopRequested===true;stop.setAttribute("aria-label","조합 중지");stop.addEventListener("click",()=>{if(stop.disabled||destroyed)return;try{Promise.resolve(options.onStopCraft()).catch(()=>{localError="중지 요청을 보내지 못했습니다.";render();});}catch{localError="중지 요청을 보내지 못했습니다.";render();}});head.append(stop);}}
 function appendCraftMessage(){const message=localError||text(state.craftMessage)||(busy()?"조합 중":"");if(message){const safe=message.length<=180&&!/[A-Za-z]:[\\/]|stacktrace|\bat \w+[.(]/i.test(message)?message:"조합 상태를 확인해 주세요.";const status=el("div","ord-na-status",safe);status.setAttribute("role","status");if(localError||state.craftTone==="error")status.dataset.tone="error";panel.append(status);}}
 function renderEarlyStory(result){
  const head=el("div","ord-na-head"),controls=el("div","ord-na-controls");head.append(el("h2","ord-na-title","첫 스토리 유닛"));appendStop(head);appendPathControls(controls);head.append(controls);panel.append(head);
  const native=new Map(list(state.catalog?.units).map(unit=>[unit.rawcode,unit])),seen=new Set(),grid=el("div","ord-na-roster"),candidates=list(result.earlyStory?.candidates).filter(candidate=>{const unit=native.get(candidate?.rawcode);if(!code(candidate?.rawcode)||seen.has(candidate.rawcode)||unit?.playable!==true||!["전설","히든"].includes(unit.tier))return false;seen.add(candidate.rawcode);return true;}).slice(0,3);
  for(const [index,candidate]of candidates.entries()){
   const unit=native.get(candidate.rawcode),unitName=(text(unit.displayName||unit.properName||unit.name)||"유닛")+" ("+unit.tier+")",owned=Number.isSafeInteger(candidate.owned)&&candidate.owned>=0?candidate.owned:0,required=Number.isSafeInteger(candidate.count)&&candidate.count>0?candidate.count:1,done=owned>=required,row=el("div","ord-na-unit"+(done?" ord-na-owned":"")),name=el("button","ord-na-name"),progress=candidate.progress,bound=value=>Math.max(0,Math.min(100,value));
   if(finite(progress?.percent)){const percent=done?100:bound(progress.percent),reachable=done?100:Math.max(percent,bound(finite(progress.choiceReachablePercent)?progress.choiceReachablePercent:percent)),track=el("span","ord-na-progress"),current=el("span","ord-na-progress-current"),choice=el("span","ord-na-progress-choice");current.style.width=percent+"%";choice.style.left=percent+"%";choice.style.width=reachable-percent+"%";choice.dataset.ready=String(progress.canCompleteWithChoiceWisp===true&&reachable>=100);track.append(current,choice);name.append(track,el("span","",Math.round(percent)+"% "+unitName));}else name.textContent=unitName;
   name.type="button";name.disabled=typeof options.onOpenUnit!=="function";name.setAttribute("aria-label",unitName+" 상세");name.addEventListener("click",()=>{if(!destroyed&&!name.disabled)options.onOpenUnit(candidate.rawcode);});row.append(el("span","ord-na-number",index+1),name,el("span","ord-na-count","보유 "+owned+" / "+required));
   if(!done){const candidateTarget=goalTargetFor(candidate.rawcode),last=list(candidateTarget?.expectedSteps).at(-1),whole=candidateTarget?.isWholeGoal===true&&candidateTarget.towardRawcode===candidate.rawcode&&candidateTarget.available===true&&candidateTarget.protectedCounts&&typeof candidateTarget.protectedCounts==="object"&&!Array.isArray(candidateTarget.protectedCounts)&&last?.type==="craft"&&list(state.catalog?.recipes).some(recipe=>String(recipe.id)===String(last.recipeId)&&recipe.resultUnitRawcode===candidate.rawcode),target=whole?candidateTarget:null,action=el("button","ord-na-action","조합");action.type="button";action.disabled=!enabled(target);action.setAttribute("aria-label",unitName+" 조합");action.title=enabled(target)?"필요한 재료부터 "+(text(unit.displayName)||"목표 유닛")+"까지 조합":"현재는 조합할 수 없습니다.";action.addEventListener("click",()=>request(target,candidate.rawcode));row.append(action);}row.append(el("span","ord-na-unit-state",done?"보유":"대기"));grid.append(row);
  }
  panel.append(grid);appendCraftMessage();
 }
 function renderReinforcements(result){
  if(!result?.reinforcements||result.phase==="early_story"||result.mapCompatible===false)return;
  const section=el("section","ord-na-reinforcements"),head=el("div","ord-na-reinforcement-head"),grid=el("div","ord-na-roster"),role=settings.reinforcementRole,native=new Map(list(state.catalog?.units).map(unit=>[unit.rawcode,unit]));head.append(el("span","ord-na-next-label","보강"));
  for(const [value,label]of [["auto","자동"],["stun","스턴"],["physical_shred","물깎"],["magic_shred","마깎"]]){const button=el("button","ord-na-reinforcement-role",label);button.type="button";button.disabled=busy()||state.calculating;button.setAttribute("aria-label",label+" 보강");button.setAttribute("aria-pressed",String(role===value));button.addEventListener("click",()=>{if(!destroyed&&!button.disabled)setSettings({reinforcementRole:value},{notify:true});});head.append(button);}section.append(head);
  const seen=new Set(),candidates=list(result.reinforcements.candidates).filter(candidate=>{const unit=native.get(candidate?.rawcode);if(!code(candidate?.rawcode)||seen.has(candidate.rawcode)||unit?.playable!==true||!nextTiers.has(unit.tier)||role!=="auto"&&!list(candidate.roles).includes(role))return false;seen.add(candidate.rawcode);return true;}).slice(0,3);
  for(const candidate of candidates){const unit=native.get(candidate.rawcode),unitName=(text(unit.displayName||unit.properName||unit.name)||"유닛")+" ("+unit.tier+")",owned=Number.isSafeInteger(candidate.owned)&&candidate.owned>=0?candidate.owned:0,required=Number.isSafeInteger(candidate.count)&&candidate.count>0?candidate.count:1,done=owned>=required,row=el("div","ord-na-unit ord-na-reinforcement-unit"+(done?" ord-na-owned":"")),name=el("button","ord-na-name"),progress=candidate.progress,bound=value=>Math.max(0,Math.min(100,value));
   if(finite(progress?.percent)){const percent=done?100:bound(progress.percent),reachable=done?100:Math.max(percent,bound(finite(progress.choiceReachablePercent)?progress.choiceReachablePercent:percent)),track=el("span","ord-na-progress"),current=el("span","ord-na-progress-current"),choice=el("span","ord-na-progress-choice");current.style.width=percent+"%";choice.style.left=percent+"%";choice.style.width=reachable-percent+"%";choice.dataset.ready=String(progress.canCompleteWithChoiceWisp===true&&reachable>=100);track.append(current,choice);name.append(track,el("span","",Math.round(percent)+"% "+unitName));}else name.textContent=unitName;
   name.type="button";name.disabled=typeof options.onOpenUnit!=="function";name.setAttribute("aria-label",unitName+" 상세");name.addEventListener("click",()=>{if(!destroyed&&!name.disabled)options.onOpenUnit(candidate.rawcode);});row.append(name);
   if(done)row.append(el("span","ord-na-reinforcement-owned","보유"));else{const target=reinforcementTargetFor(candidate.rawcode),action=el("button","ord-na-action","조합");action.type="button";action.disabled=!enabled(target,"reinforcement");action.setAttribute("aria-label",unitName+" 보강 조합");action.addEventListener("click",()=>request(target,candidate.rawcode,"reinforcement"));row.append(action);}grid.append(row);
  }
  section.append(grid);panel.append(section);
 }
 function render(){
  if(destroyed)return;
  if(deckSelectionActive&&deckSelectionNode&&typeof deckSelectionNode.matches==="function"){
   try{if(root.CSS?.supports?.("selector(select:open)")===true&&!deckSelectionNode.matches(":open")){deckSelectionActive=false;deckSelectionNode=null;}}catch{}
  }
  if(deckSelectionActive&&state.connected&&!state.craftRunning){queuedRender=true;return;}
  deckSelectionActive=false;deckSelectionNode=null;queuedRender=false;panel.replaceChildren();
  if(!state.connected){panel.append(el("div","ord-na-empty","게임 연결 대기"));return;}
  if(state.result?.phase==="early_story"&&state.result.mapCompatible!==false){renderEarlyStory(state.result);return;}
  const result=state.result,deck=result?.targetDeck,head=el("div","ord-na-head");head.append(el("h2","ord-na-title","목표 덱"));
  appendStop(head);
  if(lanes[deck?.mode])head.append(el("span","ord-na-chip",lanes[deck.mode]));
  const evidenceLabels={clear_record:"클리어 기록 기반",role_similar_alternative:"기록의 역할 대체",native_fallback:"역할 구성 기반"};
  if(evidenceLabels[deck?.sourceEvidence?.kind])head.append(el("span","ord-na-chip",evidenceLabels[deck.sourceEvidence.kind]));
  const combatValidation=deck?.combatValidation,combatLabels={risk_detected:"전투 보강 권장",conditional_model:"전투 조건부 평가",evidence_limited:"전투 근거 일부 확인",role_incomplete:"필수 역할 부족",unknown:"전투 평가 미확인",unavailable:"전투 자료 확인 필요"};
  if(combatValidation)head.append(el("span","ord-na-chip",combatLabels[combatValidation.status]||"전투 평가 미확인"));
  const rewardBudget=state.rewardBudget,budget=deck?.budgetFeasibility;
  if(Number.isSafeInteger(rewardBudget?.round)&&rewardBudget.round>0)head.append(el("span","ord-na-chip",rewardBudget.round+"라운드"));
  const budgetLabels={within_guaranteed:"확정 보상 예산 내",within_conditional:"조건부 보상 필요",over_budget:"남은 예산 부족",unknown:"보상·조건 확인 필요"};
  if(budgetLabels[budget?.status])head.append(el("span","ord-na-chip",budgetLabels[budget.status]));
  const randomForecast=deck?.randomMaterialForecast,incompleteComposition=deck?.assessment?.compositionStatus==="incomplete"||list(deck?.assessment?.missingSlots).length>0||list(deck?.assessment?.coverage).some(role=>role.status==="missing"),forecastRange=randomForecast?.scope==="material_completion"&&!incompleteComposition?probabilityRange(randomForecast):null;
  if(randomForecast?.scope==="material_completion"){
   const chip=el("span","ord-na-chip",incompleteComposition?"역할 구성 확인 필요":randomForecast.status==="known"&&randomForecast.isEstimate===false&&budget?.status==="within_guaranteed"?"재료 확보 예산 충족":forecastRange?"재료 확보 추정 "+forecastRange+(randomForecast.conditional?" · 조건부 보상 포함":" · 확정 보상 기준"):"재료 확률 확인 필요");
   if(forecastRange)chip.title="표본 "+randomForecast.samples+"개 · 95% 구간 · 목표 제작 경로 기준";
   head.append(chip);
  }
  if(deck?.recommendationFeasibility?.status==="conditional")head.append(el("span","ord-na-chip","조건부 목표"));
  else if(deck?.recommendationFeasibility?.status==="blocked")head.append(el("span","ord-na-chip","제작 조건 불충족"));
  const investment=deck?.firstInvestment;
  if(investment?.matched===true&&["전설","히든"].includes(investment.tier)&&text(investment.name))head.append(el("span","ord-na-chip","첫 "+investment.tier+" · "+investment.name));
  const adjustment=deck?.sourceEvidence?.balanceAdjustment;
  if(adjustment?.currentProfileReevaluation===true&&/^2\.[0-9]{3}$/.test(adjustment.sourceVersion||"")&&adjustment.sourceVersion!=="2.323"){
   const badge=el("span","ord-na-chip",adjustment.sourceVersion+" 기반 · 2.323 보정");
   const changes=list(adjustment.changes).map(change=>text(change.summary)).filter(Boolean);
   if(changes.length)badge.title=changes.slice(0,8).join("\n");head.append(badge);
  }
  if(elders[state.elder?.id])head.append(el("span","ord-na-chip",elders[state.elder.id]));
  const items=state.items,itemKnown=items?.status==="confirmed"&&Number.isInteger(state.localPlayer)&&items.localPlayer===state.localPlayer&&items.source?.readOnly===true&&Object.keys(itemNames).every(code=>items.counts?.[code]===0||items.counts?.[code]===1);
  if(itemKnown){const owned=Object.keys(itemNames).filter(code=>items.counts[code]>0).map(code=>itemNames[code]);head.append(el("span","ord-na-chip",owned.length?"보유 아이템 · "+owned.join(" · "):"보유 아이템 없음"));}
  else head.append(el("span","ord-na-chip","아이템 미확인"));
  const controls=el("div","ord-na-controls"),alternatives=[deck,...list(result?.alternatives)].filter(item=>item&&text(item.id));
  if(alternatives.length>1||!deck&&alternatives.length){
   const select=el("select","ord-na-select");select.setAttribute("aria-label","목표 덱");
   if(!deck){const placeholder=el("option","","목표 덱 선택");placeholder.value="";select.append(placeholder);}
   for(const item of alternatives){const option=el("option","",deckLabel(item));option.value=item.id;select.append(option);}
   select.value=alternatives.some(item=>item.id===settings.preferredDeckId)?settings.preferredDeckId:deck?.id||"";select.disabled=busy();
   const beginSelection=()=>{if(!destroyed&&state.connected&&!select.disabled){deckSelectionActive=true;deckSelectionNode=select;}};
   const endSelection=(refresh=true)=>{if(deckSelectionNode!==select)return;deckSelectionActive=false;deckSelectionNode=null;if(refresh&&queuedRender)render();};
   select.addEventListener("pointerdown",beginSelection);select.addEventListener("focus",beginSelection);
   select.addEventListener("blur",()=>endSelection());
   select.addEventListener("keydown",event=>{if(event.key==="Escape")Promise.resolve().then(()=>endSelection());});
   select.addEventListener("change",()=>{const selected=select.value;endSelection(false);if(!destroyed&&state.connected&&!busy()&&alternatives.some(item=>item.id===selected))setSettings({preferredDeckId:selected},{notify:true});if(queuedRender)render();});controls.append(select);
  }
  appendPathControls(controls);head.append(controls);
  panel.append(head);
  if(!deck||result.mapCompatible===false){panel.append(el("div","ord-na-empty",state.calculating?"목표 덱 확인 중":state.error?"목표 덱을 불러오지 못했습니다.":result?.mapCompatible===true?result.searchComplete===false?"현재 패에 맞는 목표 덱을 찾지 못했습니다.":"현재 패로 이어갈 목표 덱이 없습니다.":"현재 맵의 목표 덱을 확인하지 못했습니다."));if(state.error&&options.onRetry){const retry=el("button","ord-na-name","다시 계산");retry.type="button";retry.addEventListener("click",()=>options.onRetry());panel.append(retry);}renderReinforcements(result);return;}
  const order=list(result.order).map(item=>typeof item==="string"?item:item.rawcode),positions=new Map(order.map((item,index)=>[item,index+1]));
  const cores=new Set(list(deck.coreRawcodes)),goalOrder=goal=>[finalTiers.has(goal.tier)?0:1,cores.has(goal.rawcode)?0:1,Number(goal.owned)>0?0:1,positions.get(goal.rawcode)||999];
  const goals=list(deck.goals).slice().sort((a,b)=>{const x=goalOrder(a),y=goalOrder(b);for(let i=0;i<x.length;i++)if(x[i]!==y[i])return x[i]-y[i];return 0;});
  const grid=el("div","ord-na-roster");
  for(const [displayIndex,goal] of goals.entries()){
   const target=goalTargetFor(goal.rawcode),owned=finite(goal.owned)?goal.owned:0,required=finite(goal.count)?goal.count:1,done=owned>=required;
   const unitName=(text(goal.name)||"유닛")+(text(goal.tier)?" ("+goal.tier+")":"");
   const row=el("div","ord-na-unit"+(done?" ord-na-owned":"")),name=el("button","ord-na-name");
   const progress=goal.progress,bound=value=>Math.max(0,Math.min(100,value));
   if(finite(progress?.percent)){
    const percent=done?100:bound(progress.percent),reachable=done?100:Math.max(percent,bound(finite(progress.choiceReachablePercent)?progress.choiceReachablePercent:percent));
    const track=el("span","ord-na-progress"),current=el("span","ord-na-progress-current"),choice=el("span","ord-na-progress-choice");
    current.style.width=percent+"%";choice.style.left=percent+"%";choice.style.width=reachable-percent+"%";choice.dataset.ready=String(progress.canCompleteWithChoiceWisp===true&&reachable>=100);track.append(current,choice);
    name.append(track,el("span","",Math.round(percent)+"% "+unitName));name.title="현재 "+Math.round(percent)+"% · 선택 위습 사용 시 "+Math.round(reachable)+"%";
   }else name.textContent=unitName;
   name.type="button";name.disabled=typeof options.onOpenUnit!=="function";name.setAttribute("aria-label",unitName+" 상세");name.addEventListener("click",()=>{if(!destroyed&&!name.disabled)options.onOpenUnit(goal.rawcode);});
   row.append(el("span","ord-na-number",displayIndex+1),name,el("span","ord-na-count","보유 "+owned+" / "+required));
   if(!done){const action=el("button","ord-na-action","조합");action.type="button";action.disabled=!enabled(target);action.setAttribute("aria-label",unitName+" 조합");action.title=enabled(target)?"필요한 재료부터 "+(text(goal.name)||"목표 유닛")+"까지 조합":"현재는 조합할 수 없습니다.";action.addEventListener("click",()=>request(target,goal.rawcode));row.append(action);}
   row.append(el("span","ord-na-unit-state",done?"보유":"대기"));grid.append(row);
  }
  panel.append(grid);
  if(combatValidation){
   const summary=el("div","ord-na-next-cost","전투 평가 · 딜 "+combatState(combatValidation.damage)+" · 스턴 "+combatState(combatValidation.control?.stun)+" · 이감 "+combatState(combatValidation.control?.slow)+" · 마나·회복 "+combatState(combatValidation.mana));
   summary.title=combatTooltip(combatValidation,deck);
   panel.append(summary);
  }
  if(randomForecast?.scope==="material_completion"&&!incompleteComposition&&randomForecast.status!=="known"){
   const ranges=[];
   const guaranteedRange=probabilityRange(randomForecast.guaranteed),conditionalRange=probabilityRange(randomForecast.conditionalRewards);
   if(guaranteedRange)ranges.push("확정 보상 기준 "+guaranteedRange);
   else if(conditionalRange&&randomForecast.guaranteed?.status==="unknown")ranges.push("확정 보상 확률 미확인");
   if(conditionalRange&&(conditionalRange!==guaranteedRange||randomForecast.conditionalRewards?.remainingDraws!==randomForecast.guaranteed?.remainingDraws))ranges.push("조건부 보상 포함 "+conditionalRange);
   else if(guaranteedRange&&randomForecast.conditionalRewards?.status==="unknown")ranges.push("조건부 보상 확률 미확인");
   if(ranges.length)panel.append(el("div","ord-na-next-cost",ranges.join(" · ")));
   const drawLabels={e0IX:"일반",e017:"안흔함",e016:"특별",e019:"희귀",e015:"전설"};
   const draws=Object.entries(drawLabels).filter(([rawcode])=>Number.isSafeInteger(randomForecast.drawCounts?.[rawcode])&&randomForecast.drawCounts[rawcode]>0).map(([rawcode,label])=>label+" "+randomForecast.drawCounts[rawcode]);
   if(draws.length)panel.append(el("div","ord-na-next-cost","랜덤 위습 · "+draws.join(" · ")));
  }
  if(budget){
   const available=budget.status==="within_conditional"?budget.available?.conditional:budget.available?.guaranteed;
   const labels={choiceWisps:"선위",wood:"목재",gold:"금화"};
   const amounts=Object.entries(labels).filter(([key])=>Number.isSafeInteger(budget.required?.[key])&&budget.required[key]>0&&Number.isSafeInteger(available?.[key])).map(([key,label])=>label+" "+budget.required[key]+" / "+available[key]);
   if(amounts.length)panel.append(el("div","ord-na-next-cost",(budget.availableIsLowerBound?"필요 / 확인된 예산 · ":"필요 / 예산 · ")+amounts.join(" · ")));
  }
  if(["confirmed","partial"].includes(rewardBudget?.status)){
   const labels={choiceWisps:"선위",wood:"목재",gold:"금화"};
   for(const [kind,title]of [["guaranteed","남은 확정 보상"],["conditional","조건부 보상"]]){
    const amounts=Object.entries(labels).filter(([key])=>Number.isSafeInteger(rewardBudget[kind]?.[key])&&rewardBudget[kind][key]>0).map(([key,label])=>label+" "+rewardBudget[kind][key]);
    const random=rewardBudget[kind]?.units?.e0IX;if(Number.isSafeInteger(random)&&random>0)amounts.push("무작위 위습 "+random);
    if(amounts.length)panel.append(el("div","ord-na-next-cost",title+" · "+amounts.join(" · ")));
   }
  }
  if(text(deck.acquisitionPlan?.summary))panel.append(el("div","ord-na-next-cost",text(deck.acquisitionPlan.summary)+(deck.acquisitionPlan.futureConditional?" (획득 후)":"")));
  if(deck.acquisitionPlan?.sharedChoiceFeasible===false)panel.append(el("div","ord-na-status","초월 위습이 부족합니다. 다른 목표 덱을 선택해 주세요."));
  if(deck.assessment?.compositionStatus==="incomplete")panel.append(el("div","ord-na-status","목표 덱 구성이 부족합니다. 다른 덱을 선택해 주세요."));
  const future=list(deck.acquisitionPlan?.remainingMaterials||deck.planning?.forecast?.futureMaterials).filter(item=>finite(item.count)&&item.count>0);
  if(future.length){const names=future.map(item=>(item.rawcode==="h04S"?"초월 쿠마":text(item.name)||"별도 재료")+" "+item.count);panel.append(el("div","ord-na-next-cost","별도 재료 · "+names.join(" · ")));}
  const next=list(result.craftTargets).find(target=>nextTiers.has(target.tier)&&target.playable!==false&&target.isSummon!==true);
  if(next){const row=el("div","ord-na-next"),name=el("button","ord-na-next-name",text(next.name)||"다음 유닛"),action=el("button","ord-na-action","조합");name.type=action.type="button";name.disabled=action.disabled=!enabled(next);name.setAttribute("aria-label",name.textContent+" 다음 제작");action.setAttribute("aria-label",name.textContent+" 다음 제작");name.addEventListener("click",()=>request(next));action.addEventListener("click",()=>request(next));row.append(el("span","ord-na-next-label","다음 제작"),name,el("span","ord-na-next-cost",cost(next)),action);panel.append(row);}
  const opportunities=list(result.ownedOpportunities);
  if(opportunities.length){const details=el("details","ord-na-condition"),ul=el("ul");details.append(el("summary","","신비함 대안"));for(const item of opportunities){const candidate=alternatives.find(d=>list(item.templateIds).includes(d.id)&&d.mode===deck.mode)||alternatives.find(d=>list(item.templateIds).includes(d.id)),li=el("li"),button=el("button","ord-na-name",text(item.name)||"신비함");button.type="button";button.disabled=!candidate||busy()||state.calculating;button.setAttribute("aria-label",button.textContent+" 활용 덱 비교");button.addEventListener("click",()=>{if(candidate&&!button.disabled)setSettings({preferredDeckId:candidate.id},{notify:true});});li.append(button,el("span",""," · "+(item.ownedResult>0?"보유 "+item.ownedResult:"재료 "+(item.ownedSource||0))+(item.ownedResult>0?"":item.automaticCraftSupported?"":" · 수동 조합")));ul.append(li);}details.append(ul);panel.append(details);}
  renderReinforcements(result);appendCraftMessage();
 }
  function setSettings(patch,config={}){if(destroyed)return;const values={...settings,...patch};if(!Object.hasOwn(patch,"kingPathMode")&&Object.hasOwn(patch,"includeKingPathDecks"))values.kingPathMode=patch.includeKingPathDecks===true?"standard":"off";const next=normalize(values),changed=JSON.stringify(next)!==JSON.stringify(settings);if(next.kingPathMode!==settings.kingPathMode){try{root.localStorage?.setItem(kingPathStorageKey,JSON.stringify(next.kingPathMode));}catch{}}if(next.includeMysteriousDecks!==settings.includeMysteriousDecks){try{root.localStorage?.setItem(mysteriousStorageKey,JSON.stringify(next.includeMysteriousDecks));}catch{}}settings=next;if(changed)render();if(config.notify&&changed)options.onSettingsChange?.({...settings});}
 render();return Object.freeze({update(value={}){if(destroyed)return;state={...state,...value};if(!state.connected)state.result=null;if(Object.hasOwn(value,"craftMessage"))localError="";render();},setSettings,getSettings:()=>({...settings}),setExpanded(){},destroy(){destroyed=true;panel.remove();}});
}
root.OrdNightmarePanel=Object.freeze({create});
})(typeof window!=="undefined"?window:globalThis);

