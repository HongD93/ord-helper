(function(root,factory){
  const api=factory();
  if(typeof module==="object"&&module.exports)module.exports=api;
  root.OrdNightmareRandomForecast=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(){
  "use strict";
  const integer=value=>Number.isSafeInteger(value)&&value>=0;
  const code=value=>typeof value==="string"&&/^[A-Za-z0-9]{4}$/.test(value);
  const common=["h003","h002","h001","h007","h004","h008","h005","h009","h006"];
  function normalizeHistory(value,options){
    options=options||{};const mapHash=options.mapHash,player=options.localPlayer;
    const unknown=reason=>({status:"unknown",localPlayer:Number.isInteger(player)?player:null,drawCount:null,correctionInterval:null,commonAcquiredCounts:{},source:{readOnly:false,reason,mapSha256:mapHash||null}});
    if(!value||value.status!=="confirmed"||value.source?.readOnly!==true||value.source.sampleCount!==2||!mapHash||(value.source.mapSha256||value.source.mapHash)!==mapHash||!Number.isInteger(player)||player<0||player>3||value.localPlayer!==player)return unknown("random_history_unverified");
    if(!integer(value.drawCount)||value.drawCount>2147481599||value.correctionInterval!==2||!common.every(rawcode=>integer(value.commonAcquiredCounts?.[rawcode]))||common.reduce((sum,rawcode)=>sum+value.commonAcquiredCounts[rawcode],0)!==value.drawCount)return unknown("random_history_invalid");
    return {status:"confirmed",localPlayer:player,drawCount:value.drawCount,correctionInterval:value.correctionInterval,commonAcquiredCounts:Object.fromEntries(common.map(rawcode=>[rawcode,value.commonAcquiredCounts[rawcode]])),source:{readOnly:true,mapSha256:mapHash,sampleCount:2,method:"two_matching_native_random_history_reads"}};
  }
  function seedOf(value){let hash=2166136261;for(const ch of String(value)){hash^=ch.charCodeAt(0);hash=Math.imul(hash,16777619);}return hash>>>0||0x9e3779b9;}
  function rng(value){let state=seedOf(value);return ()=>{state^=state<<13;state^=state>>>17;state^=state<<5;return (state>>>0)/4294967296;};}
  function sourceValid(source){return code(source?.rawcode)&&source.status==="verified"&&source.consumeCount===1&&source.outputCount===1&&["fixed_weighted","history_balanced_common"].includes(source.kind)&&Array.isArray(source.outcomes)&&source.outcomes.length>0&&source.outcomes.every(item=>code(item.rawcode)&&Number.isFinite(item.probability)&&item.probability>0)&&Math.abs(source.outcomes.reduce((sum,item)=>sum+item.probability,0)-1)<1e-9&&
    (source.kind!=="history_balanced_common"||Array.isArray(source.history?.commonRawcodes)&&source.history.commonRawcodes.length===9&&new Set(source.history.commonRawcodes).size===9&&source.history.commonRawcodes.every(rawcode=>common.includes(rawcode))&&code(source.ship?.rawcode)&&Number.isFinite(source.ship.probability)&&source.ship.probability>=0&&source.ship.probability<1);}
  function distribution(source,history){
    if(!sourceValid(source))return null;
    if(source.kind==="fixed_weighted")return source.outcomes.map(item=>({rawcode:item.rawcode,probability:item.probability}));
    if(history?.status!=="confirmed")return null;
    const codes=source.history?.commonRawcodes;if(!Array.isArray(codes)||codes.length!==9||!codes.every(rawcode=>common.includes(rawcode)))return null;
    const ship=source.ship;if(!code(ship?.rawcode)||!Number.isFinite(ship.probability)||ship.probability<0||ship.probability>=1)return null;
    const balanced=(history.drawCount+1)%history.correctionInterval===0;
    return [{rawcode:ship.rawcode,probability:ship.probability},...codes.map(rawcode=>({rawcode,probability:(1-ship.probability)*(balanced?(2*codes.filter(other=>history.commonAcquiredCounts[other]>history.commonAcquiredCounts[rawcode]).length+codes.filter(other=>other!==rawcode&&history.commonAcquiredCounts[other]===history.commonAcquiredCounts[rawcode]).length)/72:1/9)}))];
  }
  function pick(source,history,random){
    if(source.kind==="fixed_weighted"){
      let remaining=random();for(const item of source.outcomes){remaining-=item.probability;if(remaining<0)return item.rawcode;}return source.outcomes.at(-1).rawcode;
    }
    if(random()<source.ship.probability)return source.ship.rawcode;
    const codes=source.history.commonRawcodes;let index=Math.floor(random()*codes.length);
    if((history.drawCount+1)%history.correctionInterval===0){let other=Math.floor(random()*(codes.length-1));if(other>=index)other++;if(history.commonAcquiredCounts[codes[other]]<history.commonAcquiredCounts[codes[index]])index=other;}
    const rawcode=codes[index];history.drawCount++;history.commonAcquiredCounts[rawcode]++;return rawcode;
  }
  function buildBanks(data,options){
    options=options||{};const samples=Math.max(32,Math.min(512,integer(options.samples)?options.samples:128)),history=normalizeHistory(options.history,options);
    const modes={guaranteed:{available:true,reasons:[],drawCounts:{},additions:Array.from({length:samples},()=>({}))},conditional:{available:true,reasons:[],drawCounts:{},additions:Array.from({length:samples},()=>({}))}};
    const reject=(mode,reason)=>{modes[mode].available=false;modes[mode].reasons.push(reason);};
    if(!data||data.schemaVersion!==1||data.map?.sha256!==options.mapHash||!Array.isArray(data.sources)){for(const mode of Object.keys(modes))reject(mode,"random_drop_catalog_mismatch");return {status:"unknown",samples,modes,historyStatus:history.status,nextDrawProbabilities:{}};}
    const seed=String(options.seed||options.sessionIdentity||options.mapHash),nextDrawProbabilities={},seen=new Set();
    for(const source of data.sources){
      const rawcode=source.rawcode;if(seen.has(rawcode)){for(const mode of Object.keys(modes))reject(mode,"random_drop_source_duplicate");continue;}seen.add(rawcode);
      const amount=group=>group?.[rawcode]===undefined?0:group[rawcode],counts=[amount(options.currentWisps),amount(options.guaranteedWisps),amount(options.conditionalWisps)];
      const validCounts=counts.every(integer),base=validCounts?counts[0]+counts[1]:0,total=validCounts?base+counts[2]:0;
      modes.guaranteed.drawCounts[rawcode]=base;modes.conditional.drawCounts[rawcode]=total;
      if(!validCounts||total>2048){for(const mode of Object.keys(modes))reject(mode,"random_draw_count_invalid");continue;}
      nextDrawProbabilities[rawcode]=distribution(source,history);
      if(!total)continue;
      if(!sourceValid(source)){if(base)reject("guaranteed","random_drop_source_unverified");reject("conditional","random_drop_source_unverified");continue;}
      if(source.kind==="history_balanced_common"&&history.status!=="confirmed"){if(base)reject("guaranteed","random_history_unknown");reject("conditional","random_history_unknown");continue;}
      for(let sample=0;sample<samples;sample++){
        const random=rng(seed+"|"+rawcode+"|"+sample),state={drawCount:history.drawCount,correctionInterval:history.correctionInterval,commonAcquiredCounts:{...history.commonAcquiredCounts}};
        for(let draw=0;draw<total;draw++){
          const output=pick(source,state,random),combined=modes.conditional.additions[sample];combined[output]=(combined[output]||0)+1;
          if(draw<base){const guaranteed=modes.guaranteed.additions[sample];guaranteed[output]=(guaranteed[output]||0)+1;}
        }
      }
    }
    for(const mode of Object.values(modes))mode.remainingDraws=Object.values(mode.drawCounts).reduce((sum,value)=>sum+value,0);
    return {status:modes.guaranteed.available&&modes.conditional.available?"ready":"partial",samples,historyStatus:history.status,seedFingerprint:seedOf(seed).toString(16),modes,nextDrawProbabilities,probabilityKind:"nominal_script_weights",assumptions:["nominal_weights_independent_uniform_model","fixed_native_correction_history","choice_wisps_reserved_for_missing_materials"],winProbability:false};
  }
  function confidence(successes,samples){
    if(!samples)return null;const z=1.959963984540054,p=successes/samples,z2=z*z,denominator=1+z2/samples,center=(p+z2/(2*samples))/denominator,half=z*Math.sqrt(p*(1-p)/samples+z2/(4*samples*samples))/denominator;
    return {level:.95,lower:Math.max(0,center-half),upper:Math.min(1,center+half),method:"wilson_binomial"};
  }
  function estimate(banks,evaluate,options){
    options=options||{};const results={};
    for(const [name,mode]of Object.entries(banks.modes||{})){
      if(!mode.available){results[name]={status:"unknown",probability:null,samples:0,successes:null,confidence:null,reasons:mode.reasons,remainingDraws:mode.remainingDraws||0,drawCounts:mode.drawCounts};continue;}
      let samples=0,successes=0,limited=false;
      for(const additions of mode.additions){if(Date.now()>(options.deadline??Infinity)){limited=true;break;}const result=evaluate(additions,name);if(result===null||result?.unknown){limited=true;break;}samples++;if(result===true||result?.success===true)successes++;}
      const valid=samples>=(options.minimumSamples||32),interval=valid?confidence(successes,samples):null;
      results[name]={status:valid?"estimated":"unknown",probability:valid?successes/samples:null,samples,successes,confidence:interval,limited,remainingDraws:mode.remainingDraws,drawCounts:mode.drawCounts,observedZero:valid&&successes===0,observedAll:valid&&successes===samples};
    }
    return combine(banks,results);
  }
  function combine(banks,results){const primary=results.guaranteed?.status==="estimated"&&results.guaranteed.successes>0?results.guaranteed:results.conditional?.status==="estimated"?results.conditional:results.guaranteed;return {...primary,scope:"material_completion",guaranteed:results.guaranteed,conditionalRewards:results.conditional,conditional:primary===results.conditional,historyStatus:banks.historyStatus,method:"deterministic_route_witness_monte_carlo",routeLimited:true,winProbability:false,isEstimate:true,assumptions:banks.assumptions||[],probabilityKind:banks.probabilityKind,seedFingerprint:banks.seedFingerprint};}
  function estimateMany(banks,evaluators,options){
    options=options||{};const results=evaluators.map(()=>({})),active={};
    for(const [name,mode]of Object.entries(banks.modes||{})){
      active[name]=new Set();for(let index=0;index<evaluators.length;index++){results[index][name]={status:"unknown",probability:null,samples:0,successes:0,confidence:null,remainingDraws:mode.remainingDraws||0,drawCounts:mode.drawCounts,reasons:mode.reasons};if(mode.available)active[name].add(index);}
    }
    let limited=false;
    for(let sample=0;sample<banks.samples;sample++){
      const row=[];let incomplete=false;
      for(const [name,mode]of Object.entries(banks.modes||{})){
        for(const index of active[name]){
          if(Date.now()>(options.deadline??Infinity)){limited=true;incomplete=true;break;}
          const value=evaluators[index](mode.additions[sample],name);
          if(value===null||value?.unknown){results[index][name].reasons=[value?.reason||"route_unverified"];active[name].delete(index);continue;}
          row.push([index,name,value===true||value?.success===true]);
        }
        if(incomplete)break;
      }
      if(incomplete)break;
      for(const [index,name,success]of row){const result=results[index][name];result.samples++;if(success)result.successes++;}
      if(Object.values(active).every(indices=>!indices.size))break;
    }
    for(const [name,indices]of Object.entries(active))for(const index of indices){const result=results[index][name],valid=result.samples>=(options.minimumSamples||32);result.limited=limited;result.status=valid?"estimated":"unknown";result.probability=valid?result.successes/result.samples:null;result.confidence=valid?confidence(result.successes,result.samples):null;result.observedZero=valid&&result.successes===0;result.observedAll=valid&&result.successes===result.samples;}
    return results.map(result=>combine(banks,result));
  }
  return {normalizeHistory,buildBanks,estimate,estimateMany,confidence,distribution};
});
