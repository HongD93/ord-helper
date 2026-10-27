"use strict";
importScripts("/recipe-graph.js", "/nightmare-balance.js", "/nightmare-records.js", "/nightmare-reward-budget.js", "/nightmare-random-forecast.js", "/nightmare-combat-verifier.js", "/nightmare-support-adaptation.js", "/nightmare-early-story.js", "/nightmare-hand-fit.js", "/nightmare-reinforcements.js", "/nightmare-strategy.js");
let advisor = null,advisorMapHash=null,recordsRevision=null;
self.onmessage = function (event) {
  const message = event.data || {};
  try {
    if (message.type === "init") {
      advisor = self.OrdNightmareStrategy.create(message.catalog, message.combat, message.rules, message.clearRecords,{randomDrops:message.randomDrops,combatValidation:message.combatValidation,storyRankings:message.storyRankings,earlyStoryRecommendations:true,handFitRecommendations:true,reinforcementRecommendations:true,recordOnly:true,supportAdaptation:true,minRecordVersion:"2.321"});
      advisorMapHash=message.catalog?.map?.sha256||null;
      recordsRevision=typeof message.recordsRevision==="string"&&/^[a-f0-9]{64}$/.test(message.recordsRevision)?message.recordsRevision:null;
      self.postMessage({type:"ready"});
    } else if(message.type==="records-update"){
      if(!advisor||typeof message.recordsRevision!=="string"||!/^[a-f0-9]{64}$/.test(message.recordsRevision)||message.recordCount!==message.clearRecords?.records?.length)throw new Error("record_update_invalid");
      const update=advisor.updateClearRecords?.(message.clearRecords,message.recordsRevision);
      if(update?.accepted!==true)throw new Error("record_update_invalid");
      recordsRevision=message.recordsRevision;
      self.postMessage({type:"records-ready",recordsRevision});
    } else if (message.type === "recommend") {
      if (!advisor) throw new Error("advisor_not_ready");
      const settings={...(message.settings||{})},items=settings.items,codes=["AI00","AI01","AI02"];
      const confirmed=items?.status==="confirmed"&&Number.isInteger(settings.localPlayer)&&items.localPlayer===settings.localPlayer&&items.source?.readOnly===true&&codes.every(code=>items.counts?.[code]===0||items.counts?.[code]===1);
      settings.items={status:confirmed?"confirmed":"unknown",localPlayer:Number.isInteger(settings.localPlayer)?settings.localPlayer:null,counts:confirmed?Object.fromEntries(codes.map(code=>[code,items.counts[code]])):{},source:{readOnly:items?.source?.readOnly===true}};
      const conditions=settings.recipeConditions,valid=Number.isInteger(settings.localPlayer)&&conditions?.localPlayer===settings.localPlayer&&conditions.source?.readOnly===true&&["confirmed","partial"].includes(conditions.status);
      const special=conditions?.special,specialValid=Boolean(advisorMapHash);
      const normalizedSpecial=self.OrdRecipeGraph?.normalizeRecipeSpecial?.(specialValid?special:null,settings.localPlayer,advisorMapHash)||{status:"unknown",localPlayer:Number.isInteger(settings.localPlayer)?settings.localPlayer:null,source:{readOnly:false}};
      settings.recipeConditions={status:valid?conditions.status:"unknown",localPlayer:Number.isInteger(settings.localPlayer)?settings.localPlayer:null,unitBans:valid?Object.fromEntries(Object.entries(conditions.unitBans||{}).filter(([rawcode,blocked])=>/^[A-Za-z0-9]{4}$/.test(rawcode)&&typeof blocked==="boolean")):{},king:{status:valid&&["allowed","blocked"].includes(conditions.king?.status)?conditions.king.status:"unknown",...(valid&&conditions.source?.mapSha256===advisorMapHash&&typeof conditions.king?.reason==="string"?{reason:conditions.king.reason.slice(0,80)}:{})},special:normalizedSpecial,source:{readOnly:conditions?.source?.readOnly===true,mapSha256:advisorMapHash}};
      settings.specialRecipeConditionsSupported=settings.specialRecipeConditionsSupported===true;
      settings.readableConditionTypes=Array.from(settings.readableConditionTypes||[]).filter(type=>["UBAN","KING"].includes(type)||settings.specialRecipeConditionsSupported&&["IFCT","CHCT","SKPT","RMAX","NIKA"].includes(type));
      settings.kingPathMode=Object.prototype.hasOwnProperty.call(settings,"kingPathMode")?["off","standard","martial_law"].includes(settings.kingPathMode)?settings.kingPathMode:"off":settings.includeKingPathDecks===true?"standard":"off";
      settings.includeKingPathDecks=settings.kingPathMode!=="off";
      settings.includeMysteriousDecks=settings.includeMysteriousDecks===true;
      settings.reinforcementRole=["stun","physical_shred","magic_shred"].includes(settings.reinforcementRole)?settings.reinforcementRole:"auto";
      settings.rewardBudget=self.OrdNightmareRewardBudget?.normalize(settings.rewardBudget,{mapHash:advisorMapHash,localPlayer:settings.localPlayer})||{status:"unknown",source:{readOnly:false}};
      settings.randomHistory=self.OrdNightmareRandomForecast?.normalizeHistory(settings.randomHistory,{mapHash:advisorMapHash,localPlayer:settings.localPlayer})||{status:"unknown",source:{readOnly:false}};
      const result = advisor.recommend(message.counts, settings);
      self.postMessage({type:"result", revision:message.revision, recordsRevision, result});
    }
  } catch (error) {
    self.postMessage({type:message.type==="records-update"?"records-error":"error", revision:message.revision,recordsRevision:message.recordsRevision, code:String(error?.message || "advisor_failed")});
  }
};
