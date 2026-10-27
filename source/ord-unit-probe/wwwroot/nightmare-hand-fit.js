(function(root,factory){
  const api=factory(typeof module==="object"&&module.exports?require("./recipe-graph.js"):root.OrdRecipeGraph);
  if(typeof module==="object"&&module.exports)module.exports=api;
  if(root)root.OrdNightmareHandFit=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(Graph){
  "use strict";
  const list=x=>Array.isArray(x)?x:[],count=x=>Number.isSafeInteger(x)&&x>=0?x:0;
  const stockOf=x=>new Map(Array.from(x instanceof Map?x:Array.isArray(x)?x.map(r=>[r.rawcode,r.count]):Object.entries(x||{}),([k,v])=>[k,count(v)]).filter(([,v])=>v>0));
  const add=(map,key,n)=>{if(n>0)map.set(key,(map.get(key)||0)+n);};
  const eps=1e-7,temporary=new Set(["h076","h04T","h04Y","h05D","h0CA","h06O","h0C5"]),residual=new Set(["흔함","안흔함","특별","희귀"]);
  function create(catalog){
    const units=new Map(list(catalog?.units).map(u=>[u.rawcode,u])),recipes=new Map(),values=new Map(),ancestorCache=new Map(),relevant=new Set();
    for(const recipe of list(catalog?.recipes).flatMap(recipe=>Graph?.expandMaterialChoices?Graph.expandMaterialChoices(recipe,catalog):[recipe]))if(Graph?.isProductionRecipe(recipe)&&units.has(recipe.resultUnitRawcode)){
      if(!recipes.has(recipe.resultUnitRawcode))recipes.set(recipe.resultUnitRawcode,[]);
      recipes.get(recipe.resultUnitRawcode).push(recipe);relevant.add(recipe.resultUnitRawcode);
      for(const row of list(recipe.ingredients))if(String(row.type).toUpperCase()==="UNIT")relevant.add(row.rawcode);
    }
    // UNIT 원가의 정적 비교 기준이며 피해량·획득 확률 또는 최적 경로의 증명이 아니다.
    function valueOf(code,path=new Set()){
      if(!units.has(code)||temporary.has(code)||path.has(code)||path.size>64)return null;
      if(values.has(code))return values.get(code);
      const choices=recipes.get(code)||[];if(!choices.length)return 1;
      const next=new Set(path);next.add(code);let best=null;
      for(const recipe of choices){const rows=list(recipe.ingredients).filter(r=>String(r.type).toUpperCase()==="UNIT");let total=0,known=true;
        for(const row of rows){const value=valueOf(row.rawcode,next);if(value===null){known=false;break;}total+=value*count(row.count);}
        if(known){const value=rows.length?total/Math.max(1,count(recipe.resultCount)):1;if(Number.isFinite(value)&&value>0&&(best===null||value<best))best=value;}
      }
      if(!path.size&&best!==null)values.set(code,best);return best;
    }
    function ancestors(code,path=new Set()){
      if(path.has(code)||path.size>64||!units.has(code)||temporary.has(code))return new Map();
      if(!path.size&&ancestorCache.has(code))return ancestorCache.get(code);
      const result=new Map([[code,1]]),next=new Set(path);next.add(code);
      for(const recipe of recipes.get(code)||[]){const branch=new Map();for(const row of list(recipe.ingredients))if(String(row.type).toUpperCase()==="UNIT")for(const [raw,n]of ancestors(row.rawcode,next))add(branch,raw,n*count(row.count)/Math.max(1,count(recipe.resultCount)));
        for(const [raw,n]of branch)result.set(raw,Math.max(result.get(raw)||0,n));}
      if(!path.size)ancestorCache.set(code,result);return result;
    }
    function preselectionScore(record,stock,settings={}){
      const rawGoals=list(record?.goals),mapping=list(record?.sourceEvidence?.mapping);
      const mode=Object.prototype.hasOwnProperty.call(settings,"kingPathMode")?settings.kingPathMode:settings.includeKingPathDecks===true?"standard":"off";
      if(mode==="off"&&[...rawGoals,...mapping].some(r=>r.rawcode==="h0C8"))return -1;
      if(settings.includeMysteriousDecks!==true&&[...rawGoals,...mapping,...list(record?.entries),...list(record?.goalProjection?.excluded),...list(record?.sourceEvidence?.goalProjection?.excluded)].some(row=>[row?.rawcode,...list(row?.candidateRawcodes)].some(code=>units.get(code)?.tier==="신비")))return -1;
      const excluded=new Set(list(record?.goalProjection?.excluded||record?.sourceEvidence?.goalProjection?.excluded).map(r=>r.rawcode));
      const identities=new Set(["exact_native_identity","explicit_native_alias","canonical_craft_form_unknown","normalized_native_identity"]);
      const validIdentity=row=>units.has(row.rawcode)&&(row.status===undefined||identities.has(row.status))&&(!row.tier||row.tier===units.get(row.rawcode).tier);
      const goals=rawGoals.filter(r=>validIdentity(r)&&units.get(r.rawcode).playable!==false&&units.get(r.rawcode).isSummon!==true&&!temporary.has(r.rawcode)&&!residual.has(units.get(r.rawcode).tier)&&!excluded.has(r.rawcode));
      if(!goals.length)return rawGoals.some(row=>!validIdentity(row))?null:-1;
      const demand=new Map();let required=0;
      const quantity=row=>row.countVerified===true&&row.countMeaning==="verified_api_quantity"&&(row.quantityEvidence||row.evidence)?.sourceProvider==="tmo"&&(row.quantityEvidence||row.evidence)?.sourceCountVerified===true&&count(row.count)>0?row.count:1;
      for(const row of goals){const amount=quantity(row),value=valueOf(row.rawcode);if(value===null)return null;required+=value*amount;
        for(const [raw,n]of ancestors(row.rawcode))add(demand,raw,n*amount);}
      let used=0;for(const [raw,n]of stockOf(stock)){if(!demand.has(raw))continue;const value=valueOf(raw);if(value===null)return null;used+=Math.min(n,demand.get(raw))*value;}
      return required>0?Math.max(0,Math.min(1,used/required)):null;
    }
    function unknown(reason,basis){return {status:"unknown",verified:false,reason,basis,coverage:null,handUtilization:null,usefulActualValue:null,actualChoiceSpent:null};}
    function assess(context={}){
      const {plan,basis="current_joint_plan"}=context;
      if(plan?.executable!==true||!Array.isArray(plan.steps)||!Array.isArray(plan.reservedGoals)||!plan.finalCounts)return unknown("complete_joint_plan_required",basis);
      const actual=stockOf(context.initialCounts),virtual=stockOf(context.virtualAdditions);
      if(basis==="current_joint_plan"&&virtual.size)return unknown("virtual_stock_in_current_plan",basis);
      const primary=new Map();for(const g of list(context.primaryGoals||context.goals||plan.goals))if(units.has(g.rawcode)&&!temporary.has(g.rawcode))primary.set(g.rawcode,Math.max(primary.get(g.rawcode)||0,count(g.count)||1));
      if(!primary.size)return unknown("primary_goals_required",basis);
      const pools=new Map(),reserved=new Map(),originTotals=new Map(),spentActual=new Map();
      const put=(code,lot)=>{if(lot.qty<=eps)return;if(!pools.has(code))pools.set(code,[]);pools.get(code).push(lot);};
      for(const [raw,n]of actual){if(!units.has(raw))continue;const id="A:"+raw;originTotals.set(id,n);put(raw,{qty:n,origins:new Map([[id,1]]),kind:"actual"});}
      for(const [raw,n]of virtual){if(!units.has(raw))return unknown("unknown_virtual_rawcode",basis);const id="V:"+raw;originTotals.set(id,n);put(raw,{qty:n,origins:new Map([[id,1]]),kind:"virtual"});}
      function take(raw,n,physical){const lots=pools.get(raw)||[],taken=[];lots.sort((a,b)=>({actual:0,virtual:1,produced:2}[a.kind]-{actual:0,virtual:1,produced:2}[b.kind]));
        let left=n;for(const lot of lots){const amount=Math.min(left,lot.qty);if(amount<=eps)continue;lot.qty-=amount;left-=amount;taken.push({...lot,qty:amount});if(physical&&lot.kind==="actual")add(spentActual,raw,amount);if(left<=eps)break;}
        return left>eps?null:taken;
      }
      const reservedCounts=new Map();for(const row of plan.reservedGoals){if(!units.has(row.rawcode)||!count(row.count))return unknown("invalid_reservations",basis);reservedCounts.set(row.rawcode,Math.max(reservedCounts.get(row.rawcode)||0,row.count));}
      for(const [raw,n]of primary)if((reservedCounts.get(raw)||0)<n)return unknown("primary_goal_not_reserved",basis);
      for(const [raw,n]of stockOf(context.protectedCounts))if(!units.has(raw)||n>(actual.get(raw)||0)||(reservedCounts.get(raw)||0)<n)return unknown("protected_stock_not_reserved",basis);
      for(const [raw,n]of reservedCounts){const existing=Math.min(n,(actual.get(raw)||0)+(virtual.get(raw)||0));reserved.set(raw,take(raw,existing,false)||[]);}
      for(const step of plan.steps){
        if(!["craft","choice"].includes(step.type)||!units.has(step.resultRawcode)||!count(step.resultCount))return unknown("invalid_step",basis);
        if(step.type==="craft"){
          const observed=new Map();for(const row of list(step.ingredients))add(observed,row.rawcode,count(row.count));
          const recipe=(recipes.get(step.resultRawcode)||[]).find(recipe=>{const expected=new Map();for(const row of list(recipe.ingredients))if(String(row.type).toUpperCase()==="UNIT")add(expected,row.rawcode,count(row.count));return !recipe.materialChoiceInvalid&&String(recipe.id)===String(step.recipeId)&&(recipe.materialChoiceRawcode||null)===(step.materialChoiceRawcode||null)&&same(expected,observed);});
          if(!recipe||recipe.resultCount!==step.resultCount)return unknown("recipe_trace_mismatch",basis);
          if(recipe.materialChoiceRawcode&&(actual.get("H099")||0)+(actual.get("H0B2")||0)>1)return unknown("ambiguous_material_choice",basis);
        }else if(step.choiceRawcode!=="e018"||step.resultCount!==1||!["h001","h002","h003","h004","h005","h006","h007","h008","h009"].includes(step.resultRawcode)||list(step.ingredients).length!==1||step.ingredients[0].rawcode!=="e018"||step.ingredients[0].count!==1)return unknown("invalid_choice_trace",basis);
        const origins=new Map();for(const row of list(step.ingredients)){if(!count(row.count))return unknown("invalid_ingredient",basis);const taken=take(row.rawcode,row.count,true);if(!taken)return unknown("trace_stock_shortage",basis);for(const lot of taken)for(const [id,per]of lot.origins)add(origins,id,per*lot.qty);}
        put(step.resultRawcode,{qty:step.resultCount,origins:new Map(Array.from(origins,([id,n])=>[id,n/step.resultCount])),kind:"produced"});
      }
      for(const [raw,n]of reservedCounts){const rows=reserved.get(raw),have=rows.reduce((s,l)=>s+l.qty,0),made=take(raw,n-have,false);if(!made)return unknown("reserved_goal_shortage",basis);rows.push(...made);}
      const final=new Map();for(const [raw,lots]of pools)add(final,raw,lots.reduce((s,l)=>s+l.qty,0));for(const [raw,lots]of reserved)add(final,raw,lots.reduce((s,l)=>s+l.qty,0));
      if(!same(final,stockOf(plan.finalCounts)))return unknown("final_counts_mismatch",basis);
      const useful=new Map(),retained=new Map(),protectedOnly=new Map(),surplus=new Map(),unused=new Map(),perGoal=[];
      function attribute(target,lot,amount){for(const [id,per]of lot.origins)add(target,id,per*amount);}
      for(const [raw,lots]of reserved){let need=primary.get(raw)||0;const contributors=new Map();for(const lot of lots){const n=Math.min(need,lot.qty);if(n>eps){attribute(useful,lot,n);attribute(contributors,lot,n);if(lot.kind==="actual")add(retained,raw,n);need-=n;}if(lot.qty-n>eps)attribute(protectedOnly,lot,lot.qty-n);}if(need>eps)return unknown("primary_goal_not_reserved",basis);if(primary.has(raw))perGoal.push({rawcode:raw,count:primary.get(raw),contributors:Object.fromEntries(contributors)});}
      for(const [raw,lots]of pools)for(const lot of lots)if(lot.qty>eps)attribute(lot.kind==="produced"?surplus:unused,lot,lot.qty);
      const partition=new Map();for(const group of [useful,protectedOnly,surplus,unused])for(const [id,n]of group)add(partition,id,n);
      if(!same(originTotals,partition))return unknown("origin_partition_mismatch",basis);
      let usefulActualValue=0,usefulFutureValue=0,nonWispUsefulValue=0,relevantActualValue=0,actualConsumedReferenceValue=0;
      // 더 비싼 대안을 택해 같은 목표의 원재료를 많이 태우는 행위에 가산점을 주지 않는다.
      for(const goal of perGoal){let total=0;const weighted=[];for(const [id,n]of Object.entries(goal.contributors)){const raw=id.slice(2),value=valueOf(raw);if(value===null)return unknown("material_value_unknown",basis);weighted.push({id,raw,value:n*value});total+=n*value;}
        const reference=valueOf(goal.rawcode);if(reference===null)return unknown("goal_value_unknown",basis);const scale=total>0?Math.min(1,reference*goal.count/total):1;
        goal.normalizationScale=scale;for(const row of weighted){const value=row.value*scale;if(row.id.startsWith("A:")){usefulActualValue+=value;if(row.raw!=="e018")nonWispUsefulValue+=value;}else usefulFutureValue+=value;}}
      for(const [raw,n]of actual)if(raw!=="e018"&&!temporary.has(raw)&&(relevant.has(raw)||primary.has(raw))){const value=valueOf(raw);if(value===null)return unknown("inventory_value_unknown",basis);relevantActualValue+=value*n;}
      for(const [raw,n]of spentActual){const value=valueOf(raw);if(value===null)return unknown("consumed_value_unknown",basis);actualConsumedReferenceValue+=value*n;}
      const denominator=usefulActualValue+usefulFutureValue;
      return {status:"verified",verified:true,basis,valuationPolicy:"minimum_acyclic_UNIT_reference",coverage:denominator>0?usefulActualValue/denominator:1,
        handUtilization:relevantActualValue>0?Math.min(1,nonWispUsefulValue/relevantActualValue):0,usefulActualValue,usefulFutureValue,nonWispUsefulValue,relevantActualValue,
        actualConsumedReferenceValue,actualChoiceSpent:spentActual.get("e018")||0,actualChoicePreserved:Math.max(0,(actual.get("e018")||0)-(spentActual.get("e018")||0)),
        pathSignature:JSON.stringify(plan.steps.map(step=>step.type==="choice"?["choice",step.resultRawcode]:["craft",String(step.recipeId),step.resultRawcode,step.resultCount])),
        usefulOrigins:Object.fromEntries(useful),retainedGoals:Object.fromEntries(retained),protectedOnlyOrigins:Object.fromEntries(protectedOnly),surplusOrigins:Object.fromEntries(surplus),unusedOrigins:Object.fromEntries(unused),perGoal,finalCounts:Object.fromEntries(final)};
    }
    function compare(a,b){if(a?.verified!==true||b?.verified!==true)return 0;return b.coverage-a.coverage||b.handUtilization-a.handUtilization||b.nonWispUsefulValue-a.nonWispUsefulValue||a.actualChoiceSpent-b.actualChoiceSpent||a.actualConsumedReferenceValue-b.actualConsumedReferenceValue;}
    function choose(plans,context){let best=null;for(const plan of list(plans)){if(!plan?.executable)continue;const fit=assess({...context,plan});if(!best||compare(fit,best.fit)<0)best={plan,fit};}return best;}
    return {preselectionScore,assess,compare,choose};
  }
  function same(a,b){for(const key of new Set([...a.keys(),...b.keys()]))if(Math.abs((a.get(key)||0)-(b.get(key)||0))>eps)return false;return true;}
  return {create};
});
