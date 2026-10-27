(function (root) {
  "use strict";
  function create(options) {
    let state = null, combat = null, mechanics = null, clearRecords = null, rewardData = null, randomDrops = null, combatValidation = null, storyRankings = null, worker = null, ready = false, busy = false;
    let revision = 0, signature = "", session = "", initializedMap = "", loading = false;
    let timer = null, timeout = null, pending = null, result = null, failed = false, destroyed = false;
    let firstInvestment = null, investmentBaseline = null, investmentAmbiguous = false, starterPhaseSeen = false, manualDeckChoice = false, manualDeckChoiceRevision = 0, lastSemanticKey = "", lastBudget = null, blockingRecompute = true, resourceRefreshPending = false;
    let activeRecordsRevision=null,pendingRecordsSnapshot=null,recordUpdateInFlight=null,recordAckTimeout=null,recordPollTimer=null,recordFetchAbort=null,recordFetchBusy=false;
    let baseRecordIds=[],recommendInFlight=null;
    let reinforcementRole="auto";
    function validLiveRecords(data,manifest){
      const catalog=state?.catalog;
      if(data?.schemaVersion!==1||data.catalogMapHash!==catalog?.map?.sha256||!Array.isArray(data.records)||data.records.length!==manifest.recordCount)return false;
      const ids=new Set(),codes=new Set((catalog.units||[]).map(unit=>unit.rawcode));
      for(const record of data.records){
        if(typeof record?.id!=="string"||!record.id||record.id.length>120||ids.has(record.id)||!Array.isArray(record.goals)||!Array.isArray(record.entries)||record.goals.length>512||record.entries.length>512)return false;
        ids.add(record.id);
        for(const [name,rows]of [["goals",record.goals],["entries",record.entries]])for(const row of rows){
          if(!Number.isSafeInteger(row?.count)||row.count<1||row.count>1000000)return false;
          if(name==="goals"||row.rawcode!=null)if(typeof row.rawcode!=="string"||!codes.has(row.rawcode))return false;
        }
      }
      return baseRecordIds.every(id=>ids.has(id));
    }
    function applyRecordUpdate(){
      if(destroyed||!pendingRecordsSnapshot||state?.craftRunning||busy||recordUpdateInFlight)return false;
      const snapshot=pendingRecordsSnapshot;
      if(!worker){clearRecords=snapshot.data;activeRecordsRevision=snapshot.revision;pendingRecordsSnapshot=null;signature="";schedule(true);return true;}
      if(!ready)return false;
      pendingRecordsSnapshot=null;recordUpdateInFlight=snapshot;pending=null;revision++;blockingRecompute=true;show();
      try{worker.postMessage({type:"records-update",recordsRevision:snapshot.revision,recordCount:snapshot.data.records.length,clearRecords:snapshot.data});}
      catch{recordUpdateInFlight=null;stopWorker();signature="";schedule(true);return false;}
      recordAckTimeout=setTimeout(()=>{recordAckTimeout=null;recordUpdateInFlight=null;stopWorker();signature="";failed=false;schedule(true);},12000);
      return true;
    }
    async function pollLiveRecords(){
      if(destroyed||recordFetchBusy||!clearRecords||!state?.catalog||!root.crypto?.subtle||typeof AbortController==="undefined")return;
      recordFetchBusy=true;const controller=new AbortController();recordFetchAbort=controller;
      const abortTimer=setTimeout(()=>controller.abort(),10000);
      try{
        const response=await fetch("/nightmare-records-live-manifest.json",{cache:"no-store",signal:controller.signal});
        if(!response.ok)return;
        const manifest=await response.json(),mapHash=state.catalog.map?.sha256;
        if(manifest?.schemaVersion!==1||manifest.catalogMapHash!==mapHash||typeof manifest.revision!=="string"||!/^[a-f0-9]{64}$/.test(manifest.revision)||!Number.isSafeInteger(manifest.recordCount)||manifest.recordCount<Math.max(1,baseRecordIds.length)||manifest.recordCount>50000||manifest.dataUrl!=="/nightmare-records-live-"+manifest.revision+".json")return;
        if([activeRecordsRevision,pendingRecordsSnapshot?.revision,recordUpdateInFlight?.revision].includes(manifest.revision))return;
        const payload=await fetch(manifest.dataUrl,{cache:"no-store",signal:controller.signal});if(!payload.ok)return;
        const bytes=await payload.arrayBuffer();if(bytes.byteLength>67108864)return;
        const digest=Array.from(new Uint8Array(await root.crypto.subtle.digest("SHA-256",bytes)),value=>value.toString(16).padStart(2,"0")).join("");if(digest!==manifest.revision)return;
        const data=JSON.parse(new TextDecoder("utf-8",{fatal:true}).decode(bytes));if(destroyed||controller.signal.aborted||!validLiveRecords(data,manifest))return;
        pendingRecordsSnapshot={revision:manifest.revision,data};
        if(!state?.craftRunning){revision++;pending=null;blockingRecompute=true;}
        applyRecordUpdate();show();
      }catch{}finally{clearTimeout(abortTimer);recordFetchBusy=false;if(recordFetchAbort===controller)recordFetchAbort=null;}
    }
    function startRecordPolling(){
      if(recordPollTimer||!clearRecords||!state?.catalog||!root.crypto?.subtle||typeof AbortController==="undefined")return;
      recordPollTimer=setInterval(pollLiveRecords,20000);pollLiveRecords();
    }
    const panel = root.OrdNightmarePanel.create({
      host:options.host, onOpenUnit:options.onOpenUnit, onShowRecipe:options.onShowRecipe, formatUnitName:options.formatUnitName,
      onStopCraft:()=>{if(state?.craftRunning&&state?.capabilities?.stopCraftSupported===true)return options.onStopCraft?.();},
      onCraftTarget:(code,goalRawcode,origin)=>{
        if(!connected()||state?.craftRunning||recordUpdateInFlight||pendingRecordsSnapshot||(busy||pending)&&blockingRecompute||state?.capabilities?.protectedCountsSupported!==true||state?.capabilities?.exactPlanSupported!==true)return;
        if(goalRawcode&&code!==goalRawcode)return;
        if(origin==="reinforcement"){
          const target=(result?.reinforcements?.craftTargets||[]).find(item=>item.rawcode===code&&item.towardRawcode===goalRawcode);
          if(validReinforcementTarget(target,code,goalRawcode))return options.onCraftTarget?.(code,target);
          return;
        }
        if(origin!=null)return;
        if(result?.phase==="early_story"&&!(result.earlyStory?.candidates||[]).some(candidate=>candidate.rawcode===code))return;
        if(result?.targetDeck?.assessment?.compositionStatus==="incomplete"||result?.targetDeck?.acquisitionPlan?.sharedChoiceFeasible===false)return;
        const target=goalRawcode?(Array.isArray(result?.goalCraftTargets)?result.goalCraftTargets.find(item=>item.towardRawcode===goalRawcode&&item.rawcode===code&&item.isWholeGoal!==false&&item.available===true&&item.resourcesReady!==false):result?.craftTargets?.find(item=>item.rawcode===code&&item.isWholeGoal!==false&&item.resourcesReady!==false)):result?.craftTargets?.find(item=>item.rawcode===code&&item.resourcesReady!==false);
        if(target&&target.protectedCounts&&target.expectedSteps?.length)return options.onCraftTarget?.(code,target);
      },
      onSettingsChange:() => { const next=panel.getSettings(),role=["stun","physical_shred","magic_shred"].includes(next.reinforcementRole)?next.reinforcementRole:"auto";if(role===reinforcementRole){manualDeckChoice = Boolean(next.preferredDeckId);manualDeckChoiceRevision++;}reinforcementRole=role;schedule(true); }, onRetry:() => {
        failed = false; signature = "";
        if (!combat || !mechanics || !clearRecords || !rewardData || !randomDrops || !combatValidation?.unitModels || !combatValidation?.scenarios || !combatValidation?.recordEvidence) loadCombat(); else schedule(true);
      }
    });
    const connected = () => Boolean(state?.catalog && state?.counts instanceof Map && state.liveState?.tone === "live" &&
      !state.liveState?.checking && Date.now() - Number(state.receivedAt || 0) < 3500 &&
      state.snapshot?.catalogMatchesLoadedArchive === true &&
      state.snapshot?.loadedMapHash === state.catalog.map?.sha256);
    function validReinforcementTarget(target,code,goalRawcode){
      if(result?.phase==="early_story"||!goalRawcode||code!==goalRawcode||state?.capabilities?.enabled!==true||!state?.capabilities?.token)return false;
      if(!(result?.reinforcements?.candidates||[]).some(item=>item.rawcode===code)||target?.origin!=="reinforcement_whole_goal_path"||target.isWholeGoal!==true||target.available!==true||target.resourcesReady!==true||target.itemsReady!==true||target.conditionsReady!==true||target.additionalConditionsUnverified===true)return false;
      const steps=target.expectedSteps,locks=target.protectedCounts,recipes=new Map((state.catalog.recipes||[]).map(recipe=>[String(recipe.id),recipe])),supported=new Set(state.capabilities.supportedRecipeIds||[]);
      if(!Array.isArray(steps)||steps.length<1||steps.length>64||!locks||typeof locks!=="object"||Array.isArray(locks)||Object.keys(locks).length>64)return false;
      for(const [raw,count]of Object.entries(locks))if(!/^[A-Za-z0-9]{4}$/.test(raw)||!Number.isSafeInteger(count)||count<1||count>(state.counts.get(raw)||0))return false;
      for(const step of steps){if(step.type==="choice"){if(state.capabilities.choiceWispSupported!==true||!/^h00[1-9]$/.test(step.resultRawcode))return false;}else if(step.type!=="craft"||!supported.has(String(step.recipeId))||!recipes.has(String(step.recipeId)))return false;}
      const last=steps.at(-1);if(last.type!=="craft"||recipes.get(String(last.recipeId))?.resultUnitRawcode!==code)return false;
      return true;
    }
    function readItems(){
      const items=state?.snapshot?.items,localPlayer=state?.snapshot?.localPlayer,codes=["AI00","AI01","AI02"];
      const valid=connected()&&items?.status==="confirmed"&&Number.isInteger(localPlayer)&&items.localPlayer===localPlayer&&items.source?.readOnly===true&&codes.every(code=>Number.isSafeInteger(items.counts?.[code])&&items.counts[code]>=0&&items.counts[code]<=1);
      return{status:valid?"confirmed":"unknown",localPlayer:Number.isInteger(localPlayer)?localPlayer:null,counts:valid?Object.fromEntries(codes.map(code=>[code,items.counts[code]])):{},source:{readOnly:items?.source?.readOnly===true}};
    }
    function readRecipeConditions(){
      const value=state?.snapshot?.recipeConditions,localPlayer=state?.snapshot?.localPlayer,valid=connected()&&Number.isInteger(localPlayer)&&value?.localPlayer===localPlayer&&value.source?.readOnly===true&&["confirmed","partial"].includes(value.status);
      const mapHash=state?.catalog?.map?.sha256,special=value?.special,specialValid=connected()&&state?.snapshot?.status==="snapshot_read"&&Boolean(mapHash);
      const normalizedSpecial=root.OrdRecipeGraph?.normalizeRecipeSpecial?.(specialValid?special:null,localPlayer,mapHash)||{status:"unknown",localPlayer:Number.isInteger(localPlayer)?localPlayer:null,source:{readOnly:false}};
      return{status:valid?value.status:"unknown",localPlayer:Number.isInteger(localPlayer)?localPlayer:null,unitBans:valid?Object.fromEntries(Object.entries(value.unitBans||{}).filter(([code,blocked])=>/^[A-Za-z0-9]{4}$/.test(code)&&typeof blocked==="boolean").sort(([left],[right])=>left<right?-1:left>right?1:0)):{},king:{status:valid&&["allowed","blocked"].includes(value.king?.status)?value.king.status:"unknown",...(valid&&value.source?.mapSha256===mapHash&&typeof value.king?.reason==="string"?{reason:value.king.reason.slice(0,80)}:{})},special:normalizedSpecial,source:{readOnly:value?.source?.readOnly===true,mapSha256:mapHash}};
    }
    const readableRecipeConditions=()=>Array.from(state?.capabilities?.readableConditionTypes||[]).filter(type=>["UBAN","KING"].includes(type)||state?.capabilities?.specialRecipeConditionsSupported===true&&["IFCT","CHCT","SKPT","RMAX","NIKA"].includes(type));
    function readRewardBudget(){
      const localPlayer=state?.snapshot?.localPlayer,mapHash=state?.catalog?.map?.sha256;
      const progression=connected()?state.snapshot.progression:null;
      return root.OrdNightmareRewardBudget?.derive(rewardData,progression,{localPlayer,mapHash})||{
        status:"unknown",mapHash,localPlayer:Number.isInteger(localPlayer)?localPlayer:null,round:null,phase:"unknown",
        guaranteed:{choiceWisps:null,wood:null,gold:null,units:{}},conditional:{choiceWisps:null,wood:null,gold:null,units:{}},source:{readOnly:false}
      };
    }
    function readRandomHistory(){
      const localPlayer=state?.snapshot?.localPlayer,mapHash=state?.catalog?.map?.sha256;
      return root.OrdNightmareRandomForecast?.normalizeHistory(connected()?state.snapshot.progression?.randomWisp:null,{localPlayer,mapHash})||{
        status:"unknown",localPlayer:Number.isInteger(localPlayer)?localPlayer:null,source:{readOnly:false}
      };
    }
    function stopWorker() {
      if (timeout) clearTimeout(timeout);
      timeout = null;
      if (worker) worker.terminate();
      worker = null; ready = false; busy = false; initializedMap = "";
      recommendInFlight=null;
      if(recordAckTimeout)clearTimeout(recordAckTimeout);recordAckTimeout=null;recordUpdateInFlight=null;
    }
    function show(error) {
      panel.update({catalog:state?.catalog,result:connected() ? result : null, connected:connected(), calculating:connected() && (loading || Boolean(recordUpdateInFlight)||Boolean(pendingRecordsSnapshot)&&!state?.craftRunning || (busy || Boolean(pending)) && (blockingRecompute || !result)),
        error:error || null,craftRunning:Boolean(state?.craftRunning),craftStopRequested:Boolean(state?.craftStopRequested),craftCanStop:state?.capabilities?.stopCraftSupported===true,craftMessage:state?.craftMessage||"",
        craftTone:state?.craftTone||"",craftAvailable:state?.capabilities?.enabled===true&&Boolean(state?.capabilities?.token)&&state?.capabilities?.protectedCountsSupported===true&&state?.capabilities?.exactPlanSupported===true,
        elder:state?.snapshot?.elder||null,items:readItems(),rewardBudget:readRewardBudget(),randomHistory:readRandomHistory(),localPlayer:state?.snapshot?.localPlayer,itemCraftAvailable:state?.capabilities?.itemRequirementsSupported===true,readableItemRawcodes:Array.from(state?.capabilities?.readableItemRawcodes||[]),recipeConditions:readRecipeConditions(),recipeConditionsAvailable:state?.capabilities?.recipeConditionsSupported===true,specialRecipeConditionsAvailable:state?.capabilities?.specialRecipeConditionsSupported===true,readableConditionTypes:readableRecipeConditions()});
      options.onCraftContextChange?.();
    }
    function initialize() {
      if (!combat || !mechanics || !clearRecords || !state?.catalog || destroyed) return false;
      const hash = state.catalog.map?.sha256;
      if (combat.map?.sha256 !== hash || mechanics.map?.sha256 !== hash || clearRecords.catalogMapHash !== hash) {
        failed = true; pending = null; result = null;
        show("현재 맵에서 추천을 사용할 수 없어요."); return false;
      }
      if (worker && initializedMap === hash) return true;
      stopWorker();
      initializedMap = hash;
      try { worker = new Worker("/nightmare-advisor-worker.js"); }
      catch {
        stopWorker(); pending = null; result = null; failed = true;
        show("추천을 불러오지 못했어요. 다시 계산해 주세요."); return false;
      }
      const owningWorker=worker;
      worker.onmessage = event => {
        const message = event.data || {};
        if (destroyed||worker!==owningWorker) return;
        if(message.type==="records-ready"||message.type==="records-error"){
          const snapshot=recordUpdateInFlight;if(!snapshot||message.recordsRevision!==snapshot.revision)return;
          if(recordAckTimeout)clearTimeout(recordAckTimeout);recordAckTimeout=null;recordUpdateInFlight=null;
          if(message.type==="records-ready"){clearRecords=snapshot.data;activeRecordsRevision=snapshot.revision;}
          signature="";pending=null;failed=false;if(!applyRecordUpdate())schedule(true);return;
        }
        if (message.type === "ready") {
          if (timeout) clearTimeout(timeout);
          timeout = null; ready = true;if(!applyRecordUpdate())flush();return;
        }
        if (message.type === "error" && message.revision === undefined) {
          stopWorker(); pending = null; result = null; failed = true;
          show("추천을 불러오지 못했어요. 다시 계산해 주세요."); return;
        }
        if(message.revision!==recommendInFlight)return;
        recommendInFlight=null;
        if (timeout) clearTimeout(timeout);
        timeout = null; busy = false;
        if (message.revision === revision&&(message.recordsRevision??null)===(activeRecordsRevision??null)&&connected()) {
          if (message.type === "result") {
            result = message.result; failed = false;
            if(result?.phase==="early_story")starterPhaseSeen=true;
            else if(result?.phase==="final_deck"||result?.targetDeck)starterPhaseSeen=false;
            if(result?.selectedDeckId&&panel.getSettings().preferredDeckId!==result.selectedDeckId)
              panel.setSettings({preferredDeckId:result.selectedDeckId},{notify:false});
            show();
          }
          else { result = null; failed = true; pending = null; show("추천을 계산하지 못했어요. 다시 계산해 주세요."); }
        }
        if(!applyRecordUpdate())flush();
        if(resourceRefreshPending&&!busy){resourceRefreshPending=false;schedule(false);}
      };
      worker.onerror = () => {
        if(destroyed||worker!==owningWorker)return;
        stopWorker(); pending = null; result = null; failed = true;
        show("추천을 불러오지 못했어요. 다시 계산해 주세요.");
      };
      try { worker.postMessage({type:"init", catalog:state.catalog, combat,rules:mechanics,clearRecords,randomDrops,combatValidation,storyRankings,recordsRevision:activeRecordsRevision}); }
      catch {
        stopWorker(); pending = null; result = null; failed = true;
        show("추천을 불러오지 못했어요. 다시 계산해 주세요."); return false;
      }
      timeout = setTimeout(() => {
        stopWorker(); pending = null; result = null; failed = true;
        show("추천 정보를 불러오는 데 오래 걸리고 있어요. 다시 계산해 주세요.");
      }, 12000);
      return true;
    }
    function flush() {
      if (!pending || busy || recordUpdateInFlight || !ready || !worker || !connected()) return;
      const request = pending; pending = null; busy = true;recommendInFlight=request.revision;show();
      try { worker.postMessage(request); }
      catch {
        stopWorker(); pending = null; result = null; failed = true;
        show("추천을 계산하지 못했어요. 다시 계산해 주세요."); return;
      }
      timeout = setTimeout(() => {
        stopWorker(); pending = null; result = null; failed = true;
        show("추천 계산이 오래 걸리고 있어요. 다시 계산해 주세요.");
      }, 12000);
    }
    function schedule(force) {
      if (destroyed) return;
      if (!connected()) {
        if (timer) clearTimeout(timer);
        timer = null; pending = null; result = null;
        if (signature !== "disconnected") { signature = "disconnected"; revision++; }
        show(); return;
      }
      const currentSession = state.snapshot.sessionIdentity || "";
      if (session && currentSession !== session) {
        panel.setSettings({boss:"unknown",round:null,wood:null,preferredDeckId:null},{notify:false});
        result = null; pending = null; stopWorker();
        firstInvestment = null; investmentBaseline = null; investmentAmbiguous = false; starterPhaseSeen = false; manualDeckChoice = false; lastSemanticKey = ""; lastBudget = null; resourceRefreshPending = false;
      }
      session = currentSession;
      const catalogUnits = Array.isArray(state.catalog.units) ? state.catalog.units : null;
      const resourceCodes=["e018","e0IX",...(rewardData?.randomSourceRawcodes||[]),...(combat?.acquisitionModel?.sharedChoiceResources||[]).map(item=>item.sourceRawcode)].filter(code=>typeof code==="string"&&/^[A-Za-z0-9]{4}$/.test(code));
      const combatTransientCodes=new Set(["h06O","h0C5"]),referencedCodes=new Set(resourceCodes);
      for(const recipe of state.catalog.recipes||[]){if(typeof recipe.resultUnitRawcode==="string")referencedCodes.add(recipe.resultUnitRawcode);for(const item of recipe.ingredients||[])if(item.type==="UNIT"&&typeof item.rawcode==="string")referencedCodes.add(item.rawcode);}
      const relevantTransient=code=>!combatTransientCodes.has(code)||referencedCodes.has(code);
      const inventoryCodes = catalogUnits ? new Set(catalogUnits.filter(unit=>unit.playable===true&&relevantTransient(unit.rawcode)||combatTransientCodes.has(unit.rawcode)&&referencedCodes.has(unit.rawcode)).map(unit=>unit.rawcode).concat(resourceCodes)) : null;
      const inventory = new Map(Array.from(state.counts).filter(([code,count])=>count>0&&relevantTransient(code)&&(!inventoryCodes||inventoryCodes.has(code))));
      const investmentUnits = new Map((catalogUnits||[]).filter(unit=>unit.playable===true&&["전설","히든"].includes(unit.tier)&&!["h076","h04T","h04Y","h05D","h0CA","h0C8"].includes(unit.rawcode)).map(unit=>[unit.rawcode,unit]));
      const investmentCounts = new Map(Array.from(inventory).filter(([code])=>investmentUnits.has(code)));
      // 전설 전용 v1 기록은 히든의 선행 여부를 입증하지 못하므로 새 관측 이력과 섞지 않는다.
      const storageKey = "ord-first-investment-v2:" + state.snapshot.processId + ":" + session + ":" + state.catalog.map?.sha256;
      if (!firstInvestment && investmentBaseline===null) {
        try { const saved=JSON.parse(root.localStorage?.getItem(storageKey)||"null"); if(saved&&investmentUnits.has(saved.rawcode))firstInvestment={...saved,tier:investmentUnits.get(saved.rawcode).tier}; else if(saved?.ambiguous===true)investmentAmbiguous=true; } catch {}
      }
      if (!firstInvestment && !investmentAmbiguous && !(starterPhaseSeen&&state.craftRunning)) {
        const completedStarter=starterPhaseSeen&&!state.craftRunning&&investmentCounts.size>0;
        const appeared=completedStarter?Array.from(investmentCounts):Array.from(investmentCounts).filter(([code,count])=>count>(investmentBaseline?.get(code)||0));
        if(appeared.length>1&&(investmentBaseline===null||investmentBaseline.size===0||completedStarter)){
          investmentAmbiguous=true;try{root.localStorage?.setItem(storageKey,JSON.stringify({ambiguous:true,evidence:"multiple_advanced_units_first_observed"}));}catch{}
          starterPhaseSeen=false;
        }
        if (appeared.length===1 && (completedStarter||investmentBaseline===null&&investmentCounts.size===1 || investmentBaseline!==null&&investmentBaseline.size===0)) {
          firstInvestment={rawcode:appeared[0][0],tier:investmentUnits.get(appeared[0][0]).tier,evidence:completedStarter?"first_completed_early_story_unit":investmentBaseline===null?"single_advanced_unit_at_attach":"first_observed_advanced_unit_creation",observedAt:state.snapshot.timestamp};
          starterPhaseSeen=false;
          try { root.localStorage?.setItem(storageKey,JSON.stringify(firstInvestment)); } catch {}
          if(!manualDeckChoice)panel.setSettings({preferredDeckId:null},{notify:false});
        }
      }
      investmentBaseline=investmentCounts;
      const detected=state.snapshot.elder;
      const elderId=detected&&["nasujuro","warcury","saturn"].includes(detected.id)?detected.id:"unknown";
      if(panel.getSettings().boss!==elderId)panel.setSettings({boss:elderId},{notify:false});
      const resource=state.snapshot.resources;
      const resourceConfirmed=resource?.status==="confirmed"&&Number.isInteger(state.snapshot.localPlayer)&&resource.localPlayer===state.snapshot.localPlayer;
      const amount=value=>resourceConfirmed&&Number.isSafeInteger(value)&&value>=0?value:null;
      const rewardBudget=readRewardBudget(),round=Number.isSafeInteger(rewardBudget.round)&&rewardBudget.round>=0?rewardBudget.round:null;
      if(panel.getSettings().round!==round)panel.setSettings({round},{notify:false});
      const settings = {...panel.getSettings(),wood:amount(resource?.wood),gold:amount(resource?.gold),firstInvestmentRawcode:firstInvestment?.rawcode||null,firstInvestmentEvidence:firstInvestment?.evidence||null,firstInvestmentAmbiguous:investmentAmbiguous,
        firstLegendRawcode:firstInvestment?.tier==="전설"?firstInvestment.rawcode:null,firstLegendEvidence:firstInvestment?.tier==="전설"?(firstInvestment.evidence==="single_advanced_unit_at_attach"?"single_legend_at_attach":"first_observed_legend_creation"):null,manualDeckChoice,manualDeckChoiceRevision,
        craftRunning:state.craftRunning===true,activeCraftTarget:state.activeCraftTarget||null,activeCraftExpectedResults:Array.isArray(state.activeCraftExpectedResults)?state.activeCraftExpectedResults:[],protectedCounts:Object.fromEntries(Object.entries(state.protectedCounts||{}).sort(([a],[b])=>a.localeCompare(b))),items:readItems(),rewardBudget,randomHistory:readRandomHistory(),localPlayer:state.snapshot.localPlayer,itemRequirementsSupported:state.capabilities?.itemRequirementsSupported===true,readableItemRawcodes:Array.from(state.capabilities?.readableItemRawcodes||[]).sort(),recipeConditions:readRecipeConditions(),recipeConditionsSupported:state.capabilities?.recipeConditionsSupported===true,specialRecipeConditionsSupported:state.capabilities?.specialRecipeConditionsSupported===true,readableConditionTypes:readableRecipeConditions().sort()};
      const supported = Array.from(state.capabilities?.supportedRecipeIds || []).sort();
      const semanticSettings={...settings};delete semanticSettings.wood;delete semanticSettings.gold;
      // 자동으로 표시한 목표 ID는 입력 변화가 아니다. 수동 선택과 revision은 계속 요청 키에 남긴다.
      if(settings.manualDeckChoice===false)delete semanticSettings.preferredDeckId;
      const semanticKey=JSON.stringify([session,Array.from(inventory).sort((a,b)=>a[0].localeCompare(b[0])),semanticSettings,supported,state.capabilities?.choiceWispSupported===true]);
      const ceilings={wood:null,gold:null};
      if(result?.targetDeck&&result.searchComplete===true&&!result.reinforcements){
        for(const kind of ["wood","gold"]){
          const values=[];let complete=true;
          for(const deck of [result.targetDeck,...(result.alternatives||[])]){
            const cost=deck.planning?.forecast?.available?deck.planning.forecast:deck.planning?.current;
            if(!Number.isFinite(cost?.[kind])){complete=false;break;}values.push(cost[kind]);
            for(const goal of deck.goals||[])for(const requirement of goal.progress?.resourceRequirements||[])if(requirement.type===kind.toUpperCase()&&Number.isFinite(requirement.count))values.push(requirement.count);
          }
          if(complete&&values.length)ceilings[kind]=Math.max(...values);
        }
      }
      const keyBudget=Object.fromEntries(["wood","gold"].map(kind=>[kind,Number.isFinite(settings[kind])&&ceilings[kind]!==null?Math.min(settings[kind],ceilings[kind]):settings[kind]]));
      const key = JSON.stringify([semanticKey,keyBudget,
        supported, state.capabilities?.choiceWispSupported === true]);
      if (!force && key === signature) return;
      const safeGrowth=!force&&semanticKey===lastSemanticKey&&lastBudget&&["wood","gold"].every(kind=>Number.isFinite(settings[kind])&&Number.isFinite(lastBudget[kind])&&settings[kind]>=lastBudget[kind]);
      // 같은 재고에서 돈만 늘면 진행 중인 유효 계산을 버리지 않고 최신 자원 재평가를 한 번으로 합친다.
      if(busy&&safeGrowth){resourceRefreshPending=true;return;}
      blockingRecompute=!safeGrowth;lastSemanticKey=semanticKey;lastBudget={wood:settings.wood,gold:settings.gold};
      signature = key; revision++;
      const request = {type:"recommend",revision,counts:Object.fromEntries(inventory),
        settings:{...settings,sessionIdentity:session,supportedRecipeIds:supported.length ? supported : undefined,
          allowChoiceWisps:state.capabilities?.choiceWispSupported === true}};
      pending = request;
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        timer = null;
        if (failed && !force) { pending = null; show("추천을 다시 계산해 주세요."); return; }
        if (initialize()) flush();
      }, 150);
      show();
    }
    function loadCombat() {
      loading = true; show();
      Promise.all(["/data/nightmare-combat.json","/data/nightmare-rules.json","/data/nightmare-clear-records.json"].map(url=>
        fetch(url,{cache:"no-store"}).then(response=>{if(!response.ok)throw new Error("combat_profile_unavailable");return response.json();})
      ).concat(["/data/nightmare-rewards.json","/data/nightmare-random-drops.json","/data/nightmare-unit-combat-models.json","/data/nightmare-combat-scenarios.json","/data/nightmare-combat-record-evidence.json","/data/nightmare-story-rankings.json"].map(url=>fetch(url,{cache:"no-store"}).then(response=>response.ok?response.json():null).catch(()=>null)))).then(([data,rules,records,rewards,drops,unitModels,scenarios,recordEvidence,storyData]) => {
        if (destroyed) return;
        loading = false; combat = data; mechanics=rules;if(!activeRecordsRevision||clearRecords?.catalogMapHash!==records.catalogMapHash){clearRecords=records;activeRecordsRevision=null;}if(!baseRecordIds.length)baseRecordIds=(records.records||[]).map(record=>record.id);rewardData=rewards; randomDrops=drops; combatValidation={unitModels,scenarios,recordEvidence};storyRankings=storyData; failed = false; signature = "";startRecordPolling();schedule(true);
      }).catch(() => {
        if (destroyed) return;
        loading = false; failed = true; pending = null; result = null;
        show("추천 정보를 불러오지 못했어요. 다시 계산해 주세요.");
      });
    }
    loadCombat();
    const staleTimer = setInterval(() => { if (state && !connected() && signature !== "disconnected") schedule(false); }, 750);
    return {
      update(next) {
        const changed=state?.craftRunning!==next.craftRunning||state?.craftStopRequested!==next.craftStopRequested||state?.capabilities?.stopCraftSupported!==next.capabilities?.stopCraftSupported||state?.craftMessage!==next.craftMessage||state?.craftTone!==next.craftTone||state?.capabilities?.protectedCountsSupported!==next.capabilities?.protectedCountsSupported||state?.capabilities?.exactPlanSupported!==next.capabilities?.exactPlanSupported;
        state = next;startRecordPolling();if(!applyRecordUpdate())schedule(false);if(changed)show();
      },
      setExpanded:open => panel.setExpanded(open),
      getCraftContext() {
        const early=result?.phase==="early_story"&&Array.isArray(result.earlyStory?.candidates);
        if(!connected()||(!result?.targetDeck&&!early)||recordUpdateInFlight||pendingRecordsSnapshot&&!state?.craftRunning||(busy||pending)&&blockingRecompute||failed)return null;
        const automaticProtectedCounts={};
        const advanced=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]);
        for(const unit of state.catalog.units||[])if(unit.playable===true&&advanced.has(unit.tier)&&(state.counts.get(unit.rawcode)||0)>0)automaticProtectedCounts[unit.rawcode]=1;
        for(const target of early?[]:[...(result.goalCraftTargets||[]),...(result.craftTargets||[])])for(const [code,count]of Object.entries(target.protectedCounts||{}))
          if(Number.isSafeInteger(count)&&count>0)automaticProtectedCounts[code]=Math.max(automaticProtectedCounts[code]||0,count);
        for(const goal of early?[]:result.targetDeck.goals||[]){const count=Math.min(Number(goal.count)||1,state.counts.get(goal.rawcode)||0);if(count>0)automaticProtectedCounts[goal.rawcode]=Math.max(automaticProtectedCounts[goal.rawcode]||0,count);}
        return {deckId:early?"early-story":result.targetDeck.id,automaticProtectedCounts,explicitProtectedCounts:{...(state.protectedCounts||{})}};
      },
      destroy() { destroyed = true; clearInterval(staleTimer);if(recordPollTimer)clearInterval(recordPollTimer);recordFetchAbort?.abort();pendingRecordsSnapshot=null;if (timer) clearTimeout(timer); stopWorker(); panel.destroy(); }
    };
  }
  root.OrdNightmareIntegration = {create};
})(globalThis);
