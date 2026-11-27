(function(root,factory){const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph);if(typeof module==="object"&&module.exports)module.exports=api;root.OrdNightmareEarlyStory=api;})(typeof globalThis!=="undefined"?globalThis:this,function(Graph){
 "use strict";
 const list=value=>Array.isArray(value)?value:[],finite=value=>typeof value==="number"&&Number.isFinite(value);
 const excluded=new Set(["h076","h04T","h04Y","h05D","h0CA","h06O","h0C5","h0C8"]);
 const classic2331="20ce1b0918e56d18ad02a11a6dddf05298d6478037721f20a29702d4cd7b145d";
 // 전설·히든 순위는 9번 스토리(어인섬) 파괴 시간 순위(nightmare-story-speed 자료의 9번 묶음)를 쓴다.
 const storyNumber=9;
 function storyGroup(rankings,hash){
  if(rankings?.schemaVersion!==1||typeof hash!=="string"||rankings.catalogMapHash!==hash)return null;
  const group=list(rankings.groups).find(row=>list(row?.stories)[0]===storyNumber);
  return group&&Number.isSafeInteger(group.rowCount)&&group.rowCount>0&&group.rowCount<=512&&list(group.units).length>0?group:null;
 }
 function create(catalog,combat,rankings,options={}){
  const hash=catalog?.map?.sha256,valid=typeof hash==="string"&&/^[a-f0-9]{64}$/i.test(hash)&&combat?.map?.sha256===hash,units=new Map(list(catalog?.units).map(unit=>[unit.rawcode,unit]));
  const materialOnly=options.materialOnly===true&&hash===classic2331&&!valid;
  const eligible=unit=>unit?.playable===true&&["전설","히든"].includes(unit.tier)&&unit.isSummon!==true&&unit.isSummoned!==true&&!excluded.has(unit.rawcode);
  const recipes=list(catalog?.recipes).filter(recipe=>Graph?.isProductionRecipe(recipe)),production=new Set(recipes.map(recipe=>recipe.resultUnitRawcode)),entries=new Map();
  const group=storyGroup(rankings,hash);
  if(group)for(const row of group.units){
   const unit=units.get(row?.rawcode),rank=row?.ranks?.[storyNumber];
   if(!eligible(unit)||!production.has(row.rawcode)||row.tier!==unit.tier||!Number.isSafeInteger(rank)||rank<1||rank>group.rowCount||entries.has(row.rawcode))continue;
   entries.set(row.rawcode,{
    rawcode:row.rawcode,name:unit.displayName||unit.name||row.rawcode,tier:unit.tier,count:1,
    storyRank:rank,storyEvidence:"story_destruction_rank",...(finite(row.seconds?.[storyNumber])?{storySeconds:row.seconds[storyNumber]}:{})
   });
  }
  // 순위가 없으면 재료 기준 모드에서만 현재 조합식 후보로 대신한다. 전투 자료가 맞는 모드는 기존처럼 후보를 만들지 않는다.
  const storyRanking=entries.size>0,rankedCandidates=!materialOnly||storyRanking;
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
   valid,materialOnly,storyRanking,storyLabel:storyRanking&&typeof group.label==="string"?group.label:null,candidates,candidateCount:entries.size,sourceCandidateCount,
   relativeQualityFraction:rankedCandidates?.25:null,
   policy:rankedCandidates?"top_quartile_story_rank_actual_inventory_percent_first":"current_recipe_materials"
  };
 }
 return{create};
});
