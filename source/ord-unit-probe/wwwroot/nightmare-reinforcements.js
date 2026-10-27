(function(root,factory){
 const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph,typeof module==="object"&&module.exports?require("./nightmare-support-adaptation.js"):root.OrdNightmareSupportAdaptation,typeof module==="object"&&module.exports?require("./nightmare-combat-verifier.js"):root.OrdNightmareCombatVerifier);
 if(typeof module==="object"&&module.exports)module.exports=api;if(root)root.OrdNightmareReinforcements=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(Graph,Support,Verifier){
 "use strict";
 const list=x=>Array.isArray(x)?x:[],finite=x=>typeof x==="number"&&Number.isFinite(x),positive=x=>Number.isSafeInteger(x)&&x>0?x:0;
 const stockOf=x=>new Map(Array.from(x instanceof Map?x:Array.isArray(x)?x.map(r=>[r.rawcode,r.count]):Object.entries(x||{}),([raw,n])=>[raw,positive(n)]).filter(([,n])=>n>0)),dict=x=>Object.fromEntries(Array.from(x).filter(([,n])=>n>0));
 const selectedRole=x=>["stun","physical_shred","magic_shred"].includes(x)?x:"auto",roles=["stun","physical_shred","magic_shred"],enemy=new Set(["enemy","enemies","all_enemies","boss","boss_enemies","line_enemies"]),allies=new Set(["ally","allies","allied","all_allies","team"]);
 const temporary=new Set(["h076","h04T","h04Y","h05D","h0CA","h06O","h0C5"]),proxies=new Set(["h04S","H08T"]),tiers=new Set(["희귀","전설","히든","초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]),uppers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]),checked=new Set(["WOOD","GOLD","ITEM","UBAN","KING","IFCT","CHCT","SKPT","RMAX","NIKA"]);
 const clamp=(x,min,max,fallback)=>Number.isSafeInteger(x)?Math.max(min,Math.min(max,x)):fallback;
 function create(catalog,combat,options={}){
  const hash=catalog?.map?.sha256,valid=Boolean(hash&&combat?.map?.sha256===hash&&Support&&Verifier),units=new Map(list(catalog?.units).map(u=>[u.rawcode,u])),profiles=valid?combat.units||{}:{},curated=valid?combat.curatedStrategyProfiles?.units||{}:{};
  const baseGraph=options.recipeGraph||Graph.create(catalog),ordinaryRecipes=list(catalog?.recipes).filter(recipe=>units.get(recipe.resultUnitRawcode)?.tier!=="신비"),ordinaryGraph=ordinaryRecipes.length===list(catalog?.recipes).length?baseGraph:Graph.create({...catalog,recipes:ordinaryRecipes});
  let includeMysteriousDecks=false,lastIncludeMysteriousDecks=null;
  const graph={...baseGraph};for(const method of ["analyze","planDeck"])if(typeof baseGraph[method]==="function")graph[method]=(...args)=>(includeMysteriousDecks?baseGraph:ordinaryGraph)[method](...args);
  const adapter=valid?Support.create(catalog,combat,{recipeGraph:graph,unitModels:options.unitModels}):null,recipes=new Map(list(catalog?.recipes).map(r=>[String(r.id),r]));
  const transcendenceCosts=options.transcendenceSlotCosts||Object.fromEntries(Array.from(units.values()).filter(u=>u.playable===true&&u.tier==="초월"&&/^H[A-Za-z0-9]{3}$/.test(u.rawcode)&&u.rawcode!=="H08T").map(u=>[u.rawcode,1]));if(units.has("H0BK")&&!options.transcendenceSlotCosts)transcendenceCosts.H0BK=1;
  let cached=null,session=null;
  const native=raw=>!temporary.has(raw)&&units.get(raw)?.playable===true&&units.get(raw)?.isSummon!==true&&units.get(raw)?.isSummoned!==true&&profiles[raw]?.verified===true&&profiles[raw]?.verificationKind==="native";
  const effects=raw=>native(raw)?list(profiles[raw].effects).filter(e=>Verifier.isRuntimeReferenceEligible(e)&&e.evidence&&["objectData","script","tooltip"].includes(e.evidence.type)&&e.isPenalty!==true&&(!finite(e.value)||e.value>0)):[];
  const main=raw=>["physicalDps","magicDps","hybridDps"].includes(profiles[raw]?.damage?.reviewedAnchorRole)||["lineDamage","bossDamage","storyDamage"].some(kind=>curated[raw]?.[kind]?.assessment==="main");
  const goals=stock=>Array.from(stock).filter(([,count])=>count>0).map(([rawcode,count])=>({rawcode,count}));
  function locksFor(stock,settings,context){const locks=stockOf(settings.protectedCounts),explicit=new Set(list(context?.activeCoreRawcodes));
   for(const [raw,n]of stock)if(explicit.has(raw)||!proxies.has(raw)&&units.get(raw)?.playable===true&&uppers.has(units.get(raw).tier)||native(raw)&&main(raw))locks.set(raw,Math.max(locks.get(raw)||0,n));
   const required=stockOf(context?.requiredSupportCounts);for(const raw of list(context?.requiredSupportRawcodes))required.set(raw,Math.max(1,required.get(raw)||0));
   for(const [raw,n]of required)if((stock.get(raw)||0)>0)locks.set(raw,Math.max(locks.get(raw)||0,Math.min(stock.get(raw),n)));
   return locks;
  }
  const sameCounts=(a,b)=>{for(const raw of new Set([...a.keys(),...b.keys()]))if((a.get(raw)||0)!==(b.get(raw)||0))return false;return true;};
  function unknownSupportPreserved(before,after){
   const kept=(sources,targets)=>sources.every(s=>targets.some(t=>t.id===s.id&&t.rawcode===s.rawcode&&t.count>=s.count&&JSON.stringify(t.conditions)===JSON.stringify(s.conditions)));
   for(const scope of ["area","single"])if(!kept(before.control[scope].sources.filter(s=>!finite(s.generalOffered)||s.branchAmbiguous),after.control[scope].sources))return false;
   for(const [metric,row]of Object.entries(before.utility))if(!kept(row.sources.filter(s=>!finite(s.value)||s.branchAmbiguous),after.utility[metric]?.sources||[]))return false;
   return true;
  }
  function deltas(before,after,code){const gains=[];
   for(const scope of ["area"])for(const scenario of ["general"]){const left=before.control[scope],right=after.control[scope],field="generalOffered",newKnown=right.sources.some(s=>s.rawcode===code&&s.numericReferenceEligible&&s.branchAmbiguous!==true&&finite(s[field])&&s[field]>0);
    const a=left.sources.length?left.metrics[scenario].knownOfferedSecondsPerReferenceSecond:0,b=right.metrics[scenario].knownOfferedSecondsPerReferenceSecond,unknown=left.sources.filter(s=>!finite(s[field])||s.branchAmbiguous),preserved=unknown.every(s=>right.sources.some(t=>t.id===s.id&&t.count>=s.count)),knownBefore=a===null&&preserved?0:a;
    if(newKnown&&preserved&&finite(knownBefore)&&finite(b)&&b>knownBefore+1e-9)gains.push({role:"stun",metric:"stun|"+scope+"|"+scenario,before:a,after:b,knownContributionBefore:knownBefore,knownContributionAfter:b,baselineUnknownSourcesPreserved:unknown.length?preserved:null,comparisonBasis:unknown.length?"preserved_unknown_sources_plus_new_known_reference":"verified_reference_delta",delta:b-knownBefore,unit:"offered_seconds_per_reference_second",referenceOnly:true,actualCombinedUptime:null});}
   for(const [metric,right]of Object.entries(after.utility)){const kind=metric.split("|")[0],role=kind==="armorReduction"?"physical_shred":kind==="magicResistanceReduction"?"magic_shred":null;if(!role)continue;
    const left=before.utility[metric],a=left?left.knownReferenceLowerBound:0,b=right.knownReferenceLowerBound,newKnown=right.sources.some(s=>s.rawcode===code&&s.numericReferenceEligible&&s.branchAmbiguous!==true&&finite(s.value)&&s.value>0),unknown=list(left?.sources).filter(s=>!finite(s.value)||s.branchAmbiguous),preserved=unknown.every(s=>right.sources.some(t=>t.id===s.id&&t.count>=s.count)),knownBefore=a;
    if(newKnown&&finite(knownBefore)&&finite(b)&&b>knownBefore+1e-9)gains.push({role,metric,before:a,after:b,knownContributionBefore:knownBefore,knownContributionAfter:b,baselineUnknownSourcesPreserved:unknown.length?preserved:null,delta:b-knownBefore,unit:metric.split("|").at(-1),conditional:metric.includes("|conditional|"),referenceOnly:true,actualStackedTotal:null});}
   return gains;
  }
  const effectFor=source=>list(profiles[source.rawcode]?.effects).find(e=>source.id===source.rawcode+":"+e.id)||list(profiles[source.rawcode]?.effects).find(e=>e.abilityRawcode===source.abilityRawcode&&e.kind===source.kind&&e.scope===source.scope&&e.mode===source.mode);
  const rangeOf=source=>{const range=effectFor(source)?.range;return finite(range)&&range>=0?range:null;};
  function activationOf(source,stun){
   if(source.continuous)return "continuous";
   const effect=effectFor(source)||{},conditions=list(source.conditions).filter(text=>{
    if(!stun)return true;
    if(/발동조건/.test(String(text)))return !/공격|적중|명중/.test(String(text))||/체력|마나|강화|스택|특성|최초|개체/.test(String(text));
    return /체력|마나|강화|스택|특성|최초|개체/.test(String(text));
   }).map(String).sort();
   return JSON.stringify([source.mode,stun?null:effect.chancePercent??null,stun?null:effect.durationSeconds??null,conditions]);
  }
  const sourceIdentity=(target,current)=>target.every(s=>current.some(t=>t.id===s.id&&t.rawcode===s.rawcode&&t.count>=s.count&&JSON.stringify(t.conditions)===JSON.stringify(s.conditions)));
  const sourceActive=(source,stun)=>stun?!finite(source.generalOffered)||source.generalOffered>0:source.value===null||source.value>0;
  function sourceReference(sources,stun){
   const known=sources.filter(s=>s.numericReferenceEligible===true&&!s.branchAmbiguous&&finite(stun?s.generalOffered:s.value));
   if(stun)return known.reduce((sum,s)=>sum+s.generalOffered*s.count,0);
   const values=known.map(s=>s.value),groups=new Map(),cross=new Map();
   for(const s of known){const value=s.value*(s.sameUnitCopiesStack?s.count:1);if(s.additiveVerified&&s.stackingGroup)groups.set(s.stackingGroup,(groups.get(s.stackingGroup)||0)+value);if(s.crossGroupAdditiveVerified){const key=s.stackingGroup||s.id;cross.set(key,s.additiveVerified?(cross.get(key)||0)+value:Math.max(cross.get(key)||0,value));}}
   return Math.max(0,...values,...groups.values(),...(cross.size?[Array.from(cross.values()).reduce((a,b)=>a+b,0)]:[]));
  }
  const sourcePartial=(sources,stun)=>sources.some(s=>!s.numericReferenceEligible||s.branchAmbiguous||!finite(stun?s.generalOffered:s.value)||!stun&&s.count>1&&!s.sameUnitCopiesStack);
  function descriptorsFor(context){
   const rows=list(context.targetGoals).filter(g=>units.has(g.rawcode)&&positive(g.count)),target=rows.length?adapter.assess(rows):null,descriptors=[];
   if(target){
    const append=(role,baseMetric,sources,stun)=>{const groups=new Map();for(const s of sources.filter(s=>sourceActive(s,stun))){const activation=activationOf(s,stun),range=rangeOf(s),key=JSON.stringify([baseMetric,activation,range]);if(!groups.has(key))groups.set(key,{role,metric:key,baseMetric,activation,range,stun,sources:[]});groups.get(key).sources.push(s);}
     // 같은 공격원을 목표별로 중복 충족시키지 않는다. 각 범위에서 전체 목표의 참조량을 함께 비교한다.
     for(const group of groups.values())if(group.range!==null)group.sources=sources.filter(s=>sourceActive(s,stun)&&activationOf(s,stun)===group.activation&&rangeOf(s)!==null&&rangeOf(s)>=group.range);
     descriptors.push(...groups.values());};
    append("stun","stun|area|general",target.control.area.sources,true);
    for(const [metric,row]of Object.entries(target.utility)){const kind=metric.split("|")[0],role=kind==="armorReduction"?"physical_shred":kind==="magicResistanceReduction"?"magic_shred":kind==="slow"?"slow":null;if(role)append(role,metric,row.sources,false);}
   }return {target,descriptors,hasTarget:list(context.targetGoals).length>0};
  }
  function assessNeeds(summary,stock,context,basis){
   const allSources=(role,baseMetric)=>role==="stun"?summary.control.area.sources:Object.entries(summary.utility).filter(([metric])=>baseMetric?metric===baseMetric:metric.split("|")[0]===({physical_shred:"armorReduction",magic_shred:"magicResistanceReduction",slow:"slow"})[role]).flatMap(([,row])=>row.sources);
   const comparisons=[];
   if(basis.hasTarget)for(const descriptor of basis.descriptors){
    const {role,metric,baseMetric,activation,range,stun,sources:required}=descriptor,all=allSources(role,baseMetric).filter(s=>sourceActive(s,stun)),sameActivation=all.filter(s=>activationOf(s,stun)===activation),comparable=sameActivation.filter(s=>range===null?required.some(t=>t.id===s.id):rangeOf(s)!==null&&rangeOf(s)>=range),identitySatisfied=sourceIdentity(required,all);
    const targetKnownReference=sourceReference(required,stun),currentKnownReference=sourceReference(comparable,stun),targetPartial=sourcePartial(required,stun),actualPartial=sourcePartial(comparable,stun)||sameActivation.some(s=>rangeOf(s)===null),partial=targetPartial||actualPartial;
    const targetRoleSources=role==="stun"?basis.target.control.area.sources:Object.entries(basis.target.utility).filter(([key])=>key===baseMetric).flatMap(([,row])=>row.sources);
    const rolePresent=allSources(role).some(s=>sourceActive(s,stun)),unmatchedUnknown=required.filter(s=>sourcePartial([s],stun)).some(s=>!sourceIdentity([s],all)),unmatchedConditions=all.length>0&&sameActivation.length===0&&!all.every(s=>sourceIdentity([s],targetRoleSources)),unknownEnvelope=!stun&&sameActivation.some(s=>sourcePartial([s],false));
    let status="unknown",gap=null;
    if(identitySatisfied){status="satisfied_identity";gap=0;}
    else if(!rolePresent){status="missing_role";gap=targetKnownReference>0&&range!==null?targetKnownReference:null;}
    else if(!unmatchedConditions&&!unknownEnvelope&&range!==null&&targetKnownReference>0){gap=Math.max(0,targetKnownReference-currentKnownReference);status=gap>1e-9?"known_reference_gap":unmatchedUnknown?"unknown":"satisfied_reference";if(status==="unknown")gap=null;}
    comparisons.push({role,metric,baseMetric,status,identitySatisfied,currentKnownReference,targetKnownReference,currentTotalReference:actualPartial?null:currentKnownReference,targetTotalReference:targetPartial?null:targetKnownReference,actualCurrent:null,actualGap:null,gap,partial,requiredSources:required.map(s=>({id:s.id,rawcode:s.rawcode,count:s.count})),range,activation,comparisonBasis:partial?"known_reference_components_only":"native_support_reference",actualSufficiencyEstablished:false});
   }
   else{
    const actual=Array.from(stock).filter(([raw])=>native(raw)&&main(raw)),modes=new Set(actual.map(([raw])=>({physicalDps:"physical",magicDps:"magic",hybridDps:"hybrid"})[profiles[raw]?.damage?.reviewedAnchorRole]).filter(Boolean));
    const required=actual.length?["stun",...(modes.has("physical")||modes.has("hybrid")?["physical_shred"]:[]),...(modes.has("magic")||modes.has("hybrid")?["magic_shred"]:[])]:[];
    for(const role of required){const sources=allSources(role).filter(s=>sourceActive(s,role==="stun")),unknown=sourcePartial(sources,role==="stun");comparisons.push({role,metric:"role_presence|"+role,status:!sources.length?"missing_role":unknown?"unknown":"present_unquantified",gap:!sources.length?null:0,actualCurrent:null,actualGap:null,currentTotalReference:null,partial:unknown,identitySatisfied:false,currentKnownReference:sourceReference(sources,role==="stun"),targetKnownReference:null,comparisonBasis:"actual_main_minimum_role_presence",actualSufficiencyEstablished:false});}
   }
   const actualModes=new Set(Array.from(stock).filter(([raw])=>native(raw)).map(([raw])=>({physicalDps:"physical",magicDps:"magic",hybridDps:"hybrid"})[profiles[raw]?.damage?.reviewedAnchorRole]).filter(Boolean)),mode=actualModes.has("hybrid")||actualModes.has("physical")&&actualModes.has("magic")?"hybrid":actualModes.size===1?[...actualModes][0]:context.mode;
   const automaticRoles=mode==="physical"?["stun","physical_shred"]:mode==="magic"?["stun","magic_shred"]:roles;
   return {basis:basis.hasTarget?"target_support_reference":"actual_main_minimum_role_presence",mode:mode||null,automaticRoles,targetBasis:context.targetBasis||null,comparisons:comparisons.filter(c=>c.role!=="slow"),slowComparisons:comparisons.filter(c=>c.role==="slow"),requestedRoles:automaticRoles.filter(role=>comparisons.some(c=>c.role===role&&["missing_role","known_reference_gap"].includes(c.status))),stunDerivedFromSlow:false,actualSufficiencyEstablished:false};
  }
  function needResolution(before,after){const resolved=[];for(const need of before.comparisons){if(!before.requestedRoles.includes(need.role)||!["missing_role","known_reference_gap"].includes(need.status))continue;const next=after.comparisons.find(c=>c.role===need.role&&c.metric===need.metric);if(!next)continue;
    if(finite(need.gap)&&need.gap>0){const remaining=next.identitySatisfied?0:finite(next.gap)?next.gap:next.status==="unknown"?need.gap:Math.max(0,need.targetKnownReference-next.currentKnownReference),amount=Math.max(0,need.gap-remaining);if(amount>1e-9)resolved.push({role:need.role,metric:need.metric,kind:need.status,beforeGap:need.gap,afterGap:remaining,delta:amount,closedFraction:Math.min(1,amount/need.gap),relativeGap:need.targetKnownReference>0?Math.min(1,need.gap/need.targetKnownReference):1,conditional:need.baseMetric?.includes("|conditional|")===true,referenceOnly:true,actualSufficiencyEstablished:false});}
    else if(need.status==="missing_role"&&(before.basis==="target_support_reference"?["satisfied_identity","satisfied_reference"].includes(next.status):next.status==="present_unquantified"&&next.currentKnownReference>0))resolved.push({role:need.role,metric:need.metric,kind:"missing_role",beforeGap:null,afterGap:null,delta:1,closedFraction:1,relativeGap:1,categoryOnly:true,conditional:need.baseMetric?.includes("|conditional|")===true,referenceOnly:true,actualSufficiencyEstablished:false});
   }return resolved;
  }
  function compareResolution(a,b){const quality=rows=>({missing:rows.filter(r=>r.kind==="missing_role").length,relative:Math.max(0,...rows.map(r=>r.relativeGap)),closed:rows.length?rows.reduce((sum,r)=>sum+r.closedFraction,0)/rows.length:0,roles:new Set(rows.map(r=>r.role)).size}),x=quality(a||[]),y=quality(b||[]);return y.missing-x.missing||y.relative-x.relative||y.closed-x.closed||y.roles-x.roles;}
  function replay(plan,stock,locks,settings,enforceMoney){
   const current=new Map(stock),all=[],usedItems=[];let conditions=settings.recipeConditions;const supported=settings.supportedRecipeIds==null?null:new Set(settings.supportedRecipeIds);
   const topCount=inventory=>Array.from(inventory).reduce((sum,[raw,n])=>sum+(transcendenceCosts[raw]||0)*n,0);let transcendenceUsed=Math.max(topCount(stock),(stock.get("H08T")||0)>0?1:0);
   if(!plan?.executable||!plan.steps.length||plan.steps.length>64)return {valid:false,reason:plan?.reason||"whole_goal_unavailable"};
   for(const [raw,n]of locks)if(n>(stock.get(raw)||0))return {valid:false,reason:"protected_material"};
   for(const step of plan.steps){
    if(!["craft","choice"].includes(step.type))return {valid:false,reason:"invalid_step"};
    if(step.type==="craft"){const canonical=recipes.get(String(step.recipeId)),recipe=canonical&&Graph.expandMaterialChoices(canonical,catalog).find(candidate=>candidate.materialChoiceRawcode===step.materialChoiceRawcode);if(!recipe||!Graph.isProductionRecipe(recipe)||recipe.resultUnitRawcode!==step.resultRawcode||!supported?.has(String(step.recipeId)))return {valid:false,reason:"unsupported_recipe"};
     const expected=new Map(),observed=new Map();for(const i of list(recipe.ingredients))if(i.type==="UNIT")expected.set(i.rawcode,(expected.get(i.rawcode)||0)+positive(i.count));for(const i of list(step.ingredients))observed.set(i.rawcode,(observed.get(i.rawcode)||0)+positive(i.count));if(!sameCounts(expected,observed)||recipe.resultCount!==step.resultCount)return {valid:false,reason:"recipe_trace_mismatch"};
     const nikaUpgrade=String(recipe.id)==="ET02"&&step.resultRawcode==="H0BK"&&["H099","H0B2"].includes(step.materialChoiceRawcode)&&expected.get(step.materialChoiceRawcode)===1;
     if((transcendenceCosts[step.resultRawcode]||0)>0&&!nikaUpgrade)transcendenceUsed+=(transcendenceCosts[step.resultRawcode]||0)*step.resultCount;
     if(transcendenceUsed>1)return {valid:false,reason:"transcendence_limit"};
    }else if(settings.allowChoiceWisps!==true||step.choiceRawcode!=="e018"||step.resultCount!==1||!/^h00[1-9]$/.test(step.resultRawcode)||step.ingredients?.length!==1||step.ingredients[0].rawcode!=="e018"||step.ingredients[0].count!==1)return {valid:false,reason:"invalid_choice_step"};
    if(list(step.ingredients).some(i=>!positive(i.count)||(current.get(i.rawcode)||0)-i.count<(locks.get(i.rawcode)||0)))return {valid:false,reason:"protected_material"};
    const requirements=list(step.nonUnitRequirements),check=Graph.evaluateRecipeConditions(requirements,conditions);if(!check.conditionsReady)return {valid:false,reason:check.blockedConditions.length?"recipe_conditions_blocked":"recipe_conditions_unknown"};
    if(requirements.some(i=>!checked.has(String(i.type).toUpperCase())))return {valid:false,reason:"conditions_unverified"};
    usedItems.push(...requirements.filter(i=>i.type==="ITEM"));if(!Graph.evaluateItems(usedItems,settings.items).itemsReady)return {valid:false,reason:"items_unavailable"};
    all.push(...requirements);const money=Graph.effectiveResourceCosts(all,settings.recipeConditions);
    if(enforceMoney&&["wood","gold"].some(key=>!finite(money[key])||money[key]>0&&(!finite(settings[key])||settings[key]<money[key])))return {valid:false,reason:money.wood>0&&finite(settings.wood)&&settings.wood<money.wood?"missing_wood":money.gold>0&&finite(settings.gold)&&settings.gold<money.gold?"missing_gold":"resource_unknown"};
    for(const i of step.ingredients)current.set(i.rawcode,(current.get(i.rawcode)||0)-i.count);current.set(step.resultRawcode,(current.get(step.resultRawcode)||0)+step.resultCount);if(topCount(current)>1)return {valid:false,reason:"transcendence_limit"};conditions=Graph.projectRecipeConditions(requirements,conditions);
   }
   if(!sameCounts(current,stockOf(plan.finalCounts)))return {valid:false,reason:"final_counts_mismatch"};
   const topRequired=Array.from(current).reduce((sum,[raw,n])=>sum+(transcendenceCosts[raw]||0)*n,0);if(topRequired>1)return {valid:false,reason:"transcendence_limit"};
   if(settings.kingPathEntryCompatibility?.status==="blocked"&&all.some(i=>i.type==="KING"&&i.count>0))return {valid:false,reason:"king_path_entry_unavailable"};
   const cost=Graph.effectiveResourceCosts(all,settings.recipeConditions);return {valid:true,stock:current,cost,nonUnitRequirements:all,...Graph.evaluateItems(all,settings.items),...Graph.evaluateRecipeConditions(all,settings.recipeConditions)};
  }
  function budgetRank(cost,choice,stock,settings){
   const need={wood:cost.wood,gold:cost.gold,choiceWisps:choice},available={wood:settings.wood,gold:settings.gold,choiceWisps:stock.get("e018")||0},fits=()=>Object.keys(need).every(k=>finite(need[k])&&(need[k]===0||finite(available[k])&&available[k]>=need[k]));
   if(fits())return "current";const budget=settings.rewardBudget;if(!["confirmed","partial"].includes(budget?.status))return "unknown";
   for(const k of Object.keys(available))available[k]=finite(available[k])&&finite(budget.guaranteed?.[k])?available[k]+budget.guaranteed[k]:null;
   return fits()?"within_guaranteed":"requires_more";
  }
  function recommend(counts,settings={},context={}){
   includeMysteriousDecks=settings.craftRunning===true&&lastIncludeMysteriousDecks!==null&&(settings.sessionIdentity||settings.sessionId||null)===session?lastIncludeMysteriousDecks:settings.includeMysteriousDecks===true;if(lastIncludeMysteriousDecks!==null&&lastIncludeMysteriousDecks!==includeMysteriousDecks)cached=null;lastIncludeMysteriousDecks=includeMysteriousDecks;
   const role=selectedRole(settings.reinforcementRole),identity=settings.sessionIdentity||settings.sessionId||null;if(identity!==session){cached=null;session=identity;}
   const empty=()=>({selectedRole:role,candidates:[],craftTargets:[],protectedCounts:{},search:{bounded:true,evaluated:0,rolePoolCounts:{}}});
   if(!valid)return empty();
   if(settings.craftRunning===true)return cached?{...cached,selectedRole:role,craftRunning:true,candidates:cached.candidates.map(c=>({...c,ready:false})),craftTargets:cached.craftTargets.map(t=>({...t,available:false,ready:false,reason:"craft_running",expectedSteps:[],expectedStepCount:0}))}:{...empty(),craftRunning:true};
   const stock=stockOf(counts),locks=locksFor(stock,settings,context);if(locks.size>64){cached=empty();return cached;}
   const baselineGoals=goals(stock),baseline=adapter.assess(baselineGoals),explicit=list(settings.reinforcementCandidateRawcodes),candidateSet=explicit.length?new Set(explicit):null;
   const targetBasis=descriptorsFor(context),needs=assessNeeds(baseline,stock,context,targetBasis),requested=role==="auto"?needs.requestedRoles:[role];
   if(!requested.length){cached={...empty(),needs,protectedCounts:dict(locks),craftRunning:false};return cached;}
   const pool=new Map(roles.map(r=>[r,[]])),analyses=new Map(),rootStates=new Map(),previewResolutions=new Map(),supported=settings.supportedRecipeIds==null?null:new Set(settings.supportedRecipeIds);
   for(const [raw,unit]of units){if(!includeMysteriousDecks&&unit.tier==="신비"||!native(raw)||proxies.has(raw)||!tiers.has(unit.tier)||(stock.get(raw)||0)>0||candidateSet&&!candidateSet.has(raw))continue;
    const roots=Array.from(recipes.values()).filter(r=>r.resultUnitRawcode===raw&&Graph.isProductionRecipe(r)&&supported?.has(String(r.id))).map(recipe=>({recipe,conditions:Graph.evaluateRecipeConditions(recipe.ingredients,settings.recipeConditions),items:Graph.evaluateItems(recipe.ingredients,settings.items)})).filter(row=>!row.conditions.blockedConditions.length&&!row.items.missingItems.some(i=>i.known&&i.missing>0));if(!roots.length)continue;
    rootStates.set(raw,roots.some(row=>row.conditions.conditionsReady&&row.items.itemsReady));
    const solo=adapter.assess([{rawcode:raw,count:1}]),none=adapter.assess([]),gains=deltas(none,solo,raw),eligible=roles.filter(r=>gains.some(g=>g.role===r));if(!eligible.length)continue;
    if(role==="auto"){const preview=new Map(stock);preview.set(raw,1);const resolution=needResolution(needs,assessNeeds(adapter.assess(goals(preview)),preview,context,targetBasis));if(!resolution.length)continue;previewResolutions.set(raw,resolution);}
    const free=new Map(stock);for(const [code,n]of locks)free.set(code,Math.max(0,(free.get(code)||0)-n));
    let analysis;try{for(const {recipe}of roots){const next=graph.analyze(raw,free,String(recipe.id),{items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:settings.localPlayer,resources:{wood:settings.wood,gold:settings.gold}});if(!analysis||next.ratio>analysis.ratio)analysis=next;}}catch{continue;}
    analyses.set(raw,analysis);for(const r of eligible)if(role!=="auto"||previewResolutions.get(raw).some(need=>need.role===r))pool.get(r).push(raw);
   }
   const limit=clamp(settings.reinforcementCandidatesPerRole,1,24,12),queue=[];
   for(const r of requested){pool.get(r).sort((a,b)=>Number(rootStates.get(b))-Number(rootStates.get(a))||Number(analyses.get(b).canCompleteWithChoiceWisp||analyses.get(b).percent===100)-Number(analyses.get(a).canCompleteWithChoiceWisp||analyses.get(a).percent===100)||(role==="auto"?compareResolution(previewResolutions.get(a),previewResolutions.get(b)):0)||analyses.get(b).ratio-analyses.get(a).ratio||a.localeCompare(b));}
   for(let index=0;index<limit;index++)for(const r of requested){const raw=pool.get(r)[index];if(raw&&!queue.includes(raw))queue.push(raw);}
   const maximum=clamp(settings.reinforcementMaxEvaluations,3,48,24),found=[],planning={items:settings.items,recipeConditions:settings.recipeConditions,localPlayer:settings.localPlayer,supportedRecipeIds:supported,allowChoiceWisps:settings.allowChoiceWisps===true,protectedCounts:locks,maxSteps:64,maxSearchNodes:clamp(settings.reinforcementMaxSearchNodes,100,16000,4000),planningKingCraftBudget:settings.planningKingCraftBudget,planningAlternativeLimit:4};
   let evaluated=0;
   for(const raw of queue){if(evaluated>=maximum)break;evaluated++;const goal=[{rawcode:raw,count:1}],actual=graph.planDeck(goal,stock,planning),proofs=[actual,...list(actual.alternativePlans)],variants=[];
    for(const plan of proofs){if(!plan.executable)continue;const played=replay(plan,stock,locks,settings,false);if(!played.valid)continue;const after=adapter.assess(goals(played.stock)),comparison=adapter.compare(baselineGoals,goals(played.stock)),resolution=role==="auto"?needResolution(needs,assessNeeds(after,played.stock,context,targetBasis)):[],effectGain=role==="auto"?resolution:deltas(baseline,after,raw).filter(g=>requested.includes(g.role));if(!comparison.accepted||!unknownSupportPreserved(baseline,after)||!effectGain.length)continue;
     const execution=replay(plan,stock,locks,settings,true);variants.push({plan,played,effectGain,needResolution:resolution,ready:execution.valid,reason:execution.valid?null:execution.reason,budget:budgetRank(played.cost,plan.choiceWispCount,stock,settings),basis:"current_unit_path"});}
    if(!variants.length&&actual.reason==="missing_material"&&planning.allowChoiceWisps){const future=new Map(stock),added=clamp(settings.reinforcementFutureChoiceBudget,1,512,128);future.set("e018",(future.get("e018")||0)+added);const planned=graph.planDeck(goal,future,planning);
     for(const plan of [planned,...list(planned.alternativePlans)]){if(!plan.executable)continue;const played=replay(plan,future,locks,settings,false);if(!played.valid)continue;const after=new Map(played.stock);after.set("e018",Math.max(0,(stock.get("e018")||0)-plan.choiceWispCount));
      const summary=adapter.assess(goals(after)),comparison=adapter.compare(baselineGoals,goals(after)),resolution=role==="auto"?needResolution(needs,assessNeeds(summary,after,context,targetBasis)):[],effectGain=role==="auto"?resolution:deltas(baseline,summary,raw).filter(g=>requested.includes(g.role));if(!comparison.accepted||!unknownSupportPreserved(baseline,summary)||!effectGain.length)continue;variants.push({plan,played,effectGain,needResolution:resolution,ready:false,reason:"missing_material",budget:budgetRank(played.cost,plan.choiceWispCount,stock,settings),basis:"future_choice_diagnostic"});}
    }
    if(!variants.length)continue;const rank=x=>({current:0,within_guaranteed:1,unknown:2,requires_more:3})[x.budget];variants.sort((a,b)=>Number(b.ready)-Number(a.ready)||(role==="auto"?compareResolution(a.needResolution,b.needResolution):0)||rank(a)-rank(b)||a.plan.choiceWispCount-b.plan.choiceWispCount||(a.played.cost.wood??Infinity)-(b.played.cost.wood??Infinity)||(a.played.cost.gold??Infinity)-(b.played.cost.gold??Infinity)||a.plan.stepCount-b.plan.stepCount);const best=variants[0],unit=units.get(raw),analysis=analyses.get(raw),last=best.plan.steps.at(-1),candidateRoles=requested.filter(r=>best.effectGain.some(g=>g.role===r));
    if(last?.type!=="craft"||last.resultRawcode!==raw)continue;
    const missingChoice=Math.max(0,best.plan.choiceWispCount-(stock.get("e018")||0)),candidate={rawcode:raw,name:unit.displayName||unit.name||raw,tier:unit.tier,count:1,owned:0,progress:{percent:analysis.percent,ratio:analysis.ratio,unitRatio:analysis.unitRatio,choiceReachablePercent:analysis.choiceReachablePercent,choiceReachableRatio:analysis.choiceReachableRatio,unitChoiceReachableRatio:analysis.unitChoiceReachableRatio,canCompleteWithChoiceWisp:analysis.canCompleteWithChoiceWisp,choiceWispAvailable:analysis.choiceWispAvailable,choiceWispNeeded:analysis.choiceWispNeeded,resourceRequirements:analysis.resourceRequirements,missingResources:analysis.missingResources,resourcesReady:analysis.resourcesReady,itemsReady:analysis.itemsReady,conditionsReady:analysis.conditionsReady,missingLeaves:analysis.missingLeaves},ready:best.ready,roles:candidateRoles,effectGain:best.effectGain,needResolution:best.needResolution,budgetStatus:best.budget,comparisonBasis:best.basis,requiredChoiceWisps:best.plan.choiceWispCount,missingChoiceWisps:missingChoice,wood:best.played.cost.wood,gold:best.played.cost.gold,stepCount:best.plan.stepCount};
    const common={rawcode:raw,name:candidate.name,tier:unit.tier,towardRawcode:raw,towardName:candidate.name,towardTier:unit.tier,goalCount:1,count:1,owned:0,needed:1,available:best.ready,ready:best.ready,reason:best.reason,origin:"reinforcement_whole_goal_path",isWholeGoal:true,protectedCounts:dict(locks),requiresExpectedSteps:true,expectedSteps:[],expectedStepCount:0,recipeId:null,resourcesReady:false,itemsReady:best.played.itemsReady,conditionsReady:best.played.conditionsReady,additionalConditionsUnverified:false};
    const action=best.ready?{...common,recipeId:last.recipeId,goalRecipeId:last.recipeId,expectedSteps:best.plan.steps.map(s=>s.type==="choice"?{type:"choice",resultRawcode:s.resultRawcode}:{type:"craft",recipeId:s.recipeId,...(s.materialChoiceRawcode?{materialChoiceRawcode:s.materialChoiceRawcode}:{})}),expectedStepCount:best.plan.stepCount,plan:best.plan.steps,expectedFinalCounts:dict(best.played.stock),choiceWisps:best.plan.choiceWispCount,wood:best.played.cost.wood,gold:best.played.cost.gold,itemRequirements:best.played.itemRequirements,conditionRequirements:best.played.conditionRequirements,nonUnitRequirements:best.played.nonUnitRequirements,resourcesReady:true,completesGoal:true}:common;
    found.push({candidate,action});
   }
   const effectKey=row=>{const role=requested.find(r=>row.candidate.roles.includes(r)),metric=row.candidate.effectGain.filter(g=>g.role===role).map(g=>g.metric).sort()[0];return {roleIndex:requested.indexOf(role),metric,delta:row.candidate.effectGain.find(g=>g.metric===metric).delta};};
   const ranking=(a,b)=>{const x=effectKey(a),y=effectKey(b);return Number(b.candidate.ready)-Number(a.candidate.ready)||(role==="auto"?compareResolution(a.candidate.needResolution,b.candidate.needResolution):0)||({current:0,within_guaranteed:1,unknown:2,requires_more:3})[a.candidate.budgetStatus]-({current:0,within_guaranteed:1,unknown:2,requires_more:3})[b.candidate.budgetStatus]||a.candidate.missingChoiceWisps-b.candidate.missingChoiceWisps||a.candidate.requiredChoiceWisps-b.candidate.requiredChoiceWisps||(a.candidate.wood??Infinity)-(b.candidate.wood??Infinity)||a.candidate.stepCount-b.candidate.stepCount||x.roleIndex-y.roleIndex||x.metric.localeCompare(y.metric)||y.delta-x.delta||a.candidate.rawcode.localeCompare(b.candidate.rawcode);};
   found.sort(ranking);const selected=[];for(const ready of [true,false]){const group=found.filter(row=>row.candidate.ready===ready);for(const r of requested){const next=group.find(row=>row.candidate.roles.includes(r)&&!selected.includes(row));if(next&&selected.length<3)selected.push(next);}for(const row of group)if(selected.length<3&&!selected.includes(row))selected.push(row);}selected.sort(ranking);
   cached={selectedRole:role,needs,candidates:selected.slice(0,3).map(row=>row.candidate),craftTargets:selected.slice(0,3).map(row=>row.action),protectedCounts:dict(locks),craftRunning:false,search:{bounded:true,evaluated,rolePoolCounts:Object.fromEntries(Array.from(pool,([r,rows])=>[r,rows.length])),perRoleLimit:limit,evaluationLimit:maximum,truncated:queue.length>evaluated||requested.some(r=>pool.get(r).length>limit)}};return cached;
  }
  return {recommend};
 }
 return {create,normalizeRole:selectedRole};
});
