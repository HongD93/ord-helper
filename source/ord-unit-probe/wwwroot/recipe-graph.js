(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (root) root.OrdRecipeGraph = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";
  const positive = value => Number.isFinite(Number(value)) && Number(value) > 0 ? Math.floor(Number(value)) : 0;
  const add = (map, code, count) => { if (count > 0) map.set(code, (map.get(code) || 0) + count); };
  const merge = (target, source) => { for (const [code, count] of source) add(target, code, count); };
  const sorted = map => Array.from(map, ([rawcode, count]) => ({rawcode, count})).sort((a,b) => a.rawcode < b.rawcode ? -1 : a.rawcode > b.rawcode ? 1 : 0);
  const ratio = plan => plan.cost > 0 ? Math.max(0, Math.min(1, 1 - plan.missingCost / plan.cost)) : 0;
  const percentage = plan => ratio(plan) >= 1 ? 100 : Math.min(99,Math.round(ratio(plan)*100));
  function isProductionRecipe(recipe) {
    if (!unitsOf(recipe).size && (recipe.ingredients||[]).some(item=>String(item.type).toUpperCase()==="RDUN"&&item.rawcode==="A800")) return false;
    if ((recipe.ingredients||[]).length || !(recipe.upgradeRoutes||[]).length) return true;
    // UPUN 관계만 있는 빈 등록은 생성 명령이 아니다. 실제 능력·시전자·입력 근거가 있으면 유지한다.
    return recipe.abilityProfilePresent === true || Boolean(recipe.hotkey) || (recipe.casterUnitRawcodes||[]).length > 0 ||
      Boolean(recipe.chatCommand) || (recipe.chatCommands||[]).length > 0;
  }
  const unitsOf = recipe => {
    const result = new Map();
    for (const item of recipe.ingredients || []) if (String(item.type).toUpperCase() === "UNIT" && typeof item.rawcode === "string") add(result, item.rawcode, positive(item.count));
    return result;
  };
  function stockOf(counts) {
    if (counts instanceof Map) return new Map(Array.from(counts, ([code,count]) => [String(code), positive(count)]));
    if (Array.isArray(counts)) return new Map(counts.map(item => [String(item.rawcode),positive(item.count)]));
    return new Map(Object.entries(counts || {}).map(([code,count]) => [code,positive(count)]));
  }
  const nikaForms=["H099","H0B2"];
  function expandMaterialChoices(recipe,catalog){
    const marked=(recipe?.ingredients||[]).filter(item=>Object.prototype.hasOwnProperty.call(item,"acceptedRawcodes"));if(!marked.length)return [recipe];
    const item=marked[0],codes=new Set((catalog?.units||[]).map(unit=>unit.rawcode));
    const sourceValid=recipe?.id==="ET02"&&recipe.resultUnitRawcode==="H0BK"&&marked.length===1&&String(item.type).toUpperCase()==="UNIT"&&item.count===1&&Array.isArray(item.acceptedRawcodes)&&item.acceptedRawcodes.length===2&&new Set(item.acceptedRawcodes).size===2&&nikaForms.every(code=>item.acceptedRawcodes.includes(code)&&codes.has(code));
    if(sourceValid&&nikaForms.includes(recipe.materialChoiceRawcode)&&item.rawcode===recipe.materialChoiceRawcode&&item.logicalRawcode==="H08T")return [recipe];
    const valid=sourceValid&&item.rawcode==="H08T"&&!recipe.materialChoiceRawcode;
    if(!valid)return [{...recipe,materialChoiceInvalid:true}];
    return nikaForms.map(rawcode=>({...recipe,materialChoiceRawcode:rawcode,ingredients:recipe.ingredients.map(ingredient=>ingredient===item?{...ingredient,rawcode,logicalRawcode:"H08T"}:ingredient)}));
  }
  function materialChoiceState(recipe,counts){
    if(recipe?.materialChoiceInvalid)return {known:false,ambiguous:true,reason:"invalid_material_choice"};
    if(!recipe?.materialChoiceRawcode)return null;
    const stock=counts instanceof Map?counts:stockOf(counts),owned=nikaForms.reduce((sum,code)=>sum+(stock.get(code)||0),0);
    return {known:true,owned,ambiguous:owned>1,rawcode:owned===1?nikaForms.find(code=>(stock.get(code)||0)===1):null,reason:owned>1?"ambiguous_material_choice":null};
  }
  const matchesMaterialChoice=(recipe,options)=>recipe?.id!=="ET02"||recipe.resultUnitRawcode!=="H0BK"||options?.materialChoiceRawcode==null||recipe.materialChoiceRawcode===options.materialChoiceRawcode;
  const supportedItemCodes = new Set(["AI00","AI01","AI02"]);
  function itemStateOf(state) {
    const stock=new Map(),known=new Set();
    for(const code of supportedItemCodes){const value=state?.counts?.[code];if(state?.status==="confirmed"&&(value===0||value===1)){stock.set(code,value);known.add(code);}}
    return {stock,known};
  }
  function itemBalances(requirements,known,stock) {
    const totals=new Map(),names=new Map();
    for(const item of requirements||[])if(String(item.type).toUpperCase()==="ITEM"&&typeof item.rawcode==="string"&&positive(item.count)){add(totals,item.rawcode,positive(item.count));names.set(item.rawcode,item.name||item.rawcode);}
    const itemRequirements=sorted(totals).map(({rawcode,count})=>{const valid=known.has(rawcode),have=valid?stock.get(rawcode)||0:null;return {type:"ITEM",rawcode,name:names.get(rawcode),count,have,missing:valid?Math.max(0,count-have):null,known:valid};});
    const missingItems=itemRequirements.filter(item=>!item.known||item.missing>0);
    return {itemRequirements,missingItems,itemsReady:missingItems.length===0};
  }
  function evaluateItems(requirements,state){const balance=itemStateOf(state);return itemBalances(requirements,balance.known,balance.stock);}
  const specialTypes=new Set(["IFCT","CHCT","SKPT","RMAX","NIKA"]);
  function expandRecipeRequirements(requirements,batches){
    if(!Number.isSafeInteger(batches)||batches<1)throw new RangeError("Invalid recipe batch count");
    return (requirements||[]).map(item=>({...item,count:String(item.type).toUpperCase()==="NIKA"?Math.min(1,positive(item.count)):positive(item.count)*(String(item.type).toUpperCase()==="RMAX"?1:batches),executions:batches}));
  }
  function normalizeRecipeSpecial(value,localPlayer,mapHash){
    const integer=(number,maximum)=>Number.isSafeInteger(number)&&number>=0&&number<=maximum;
    const valid=Number.isInteger(localPlayer)&&localPlayer>=0&&localPlayer<=3&&value?.localPlayer===localPlayer&&["confirmed","partial"].includes(value?.status)&&value?.source?.readOnly===true&&Number.isSafeInteger(value.source.sampleCount)&&value.source.sampleCount>=2&&(!mapHash||value.source.mapSha256===mapHash);
    const fields=["distortionWoodSurcharge","distortionWoodIncrement","transformationCount","skillPoints","round"],result={status:valid?value.status:"unknown",localPlayer:Number.isInteger(localPlayer)?localPlayer:null,source:valid?{...value.source}:{readOnly:false}};
    for(const name of fields)result[name]=valid&&integer(value[name],name==="round"?65:name==="distortionWoodIncrement"?1000:1000000)?value[name]:null;
    if(result.distortionWoodIncrement!==5){result.distortionWoodIncrement=null;result.distortionWoodSurcharge=null;}
    if(valid)result.status=fields.every(name=>result[name]!==null)?"confirmed":"partial";
    const nika=value?.nikaEnhancement,nikaValid=Number.isInteger(localPlayer)&&localPlayer>=0&&localPlayer<=3&&nika?.status==="confirmed"&&nika.localPlayer===localPlayer&&integer(nika.stage,2)&&nika.source?.readOnly===true&&Number.isSafeInteger(nika.source.sampleCount)&&nika.source.sampleCount>=2&&typeof mapHash==="string"&&mapHash.length>0&&nika.source.mapSha256===mapHash;
    result.nikaEnhancement={status:nikaValid?"confirmed":"unknown",localPlayer:Number.isInteger(localPlayer)?localPlayer:null,stage:nikaValid?nika.stage:null,source:nikaValid?{...nika.source}:{readOnly:false}};
    return result;
  }
  function evaluateRecipeConditions(requirements,state) {
    const unique=new Map(),stateKnown=state?.status==="confirmed"||state?.status==="partial";
    for(const item of requirements||[]){const type=String(item.type).toUpperCase();if(!["UBAN","KING"].includes(type)||!positive(item.count))continue;
      const key=type+"\0"+String(item.rawcode||"");if(!unique.has(key))unique.set(key,{...item,type,count:positive(item.count)});else unique.get(key).count=Math.max(unique.get(key).count,positive(item.count));}
    const conditionRequirements=Array.from(unique.values(),item=>{
      let allowed=null,reason="condition_unknown";
      if(stateKnown&&item.type==="UBAN"&&typeof item.rawcode==="string"&&Object.prototype.hasOwnProperty.call(state.unitBans||{},item.rawcode)&&typeof state.unitBans[item.rawcode]==="boolean"){allowed=!state.unitBans[item.rawcode];reason=allowed?"unit_not_banned":"unit_banned";}
      if(stateKnown&&item.type==="KING"&&["allowed","blocked"].includes(state.king?.status)){allowed=state.king.status==="allowed";reason=typeof state.king.reason==="string"&&state.king.reason?state.king.reason:allowed?"king_allowed":"king_blocked";}
      return {type:item.type,rawcode:item.rawcode,name:item.name||item.rawcode||item.type,count:item.count,known:allowed!==null,allowed,reason};
    });
    const special=normalizeRecipeSpecial(state?.special,state?.localPlayer,state?.source?.mapSha256),totals=new Map();let distortionExecutions=0;
    for(const item of requirements||[]){const type=String(item.type).toUpperCase();if(!specialTypes.has(type)||!positive(item.count))continue;
      const required=type==="IFCT"&&item.woodCost===true&&positive(item.baseCount)?positive(item.baseCount):positive(item.count);
      if(type==="RMAX")totals.set(type,Math.min(totals.get(type)??Infinity,required));
      else if(type==="NIKA")totals.set(type,1);
      else totals.set(type,(totals.get(type)||0)+required);
      if(type==="IFCT")distortionExecutions+=positive(item.executions)||1;
    }
    let distortionWoodCost=0;
    for(const [type,count]of totals){
      let have=null,known=false,allowed=false,missing=null,reason="special_condition_unknown",needed=count;
      const name={IFCT:"왜곡 목재",CHCT:"변화가능 횟수",SKPT:"특성 포인트",RMAX:"조합 가능 라운드",NIKA:"루피 특성 강화"}[type];
      if(type==="IFCT"){
        known=special.distortionWoodSurcharge!==null&&special.distortionWoodIncrement!==null;
        distortionWoodCost=known?count+distortionExecutions*special.distortionWoodSurcharge+(distortionExecutions*(distortionExecutions-1)/2)*(special.distortionWoodIncrement||0):null;
        if(distortionWoodCost!==null&&!Number.isSafeInteger(distortionWoodCost)){known=false;distortionWoodCost=null;}
        needed=distortionWoodCost??count;allowed=known;reason=known?"distortion_wood_cost_known":"distortion_wood_cost_unknown";
      }else if(type==="NIKA"){
        known=special.nikaEnhancement.status==="confirmed";have=known?Number(special.nikaEnhancement.stage===1):null;allowed=known&&have===1;missing=known?1-have:null;needed=1;reason=known?(allowed?"special_condition_ready":special.nikaEnhancement.stage===2?"nika_already_completed":"nika_enhancement_incomplete"):"special_condition_unknown";
      }else{
        have=special[{CHCT:"transformationCount",SKPT:"skillPoints",RMAX:"round"}[type]];known=have!==null;
        allowed=known&&(type==="RMAX"?have<=count:have>=count);missing=known?(type==="RMAX"?Number(have>count):Math.max(0,count-have)):null;
        reason=known?(allowed?"special_condition_ready":type==="RMAX"?"round_deadline_passed":"special_resource_shortage"):"special_condition_unknown";
      }
      conditionRequirements.push({type,rawcode:type,name,count:needed,baseCount:count,have,missing,known,allowed,reason,...(type==="IFCT"?{woodCost:true,executions:distortionExecutions}:{})});
    }
    const blockedConditions=conditionRequirements.filter(item=>item.known&&!item.allowed),unknownConditions=conditionRequirements.filter(item=>!item.known);
    return {conditionRequirements,blockedConditions,unknownConditions,missingConditions:[...blockedConditions,...unknownConditions],conditionsReady:!blockedConditions.length&&!unknownConditions.length,distortionWoodCost};
  }
  function projectRecipeConditions(requirements,state){
    const special=normalizeRecipeSpecial(state?.special,state?.localPlayer,state?.source?.mapSha256),result={...(state||{}),special};
    let distortionExecutions=0;
    for(const item of requirements||[]){const type=String(item.type).toUpperCase(),count=positive(item.count);if(!count)continue;
      if(type==="CHCT"&&special.transformationCount!==null)special.transformationCount=Math.max(0,special.transformationCount-count);
      if(type==="SKPT"&&special.skillPoints!==null)special.skillPoints=Math.max(0,special.skillPoints-count);
      if(type==="NIKA"&&special.nikaEnhancement.status==="confirmed"&&special.nikaEnhancement.stage===1)special.nikaEnhancement={...special.nikaEnhancement,stage:2};
      if(type==="IFCT")distortionExecutions+=positive(item.executions)||1;
    }
    if(distortionExecutions&&special.distortionWoodSurcharge!==null)special.distortionWoodSurcharge=special.distortionWoodIncrement===null?null:special.distortionWoodSurcharge+distortionExecutions*special.distortionWoodIncrement;
    return result;
  }
  function effectiveResourceCosts(requirements,state){
    let wood=0,gold=0;for(const item of requirements||[]){const type=String(item.type).toUpperCase();if(type==="WOOD")wood+=positive(item.count);if(type==="GOLD")gold+=positive(item.count);}
    const special=evaluateRecipeConditions(requirements,state);return {wood:special.distortionWoodCost===null?null:wood+special.distortionWoodCost,gold,distortionWoodCost:special.distortionWoodCost};
  }
  function create(catalog, settings) {
    settings = settings || {};
    const catalogHash=catalog.map?.sha256||catalog.source_sha256;
    const scopedConditions=state=>catalogHash?{...(state||{}),special:normalizeRecipeSpecial(state?.special,state?.localPlayer,catalogHash)}:state;
    const units = new Map((catalog.units || []).map(unit => [unit.rawcode,unit]));
    const recipes = new Map();
    const parents = new Map();
    const eligible = new Set(settings.choiceEligibleRawcodes || (catalog.units || []).filter(unit => unit.playable && unit.tier === "흔함").map(unit => unit.rawcode));
    const choiceCode = settings.choiceWispRawcode || "e018";
    const deckMarker = Symbol("deck-reservation-root");
    let deckSerial = 0;
    const link = (ingredient, result, recipeId, upgrade) => {
      if (ingredient === result) return;
      if (!parents.has(ingredient)) parents.set(ingredient,new Map());
      const list = parents.get(ingredient);
      if (!list.has(result)) list.set(result,{rawcode:result,recipeIds:[],upgradeSourceRecipeIds:[],upgradeOnly:true});
      const edge = list.get(result);
      const ids = upgrade ? edge.upgradeSourceRecipeIds : edge.recipeIds;
      if (!ids.includes(recipeId)) ids.push(recipeId);
      if (!upgrade) edge.upgradeOnly = false;
    };
    for (const recipe of (catalog.recipes || []).flatMap(recipe=>expandMaterialChoices(recipe,catalog))) {
      if (!recipe || typeof recipe.resultUnitRawcode !== "string" || !positive(recipe.resultCount)) continue;
      if (!recipes.has(recipe.resultUnitRawcode)) recipes.set(recipe.resultUnitRawcode,[]);
      recipes.get(recipe.resultUnitRawcode).push(recipe);
      for (const code of unitsOf(recipe).keys()) link(code,recipe.resultUnitRawcode,String(recipe.id),false);
      // UPUN 경로는 상위 관계에만 사용하고 소비 재료에는 절대 더하지 않는다.
      for (const code of recipe.upgradeRoutes || []) if (typeof code === "string") link(recipe.resultUnitRawcode,code,String(recipe.id),true);
    }
    for (const list of recipes.values()) list.sort((a,b) => String(a.id) < String(b.id) ? -1 : String(a.id) > String(b.id) ? 1 : 0);
    for (const list of parents.values()) for (const edge of list.values()) if (!edge.recipeIds.length)
      edge.recipeIds = (recipes.get(edge.rawcode) || []).map(recipe => String(recipe.id));
    function empty() { return {cost:0,missingCost:0,leaves:new Map(),missingLeaves:new Map(),conditions:[],rows:[],warnings:new Set()}; }
    function combine(target, part) {
      target.cost += part.cost; target.missingCost += part.missingCost;
      merge(target.leaves,part.leaves); merge(target.missingLeaves,part.missingLeaves);
      target.conditions.push(...part.conditions);
      for (const warning of part.warnings) target.warnings.add(warning);
    }
    // 보유한 중간 재료가 대신하는 기본 재료의 가치를 계산한다. 자원 조건은 이미 지불한 것으로 취급한다.
    function nominal(code, count, path, depth) {
      const result = empty();
      if (!count) return result;
      const list = recipes.get(code);
      if (!list || !list.length || path.has(code) || depth > 64) {
        result.cost = count; add(result.leaves,code,count);
        if (path.has(code) || depth > 64) result.warnings.add("recipe_cycle:" + code);
        return result;
      }
      const recipe = list[0], next = new Set(path); next.add(code);
      const batches = Math.ceil(count / positive(recipe.resultCount));
      const ingredients = unitsOf(recipe);
      if (!ingredients.size) { result.cost = count; add(result.leaves,code,count); return result; }
      for (const [child,need] of ingredients) combine(result,nominal(child,need*batches,next,depth+1));
      return result;
    }
    function leaf(code, need, taken, warning) {
      const result = empty(); result.cost = need; result.missingCost = need - taken;
      add(result.leaves,code,need); add(result.missingLeaves,code,need-taken);
      if (warning) result.warnings.add(warning);
      return result;
    }
    function take(stock, code, need) {
      const amount = Math.min(need,stock.get(code) || 0);
      stock.set(code,(stock.get(code) || 0)-amount); return amount;
    }
    function demand(code, need, stock, path, depth, reserved,materialInventory) {
      const owned = reserved === undefined ? take(stock,code,need) : reserved;
      const missing = need - owned, list = recipes.get(code);
      if (!list || !list.length || path.has(code) || depth > 64) return leaf(code,need,owned,path.has(code) || depth > 64 ? "recipe_cycle:"+code : null);
      const credit = nominal(code,owned,path,depth);
      if (!missing) return credit;
      const next = new Set(path); next.add(code);
      let best = null;
      for (const recipe of list) {
        const candidateStock = new Map(stock);
        const plan = apply(recipe,Math.ceil(missing / positive(recipe.resultCount)),candidateStock,next,depth+1,materialInventory);
        // UNIT 없는 특별 조건 조합은 기본 재료를 임의로 만들어 100%로 표시하지 않는다.
        if (!plan.cost) { plan.cost = missing; plan.missingCost = missing; add(plan.leaves,code,missing); add(plan.missingLeaves,code,missing); }
        combine(plan,credit);
        const candidate = {plan,stock:candidateStock,recipe};
        if (!best || ratio(plan) > ratio(best.plan)+1e-12) best = candidate;
      }
      stock.clear(); for (const [key,value] of best.stock) stock.set(key,value);
      return best.plan;
    }
    function apply(recipe, batches, stock, path, depth,materialInventory) {
      const result = empty();
      const choice=materialChoiceState(recipe,materialInventory||stock);if(choice?.ambiguous)result.warnings.add(choice.reason);
      const ingredients = sorted(unitsOf(recipe)).map(item => ({...item,count:item.count*batches,...(recipe.materialChoiceRawcode===item.rawcode?{logicalRawcode:"H08T",acceptedRawcodes:nikaForms.slice()}:{})}));
      // 직접 보유한 재료를 먼저 예약한다. 하위 가지가 다른 직접 재료를 먼저 써버리지 않는다.
      for (const item of ingredients) item.have = take(stock,item.rawcode,item.count);
      for (const item of ingredients) {
        const part = demand(item.rawcode,item.count,stock,path,depth,item.have,materialInventory);
        result.rows.push({...item,missing:item.count-item.have,percent:percentage(part)});
        combine(result,part);
      }
      for (const item of expandRecipeRequirements(recipe.ingredients,batches)) if (String(item.type).toUpperCase() !== "UNIT")
        result.conditions.push({...item,recipeId:String(recipe.id)});
      return result;
    }
    function upper(code, warnings) {
      const direct = Array.from((parents.get(code) || new Map()).values(), edge => ({...edge,recipeIds:edge.recipeIds.slice().sort()})).sort((a,b) => a.rawcode.localeCompare(b.rawcode));
      const terminal = new Map();
      function visit(current,path) {
        if (path.has(current)) { warnings.add("upper_cycle:"+current); return; }
        const edges = parents.get(current);
        if (!edges || !edges.size) {
          if (current !== code) terminal.set(current,{rawcode:current,recipeIds:(recipes.get(current) || []).map(r => String(r.id))});
          return;
        }
        const next = new Set(path); next.add(current);
        for (const result of edges.keys()) visit(result,next);
      }
      visit(code,new Set());
      return {parents:direct,topParents:Array.from(terminal.values()).sort((a,b) => a.rawcode.localeCompare(b.rawcode))};
    }
    function plan(code, counts, recipeId, options) {
      if (recipeId && typeof recipeId === "object") { options = recipeId; recipeId = null; }
      options = options || {};
      options={...options,recipeConditions:scopedConditions(options.recipeConditions)};
      const inventory = stockOf(counts), itemInventory=itemStateOf(options.items);
      const supported = options.supportedRecipeIds == null ? null : new Set(options.supportedRecipeIds);
      const maxSteps = Math.max(1,Math.min(2000,positive(options.maxSteps)||64));
      const maxSearch = Math.max(1,Math.min(100000,positive(options.maxSearchNodes)||12000));
      const alternativeLimit=Math.max(1,Math.min(4,positive(options.planningAlternativeLimit)||1));
      const sharedSearch=options.planningSearchBudget&&Number.isFinite(options.planningSearchBudget.remainingNodes)?options.planningSearchBudget:null;
      const deadline=Number.isFinite(options.planningDeadline)?options.planningDeadline:null;
      const kingCraftBudget=Number.isSafeInteger(options.planningKingCraftBudget)&&options.planningKingCraftBudget>=0?options.planningKingCraftBudget:null;
      const verifiedCommon = new Set(["h001","h002","h003","h004","h005","h006","h007","h008","h009"]);
      const planChoiceCode = "e018";
      const choiceAllowed = options.allowChoiceWisps === true && options.choiceWispSupported !== false;
      const canChoose = rawcode => choiceAllowed && verifiedCommon.has(rawcode) && (!settings.choiceEligibleRawcodes || eligible.has(rawcode));
      let searchNodes = 0, failure = null;
      const searchLimit = {};
      const tick = () => { if (++searchNodes > maxSearch||sharedSearch&&--sharedSearch.remainingNodes<0||deadline!==null&&Date.now()>deadline) throw searchLimit; };
      const clone = state => ({stock:new Map(state.stock),items:new Map(state.items),steps:state.steps.slice(),conditions:state.conditions.slice(),kingCraftCount:state.kingCraftCount,nikaCraftCount:state.nikaCraftCount||0});
      const fail = (reason, rawcode, count, state, details) => {
        const candidate = {reason,missing:count > 0 ? [{rawcode,count}] : [],details:details || null,depth:state.steps.length};
        if (!failure || candidate.depth > failure.depth) failure = candidate;
      };
      function* makeReserved(rawcode, count, state, ancestors) {
        tick();
        const start = clone(state);
        const missing = count-take(start.stock,rawcode,count);
        if (!missing) { yield start; return; }
        if (ancestors.has(rawcode) || ancestors.size > 64) {
          fail("recipe_cycle",rawcode,missing,start); return;
        }
        const candidates = (recipes.get(rawcode) || []).filter(isProductionRecipe).filter(recipe=>matchesMaterialChoice(recipe,options));
        let triedSupported = false;
        for (const recipe of candidates) {
          if (supported && !supported.has(String(recipe.id))) continue;
          triedSupported = true;
          const next = new Set(ancestors); next.add(rawcode);
          for (const made of runRecipe(recipe,start,next)) {
            const reserved = clone(made);
            const amount = take(reserved.stock,rawcode,missing);
            if (amount >= missing) yield reserved;
            else if (amount > 0) yield* makeReserved(rawcode,missing-amount,reserved,ancestors);
          }
        }
        if (canChoose(rawcode) && (start.stock.get(planChoiceCode)||0)>0) {
          if (start.steps.length >= maxSteps) { fail("step_limit",rawcode,missing,start); return; }
          const converted = clone(start);
          take(converted.stock,planChoiceCode,1);
          // 만들어진 흔함 한 개는 상위 재료로 즉시 예약하므로 free stock에는 넣지 않는다.
          converted.steps.push({type:"choice",choiceRawcode:planChoiceCode,resultRawcode:rawcode,resultCount:1,
            ingredients:[{rawcode:planChoiceCode,count:1}],nonUnitRequirements:[]});
          if (missing === 1) yield converted;
          else yield* makeReserved(rawcode,missing-1,converted,ancestors);
        } else if (!candidates.length || triedSupported) fail("missing_material",rawcode,missing,start);
        else fail("unsupported_recipe",rawcode,missing,start,{recipeIds:candidates.map(recipe=>String(recipe.id))});
      }
      function* runRecipe(recipe, state, ancestors) {
        tick();
        const reservationRoot = recipe[deckMarker] === true;
        const choice=materialChoiceState(recipe,options.materialChoiceInventory||inventory);
        if(choice?.ambiguous){fail(choice.reason,recipe.resultUnitRawcode,1,state);return;}
        if(choice?.owned===1&&(choice.rawcode!==recipe.materialChoiceRawcode||(state.stock.get(choice.rawcode)||0)<1)){fail("protected_material",choice.rawcode,1,state);return;}
        if (!reservationRoot && state.steps.length >= maxSteps) { fail("step_limit",recipe.resultUnitRawcode,1,state); return; }
        const start = clone(state);
        const conditions = (recipe.ingredients||[]).filter(item=>String(item.type).toUpperCase()!=="UNIT").map(item=>({...item,recipeId:String(recipe.id)}));
        if(!reservationRoot&&conditions.some(item=>String(item.type).toUpperCase()==="NIKA"&&positive(item.count))){if(start.nikaCraftCount>0){const projected=projectRecipeConditions(start.conditions,options.recipeConditions);fail("recipe_conditions_blocked",null,0,start,{...evaluateRecipeConditions(conditions,projected),nonUnitRequirements:[...start.conditions,...conditions]});return;}start.nikaCraftCount++;}
        if(!reservationRoot&&conditions.some(item=>String(item.type).toUpperCase()==="KING"&&positive(item.count)))start.kingCraftCount++;
        if(kingCraftBudget!==null&&start.kingCraftCount>kingCraftBudget){fail("king_path_craft_budget",recipe.resultUnitRawcode,0,start,{planningKingCraftBudget:kingCraftBudget,requiredKingCrafts:start.kingCraftCount,nonUnitRequirements:[...start.conditions,...conditions]});return;}
        const conditionCheck=evaluateRecipeConditions([...start.conditions,...conditions],options.recipeConditions);
        if(!conditionCheck.conditionsReady){fail(conditionCheck.unknownConditions.length?"recipe_conditions_unknown":"recipe_conditions_blocked",null,0,start,{...evaluateRecipeConditions([...start.conditions,...conditions],options.recipeConditions),nonUnitRequirements:[...start.conditions,...conditions]});return;}
        const itemCheck=itemBalances(conditions,itemInventory.known,start.items);
        if(!itemCheck.itemsReady){fail(itemCheck.missingItems.some(item=>!item.known)?"items_unknown":"missing_item",null,0,start,{...evaluateRecipeConditions([...start.conditions,...conditions],options.recipeConditions),...evaluateItems([...start.conditions,...conditions],options.items),nonUnitRequirements:[...start.conditions,...conditions]});return;}
        // 부모 조합의 아이템도 형제 UNIT과 함께 먼저 예약해 하위 경로가 재사용하지 못한다.
        for(const item of itemCheck.itemRequirements)take(start.items,item.rawcode,item.count);
        start.conditions.push(...conditions);
        const ingredients = reservationRoot && recipe.preserveGoalOrder ? Array.from(unitsOf(recipe),([rawcode,count])=>({rawcode,count})) : sorted(unitsOf(recipe));
        const deficits = [];
        // 형제 재료를 먼저 전부 예약한 뒤 하위 제작을 시작한다.
        for (const item of ingredients) {
          const missing = item.count-take(start.stock,item.rawcode,item.count);
          if (missing) deficits.push({rawcode:item.rawcode,count:missing});
        }
        function* satisfy(index, current) {
          tick();
          if (index < deficits.length) {
            const item = deficits[index];
            for (const filled of makeReserved(item.rawcode,item.count,current,ancestors)) yield* satisfy(index+1,filled);
            return;
          }
          if (!reservationRoot && current.steps.length >= maxSteps) { fail("step_limit",recipe.resultUnitRawcode,1,current); return; }
          const finished = clone(current);
          if (!reservationRoot) finished.steps.push({type:"craft",recipeId:String(recipe.id),resultRawcode:recipe.resultUnitRawcode,
            resultCount:positive(recipe.resultCount),ingredients:ingredients.map(item=>({...item})),nonUnitRequirements:conditions,itemRequirements:itemCheck.itemRequirements,...(recipe.materialChoiceRawcode?{materialChoiceRawcode:recipe.materialChoiceRawcode}:{})});
          add(finished.stock,recipe.resultUnitRawcode,positive(recipe.resultCount));
          yield finished;
        }
        yield* satisfy(0,start);
      }
      const candidates = (recipes.get(code)||[]).filter(isProductionRecipe).filter(recipe=>recipeId==null || String(recipe.id)===String(recipeId)).filter(recipe=>matchesMaterialChoice(recipe,options));
      const failed = reason => ({executable:false,chosenRecipe:null,steps:[],stepCount:0,missing:failure ? failure.missing : [],
        reason,reasonDetails:failure ? failure.details : null,usesChoiceWisps:false,choiceWispCount:0,
        nonUnitRequirements:failure?.details?.nonUnitRequirements||[],conditionRequirements:failure?.details?.conditionRequirements||[],blockedConditions:failure?.details?.blockedConditions||[],unknownConditions:failure?.details?.unknownConditions||[],missingConditions:failure?.details?.missingConditions||[],conditionsReady:failure?.details?.conditionsReady!==false,itemRequirements:failure?.details?.itemRequirements||[],missingItems:failure?.details?.missingItems||[],itemsReady:!failure?.details?.missingItems?.length,
        additionalConditionsUnverified:Boolean(failure?.details?.missingItems?.some(item=>!item.known)||failure?.details?.unknownConditions?.length||failure?.details?.nonUnitRequirements?.some(item=>!["ITEM","UBAN","KING",...specialTypes].includes(String(item.type).toUpperCase()))),projectedItems:Object.fromEntries(itemInventory.stock),projectedCounts:Object.fromEntries(inventory),searchNodes});
      if (!candidates.length) return failed("unknown_recipe");
      const protectedRoot = inventory.get(code)||0;
      const initial = {stock:new Map(inventory),items:new Map(itemInventory.stock),steps:[],conditions:[],kingCraftCount:0,nikaCraftCount:0};
      // 이미 보유한 목표를 하위 순환 조합의 재료로 쓰지 않아 추가 생성량을 보장한다.
      initial.stock.set(code,0);
      let rootSupported = false;
      const completed=[],seenProofs=new Set();let searchComplete=true,limitReached=false;
      const finish=()=>alternativeLimit===1?completed[0]:{...completed[0],searchNodes,alternativePlans:completed.slice(1),alternativeSearch:{limit:alternativeLimit,proofCount:completed.length,searchComplete,limitReached,searchNodes}};
      try {
        for (const recipe of candidates) {
          if (supported && !supported.has(String(recipe.id))) continue;
          rootSupported = true;
          for (const result of runRecipe(recipe,initial,new Set([code]))) {
            if ((result.stock.get(code)||0)<1) continue;
            add(result.stock,code,protectedRoot);
            const choiceWispCount = result.steps.filter(step=>step.type==="choice").length;
            const proof={executable:true,chosenRecipe:recipe,steps:result.steps,stepCount:result.steps.length,missing:[],reason:null,reasonDetails:null,
              usesChoiceWisps:choiceWispCount>0,choiceWispCount,nonUnitRequirements:result.conditions,planningKingCraftCount:result.kingCraftCount,planningKingCraftBudget:kingCraftBudget,
              ...evaluateItems(result.conditions,options.items),...evaluateRecipeConditions(result.conditions,options.recipeConditions),projectedItems:Object.fromEntries(result.items),projectedRecipeConditions:projectRecipeConditions(result.conditions,options.recipeConditions),
              additionalConditionsUnverified:result.conditions.some(item=>!["ITEM","UBAN","KING",...specialTypes].includes(String(item.type).toUpperCase())),projectedCounts:Object.fromEntries(result.stock),searchNodes};
            const identity=JSON.stringify([proof.steps.map(step=>[step.type,step.recipeId,step.resultRawcode,step.resultCount,step.ingredients]),Object.entries(proof.projectedCounts).filter(([,n])=>n>0).sort()]);
            if(!seenProofs.has(identity)){seenProofs.add(identity);completed.push(proof);}
            if(completed.length>=alternativeLimit){limitReached=true;searchComplete=false;return finish();}
          }
        }
      } catch (error) {
        if (error !== searchLimit) throw error;
        if(completed.length){searchComplete=false;return finish();}
        return failed("search_limit");
      }
      if(completed.length)return finish();
      return failed(!rootSupported ? "unsupported_recipe" : failure ? failure.reason : "missing_material");
    }
    function planDeck(goals, counts, options) {
      options = options || {};
      const inventory = stockOf(counts), desired = new Map();
      const invalid = () => ({executable:false,materialsReady:false,goals:[],steps:[],stepCount:0,missing:[],missingIsEstimate:true,
        reason:"invalid_goals",choiceWispCount:0,usesChoiceWisps:false,nonUnitRequirements:[],additionalConditionsUnverified:false,
        finalCounts:Object.fromEntries(inventory),projectedCounts:Object.fromEntries(inventory),freeCounts:Object.fromEntries(inventory),searchNodes:0});
      if (!Array.isArray(goals) || !goals.length || goals.length > 32) return invalid();
      for (const goal of goals) {
        const code = goal && goal.rawcode, count = goal && (goal.count === undefined ? 1 : goal.count);
        if (typeof code !== "string" || !units.has(code) || !Number.isSafeInteger(count) || count < 1 || count > 1000) return invalid();
        // 동일 목표를 여러 역할로 지정해도 요구 수량을 중복 가산하지 않는다.
        desired.set(code,Math.max(desired.get(code)||0,count));
      }
      const reserved = new Map(desired);
      for (const [code,count] of stockOf(options.protectedCounts)) {
        if (!units.has(code) || count > (inventory.get(code)||0)) return invalid();
        if (count) reserved.set(code,Math.max(reserved.get(code)||0,count));
      }
      // 실행할 수 없는 내부 예약 노드다. 실제 조합 단계나 반환 재고에는 포함하지 않는다.
      const virtualCode = "\0ORD_DECK_"+(++deckSerial), virtualId = virtualCode+"_ROOT";
      const root = {id:virtualId,resultUnitRawcode:virtualCode,resultCount:1,ingredients:Array.from(reserved,([rawcode,count])=>({type:"UNIT",rawcode,count})),preserveGoalOrder:options.preserveGoalOrder===true,[deckMarker]:true};
      const allowed = options.supportedRecipeIds == null ? null : new Set(options.supportedRecipeIds);
      if (allowed) allowed.add(virtualId);
      recipes.set(virtualCode,[root]);
      let result;
      try { result = plan(virtualCode,inventory,virtualId,{...options,supportedRecipeIds:allowed}); }
      finally { recipes.delete(virtualCode); }
      const rows = Array.from(desired,([rawcode,count])=>({rawcode,count,ownedReserved:Math.min(count,inventory.get(rawcode)||0),toProduce:Math.max(0,count-(inventory.get(rawcode)||0))}));
      const normalize=proof=>{const freeCounts={...proof.projectedCounts};delete freeCounts[virtualCode];const finalCounts={...freeCounts};
      if(proof.executable)for(const [code,count]of reserved)finalCounts[code]=(finalCounts[code]||0)+count;
      const {alternativePlans,...plain}=proof;
      return {...plain,chosenRecipe:null,executable:proof.executable,materialsReady:proof.executable,goals:rows,
        reservedGoals:Array.from(reserved,([rawcode,count])=>({rawcode,count})),missingIsEstimate:!result.executable,
        finalCounts,projectedCounts:finalCounts,freeCounts};};
      const normalized=normalize(result);if(result.alternativePlans)normalized.alternativePlans=result.alternativePlans.map(normalize);return normalized;
    }
    function analyze(code, counts, recipeId, options) {
      if (recipeId && typeof recipeId === "object") { options = recipeId; recipeId = null; }
      options = options || {};
      options={...options,recipeConditions:scopedConditions(options.recipeConditions)};
      const stock = stockOf(counts), list = recipes.get(code) || [];
      const matching = (recipeId == null ? list : list.filter(recipe => String(recipe.id) === String(recipeId))).filter(recipe=>matchesMaterialChoice(recipe,options));
      let best = null;
      for (const recipe of matching) {
        const plan = apply(recipe,1,new Map(stock),new Set([code]),0,stock);
        if (!best || ratio(plan) > ratio(best.plan)+1e-12) best = {recipe,plan};
      }
      const warnings = best ? best.plan.warnings : new Set();
      if (recipeId != null && !matching.length) warnings.add("unknown_recipe:"+recipeId);
      const relations = upper(code,warnings);
      const plan = best ? best.plan : empty();
      const direct = plan.rows;
      const leaves = sorted(plan.leaves).map(item => ({...item,have:item.count-(plan.missingLeaves.get(item.rawcode)||0),
        ...(best?.recipe.materialChoiceRawcode===item.rawcode?{logicalRawcode:"H08T",acceptedRawcodes:nikaForms.slice()}:{}),
        owned:stock.get(item.rawcode)||0,missing:plan.missingLeaves.get(item.rawcode)||0,
        percent:(plan.missingLeaves.get(item.rawcode)||0) === 0 ? 100 : Math.min(99,Math.round((1-(plan.missingLeaves.get(item.rawcode)||0)/item.count)*100))}));
      const missingLeaves = leaves.filter(item => item.missing > 0).map(item => ({...item,count:item.missing,required:item.count}));
      const missingDirect = direct.filter(item => item.missing > 0).map(item => ({...item,count:item.missing,required:item.count}));
      const conditionState=evaluateRecipeConditions(plan.conditions,options.recipeConditions);
      const resourceTotals = new Map();
      for (const condition of plan.conditions) {
        const type = String(condition.type).toUpperCase();
        if ((type === "GOLD" || type === "WOOD") && positive(condition.count)) add(resourceTotals,type,positive(condition.count));
      }
      if(plan.conditions.some(item=>String(item.type).toUpperCase()==="IFCT"))resourceTotals.set("WOOD",conditionState.distortionWoodCost===null?null:(resourceTotals.get("WOOD")||0)+conditionState.distortionWoodCost);
      const resourceRequirements = Array.from(resourceTotals,([type,count]) => {
        const value = options.resources?.[type.toLowerCase()], known = typeof count==="number"&&Number.isFinite(count)&&typeof value === "number" && Number.isFinite(value) && value >= 0;
        return {type,count,have:known ? value : null,missing:known ? Math.max(0,count-value) : null,known};
      }).sort((a,b)=>a.type.localeCompare(b.type));
      const missingResources = resourceRequirements.filter(item=>!item.known || item.missing>0);
      const resourcesReady = missingResources.length===0;
      const {itemRequirements,missingItems,itemsReady}=evaluateItems(plan.conditions,options.items);
      const materialChoiceAmbiguous=warnings.has("ambiguous_material_choice")||warnings.has("invalid_material_choice");
      const readiness=resourcesReady&&itemsReady&&!materialChoiceAmbiguous&&!conditionState.missingConditions.some(item=>specialTypes.has(item.type));
      // 필요한 화폐 종류마다 최대 5%만 배정하고, 나머지는 유닛 재료 진척률로 표시한다.
      // 경로는 기존 재귀 UNIT 선택 정책을 유지하며 그 선택 경로의 실제 제작 비용만 합산한다.
      const moneyOnly = plan.cost===0 && (resourceRequirements.length>0||itemRequirements.length>0) && plan.conditions.every(item=>["GOLD","WOOD","ITEM"].includes(String(item.type).toUpperCase()));
      const unitProgress = moneyOnly ? 1 : ratio(plan);
      const currencyWeights = Object.fromEntries(resourceRequirements.map(item=>[item.type,.05]));
      const itemWeight=itemRequirements.length?.05:0;
      const unitWeight = 1-resourceRequirements.length*.05-itemWeight;
      const progressWeighting = {unitWeight,currencyWeights,itemWeight,policy:"unit_progress_with_capped_currency_weights"};
      const currencyContribution = resourceRequirements.reduce((sum,item)=>sum+(item.known ? Math.min(1,item.have/item.count) : 0)*currencyWeights[item.type],0);
      const itemTotal=itemRequirements.reduce((sum,item)=>sum+item.count,0);
      const itemRatio=itemTotal?itemRequirements.reduce((sum,item)=>sum+(item.known?Math.min(item.count,item.have):0),0)/itemTotal:1;
      const weightedProgress = unit => Math.max(0,Math.min(1,unit*unitWeight+currencyContribution+itemRatio*itemWeight));
      const unitComplete = moneyOnly || plan.cost>0 && plan.missingCost===0;
      let progress = weightedProgress(unitProgress);
      if ((!unitComplete || !readiness) && progress>=1) progress=1-Number.EPSILON;
      const percent = unitComplete && readiness ? 100 : Math.min(99,Math.round(progress*100));
      const choiceWispAvailable = options.choiceWispCount === undefined ? stock.get(choiceCode)||0 : positive(options.choiceWispCount);
      const choiceWispNeeded = missingLeaves.filter(item => eligible.has(item.rawcode)).reduce((sum,item) => sum+item.count,0);
      const missingNonChoiceLeaves = missingLeaves.filter(item => !eligible.has(item.rawcode));
      const choiceCoveredCount = Math.min(choiceWispAvailable,choiceWispNeeded);
      const choiceRemainingCost = Math.max(0,plan.missingCost-choiceCoveredCount);
      const unitChoiceReachableRatio = plan.cost > 0 ? Math.max(0,Math.min(1,1-choiceRemainingCost/plan.cost)) : moneyOnly ? 1 : 0;
      const choiceUnitsComplete = moneyOnly || plan.cost>0 && choiceRemainingCost===0;
      let choiceReachableRatio = weightedProgress(unitChoiceReachableRatio);
      if ((!choiceUnitsComplete || !readiness) && choiceReachableRatio>=1) choiceReachableRatio = 1-Number.EPSILON;
      const choiceReachablePercent = choiceUnitsComplete && readiness ? 100 : Math.min(99,Math.round(choiceReachableRatio*100));
      const canCompleteWithChoiceWisp = Boolean(best) && readiness && conditionState.conditionsReady && choiceWispNeeded > 0 && !missingNonChoiceLeaves.length && choiceWispAvailable >= choiceWispNeeded;
      const conditions = new Map();
      for (const condition of plan.conditions) {
        const key = [condition.recipeId,condition.type,condition.rawcode,condition.name].join("\0");
        if (!conditions.has(key)) conditions.set(key,{...condition});
        else conditions.get(key).count=String(condition.type).toUpperCase()==="RMAX"?Math.min(conditions.get(key).count,condition.count):String(condition.type).toUpperCase()==="NIKA"?1:conditions.get(key).count+condition.count;
      }
      const nonUnitRequirements=Array.from(conditions.values());
      const additionalConditionsUnverified=nonUnitRequirements.some(item=>!["GOLD","WOOD","ITEM","UBAN","KING",...specialTypes].includes(String(item.type).toUpperCase()))||resourceRequirements.some(item=>!item.known)||itemRequirements.some(item=>!item.known)||conditionState.unknownConditions.length>0;
      return {rawcode:code,chosenRecipe:best ? best.recipe : null,...(best?.recipe.materialChoiceRawcode?{materialChoiceRawcode:best.recipe.materialChoiceRawcode,materialChoiceAmbiguous}:{}),percent,ratio:progress,unitRatio:unitProgress,unitChoiceReachableRatio,progressWeighting,resourceRequirements,missingResources,resourcesReady,itemRequirements,missingItems,itemsReady,itemRatio,resourceSelectionPolicy:"selected_recursive_unit_path",missingDirect,missingLeaves,direct,leaves,
        ...relations,...conditionState,nonUnitRequirements,additionalConditionsUnverified,
        warnings:Array.from(warnings).sort(),choiceWispAvailable,choiceWispNeeded,choiceAvailable:choiceWispAvailable,choiceNeeded:choiceWispNeeded,
        choiceCoveredCount,choiceReachableRatio,choiceReachablePercent,canCompleteWithChoiceWisp,missingNonChoiceLeaves,
        progressTone:best && unitComplete && readiness && conditionState.conditionsReady ? "complete" : canCompleteWithChoiceWisp ? "choice-ready" : "incomplete"};
    }
    return {analyze,plan,planDeck};
  }
  return {create,isProductionRecipe,evaluateItems,evaluateRecipeConditions,normalizeRecipeSpecial,projectRecipeConditions,effectiveResourceCosts,expandRecipeRequirements,expandMaterialChoices};
});


