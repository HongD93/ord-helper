(function(root,factory){
  const api=factory();
  if(typeof module==="object"&&module.exports)module.exports=api;
  root.OrdNightmareRewardBudget=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(){
  "use strict";
  const keys=["choiceWisps","wood","gold"],integer=value=>Number.isSafeInteger(value)&&value>=0;
  const code=value=>typeof value==="string"&&/^[A-Za-z0-9]{4}$/.test(value);
  const empty=known=>({choiceWisps:known?0:null,wood:known?0:null,gold:known?0:null,units:{}});
  function unknown(options,reason){return {status:"unknown",mapHash:options?.mapHash||null,localPlayer:Number.isInteger(options?.localPlayer)?options.localPlayer:null,round:null,storyIndex:null,guaranteed:empty(false),conditional:empty(false),events:[],unknownEvents:[],randomSourceRawcodes:[],reasons:[reason||"progression_unknown"],source:{readOnly:false,method:"map_reward_schedule"}};}
  function amounts(value,nullable){
    if(!value||typeof value!=="object"||!value.units||typeof value.units!=="object")return null;
    const result={units:{}};
    for(const key of keys){if(value[key]===null&&nullable)result[key]=null;else if(integer(value[key]))result[key]=value[key];else return null;}
    for(const [rawcode,count]of Object.entries(value.units)){if(!code(rawcode)||!integer(count)||rawcode==="e018")return null;if(count)result.units[rawcode]=count;}
    return result;
  }
  function normalize(value,options){
    options=options||{};
    const mapHash=options.mapHash||value?.mapHash,localPlayer=options.localPlayer??value?.localPlayer;
    if(!value||!["confirmed","partial"].includes(value.status)||value.source?.readOnly!==true||typeof mapHash!=="string"||!mapHash||value.mapHash!==mapHash||!Number.isInteger(localPlayer)||localPlayer<0||localPlayer>15||value.localPlayer!==localPlayer||!Number.isInteger(value.round)||value.round<1)return unknown(options,"reward_budget_unverified");
    const guaranteed=amounts(value.guaranteed,true),conditional=amounts(value.conditional,true);
    if(!guaranteed||!conditional)return unknown(options,"reward_budget_invalid");
    return {status:value.status,mapHash,localPlayer,round:value.round,storyIndex:Number.isInteger(value.storyIndex)&&value.storyIndex>=0?value.storyIndex:null,guaranteed,conditional,
      events:Array.isArray(value.events)?value.events.filter(event=>event&&typeof event.id==="string").map(event=>({...event})):[],
      unknownEvents:Array.isArray(value.unknownEvents)?value.unknownEvents.filter(id=>typeof id==="string"):[],
      randomSourceRawcodes:Array.isArray(value.randomSourceRawcodes)?Array.from(new Set(value.randomSourceRawcodes.filter(code))):[],
      reasons:Array.isArray(value.reasons)?value.reasons.filter(reason=>typeof reason==="string"):[],source:{...value.source,readOnly:true}};
  }
  function derive(data,progression,options){
    options=options||{};const hash=options.mapHash,player=options.localPlayer;
    if(!data||data.schemaVersion!==1||data.map?.sha256!==hash||!Array.isArray(data.events))return unknown(options,"reward_schedule_mismatch");
    const nativeHash=progression?.mapHash||progression?.source?.mapHash||progression?.source?.mapSha256||progression?.source?.loadedMapHash;
    if(!progression||!["confirmed","partial"].includes(progression.status)||progression.source?.readOnly!==true||progression.localPlayer!==player||nativeHash!==hash||!Number.isInteger(progression.round)||progression.round<1||!Number.isInteger(data.maxRound)||progression.round>data.maxRound)return unknown(options,"progression_unknown");
    const round=progression.round,storyIndex=Number.isInteger(progression.storyIndex)&&progression.storyIndex>=0?progression.storyIndex:null;
    const result={status:"confirmed",mapHash:hash,localPlayer:player,round,storyIndex,guaranteed:empty(true),conditional:empty(true),events:[],unknownEvents:[],randomSourceRawcodes:Array.isArray(data.randomSourceRawcodes)?data.randomSourceRawcodes.filter(code):[],reasons:[],source:{readOnly:true,method:"native_progression_and_map_reward_schedule",progressionStatus:progression.status}};
    if(progression.gameEnded===true){result.source.gameEnded=true;return normalize(result,options);}
    if(progression.difficulty!=="nightmare")return unknown(options,progression.difficulty==="other"?"nightmare_not_active":"difficulty_unknown");
    if(progression.activePlayer!==true)return unknown(options,progression.activePlayer===false?"player_inactive":"active_player_unknown");
    const seen=new Set(),add=(total,grant)=>{for(const key of keys)total[key]+=grant[key];for(const [rawcode,count]of Object.entries(grant.units))total.units[rawcode]=(total.units[rawcode]||0)+count;};
    for(const event of data.events){
      if(!event||typeof event.id!=="string"||seen.has(event.id)){result.status="partial";result.reasons.push("reward_schedule_invalid");continue;}
      seen.add(event.id);
      if(event.preClear!==true||event.repeatable===true||event.budgetable===false)continue;
      const grant=amounts(event.grant,false);
      if(!grant||!["guaranteed","conditional"].includes(event.certainty)){result.status="partial";result.unknownEvents.push(event.id);continue;}
      const timing=event.timing||{},flag=event.claimFlag;
      const explicit=progression.rewardEvents?.[event.id];
      const tech=flag?.type==="player_tech"?(progression.playerTechLevels||progression.techLevels||{})[flag.rawcode]:null;
      if(explicit==="claimed"||explicit==="missed"||flag?.type==="player_tech"&&progression.claimedTechs?.[flag.rawcode]===true||integer(tech)&&tech>=(flag.minimumLevel||1))continue;
      let pending=false,conditional=event.certainty==="conditional";
      if(timing.type==="round_start"||timing.type==="round_end"){
        // 같은 라운드의 지급 경계는 현재 보유 재고와 중복될 수 있으므로 항상 제외한다.
        if(!Number.isInteger(timing.round)||timing.round<=round)continue;
        pending=true;
      }else if(timing.type==="story_clear"){
        if(!Number.isInteger(timing.storyOrder)||storyIndex===null){result.status="partial";result.unknownEvents.push(event.id);continue;}
        if(timing.storyOrder<storyIndex)continue;
        // on은 다음 스토리의 생성 때 바뀐다. 현재 단계는 보상 지급 뒤에도 같은 값일 수 있다.
        const explicitlyPending=explicit==="pending"||explicit==="available"||flag?.type==="player_tech"&&progression.claimedTechs?.[flag.rawcode]===false||integer(tech)&&tech<(flag.minimumLevel||1);
        if(timing.storyOrder===storyIndex&&!explicitlyPending){result.status="partial";result.unknownEvents.push(event.id);result.reasons.push("current_story_claim_unknown");continue;}
        const deadline=event.deadline;
        if(deadline?.type==="round_end"&&Number.isInteger(deadline.round)&&round>deadline.round){result.status="partial";result.unknownEvents.push(event.id);result.reasons.push("story_deadline_state_inconsistent");continue;}
        pending=true;conditional=true;
      }else if(explicit==="pending"||explicit==="available"){
        pending=true;conditional=true;
      }else{result.status="partial";result.unknownEvents.push(event.id);continue;}
      if(event.eligibility?.activePlayer===true&&progression.activePlayer===false)continue;
      if(event.eligibility?.activePlayer===true&&progression.activePlayer!==true){result.status="partial";result.unknownEvents.push(event.id);continue;}
      if(!pending)continue;
      add(conditional?result.conditional:result.guaranteed,grant);
      result.events.push({id:event.id,certainty:conditional?"conditional":"guaranteed",timing:{...timing},grant});
    }
    result.reasons=Array.from(new Set(result.reasons));
    return normalize(result,options);
  }
  return {derive,normalize};
});
