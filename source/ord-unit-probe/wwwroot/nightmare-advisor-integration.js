(function (root) {
  "use strict";
  const evidenceHash=value=>typeof value==="string"&&/^[a-f0-9]{64}$/.test(value);
  const combatInventories = new WeakMap();
  const classic2331Hash = "20ce1b0918e56d18ad02a11a6dddf05298d6478037721f20a29702d4cd7b145d";
  function configureCombatInventory(catalog,data) {
    combatInventories.delete(catalog);
    if (catalog?.map?.sha256!==classic2331Hash||data?.schemaVersion!==1||data.mapSha256!==classic2331Hash||
        !Array.isArray(data.temporaryRawcodes)||!data.canonicalByRawcode||typeof data.canonicalByRawcode!=="object"||Array.isArray(data.canonicalByRawcode)) return null;
    const units=new Map((catalog.units||[]).map(unit=>[unit.rawcode,unit]));
    const recipeMaterials=new Set(),recipeResults=new Set();
    for(const recipe of catalog.recipes||[]){
      recipeResults.add(recipe.resultUnitRawcode);
      for(const code of recipe.casterUnitRawcodes||[])recipeMaterials.add(code);
      for(const item of recipe.ingredients||[]){
        if(item.type!=="UNIT")continue;
        recipeMaterials.add(item.rawcode);
        recipeMaterials.add(item.logicalRawcode);
        for(const code of item.acceptedRawcodes||[])recipeMaterials.add(code);
      }
    }
    const progressSources=new Set(),progressResults=new Set();
    for(const transform of catalog.progressTransformations?.entries||[]){
      progressSources.add(transform.sourceRawcode);
      progressResults.add(transform.resultRawcode);
    }
    const temporary=new Set(data.temporaryRawcodes);
    const aliases=new Map(Object.entries(data.canonicalByRawcode));
    if(temporary.size!==data.temporaryRawcodes.length||
       [...temporary].some(code=>!units.has(code)||recipeMaterials.has(code)||recipeResults.has(code)||progressSources.has(code)||progressResults.has(code))||
       [...aliases].some(([form,canonical])=>units.get(form)?.playable!==true||units.get(canonical)?.playable!==true||
         recipeMaterials.has(form)||recipeMaterials.has(canonical)||recipeResults.has(form)||progressSources.has(form)||
         progressResults.has(form)||progressResults.has(canonical)||
         temporary.has(form)||temporary.has(canonical)||aliases.has(canonical)))return null;
    const equivalent=new Set([...aliases.keys(),...aliases.values()]);
    const identity=Object.freeze({
      workerMetadata:Object.freeze({schemaVersion:1,mapSha256:classic2331Hash,
        canonicalByRawcode:Object.freeze(Object.fromEntries(aliases))}),
      hide:code=>temporary.has(code)||aliases.has(code),
      normalize(counts){
        const result=new Map();
        for(const [code,count] of counts){
          if(temporary.has(code))continue;
          const canonical=aliases.get(code)||code;
          const total=(result.get(canonical)||0)+count;
          if(!Number.isSafeInteger(total))return null;
          result.set(canonical,total);
        }
        return result;
      },
      nativeProtection(counts){
        // Neither side of these identity pairs can be selected or consumed by a recipe.
        // Keeping a specific form in a native request would abort on a harmless combat transition.
        return counts&&Object.fromEntries(Object.entries(counts).filter(([code])=>!equivalent.has(code)));
      }
    });
    combatInventories.set(catalog,identity);
    return identity;
  }
  const combatInventory=catalog=>combatInventories.get(catalog)||null;
  const nativeRunId=value=>typeof value==="string"&&/^auto-\d{8}T\d{13}Z-[a-f0-9]{16}$/.test(value);
  const nativeRoster=rows=>Array.isArray(rows)?rows.map(row=>[row?.rawcode,row?.count]).sort(([left],[right])=>String(left)<String(right)?-1:String(left)>String(right)?1:0):null;
  function validNativeRecord(record,catalog){
    const proof=record?.localNativeProof,version=record?.version,units=new Set((catalog?.units||[]).map(unit=>unit.rawcode)),mapHash=catalog?.map?.sha256;
    if(record?.sourceProvider!=="local-native"||record.sourceOutcome!=="local_native_victory_confirmed"||record.id!=="local-"+proof?.canonicalRunId||record.sourceUrl!=null||!Array.isArray(record.sourceUrls)||record.sourceUrls.length||!Array.isArray(record.sourceRecordIds)||record.sourceRecordIds.length!==1||record.sourceRecordIds[0]!==record.id||record.rosterComplete!==true||record.fullRosterCountsVerified!==true)return false;
    if(proof?.schemaVersion!==1||proof.kind!=="verified_native_clear"||!nativeRunId(proof.canonicalRunId)||![proof.automaticRunSha256,proof.clearRecordSha256,proof.finalInventorySha256,proof.catalogMapHash].every(evidenceHash)||proof.catalogMapHash!==mapHash||proof.nativeVictoryConfirmed!==true||proof.terminalRound!==65||!Array.isArray(proof.roster)||!proof.roster.length)return false;
    const mapVersion=String(catalog.map?.version||"").match(/^(\d+\.\d{3})(?:\[([^\]]+)\])?$/);
    if(!mapVersion||proof.versionNumber!==mapVersion[1]||proof.variant!==(mapVersion[2]||null)||version?.number!==proof.versionNumber||version.variant!==proof.variant||version.numericVerified!==true||version.sourceNumericVersionVerified!==true||version.source!=="verified_native_loaded_map"||version.assumedByUser===true||version.datePolicyApplied===true)return false;
    const terminal=Date.parse(proof.terminalObservedAt),active=Date.parse(proof.lastActiveObservedAt);
    if(!Number.isFinite(terminal)||!Number.isFinite(active)||terminal<active||terminal-active>2000||!Array.isArray(record.entries)||!Array.isArray(record.goals))return false;
    const validRows=rows=>rows.length<=512&&rows.every(row=>units.has(row?.rawcode)&&Number.isSafeInteger(row.count)&&row.count>0&&row.count<=1000000)&&new Set(rows.map(row=>row.rawcode)).size===rows.length;
    if(!validRows(proof.roster)||!validRows(record.goals))return false;
    const entries=record.entries.filter(row=>!["context_only","excluded_summon"].includes(row?.status));
    if(!validRows(entries)||JSON.stringify(nativeRoster(proof.roster))!==JSON.stringify(nativeRoster(record.goals))||JSON.stringify(nativeRoster(proof.roster))!==JSON.stringify(nativeRoster(entries)))return false;
    return entries.every(row=>{const quantity=row.quantityEvidence;return row.countVerified===true&&row.countMeaning==="verified_native_quantity"&&quantity?.kind==="verified_native_last_active_quantity"&&quantity.sourceProvider==="local-native"&&quantity.sourceCountVerified===true&&quantity.canonicalRunId===proof.canonicalRunId&&quantity.automaticRunSha256===proof.automaticRunSha256&&quantity.finalInventorySha256===proof.finalInventorySha256&&quantity.catalogMapHash===mapHash&&quantity.rawcode===row.rawcode&&quantity.count===row.count;});
  }
  function sameNativeRun(left,right){
    const a=left.localNativeProof,b=right.localNativeProof;
    return a.canonicalRunId===b.canonicalRunId&&a.automaticRunSha256===b.automaticRunSha256&&a.catalogMapHash===b.catalogMapHash&&a.versionNumber===b.versionNumber&&a.variant===b.variant&&Date.parse(a.terminalObservedAt)===Date.parse(b.terminalObservedAt)&&Date.parse(a.lastActiveObservedAt)===Date.parse(b.lastActiveObservedAt)&&JSON.stringify(nativeRoster(a.roster))===JSON.stringify(nativeRoster(b.roster));
  }
  function mergeLocalRecords(publicData,localData,catalog){
    if(publicData?.schemaVersion!==1||publicData.catalogMapHash!==catalog?.map?.sha256||!Array.isArray(publicData.records)||localData?.schemaVersion!==1||localData.catalogMapHash!==publicData.catalogMapHash||!Array.isArray(localData.records)||localData.records.length>50000)throw new TypeError("local_record_snapshot_invalid");
    const records=publicData.records.slice(),ids=new Map(records.map(record=>[record.id,record])),runs=new Map(),seen=new Set();
    for(const record of records)if(record.sourceProvider==="local-native"&&validNativeRecord(record,catalog)){const id=record.localNativeProof.canonicalRunId;if(runs.has(id))throw new TypeError("local_record_duplicate_source");runs.set(id,record);}
    for(const record of localData.records.slice().sort((a,b)=>String(a?.id)<String(b?.id)?-1:String(a?.id)>String(b?.id)?1:0)){
      if(!validNativeRecord(record,catalog)||seen.has(record.localNativeProof.canonicalRunId))throw new TypeError("local_record_proof_invalid");
      const id=record.localNativeProof.canonicalRunId;seen.add(id);
      const existing=runs.get(id)||ids.get(record.id);
      if(existing){if(!validNativeRecord(existing,catalog)||!sameNativeRun(existing,record))throw new TypeError("local_record_source_conflict");continue;}
      records.push(record);ids.set(record.id,record);runs.set(id,record);
    }
    if(records.length>50000)throw new TypeError("local_record_snapshot_limit");
    return records.length===publicData.records.length?publicData:{...publicData,records};
  }
  function create(options) {
    let state = null, combat = null, mechanics = null, clearRecords = null, rewardData = null, randomDrops = null, combatValidation = null, storyRankings = null, worker = null, ready = false, busy = false;
    let revision = 0, signature = "", session = "", initializedMap = "", loading = false;
    let timer = null, timeout = null, pending = null, result = null, failed = false, destroyed = false;
    let firstInvestment = null, investmentBaseline = null, investmentAmbiguous = false, starterPhaseSeen = false, manualDeckChoice = false, manualDeckChoiceRevision = 0, lastSemanticKey = "", lastBudget = null, blockingRecompute = true, resourceRefreshPending = false;
    let activeRecordsRevision=null,pendingRecordsSnapshot=null,recordUpdateInFlight=null,recordAckTimeout=null,recordPollTimer=null,recordFetchAbort=null,recordFetchBusy=false;
    let baseRecordIds=[],recommendInFlight=null,historicalRecords=null;
    let publicRecordsData=null,publicRecordsRevision=null,localRecordsData=null,localRecordsRevision=null;
    let reinforcementRole="auto";
    let priorityAction=null,lastOverlayMessage="";
    const overlayBridge=root.chrome?.webview;
    let overlayActionSerial=0,overlayActionKey="",overlayAction=null,activeOverlayDispatchId=null;
    function validLiveRecords(data,manifest,requireBase=true){
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
      return !requireBase||baseRecordIds.every(id=>ids.has(id));
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
    async function fetchRecordSnapshot(local,controller){
      const manifestUrl=local?"/api/local-clear-records/manifest":"/nightmare-records-live-manifest.json";
      const response=await fetch(manifestUrl,{cache:"no-store",signal:controller.signal});if(!response.ok)return null;
      const manifest=await response.json(),mapHash=state?.catalog?.map?.sha256;
      const expectedUrl=local?"/api/local-clear-records/"+manifest.revision+".json":"/nightmare-records-live-"+manifest.revision+".json";
      if(manifest?.schemaVersion!==1||manifest.catalogMapHash!==mapHash||!evidenceHash(manifest.revision)||!Number.isSafeInteger(manifest.recordCount)||manifest.recordCount<(local?0:Math.max(1,baseRecordIds.length))||manifest.recordCount>50000||manifest.dataUrl!==expectedUrl)return null;
      if(manifest.revision===(local?localRecordsRevision:publicRecordsRevision))return null;
      const payload=await fetch(manifest.dataUrl,{cache:"no-store",signal:controller.signal});if(!payload.ok)return null;
      const bytes=await payload.arrayBuffer();if(bytes.byteLength>67108864)return null;
      const digest=Array.from(new Uint8Array(await root.crypto.subtle.digest("SHA-256",bytes)),value=>value.toString(16).padStart(2,"0")).join("");if(digest!==manifest.revision)return null;
      const data=JSON.parse(new TextDecoder("utf-8",{fatal:true}).decode(bytes));if(!validLiveRecords(data,manifest,!local))return null;
      if(local&&!data.records.every(record=>validNativeRecord(record,state.catalog)))return null;
      return {revision:manifest.revision,data};
    }
    async function pollLiveRecords(){
      if(destroyed||recordFetchBusy||!clearRecords||!state?.catalog||!root.crypto?.subtle||typeof AbortController==="undefined")return;
      recordFetchBusy=true;const controller=new AbortController();recordFetchAbort=controller;
      const abortTimer=setTimeout(()=>controller.abort(),10000);
      try{
        const updates=await Promise.allSettled([fetchRecordSnapshot(false,controller),fetchRecordSnapshot(true,controller)]);
        if(destroyed)return;
        const publicUpdate=updates[0].status==="fulfilled"?updates[0].value:null,localUpdate=updates[1].status==="fulfilled"?updates[1].value:null;
        const publicData=publicUpdate?.data||publicRecordsData||clearRecords,localData=localUpdate?mergeLocalRecords(localRecordsData||{schemaVersion:1,catalogMapHash:publicData.catalogMapHash,records:[]},localUpdate.data,state.catalog):localRecordsData;
        const data=localData?mergeLocalRecords(publicData,localData,state.catalog):publicData;
        let nextRevision=publicUpdate?.revision??publicRecordsRevision;
        if(data!==publicData)nextRevision=Array.from(new Uint8Array(await root.crypto.subtle.digest("SHA-256",new TextEncoder().encode(JSON.stringify(data)))),value=>value.toString(16).padStart(2,"0")).join("");
        if(destroyed||data.catalogMapHash!==state?.catalog?.map?.sha256)return;
        publicRecordsData=publicData;if(publicUpdate)publicRecordsRevision=publicUpdate.revision;
        localRecordsData=localData;if(localUpdate)localRecordsRevision=localUpdate.revision;
        if(!nextRevision||[activeRecordsRevision,pendingRecordsSnapshot?.revision,recordUpdateInFlight?.revision].includes(nextRevision))return;
        pendingRecordsSnapshot={revision:nextRevision,data};
        if(!state?.craftRunning){revision++;pending=null;blockingRecompute=true;}
        applyRecordUpdate();show();
      }catch{}finally{clearTimeout(abortTimer);recordFetchBusy=false;if(recordFetchAbort===controller)recordFetchAbort=null;}
    }
    function startRecordPolling(){
      if(recordPollTimer||!state?.catalog||clearRecords?.catalogMapHash!==state.catalog.map?.sha256||!root.crypto?.subtle||typeof AbortController==="undefined")return;
      recordPollTimer=setInterval(pollLiveRecords,20000);pollLiveRecords();
    }
    const panel = root.OrdNightmarePanel.create({
      host:options.host, onOpenUnit:options.onOpenUnit, onShowRecipe:options.onShowRecipe, formatUnitName:options.formatUnitName,
      onStopCraft:()=>{if(state?.craftRunning&&state?.capabilities?.stopCraftSupported===true)return options.onStopCraft?.();},
      onCraftTarget:(code,goalRawcode,origin,actionOptions)=>{
        if(!connected()||state?.craftRunning||recordUpdateInFlight||pendingRecordsSnapshot||(busy||pending)&&blockingRecompute||state?.capabilities?.protectedCountsSupported!==true||state?.capabilities?.exactPlanSupported!==true)return;
        const overlayActionId=actionOptions?.overlayActionId;
        if(overlayActionId!=null&&overlayActionId!==activeOverlayDispatchId)return;
        const sourceOptions=overlayActionId?{overlayActionId}:undefined;
        if(goalRawcode&&code!==goalRawcode)return;
        if(origin==="reinforcement"){
          const target=(result?.reinforcements?.craftTargets||[]).find(item=>item.rawcode===code&&item.towardRawcode===goalRawcode);
          if(validReinforcementTarget(target,code,goalRawcode))return options.onCraftTarget?.(code,target,sourceOptions);
          return;
        }
        if(origin!=null)return;
        if(result?.phase==="early_story"&&!(result.earlyStory?.candidates||[]).some(candidate=>candidate.rawcode===code))return;
        if(result?.targetDeck?.assessment?.compositionStatus==="incomplete"||result?.targetDeck?.acquisitionPlan?.sharedChoiceFeasible===false)return;
        const target=goalRawcode?(Array.isArray(result?.goalCraftTargets)?result.goalCraftTargets.find(item=>item.towardRawcode===goalRawcode&&item.rawcode===code&&item.isWholeGoal!==false&&item.available===true&&item.resourcesReady!==false):result?.craftTargets?.find(item=>item.rawcode===code&&item.isWholeGoal!==false&&item.resourcesReady!==false)):result?.craftTargets?.find(item=>item.rawcode===code&&item.resourcesReady!==false);
        if(target&&target.protectedCounts&&target.expectedSteps?.length)return options.onCraftTarget?.(code,target,sourceOptions);
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
    function publishOverlay() {
      const bridge=overlayBridge;
      if(!bridge)return;
      const snapshot=state?.snapshot;
      const card=!destroyed&&connected()&&(!failed||priorityAction||state.craftRunning)&&snapshot.progression?.gameEnded!==true?panel.overlayRecommendation():null;
      const semanticKey=card?.action?JSON.stringify([session,snapshot.processId,snapshot.sessionIdentity,revision,activeRecordsRevision,card.action,card.actionKey]):"";
      if(semanticKey!==overlayActionKey){
        overlayActionKey=semanticKey;
        overlayAction=semanticKey?{id:"overlay-"+(++overlayActionSerial),action:card.action,key:card.actionKey}:null;
      }
      const message=card?{type:"recommendation-overlay",processId:snapshot.processId,timestamp:snapshot.timestamp,
        title:card.title,detail:card.detail,percent:card.percent??null,action:card.action??null,actionId:overlayAction?.id??null}:{type:"recommendation-overlay"};
      const key=JSON.stringify(message);
      if(key===lastOverlayMessage)return;
      try{bridge.postMessage(message);lastOverlayMessage=key;}catch{}
    }
    function onOverlayAction(event){
      const message=event.data;
      if(destroyed||message?.type!=="recommendation-overlay-action"||!overlayAction||message.actionId!==overlayAction.id||!connected()||state.snapshot.progression?.gameEnded===true)return;
      const card=panel.overlayRecommendation(),action=overlayAction;
      const currentKey=card?.action?JSON.stringify([session,state.snapshot.processId,state.snapshot.sessionIdentity,revision,activeRecordsRevision,card.action,card.actionKey]):"";
      if(currentKey!==overlayActionKey)return;
      overlayAction=null;overlayActionKey="";activeOverlayDispatchId=action.id;
      try{panel.requestOverlayAction(action.action,action.key,action.id);}
      finally{activeOverlayDispatchId=null;publishOverlay();}
    }
    overlayBridge?.addEventListener?.("message",onOverlayAction);
    function show(error) {
      panel.update({catalog:state?.catalog,result:connected() ? result : null, connected:connected(), priorityAction, calculating:connected() && (loading || Boolean(recordUpdateInFlight)||Boolean(pendingRecordsSnapshot)&&!state?.craftRunning || (busy || Boolean(pending)) && (blockingRecompute || !result)),
        error:error || null,craftRunning:Boolean(state?.craftRunning),craftStopRequested:Boolean(state?.craftStopRequested),craftCanStop:state?.capabilities?.stopCraftSupported===true,craftMessage:state?.craftMessage||"",
        craftTone:state?.craftTone||"",craftAvailable:state?.capabilities?.enabled===true&&Boolean(state?.capabilities?.token)&&state?.capabilities?.protectedCountsSupported===true&&state?.capabilities?.exactPlanSupported===true,
        elder:state?.snapshot?.elder||null,items:readItems(),rewardBudget:readRewardBudget(),randomHistory:readRandomHistory(),localPlayer:state?.snapshot?.localPlayer,itemCraftAvailable:state?.capabilities?.itemRequirementsSupported===true,readableItemRawcodes:Array.from(state?.capabilities?.readableItemRawcodes||[]),recipeConditions:readRecipeConditions(),recipeConditionsAvailable:state?.capabilities?.recipeConditionsSupported===true,specialRecipeConditionsAvailable:state?.capabilities?.specialRecipeConditionsSupported===true,readableConditionTypes:readableRecipeConditions()});
      publishOverlay();
      if(typeof options.onUiState==="function"){
        const card=panel.overlayRecommendation(),hasGoal=Boolean(result?.targetDeck||result?.earlyStory);
        const calculating=Boolean(connected()&&(loading||recordUpdateInFlight||pendingRecordsSnapshot&&!state?.craftRunning||(busy||pending)&&(blockingRecompute||!result)));
        options.onUiState({scope:"advisor",enabled:card?.action==="craft"?1:0,total:hasGoal||card?.action==="craft"?1:0,
          connected:connected(),craftRunning:Boolean(state?.craftRunning),calculating,blocked:{}});
      }
      options.onCraftContextChange?.();
    }
    function initialize() {
      if (!combat || !mechanics || !clearRecords || !state?.catalog || destroyed) return false;
      const hash = state.catalog.map?.sha256;
      const nativeData=combat.map?.sha256===hash&&mechanics.map?.sha256===hash&&clearRecords.catalogMapHash===hash;
      if(!nativeData&&historicalRecords?.catalogMapHash===hash&&historicalRecords.projection?.kind==="historical_roster_identity"&&clearRecords.catalogMapHash!==hash){
        clearRecords=publicRecordsData=historicalRecords;
        baseRecordIds=historicalRecords.records.map(record=>record.id);
        activeRecordsRevision=publicRecordsRevision=null;
      }
      if (!nativeData && (clearRecords.catalogMapHash!==hash||clearRecords.projection?.kind!=="historical_roster_identity")) {
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
      const inventory=combatInventory(state.catalog);
      const workerCatalog=inventory?{...state.catalog,combatInventory:inventory.workerMetadata}:state.catalog;
      try { worker.postMessage({type:"init", catalog:workerCatalog, combat:nativeData?combat:{},rules:nativeData?mechanics:{},clearRecords,storyRankings,
        ...(nativeData?{randomDrops,combatValidation}:{}),recordsRevision:activeRecordsRevision}); }
      catch {
        stopWorker(); pending = null; result = null; failed = true;
        show("추천을 불러오지 못했어요. 다시 계산해 주세요."); return false;
      }
      startRecordPolling();
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
      const excludedSummons=new Set(["h076","h04T","h04Y","h05D","h0CA"]);
      for(const recipe of state.catalog.recipes||[]){if(typeof recipe.resultUnitRawcode==="string")referencedCodes.add(recipe.resultUnitRawcode);for(const item of recipe.ingredients||[])if(item.type==="UNIT"&&typeof item.rawcode==="string")referencedCodes.add(item.rawcode);}
      const relevantTransient=code=>!excludedSummons.has(code)&&(!combatTransientCodes.has(code)||referencedCodes.has(code));
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
      const rewardBudget=readRewardBudget();
      const classicProgression=root.OrdNightmarePanel.confirmedClassicProgression?.(state.catalog,state.snapshot);
      const round=Number.isSafeInteger(rewardBudget.round)&&rewardBudget.round>=0?rewardBudget.round:classicProgression?.round??null;
      const materialStarter=Boolean(classicProgression&&classicProgression.round<30&&classicProgression.storyIndex<14);
      if(panel.getSettings().round!==round)panel.setSettings({round},{notify:false});
      const settings = {...panel.getSettings(),wood:amount(resource?.wood),gold:amount(resource?.gold),firstInvestmentRawcode:firstInvestment?.rawcode||null,firstInvestmentEvidence:firstInvestment?.evidence||null,firstInvestmentAmbiguous:investmentAmbiguous,
        firstLegendRawcode:firstInvestment?.tier==="전설"?firstInvestment.rawcode:null,firstLegendEvidence:firstInvestment?.tier==="전설"?(firstInvestment.evidence==="single_advanced_unit_at_attach"?"single_legend_at_attach":"first_observed_legend_creation"):null,manualDeckChoice,manualDeckChoiceRevision,materialStarter,
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
      ).concat(["/data/nightmare-rewards.json","/data/nightmare-random-drops.json","/data/nightmare-unit-combat-models.json","/data/nightmare-combat-scenarios.json","/data/nightmare-combat-record-evidence.json","/data/nightmare-story-rankings.json","/data/nightmare-historical-records.json"].map(url=>fetch(url,{cache:"no-store"}).then(response=>response.ok?response.json():null).catch(()=>null)))).then(([data,rules,records,rewards,drops,unitModels,scenarios,recordEvidence,storyData,history]) => {
        if (destroyed) return;
        historicalRecords=history?.schemaVersion===1&&Array.isArray(history.records)&&history.records.length?history:null;
        loading = false; combat = data; mechanics=rules;if(!publicRecordsData||publicRecordsData.catalogMapHash!==records.catalogMapHash){publicRecordsData=records;publicRecordsRevision=null;localRecordsData=null;localRecordsRevision=null;clearRecords=records;activeRecordsRevision=null;baseRecordIds=(records.records||[]).map(record=>record.id);}rewardData=rewards; randomDrops=drops; combatValidation={unitModels,scenarios,recordEvidence};storyRankings=storyData; failed = false; signature = "";startRecordPolling();schedule(true);
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
        const inventory=combatInventory(next.catalog);
        state=inventory&&next.counts instanceof Map?{...next,counts:inventory.normalize(next.counts)}:next;
        const action=connected()?root.OrdNightmarePanel.progressionAction?.(state.catalog,state.snapshot,state.counts)??null:null;
        const priorityChanged=priorityAction?.title!==action?.title||priorityAction?.detail!==action?.detail;
        priorityAction=action;
        startRecordPolling();if(!applyRecordUpdate())schedule(false);if(changed||priorityChanged)show();else publishOverlay();
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
      destroy() { destroyed = true;overlayBridge?.removeEventListener?.("message",onOverlayAction);publishOverlay(); clearInterval(staleTimer);if(recordPollTimer)clearInterval(recordPollTimer);recordFetchAbort?.abort();pendingRecordsSnapshot=null;if (timer) clearTimeout(timer); stopWorker(); panel.destroy(); }
    };
  }
  root.OrdNightmareIntegration = {create,validNativeRecord,mergeLocalRecords,configureCombatInventory,combatInventory};
})(globalThis);
