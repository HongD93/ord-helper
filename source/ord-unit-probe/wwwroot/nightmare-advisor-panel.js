(function(root){
"use strict";
const modes={physical:"물딜",magic:"마딜",hybrid:"혼합"};
const bosses={nasujuro:"나스쥬로",warcury:"워큐리",saturn:"새턴"};
const roles={physical:new Set(["slow","stun","armorReduction","attackSpeedBuff","attackDamageBuff"]),magic:new Set(["slow","stun","magicResistanceReduction","magicAmplification","attackSpeedBuff"])};
const array=value=>Array.isArray(value)?value:[];
const validCode=value=>typeof value==="string"&&/^[A-Za-z0-9]{4}$/.test(value);
const finite=value=>typeof value==="number"&&Number.isFinite(value);
const string=value=>typeof value==="string"?value.trim():"";
function normalize(value){const number=(item,min)=>item===null||item===undefined||item===""?null:Number.isFinite(Number(item))&&Number(item)>=min?Math.floor(Number(item)):null;return{boss:Object.hasOwn(bosses,value.boss)||value.boss==="none"?value.boss:"unknown",round:number(value.round,1),wood:number(value.wood,0),direction:["physical","magic","hybrid"].includes(value.direction)?value.direction:"all"};}
function create(options){
 const host=options?.host;if(!host||typeof host.append!=="function")throw new TypeError("A panel host is required.");
 const doc=host.ownerDocument||root.document;
 const el=(tag,cls,value)=>{const node=doc.createElement(tag);if(cls)node.className=cls;if(value!==undefined)node.textContent=String(value);return node;};
 if(!doc.getElementById("ord-nightmare-panel-style")){const style=el("style");style.id="ord-nightmare-panel-style";style.textContent=`.ord-na{margin:4px 3px 6px;padding:7px 8px;border:1px solid #dfd7ed;border-radius:4px;background:#fff;color:#5c536b;font-size:10px}.ord-na-head{display:flex;align-items:center;gap:6px;margin-bottom:5px}.ord-na-chip{padding:3px 7px;border-radius:3px;background:#eee8ff;color:#6951aa;font-size:10px}.ord-na-elder{background:#f5f2f9;color:#94839f}.ord-na-targets{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:6px}.ord-na-target{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:4px 7px;min-width:0;padding:6px 7px;border:1px solid #e7e1ef;border-radius:3px;background:#faf8ff}.ord-na-name{min-width:0;padding:0;border:0;background:none;color:#5c468f;text-align:left;font:inherit;font-size:11px;cursor:pointer;overflow-wrap:anywhere}.ord-na-name:hover:not(:disabled){text-decoration:underline}.ord-na-name:disabled{cursor:default;color:#aaa0b5}.ord-na-cost{grid-column:1;font-size:9px;line-height:1.4;color:#9b8ca6;overflow-wrap:anywhere}.ord-na-craft{grid-column:2;grid-row:1 / span 2;padding:5px 9px;border:0;border-radius:3px;background:#6b55bd;color:#fff;font:inherit;cursor:pointer}.ord-na-craft:disabled{background:#e6e0ef;color:#aaa0b5;cursor:default}.ord-na-status{display:flex;gap:7px;align-items:center;margin-top:5px;min-height:0;color:#96869f;line-height:1.5}.ord-na-status[data-tone=error]{color:#b65b6e}.ord-na-cancel{padding:2px 7px;border:1px solid #ddd5e8;border-radius:3px;background:#fff;color:#886b8f;font:inherit;cursor:pointer}.ord-na-empty{padding:3px 0;color:#a297af}.ord-na button:focus-visible{outline:2px solid #9f89da;outline-offset:2px}@media(max-width:1000px){.ord-na-targets{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:460px){.ord-na-targets{grid-template-columns:1fr}}`; (doc.head||doc.documentElement).append(style);}
 let settings=normalize(options.settings||{}),state={connected:false,result:null,calculating:false,error:null,craftAvailable:false,craftRunning:false,craftMessage:null,craftTone:null,craftCanCancel:true},destroyed=false,pending=false,cancelPending=false,localError=null;
 const panel=el("section","ord-na");panel.setAttribute("aria-label","추천 조합");host.append(panel);
 const busy=()=>state.craftRunning===true||pending;
 function candidates(result){
  const mode=result?.directionAdvice?.mode,supports=array(result?.immediateSupports),byCode=new Map(supports.filter(item=>validCode(item.rawcode)).map(item=>[item.rawcode,item]));
  const relevant=item=>!roles[mode]||array(item.roles).some(role=>roles[mode].has(role));
  const output=[],seen=new Set(),add=item=>{if(!item||!validCode(item.rawcode)||seen.has(item.rawcode)||!relevant(item))return;seen.add(item.rawcode);output.push(item);};
  const priorities=array(result?.directionAdvice?.firstPriorities).length?result.directionAdvice.firstPriorities:array(result?.nextPriorities);
  for(const priority of priorities)add(byCode.get(priority.targetRawcode||priority.towardRawcode||priority.rawcode));
  for(const item of supports)add(item);
  return output.slice(0,4);
 }
 const ready=item=>state.connected===true&&state.craftAvailable===true&&!state.calculating&&!state.error&&!busy()&&item.materialsReady===true&&item.resourcesReady!==false&&typeof options.onCraftTarget==="function";
 function request(item){
  if(destroyed||!ready(item))return;pending=true;localError=null;render();
  try{const task=options.onCraftTarget(item.rawcode);if(task&&typeof task.then==="function")Promise.resolve(task).catch(()=>{localError="조합하지 못했습니다.";}).finally(()=>{pending=false;render();});else{pending=false;render();}}
  catch{pending=false;localError="조합하지 못했습니다.";render();}
 }
 function render(){
  if(destroyed)return;panel.replaceChildren();
  if(!state.connected){panel.append(el("div","ord-na-empty","게임 연결 대기"));return;}
  const result=state.result,head=el("div","ord-na-head"),mode=result?.directionAdvice?.mode;
  head.append(el("span","ord-na-chip","보강 후보"));
  if(modes[mode])head.append(el("span","ord-na-chip",modes[mode]));
  const elder=state.elder,elderId=typeof elder==="string"?elder:elder?.id,elderKey={o02E:"warcury",o031:"saturn",o032:"nasujuro"}[elderId]||elderId;if(bosses[elderKey])head.append(el("span","ord-na-chip ord-na-elder",bosses[elderKey]));
  if(head.children.length)panel.append(head);
  const items=result?.mapCompatible===false?[]:candidates(result),grid=el("div","ord-na-targets");
  for(const item of items){
   const row=el("div","ord-na-target"),caption=string(item.name)+(string(item.tier)?" ("+item.tier+")":""),name=el("button","ord-na-name",caption),craft=el("button","ord-na-craft","조합");name.type=craft.type="button";name.disabled=craft.disabled=!ready(item);craft.setAttribute("aria-label",caption+" 조합");name.setAttribute("aria-label",caption+" 조합");name.addEventListener("click",()=>request(item));craft.addEventListener("click",()=>request(item));
   const costs=[];if(finite(item.choiceWisps)&&item.choiceWisps>0)costs.push("선위 "+item.choiceWisps);if(finite(item.wood)&&item.wood>0)costs.push("목재 "+item.wood);if(item.resourcesReady===false)costs.push("자원 부족");else if(item.materialsReady!==true)costs.push("재료 부족");
   row.append(name,el("span","ord-na-cost",costs.join(" · ")),craft);grid.append(row);
  }
  if(items.length)panel.append(grid);else if(!busy())panel.append(el("div","ord-na-empty",state.calculating?"확인 중":state.error?"추천을 확인하지 못했습니다.":"조합할 유닛 없음"));
  const message=string(state.craftMessage),safeMessage=message.length<=120&&!/[A-Za-z]:[\\/]|stacktrace|\bat \w+[.(]/i.test(message)?message:"";
  const statusText=localError||safeMessage||(busy()?"조합 중":"");
  if(statusText||busy()){const status=el("div","ord-na-status");status.setAttribute("role","status");if(localError||state.craftTone==="error")status.dataset.tone="error";status.append(el("span","",statusText));if(busy()&&state.craftCanCancel!==false&&typeof options.onCancelCraft==="function"){const cancel=el("button","ord-na-cancel",cancelPending?"취소 중":"취소");cancel.type="button";cancel.disabled=cancelPending;cancel.addEventListener("click",()=>{if(destroyed||!busy()||cancelPending)return;cancelPending=true;render();try{const task=options.onCancelCraft();if(task&&typeof task.catch==="function")task.catch(()=>{cancelPending=false;localError="취소하지 못했습니다.";render();});}catch{cancelPending=false;localError="취소하지 못했습니다.";render();}});status.append(cancel);}panel.append(status);}
 }
 render();
 return Object.freeze({update(value={}){if(destroyed)return;state={...state,...value};if(!state.connected)state.result=null;if(!state.craftRunning&&!pending)cancelPending=false;if(Object.hasOwn(value,"craftMessage"))localError=null;render();},setSettings(patch,config={}){if(destroyed)return;const next=normalize({...settings,...patch}),changed=JSON.stringify(next)!==JSON.stringify(settings);settings=next;render();if(config.notify&&changed)options.onSettingsChange?.({...settings});},getSettings:()=>({...settings}),setExpanded(){},destroy(){destroyed=true;panel.remove();}});
}
root.OrdNightmarePanel=Object.freeze({create});
})(typeof window!=="undefined"?window:globalThis);
