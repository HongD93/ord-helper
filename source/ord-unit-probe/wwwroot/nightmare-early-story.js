(function(root,factory){const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph);if(typeof module==="object"&&module.exports)module.exports=api;root.OrdNightmareEarlyStory=api;})(typeof globalThis!=="undefined"?globalThis:this,function(Graph){
 "use strict";
 const list=value=>Array.isArray(value)?value:[],finite=value=>typeof value==="number"&&Number.isFinite(value),clean=value=>String(value||"").replace(/\|c[0-9a-f]{8}|\|r/gi,"").replace(/\|n/gi," ").trim();
 const excluded=new Set(["h076","h04T","h04Y","h05D","h0CA","h06O","h0C5","h0C8"]);
 const classic2331="20ce1b0918e56d18ad02a11a6dddf05298d6478037721f20a29702d4cd7b145d";
 const classic2323="0794996c9bc73eaa3020f20ebc45fb37cfa64a6e98ffe8aa0dfa3b356e77bc5c";
 function create(catalog,combat,rankings,options={}){
  const hash=catalog?.map?.sha256,valid=typeof hash==="string"&&/^[a-f0-9]{64}$/i.test(hash)&&combat?.map?.sha256===hash,units=new Map(list(catalog?.units).map(unit=>[unit.rawcode,unit]));
  const materialOnly=options.materialOnly===true&&hash===classic2331&&!valid;
  const eligible=unit=>unit?.playable===true&&["전설","히든"].includes(unit.tier)&&unit.isSummon!==true&&unit.isSummoned!==true&&!excluded.has(unit.rawcode);
  const native=code=>{const profile=combat?.units?.[code];return valid&&eligible(units.get(code))&&profile?.verified===true&&profile.verificationKind==="native"&&profile.mapSha256===hash;};
  const recipes=list(catalog?.recipes).filter(recipe=>Graph?.isProductionRecipe(recipe)),production=new Set(recipes.map(recipe=>recipe.resultUnitRawcode)),entries=new Map();
  const referenceData=materialOnly&&rankings?.catalogMapHash===classic2323;
  const conditions=rankings?.conditions;
  const rankingsValid=rankings?.schemaVersion===1&&(rankings.catalogMapHash===hash||referenceData)&&
   list(rankings.entries).length>0&&rankings.entries.length<=512&&conditions?.difficulty==="악몽"&&
   conditions.story==="마린포드"&&conditions.durationSeconds===60&&conditions.startingHealthPercent===100&&
   conditions.buffPolicy==="cooldown"&&conditions.holdControl===false;
  if(rankingsValid)for(const row of rankings.entries){
   const unit=units.get(row?.rawcode);
   const eligibleRank=referenceData?eligible(unit):native(row?.rawcode);
   if(!eligibleRank||!production.has(row.rawcode)||row.tier!==unit.tier||
      clean(unit.displayName||unit.properName||unit.name)!==row.catalogName||
      !Number.isSafeInteger(row.rank)||row.rank<1||row.rank>rankings.entries.length||
      row.metric!=="damage_percent"||!finite(row.value)||row.value<=0||row.value>100||entries.has(row.rawcode))continue;
   entries.set(row.rawcode,{
    rawcode:row.rawcode,name:unit.displayName||unit.name||row.rawcode,tier:unit.tier,count:1,
    storyRank:row.rank,storyEvidence:"historical_story_reference",
    ...(referenceData?{}:{storyDamagePercent:row.value,measurementVersion:rankings.measurementVersion??null}),
    sourceExperimentVersion:rankings.sourceExperimentVersion||null
   });
  }
  const historicalStoryReference=referenceData&&entries.size>0;
  const rankedCandidates=!materialOnly||historicalStoryReference;
  if(!rankedCandidates)for(const unit of units.values()){
   if(!eligible(unit)||!production.has(unit.rawcode))continue;
   entries.set(unit.rawcode,{rawcode:unit.rawcode,name:unit.displayName||unit.name||unit.rawcode,tier:unit.tier,count:1,storyEvidence:"current_recipe_materials"});
  }
  const sourceCandidateCount=entries.size;
  if(rankedCandidates){
   const qualityLimit=Math.ceil(sourceCandidateCount*.25),qualityCodes=new Set(Array.from(entries.values()).sort((a,b)=>a.storyRank-b.storyRank).slice(0,qualityLimit).map(row=>row.rawcode));
   for(const code of entries.keys())if(!qualityCodes.has(code))entries.delete(code);
  }
  const graph=options.recipeGraph||Graph?.create(catalog);
  function candidates(counts,settings={}){
   if((!valid&&!materialOnly)||!graph?.analyze)return[];const inventory=counts instanceof Map?counts:new Map(Object.entries(counts||{})),supported=settings.supportedRecipeIds==null?null:new Set(settings.supportedRecipeIds);
   const rows=[];for(const row of entries.values()){
    if(!(materialOnly&&supported?.size===0)&&!recipes.some(recipe=>recipe.resultUnitRawcode===row.rawcode&&(!supported||supported.has(String(recipe.id)))))continue;
    const progress=graph.analyze(row.rawcode,inventory,undefined,{resources:{wood:settings.wood,gold:settings.gold},items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:settings.localPlayer});
    rows.push({...row,owned:inventory.get(row.rawcode)||0,progress,ready:false});
   }
   const percent=row=>finite(row.progress?.percent)?row.progress.percent:-1,ratio=row=>finite(row.progress?.ratio)?row.progress.ratio:-1;
   rows.sort((a,b)=>percent(b)-percent(a)||ratio(b)-ratio(a)||(a.storyRank??Infinity)-(b.storyRank??Infinity)||a.rawcode.localeCompare(b.rawcode));return rows;
  }
  return{
   valid,materialOnly,historicalStoryReference,candidates,candidateCount:entries.size,sourceCandidateCount,
   relativeQualityFraction:rankedCandidates ? .25 : null,
   storyMeasurementVersion:rankingsValid&&!referenceData?rankings.measurementVersion??null:null,
   sourceExperimentVersion:rankingsValid&&rankedCandidates?rankings.sourceExperimentVersion||null:null,
   policy:rankedCandidates?"top_quartile_story_reference_actual_inventory_percent_first":"current_recipe_materials"
  };
 }
 return{create};
});
