const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const source=fs.readFileSync(path.join(__dirname,'../wwwroot/nightmare-advisor-integration.js'),'utf8'),Records=require('../wwwroot/nightmare-records.js');
require('../wwwroot/nightmare-strategy-panel.js');
const {progressionAction}=globalThis.OrdNightmarePanel;
const hash='f'.repeat(64),catalog={map:{sha256:hash,version:'2.323[C]'},units:[{rawcode:'h001',tier:'흔함',playable:true},{rawcode:'F001',tier:'불멸',playable:true}]},row={rawcode:'h001',count:1};
const base={schemaVersion:1,catalogMapHash:hash,records:[{id:'public-base',entries:[row],goals:[row]}]},copy=value=>JSON.parse(JSON.stringify(value)),tests=[];
function local(number=1){
 const run='auto-20261028T1253254060176Z-'+String(number).padStart(16,'0'),id='local-'+run,proof={schemaVersion:1,kind:'verified_native_clear',canonicalRunId:run,automaticRunSha256:'a'.repeat(64),clearRecordSha256:'b'.repeat(64),finalInventorySha256:'c'.repeat(64),catalogMapHash:hash,versionNumber:'2.323',variant:'C',nativeVictoryConfirmed:true,terminalRound:65,terminalObservedAt:'2026-10-28T12:53:25.400Z',lastActiveObservedAt:'2026-10-28T12:53:25.150Z',roster:[{rawcode:'F001',count:1}]};
 const entry={rawcode:'F001',count:1,status:'exact_native_identity',sourceTier:'불멸',countVerified:true,countMeaning:'verified_native_quantity',quantityEvidence:{kind:'verified_native_last_active_quantity',sourceProvider:'local-native',sourceCountVerified:true,canonicalRunId:run,automaticRunSha256:proof.automaticRunSha256,finalInventorySha256:proof.finalInventorySha256,catalogMapHash:hash,rawcode:'F001',count:1}};
 return{id,sourceProvider:'local-native',sourceUrl:null,sourceUrls:[],sourceRecordIds:[id],sourceOutcome:'local_native_victory_confirmed',cohort:'nightmare-regular',version:{number:'2.323',variant:'C',numericVerified:true,sourceNumericVersionVerified:true,source:'verified_native_loaded_map',assumedByUser:false,datePolicyApplied:false},rosterComplete:true,fullRosterCountsVerified:true,localNativeProof:proof,entries:[entry],goals:[{rawcode:'F001',count:1}]};
}
const dataset=records=>({schemaVersion:1,catalogMapHash:hash,records});
function clock(){let now=100000,id=0;const timers=new Map();return{get now(){return now;},setTimeout(fn,ms){const key=++id;timers.set(key,{fn,at:now+ms});return key;},clearTimeout(key){timers.delete(key);},setInterval(fn,ms){const key=++id;timers.set(key,{fn,at:now+ms,interval:ms});return key;},clearInterval(key){timers.delete(key);},tick(ms){const end=now+ms;for(;;){const item=[...timers].filter(([,value])=>value.at<=end).sort((a,b)=>a[1].at-b[1].at)[0];if(!item)break;now=item[1].at;if(item[1].interval)item[1].at+=item[1].interval;else timers.delete(item[0]);item[1].fn();}now=end;},get size(){return timers.size;}};}
const cryptoJobs=new Set(),subtle={digest(...args){let pending;pending=crypto.webcrypto.subtle.digest(...args).finally(()=>cryptoJobs.delete(pending));cryptoJobs.add(pending);return pending;}};
async function settle(){for(let i=0;i<20;i++){if(cryptoJobs.size)await Promise.allSettled([...cryptoJobs]);await new Promise(resolve=>setImmediate(resolve));}}
function setup(options={}){
 const timer=clock(),workers=[],requests=[],updates=[],publicSource={enabled:options.publicEnabled!==false,data:options.publicData||base},nativeSource={enabled:options.localEnabled!==false,data:options.localData||dataset([])},panelSettings={boss:'unknown',direction:'all',preferredDeckId:null};
 class Worker{constructor(){this.messages=[];workers.push(this);}postMessage(message){this.messages.push(message);}emit(data){this.onmessage({data});}terminate(){this.terminated=true;}}
 const panel={update:value=>updates.push(value),overlayRecommendation:()=>options.overlay?.(updates.at(-1))||null,getSettings:()=>({...panelSettings}),setSettings:value=>Object.assign(panelSettings,value),setExpanded(){},destroy(){}};
 const response=async(source,isLocal,settings,manifest)=>{
   if(!source.enabled)return{ok:false};if(source.pending)return new Promise((resolve,reject)=>settings.signal.addEventListener('abort',()=>reject(Error('aborted'))));
   const bytes=Buffer.from(JSON.stringify(source.data)),revision=crypto.createHash('sha256').update(bytes).digest('hex'),prefix=isLocal?'/api/local-clear-records/':'/nightmare-records-live-';
   if(manifest)return{ok:true,json:async()=>({...{schemaVersion:1,revision,catalogMapHash:hash,recordCount:source.data.records.length,dataUrl:prefix+revision+'.json'},...source.manifest})};
   return{ok:true,arrayBuffer:async()=>Uint8Array.from(Buffer.from(source.corrupt?'corrupt':JSON.stringify(source.data))).buffer};
 };
 const fetch=async(url,settings)=>{requests.push({url,settings});if(url==='/api/local-clear-records/manifest')return response(nativeSource,true,settings,true);if(url.startsWith('/api/local-clear-records/'))return response(nativeSource,true,settings,false);if(url==='/nightmare-records-live-manifest.json')return response(publicSource,false,settings,true);if(url.startsWith('/nightmare-records-live-'))return response(publicSource,false,settings,false);return{ok:true,json:async()=>url==='/data/nightmare-clear-records.json'?base:{map:{sha256:hash},catalogMapHash:hash}};};
 const context=vm.createContext({crypto:{subtle},chrome:options.bridge?{webview:{postMessage:options.bridge}}:undefined,OrdNightmarePanel:{create:()=>panel,progressionAction:options.progressionAction||progressionAction},OrdNightmareRewardBudget:{derive:()=>({status:'unknown',round:null})},OrdNightmareRandomForecast:{normalizeHistory:()=>({status:'unknown'})},Worker,Map,Set,Object,Array,Number,String,JSON,Boolean,Uint8Array,TextDecoder,TextEncoder,AbortController,Date:{now:()=>timer.now,parse:Date.parse},fetch,setTimeout:timer.setTimeout,clearTimeout:timer.clearTimeout,setInterval:timer.setInterval,clearInterval:timer.clearInterval});vm.runInContext(source,context);
 const api=context.OrdNightmareIntegration,integration=api.create({host:{}}),state={catalog,counts:new Map([['h001',1]]),liveState:{tone:'live',checking:false},snapshot:{sessionIdentity:'automatic-local-clear',catalogMatchesLoadedArchive:true,loadedMapHash:hash,localPlayer:0},capabilities:{enabled:true,token:'fixture',protectedCountsSupported:true,exactPlanSupported:true,supportedRecipeIds:new Set()}};Object.defineProperty(state,'receivedAt',{get:()=>timer.now});integration.update(state);
 return{timer,workers,requests,updates,publicSource,nativeSource,integration,state,api,async start(){await settle();timer.tick(150);await settle();const worker=workers[0];assert(worker);worker.emit({type:'ready'});await settle();return worker;},complete(worker){const request=worker.messages.filter(message=>message.type==='recommend').at(-1);assert(request);worker.emit({type:'result',revision:request.revision,recordsRevision:worker.recordsRevision??worker.messages.find(message=>message.type==='init').recordsRevision,result:{selectedDeckId:'old',targetDeck:{id:'old',goals:[],assessment:{compositionStatus:'complete'}}}});},async poll(){timer.tick(20000);await settle();},ack(worker){const message=worker.messages.filter(row=>row.type==='records-update').at(-1);assert(message);worker.recordsRevision=message.recordsRevision;worker.emit({type:'records-ready',recordsRevision:message.recordsRevision});timer.tick(150);return message;}};
}
async function test(name,fn){try{await fn();tests.push({name,passed:true});}catch(error){tests.push({name,passed:false,error:error.stack});}}
(async()=>{
 await test('native records satisfy the existing provenance verifier before merging',()=>{const env=setup({localEnabled:false});const record=local();assert(Records.verifiedLocalSource(record,hash));assert(env.api.validNativeRecord(record,catalog));env.integration.destroy();});
 await test('startup merges a confirmed local clear without a separate publisher',async()=>{const env=setup({localData:dataset([local()])}),worker=await env.start(),init=worker.messages.find(message=>message.type==='init');assert.equal(init.clearRecords.records.length,2);assert(init.clearRecords.records.some(record=>record.id===local().id));assert.match(init.recordsRevision,/^[a-f0-9]{64}$/);env.integration.destroy();assert.equal(env.timer.size,0);});
 await test('the same raw run in public and native sources is counted once and retains its established copy',async()=>{const saved=local(),native=copy(saved);native.localNativeProof.clearRecordSha256='d'.repeat(64);native.localNativeProof.terminalObservedAt='2026-10-28T12:53:25.400+00:00';const env=setup({publicData:dataset([...base.records,saved]),localData:dataset([native])}),worker=await env.start(),init=worker.messages.find(message=>message.type==='init');assert.equal(init.clearRecords.records.length,2);assert.equal(init.clearRecords.records[1].localNativeProof.clearRecordSha256,saved.localNativeProof.clearRecordSha256);env.complete(worker);const count=worker.messages.filter(message=>message.type==='records-update').length;await env.poll();assert.equal(worker.messages.filter(message=>message.type==='records-update').length,count);env.integration.destroy();});
 await test('an unchanged public revision still polls and appends newly confirmed local clears',async()=>{const env=setup(),worker=await env.start();env.complete(worker);const before=env.requests.filter(row=>row.url.startsWith('/nightmare-records-live-')&&!row.url.includes('manifest')).length;env.nativeSource.data=dataset([local()]);await env.poll();const update=worker.messages.at(-1);assert.equal(update.type,'records-update');assert.equal(update.clearRecords.records.length,2);assert.equal(env.requests.filter(row=>row.url.startsWith('/nightmare-records-live-')&&!row.url.includes('manifest')).length,before);env.integration.destroy();});
 await test('public snapshot failure does not block native clear ingestion',async()=>{const env=setup({publicEnabled:false,localData:dataset([local()])}),worker=await env.start();assert.equal(worker.messages[0].clearRecords.records.length,2);env.integration.destroy();});
 await test('local HTTP failure preserves prior local records while public data advances',async()=>{const env=setup({localData:dataset([local()])}),worker=await env.start();env.complete(worker);env.nativeSource.enabled=false;env.publicSource.data=dataset([...base.records,{id:'public-next',entries:[row],goals:[row]}]);await env.poll();const update=worker.messages.at(-1);assert.equal(update.type,'records-update');assert.equal(update.clearRecords.records.length,3);assert(update.clearRecords.records.some(record=>record.id===local().id));env.integration.destroy();});
 await test('an empty successful native snapshot cannot retract previously verified local clears',async()=>{const env=setup({localData:dataset([local()])}),worker=await env.start();env.complete(worker);const count=worker.messages.filter(message=>message.type==='records-update').length;env.nativeSource.data=dataset([]);await env.poll();assert.equal(worker.messages.filter(message=>message.type==='records-update').length,count);env.integration.destroy();});
 await test('hash corruption, cross-map data, unknown units and incomplete native proofs keep last good data',async()=>{for(const kind of ['corrupt','map','unit','proof']){const env=setup(),worker=await env.start();env.complete(worker);env.nativeSource.data=dataset([local()]);if(kind==='corrupt')env.nativeSource.corrupt=true;if(kind==='map')env.nativeSource.manifest={catalogMapHash:'0'.repeat(64)};if(kind==='unit')env.nativeSource.data.records[0].goals[0].rawcode='xxxx';if(kind==='proof')env.nativeSource.data.records[0].localNativeProof.nativeVictoryConfirmed=false;await env.poll();assert(!worker.messages.some(message=>message.type==='records-update'),kind);assert.equal(env.updates.at(-1).result.selectedDeckId,'old');env.integration.destroy();}});
 await test('a conflicting raw hash for an existing run cannot replace its proven source',async()=>{const env=setup({localData:dataset([local()])}),worker=await env.start();env.complete(worker);const bad=local();bad.localNativeProof.automaticRunSha256='e'.repeat(64);bad.entries[0].quantityEvidence.automaticRunSha256=bad.localNativeProof.automaticRunSha256;env.nativeSource.data=dataset([bad,local(2)]);await env.poll();assert(!worker.messages.some(message=>message.type==='records-update'));env.integration.destroy();});
 await test('duplicate native run IDs reject the snapshot rather than inflating counts',async()=>{const env=setup({localData:dataset([local(),local()])}),worker=await env.start();assert.equal(worker.messages[0].clearRecords.records.length,1);env.integration.destroy();});
 await test('native updates defer during crafting and apply once idle with worker acknowledgement',async()=>{const env=setup(),worker=await env.start();env.complete(worker);env.state.craftRunning=true;env.integration.update(env.state);env.timer.tick(150);env.complete(worker);env.nativeSource.data=dataset([local()]);await env.poll();assert(!worker.messages.some(message=>message.type==='records-update'));assert.equal(env.updates.at(-1).result.selectedDeckId,'old');env.state.craftRunning=false;env.integration.update(env.state);assert.equal(worker.messages.at(-1).type,'records-update');assert.equal(env.integration.getCraftContext(),null);const update=env.ack(worker);assert.equal(worker.messages.at(-1).type,'recommend');assert.equal(worker.recordsRevision,update.recordsRevision);env.integration.destroy();});
 await test('failed native reads are retried and a later valid snapshot recovers',async()=>{const env=setup(),worker=await env.start();env.complete(worker);env.nativeSource.data=dataset([local()]);env.nativeSource.corrupt=true;await env.poll();assert(!worker.messages.some(message=>message.type==='records-update'));env.nativeSource.corrupt=false;await env.poll();assert.equal(worker.messages.at(-1).type,'records-update');env.integration.destroy();});
 await test('a new helper instance recovers the persisted native source on startup',async()=>{for(let i=0;i<2;i++){const env=setup({localData:dataset([local(),local(2)])}),worker=await env.start();assert.equal(worker.messages[0].clearRecords.records.length,3);env.integration.destroy();assert.equal(env.timer.size,0);}});
 await test('destroy aborts both source requests and clears polling timers',async()=>{const env=setup(),worker=await env.start();env.complete(worker);env.publicSource.pending=true;env.nativeSource.pending=true;env.timer.tick(20000);await settle();const pending=env.requests.slice(-2);assert(pending.every(row=>row.url.endsWith('manifest')||row.url.endsWith('manifest.json')));env.integration.destroy();assert(pending.every(row=>row.settings.signal.aborted));await settle();assert.equal(env.timer.size,0);});
 await test('overlay publishes a fresh result and clears on recompute, new match and game end',async()=>{
   const messages=[],card={title:'다음 조합',detail:'목표 덱 제작 순서'};
   const env=setup({bridge:message=>messages.push(copy(message)),overlay:frame=>frame?.result&&!frame.calculating?card:null});
   const type='recommendation-overlay',clear={type};
   const worker=await env.start();
   Object.assign(env.state.snapshot,{processId:100,timestamp:'2026-11-06T00:00:00Z'});
   env.complete(worker);
   assert.deepEqual(messages.at(-1),{type,processId:100,timestamp:env.state.snapshot.timestamp,...card,percent:null,action:null,actionId:null});
   env.integration.update(env.state);
   assert.equal(messages.length,2);
   env.state.snapshot.timestamp='2026-11-06T00:00:01Z';
   env.integration.update(env.state);
   assert.equal(messages.at(-1).timestamp,env.state.snapshot.timestamp);
   env.state.counts.set('h001',2);
   env.integration.update(env.state);
   assert.deepEqual(messages.at(-1),clear);
   env.timer.tick(150);
   env.complete(worker);
   assert.equal(messages.at(-1).title,card.title);
   env.state.snapshot.progression={gameEnded:true};
   env.integration.update(env.state);
   assert.deepEqual(messages.at(-1),clear);
   env.state.snapshot.progression=null;
   env.state.snapshot.sessionIdentity='next-match';
   env.integration.update(env.state);
   assert.deepEqual(messages.at(-1),clear);
   env.integration.destroy();
 });
 await test('2.331C nightmare gives verified story deadlines priority and only suggests held upper wisps',()=>{
   const map='20ce1b0918e56d18ad02a11a6dddf05298d6478037721f20a29702d4cd7b145d';
   const fixtureCatalog={map:{sha256:map}};
   const snapshot={loadedMapHash:map,catalogMatchesLoadedArchive:true,status:'snapshot_read',
     stableRoots:true,stableUnits:true,readErrors:0,localPlayer:0,
     progression:{status:'confirmed',localPlayer:0,activePlayer:true,gameEnded:false,
       difficulty:'nightmare',difficultyValue:6,round:29,storyIndex:13,source:{readOnly:true,mapSha256:map,
         scriptSha256:'308258a35af767807cabcc595f0f9513ff1aea52c82bcc0171dbdae6239fef13'}}};
   const held=new Map([['e018',6],['e016',2],['e019',1],['e015',1]]);
   const action=()=>progressionAction(fixtureCatalog,snapshot,held);
   assert.equal(action().title,'전설 위습 먼저 사용');
   for(const round of [30,34,35]){
     snapshot.progression.round=round;
     assert.deepEqual(action(),{title:'스토리 먼저 진행',detail:'35라운드까지 와노쿠니 · 12/13 완료'});
   }
   snapshot.progression.storyIndex=14;
   held.delete('e015');
   assert.equal(action().title,'희귀 위습 먼저 사용');
   held.delete('e019');
   assert.equal(action().title,'특별 위습 먼저 사용');
   held.delete('e016');
   assert.equal(action(),null);
   snapshot.progression.storyIndex=13;
   snapshot.progression.round=36;
   assert.equal(action(),null);
   snapshot.progression.round=34;
   for(const stale of [
     {...snapshot,loadedMapHash:'other'},{...snapshot,stableUnits:false},{...snapshot,localPlayer:1},
     {...snapshot,progression:{...snapshot.progression,gameEnded:true}},
     {...snapshot,progression:{...snapshot.progression,source:{...snapshot.progression.source,readOnly:false}}}
   ])assert.equal(progressionAction(fixtureCatalog,stale,held),null);
 });
 await test('overlay refreshes an independent story action even when the deck request is unchanged',async()=>{
   const messages=[];
   const env=setup({bridge:message=>messages.push(copy(message)),overlay:frame=>frame?.priorityAction,
     progressionAction:(_catalog,snapshot)=>snapshot.progression?.round===34?{title:'스토리 먼저 진행',detail:'35라운드까지 와노쿠니'}:null});
   const worker=await env.start();
   env.complete(worker);
   const requests=worker.messages.length;
   env.state.snapshot.progression={round:34};
   env.integration.update(env.state);
   assert.equal(worker.messages.length,requests);
   assert.equal(env.updates.at(-1).priorityAction.title,'스토리 먼저 진행');
   assert.equal(messages.at(-1).title,'스토리 먼저 진행');
   env.state.snapshot.progression={round:35};
   env.integration.update(env.state);
   assert.deepEqual(messages.at(-1),{type:'recommendation-overlay'});
   env.integration.destroy();
 });
 const report={passed:tests.filter(row=>row.passed).length,total:tests.length,tests,runtimeDataRequired:false,gameInputSent:false};console.log(JSON.stringify(report,null,2));if(report.passed!==report.total)process.exitCode=1;
})();
