(function(root,factory){
  const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph);
  if(typeof module==="object"&&module.exports)module.exports=api;
  if(root)root.OrdStartingEvolutionForecast=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(Graph){
  "use strict";
  const finite=value=>typeof value==="number"&&Number.isFinite(value),bounded=value=>Math.max(0,Math.min(100,value));
  const tiers={rare:new Set(["rare","희귀","희귀함"]),special:new Set(["special","특별","특별함"])};
  const rawcode=value=>typeof value==="string"&&/^[A-Za-z0-9]{4}$/.test(value);
  function reachable(analysis){
    const current=bounded(finite(analysis?.ratio)?analysis.ratio*100:finite(analysis?.percent)?analysis.percent:0);
    const choice=finite(analysis?.choiceReachableRatio)?analysis.choiceReachableRatio*100:finite(analysis?.choiceReachablePercent)?analysis.choiceReachablePercent:current;
    return Math.max(current,bounded(choice));
  }
  function create(catalog,options={}){
    const hash=catalog?.map?.sha256,valid=typeof hash==="string"&&/^[a-f0-9]{64}$/i.test(hash)&&Array.isArray(catalog?.units)&&Array.isArray(catalog?.recipes),units=new Map((Array.isArray(catalog?.units)?catalog.units:[]).map(unit=>[unit.rawcode,unit]));
    const tierLabels=new Map((Array.isArray(catalog?.tiers)?catalog.tiers:[]).map(tier=>[String(tier.id),String(tier.label||tier.id)]));
    const tierOf=unit=>tierLabels.get(String(unit?.tier))||String(unit?.tier||"");
    const graph=options.recipeGraph||(valid?Graph?.create?.(catalog):null),cache=new Map();let lastKey="";
    function reject(){lastKey="";cache.clear();return null;}
    function forecast(targetRawcode,counts,settings={}){
      const target=units.get(targetRawcode),snapshot=settings.snapshot,state=snapshot?.startingEvolution,source=state?.source,progression=snapshot?.progression;
      if(!settings.analysis||!tiers.rare.has(tierOf(target))||target?.playable!==true)return null;
      const now=settings.now??Date.now(),age=now-settings.receivedAt,player=snapshot?.localPlayer,result=units.get(state?.resultRawcode);
      if(!valid||!graph?.analyze||!(counts instanceof Map)||
        settings.mode!=="live"||settings.liveState?.tone!=="live"||settings.liveState?.checking===true||!finite(age)||age<0||age>3000||
        snapshot?.status!=="snapshot_read"||snapshot.stableRoots!==true||snapshot.stableUnits!==true||snapshot.readErrors!==0||
        snapshot.catalogMatchesLoadedArchive!==true||snapshot.loadedMapHash!==hash||snapshot.gameVersion!=="1.28.5.7680"||!Number.isSafeInteger(snapshot.processId)||snapshot.processId<=0||
        !Number.isInteger(player)||player<0||player>3||state?.status!=="confirmed"||state.localPlayer!==player||
        progression?.status!=="confirmed"||progression.localPlayer!==player||progression.activePlayer!==true||progression.gameEnded!==false||progression.victory===true||
        !["charging","activating"].includes(state.phase)||state.pendingCount!==1||state.targetKills!==152||
        !Number.isSafeInteger(state.killCount)||state.killCount<0||state.killCount>152||!rawcode(state.resultRawcode)||
        state.phase==="charging"&&state.killCount>=152||state.phase==="activating"&&state.killCount!==152||
        result?.playable!==true||result.isSummon===true||result.isSummoned===true||!tiers.special.has(tierOf(result))||
        source?.readOnly!==true||source.mapSha256!==hash||!Number.isSafeInteger(source.sampleCount)||source.sampleCount<2||
        !Array.isArray(snapshot.counts))return reject();
      const observed=new Map();
      for(const unit of snapshot.counts){
        const count=unit?.activeCount??unit?.count;
        if(!rawcode(unit?.rawcode)||!Number.isSafeInteger(count)||count<0||observed.has(unit.rawcode))return reject();
        if(count>0)observed.set(unit.rawcode,count);
      }
      const stock=new Map();
      for(const [code,count]of counts){
        if(!rawcode(code)||!Number.isSafeInteger(count)||count<0)return reject();
        if(count>0)stock.set(code,count);
      }
      if(stock.size!==observed.size||Array.from(stock).some(([code,count])=>observed.get(code)!==count))return reject();
      const fromPercent=reachable(settings.analysis);if(fromPercent>=100)return null;
      // 킬 수·시각·순번은 실제 재료가 아니다. 예상 결과1개와 실제 분석 입력이 같은 동안 계산을 재사용한다.
      const key=JSON.stringify([hash,snapshot.processId,snapshot.sessionIdentity||null,player,state.resultRawcode,
        Array.from(stock).sort(([a],[b])=>a.localeCompare(b)),settings.resources||null,settings.items||null,settings.recipeConditions||null]);
      if(key!==lastKey){cache.clear();lastKey=key;}
      if(!cache.has(targetRawcode)){
        const projected=new Map(stock);projected.set(state.resultRawcode,(projected.get(state.resultRawcode)||0)+1);
        try{
          const analysis=graph.analyze(targetRawcode,projected,undefined,{resources:settings.resources,items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:player});
          let end=reachable(analysis);
          if(analysis?.resourcesReady===false||analysis?.itemsReady===false||analysis?.missingResources?.length||analysis?.missingItems?.length)end=Math.min(99,end);
          cache.set(targetRawcode,finite(end)?bounded(end):null);
        }catch{cache.set(targetRawcode,null);}
      }
      const projected=cache.get(targetRawcode);if(!finite(projected)||projected<=fromPercent+1e-9)return null;
      const toPercent=Math.max(fromPercent,Math.min(100,projected));
      // 실제 analysis·수량·계획·조합 가능 여부는 반환하거나 변경하지 않는다.
      return Object.freeze({resultRawcode:state.resultRawcode,fromPercent,toPercent,width:toPercent-fromPercent,labelPercent:Math.round(toPercent)});
    }
    return Object.freeze({forecast});
  }
  return Object.freeze({create});
});
