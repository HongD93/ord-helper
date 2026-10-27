(function(root,factory){const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph);if(typeof module==="object"&&module.exports)module.exports=api;root.OrdNightmareEarlyStory=api;})(typeof globalThis!=="undefined"?globalThis:this,function(Graph){
 "use strict";
 const list=value=>Array.isArray(value)?value:[],finite=value=>typeof value==="number"&&Number.isFinite(value),clean=value=>String(value||"").replace(/\|c[0-9a-f]{8}|\|r/gi,"").replace(/\|n/gi," ").trim();
 const excluded=new Set(["h076","h04T","h04Y","h05D","h0CA","h06O","h0C5","h0C8"]);
 function create(catalog,combat,rankings,options={}){
  const hash=catalog?.map?.sha256,valid=typeof hash==="string"&&/^[a-f0-9]{64}$/i.test(hash)&&combat?.map?.sha256===hash,units=new Map(list(catalog?.units).map(unit=>[unit.rawcode,unit]));
  const native=code=>{const unit=units.get(code),profile=combat?.units?.[code];return valid&&unit?.playable===true&&["전설","히든"].includes(unit.tier)&&unit.isSummon!==true&&unit.isSummoned!==true&&!excluded.has(code)&&profile?.verified===true&&profile.verificationKind==="native"&&profile.mapSha256===hash;};
  const recipes=list(catalog?.recipes).filter(recipe=>Graph?.isProductionRecipe(recipe)),production=new Set(recipes.map(recipe=>recipe.resultUnitRawcode)),entries=new Map();
  const conditions=rankings?.conditions,rankingsValid=rankings?.schemaVersion===1&&rankings.catalogMapHash===hash&&list(rankings.entries).length>0&&rankings.entries.length<=512&&conditions?.difficulty==="악몽"&&conditions.story==="마린포드"&&conditions.durationSeconds===60&&conditions.startingHealthPercent===100&&conditions.buffPolicy==="cooldown"&&conditions.holdControl===false;
  if(rankingsValid)for(const row of rankings.entries){const unit=units.get(row?.rawcode);if(!native(row?.rawcode)||!production.has(row.rawcode)||row.tier!==unit.tier||clean(unit.displayName||unit.properName||unit.name)!==row.catalogName||!Number.isSafeInteger(row.rank)||row.rank<1||row.rank>rankings.entries.length||!finite(row.value)||row.value<=0||row.metric!=="damage_percent"||row.value>100||entries.has(row.rawcode))continue;entries.set(row.rawcode,{rawcode:row.rawcode,name:unit.displayName||unit.name||row.rawcode,tier:unit.tier,count:1,storyRank:row.rank,storyDamagePercent:row.value,storyEvidence:"historical_story_reference",measurementVersion:rankings.measurementVersion??null,sourceExperimentVersion:rankings.sourceExperimentVersion||null});}
  const sourceCandidateCount=entries.size,qualityLimit=Math.ceil(sourceCandidateCount*.25),qualityCodes=new Set(Array.from(entries.values()).sort((a,b)=>a.storyRank-b.storyRank).slice(0,qualityLimit).map(row=>row.rawcode));
  for(const code of entries.keys())if(!qualityCodes.has(code))entries.delete(code);
  const graph=options.recipeGraph||Graph?.create(catalog);
  function candidates(counts,settings={}){
   if(!valid||!graph?.analyze)return[];const inventory=counts instanceof Map?counts:new Map(Object.entries(counts||{})),supported=settings.supportedRecipeIds==null?null:new Set(settings.supportedRecipeIds);
   const rows=[];for(const row of entries.values()){
    if(!recipes.some(recipe=>recipe.resultUnitRawcode===row.rawcode&&(!supported||supported.has(String(recipe.id)))))continue;
    const progress=graph.analyze(row.rawcode,inventory,undefined,{resources:{wood:settings.wood,gold:settings.gold},items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:settings.localPlayer});
    rows.push({...row,owned:inventory.get(row.rawcode)||0,progress,ready:false});
   }
   const percent=row=>finite(row.progress?.percent)?row.progress.percent:-1,ratio=row=>finite(row.progress?.ratio)?row.progress.ratio:-1;
   rows.sort((a,b)=>percent(b)-percent(a)||ratio(b)-ratio(a)||(a.storyRank??Infinity)-(b.storyRank??Infinity)||a.rawcode.localeCompare(b.rawcode));return rows;
  }
  return{valid,candidates,candidateCount:entries.size,sourceCandidateCount,relativeQualityFraction:.25,storyMeasurementVersion:rankingsValid?rankings.measurementVersion??null:null,sourceExperimentVersion:rankingsValid?rankings.sourceExperimentVersion||null:null,policy:"top_quartile_story_reference_actual_inventory_percent_first"};
 }
 return{create};
});
