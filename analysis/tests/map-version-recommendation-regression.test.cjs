const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const web = path.resolve(process.argv[2] || path.join(__dirname, '../../source/ord-unit-probe/wwwroot'));
const classicHash = '20ce1b0918e56d18ad02a11a6dddf05298d6478037721f20a29702d4cd7b145d';
const oldHash = 'a'.repeat(64);
const futureHash = 'b'.repeat(64);
const unit = (rawcode, tier, displayName) => ({rawcode, tier, displayName, playable:true});
const catalog = {
  map:{sha256:classicHash, version:'2.331[C]'},
  units:[unit('h001','흔함','재료'), unit('l001','전설','물리'), unit('l002','전설','마법'), unit('l003','전설','새 유닛')],
  tiers:[{label:'흔함',unitRawcodes:['h001']}, {label:'전설',unitRawcodes:['l001','l002','l003']}],
  recipes:[{id:'R001',resultUnitRawcode:'l001',resultCount:1,ingredients:[{type:'UNIT',rawcode:'h001',count:2}]}]
};

async function checkBoard() {
  const html = fs.readFileSync(path.join(web, 'index.html'), 'utf8');
  const section = (start,end) => html.slice(html.indexOf(start),html.indexOf(end));
  const element = () => ({append(){},replaceChildren(){},setAttribute(){},addEventListener(){}});
  const metadata = {
    mapSha256:oldHash,
    groups:[{name:'전설 [물딜]',primaryRole:'physical',unitRawcodes:['l001','l003']},
      {name:'전설 [마딜]',primaryRole:'magic',unitRawcodes:['l002']}],
    units:{l001:{sourceVerified:true,groupName:'전설 [물딜]'},l002:{sourceVerified:true,groupName:'전설 [마딜]'},
      l003:{sourceVerified:true,groupName:'전설 [물딜]'}},
    wispMetadata:{verified:true,replacements:['h999']}
  };
  const identities = {sourceMapSha256:oldHash,units:{
    l001:{name:'물리',tier:'전설'},l002:{name:'마법',tier:'전설'},l003:{name:'이전 유닛',tier:'전설'}
  }};
  const node = vm.createContext({
    document:{createElement:element,createTextNode:element},byId:element,makeElement:element,
    updateStatus(){},loadStoryRankings(){},renderBoard(){},
    window:{OrdRecipeGraph:{create(_catalog,settings){return settings;}}},
    async fetch(url){
      const json = {'/data/map-catalog.json':{...catalog,map:{sha256:futureHash}},
        '/data/combat-metadata.json':metadata,'/board-display-identities.json':identities}[url];
      return {ok:Boolean(json),json:async()=>json};
    }
  });
  await vm.runInContext([
    'let catalog,catalogError=false,nativeUnitDescriptions,displayGroups,recipeGraph,startingEvolutionForecast,boardGroups,latestSnapshot;',
    'const unitByCode=new Map(),unitsByTier=new Map(),recipesByUnit=new Map(),sections=new Map();',
    section('const canonicalTiers', 'const unstableStatuses'),
    section('function normalizeTier', 'function normalizeStoryRankings'),
    section('function groupColumn', 'function updateStatus'),
    section('async function fetchNativeUnitDescriptions', 'function toast'),
    'loadCatalog()'
  ].join('\n'),node);
  assert.equal(vm.runInContext('catalogError',node),false);
  const groups = JSON.parse(vm.runInContext('JSON.stringify(boardGroups.map(g=>[g.label,g.units.map(u=>u.rawcode)]))',node));
  assert.deepEqual(groups.filter(([label])=>label.startsWith('전설')),[
    ['전설 [물딜]',['l001']],['전설 [마딜]',['l002']],['전설',['l003']]
  ]);
  assert.equal(vm.runInContext('recipeGraph.choiceEligibleRawcodes',node),undefined);
}

async function checkCombatInventory() {
  const html=fs.readFileSync(path.join(web,'index.html'),'utf8');
  const section=(start,end)=>html.slice(html.indexOf(start),html.indexOf(end));
  const read=name=>JSON.parse(fs.readFileSync(path.join(web,'data',name),'utf8'));
  const mapCatalog=read('map-catalog.json'),identities=read('combat-inventory-identities.json');
  const element=()=>({append(){},replaceChildren(){},setAttribute(){},addEventListener(){}});
  const flashes=[];
  const node=vm.createContext({
    document:{createElement:element,createTextNode:element},byId:element,makeElement:element,
    updateStatus(){},loadStoryRankings(){},renderBoard(){},
    flash:(code,delta)=>{if(delta)flashes.push([code,delta]);},window:{},
    async fetch(url){
      const json={'/data/map-catalog.json':mapCatalog,'/data/combat-inventory-identities.json':identities}[url];
      return {ok:Boolean(json),json:async()=>json};
    }
  });
  vm.runInContext(fs.readFileSync(path.join(web,'nightmare-advisor-integration.js'),'utf8'),node);
  node.window.OrdNightmareIntegration=node.OrdNightmareIntegration;
  await vm.runInContext([
    'let catalog,catalogError=false,nativeUnitDescriptions,displayGroups,recipeGraph,startingEvolutionForecast,boardGroups,latestSnapshot;',
    'let liveCounts,liveReceivedAt,liveState,mode="live";',
    'const unitByCode=new Map(),unitsByTier=new Map(),recipesByUnit=new Map(),sections=new Map();',
    section('const canonicalTiers','const unstableStatuses'),
    'const unstableStatuses=new Set(["units_changed","world_changed","partial_read"]);',
    section('function normalizeTier','function normalizeStoryRankings'),
    section('function groupColumn','function updateStatus'),
    section('function setLiveState','function flash'),
    section('function applySnapshot','function openDetail'),
    section('async function fetchNativeUnitDescriptions','function toast'),
    'loadCatalog()'
  ].join('\n'),node);
  assert.equal(vm.runInContext('catalogError',node),false);
  const inventory=node.OrdNightmareIntegration.combatInventory(mapCatalog);
  assert(inventory);
  const visibility=code=>vm.runInContext(`unitByCode.get(${JSON.stringify(code)}).visible`,node);
  assert(identities.temporaryRawcodes.every(code=>!visibility(code)));
  assert(visibility('h067')&&!visibility('h0AF')&&visibility('h0CA')&&visibility('h00H'));
  assert(visibility('H0B2')&&visibility('H0B3')&&visibility('H093'));
  const snapshot=(sequence,entries)=>({sequence,timestamp:new Date().toISOString(),status:'snapshot_read',
    catalogMatchesLoadedArchive:true,loadedMapHash:classicHash,stableUnits:true,readErrors:0,
    counts:entries.map(([rawcode,count])=>({rawcode,count}))});
  node.applySnapshot(snapshot(1,[['h067',1],['h00H',3],['H0B2',1]]));
  const combat=snapshot(2,[['h0AF',1],['h00H',3],['H0B2',1],...identities.temporaryRawcodes.map(code=>[code,1])]);
  node.applySnapshot(combat);
  assert.equal(vm.runInContext('latestSnapshot',node),combat);
  assert.deepEqual(flashes,[]);
  assert.equal(vm.runInContext('liveCounts.get("h067")',node),1);
  node.applySnapshot(snapshot(3,[['h067',1],['h00H',2],['H0B3',1]]));
  assert.deepEqual(flashes,[['h00H',-1],['H0B2',-1],['H0B3',1]]);
  assert.deepEqual(JSON.parse(JSON.stringify(inventory.nativeProtection({h067:1,h0AF:1,h00H:2,H0B2:1}))),{h00H:2,H0B2:1});
  for(const bad of [
    {...identities,mapSha256:futureHash},
    {...identities,temporaryRawcodes:[...identities.temporaryRawcodes,'h00H']},
    {...identities,canonicalByRawcode:{...identities.canonicalByRawcode,H0B3:'H0B2'}},
    {...identities,canonicalByRawcode:{...identities.canonicalByRawcode,H093:'H091'}}
  ])assert.equal(node.OrdNightmareIntegration.configureCombatInventory(mapCatalog,bad),null);
}

async function checkAdvisorCombatInventory() {
  const read=name=>JSON.parse(fs.readFileSync(path.join(web,'data',name),'utf8'));
  const mapCatalog=read('map-catalog.json'),identities=read('combat-inventory-identities.json');
  const timers=new Map(),workers=[],updates=[];
  let timerId=0,panelSettings={direction:'all'};
  const node=vm.createContext({
    Map,Set,Date,
    Worker:class {
      constructor(){this.messages=[];workers.push(this);}
      postMessage(message){this.messages.push(message);}
      terminate(){}
    },
    setTimeout(fn,delay){const id=++timerId;timers.set(id,{fn,delay});return id;},
    clearTimeout:id=>timers.delete(id),setInterval:()=>++timerId,clearInterval(){},
    fetch:async()=>({ok:true,json:async()=>({map:{sha256:classicHash},catalogMapHash:classicHash,records:[]})}),
    OrdNightmarePanel:{create:()=>({update:state=>updates.push(state),setSettings(value){panelSettings={...panelSettings,...value};},
      getSettings:()=>panelSettings,destroy(){}})}
  });
  vm.runInContext(fs.readFileSync(path.join(web,'nightmare-advisor-integration.js'),'utf8'),node);
  const api=node.OrdNightmareIntegration;
  assert(api.configureCombatInventory(mapCatalog,identities));
  const advisor=api.create({});
  const state={catalog:mapCatalog,counts:new Map([['h067',1],['h00H',3]]),receivedAt:Date.now(),
    snapshot:{sessionIdentity:'combat-audit',loadedMapHash:classicHash,catalogMatchesLoadedArchive:true,localPlayer:0},
    liveState:{tone:'live',checking:false},capabilities:{supportedRecipeIds:new Set(['A029'])}};
  const runDebounce=()=>{
    for(const [id,{fn,delay}] of timers)if(delay===150){timers.delete(id);fn();}
  };
  advisor.update(state);
  for(let i=0;i<20;i++)await Promise.resolve();
  runDebounce();
  assert.equal(workers.length,1);
  const worker=workers[0];
  assert.equal(worker.messages[0].catalog.combatInventory.canonicalByRawcode.h0AF,'h067');
  assert.equal(mapCatalog.combatInventory,undefined);
  worker.onmessage({data:{type:'ready'}});
  const request=worker.messages.at(-1);
  worker.onmessage({data:{type:'result',revision:request.revision,result:{targetDeck:{id:'ready',goals:[]}}}});
  assert.equal(updates.at(-1).calculating,false);
  advisor.update({...state,counts:new Map([['h0AF',1],['h00H',3],...identities.temporaryRawcodes.map(code=>[code,1])])});
  runDebounce();
  assert.equal(worker.messages.at(-1),request);
  assert.equal(updates.at(-1).calculating,false);
  advisor.update({...state,counts:new Map([['h067',1],['h00H',2]])});
  runDebounce();
  assert.equal(worker.messages.at(-1).counts.h00H,2);
  assert.equal(updates.at(-1).calculating,true);
  advisor.destroy();
}

function checkStarter() {
  const Strategy = require(path.join(web,'nightmare-strategy.js'));
  const panel = vm.createContext({});
  vm.runInContext(fs.readFileSync(path.join(web,'nightmare-strategy-panel.js'),'utf8'),panel);
  const snapshot = {loadedMapHash:classicHash,catalogMatchesLoadedArchive:true,status:'snapshot_read',
    stableRoots:true,stableUnits:true,readErrors:0,localPlayer:0,progression:{localPlayer:0,status:'confirmed',
      activePlayer:true,gameEnded:false,difficulty:'nightmare',difficultyValue:6,round:10,storyIndex:4,
      source:{readOnly:true,mapSha256:classicHash,scriptSha256:'308258a35af767807cabcc595f0f9513ff1aea52c82bcc0171dbdae6239fef13'}}};
  const progression = panel.OrdNightmarePanel.confirmedClassicProgression(catalog,snapshot);
  assert.equal(progression?.round,10);
  assert.equal(panel.OrdNightmarePanel.confirmedClassicProgression(catalog,{...snapshot,loadedMapHash:futureHash}),null);
  const records = {schemaVersion:1,catalogMapHash:classicHash,records:[],
    projection:{kind:'historical_roster_identity',sourceMapHash:oldHash,sourceRevision:oldHash}};
  const strategy = Strategy.create(catalog,{map:{sha256:oldHash}},{},records,{recordOnly:true,earlyStoryRecommendations:true});
  const counts = {h001:2};
  const settings = {round:progression.round,wood:0,gold:0,materialStarter:true,supportedRecipeIds:['R001']};
  const result = strategy.recommend(counts,settings);
  assert.equal(result.phase,'early_story');
  assert.equal(result.earlyStory.materialOnly,true);
  assert.equal(result.earlyStory.candidates[0].rawcode,'l001');
  assert.equal(result.earlyStory.candidates[0].storyRank,undefined);
  assert.deepEqual(result.craftTargets[0].expectedSteps,[{type:'craft',recipeId:'R001'}]);
  const preview = strategy.recommend(counts,{...settings,supportedRecipeIds:[]});
  assert.equal(preview.earlyStory.candidates[0].rawcode,'l001');
  assert.deepEqual(preview.craftTargets,[]);
  assert.notEqual(strategy.recommend(counts,{...settings,materialStarter:false}).phase,'early_story');
}

function checkHistoricalStarterRanks() {
  const Strategy = require(path.join(web,'nightmare-strategy.js'));
  const sourceHash = '0794996c9bc73eaa3020f20ebc45fb37cfa64a6e98ffe8aa0dfa3b356e77bc5c';
  const starters = Array.from({length:8},(_,index)=>unit(`l00${index+1}`,'전설',`전설 ${index+1}`));
  const current = {...catalog,units:[catalog.units[0],...starters],recipes:starters.map((entry,index)=>({
    id:`R00${index+1}`,resultUnitRawcode:entry.rawcode,resultCount:1,
    ingredients:[{type:'UNIT',rawcode:'h001',count:index===0?10:2}]
  }))};
  const records = {schemaVersion:1,catalogMapHash:classicHash,records:[],
    projection:{kind:'historical_roster_identity',sourceMapHash:sourceHash,sourceRevision:oldHash}};
  const rankings = {schemaVersion:1,catalogMapHash:sourceHash,measurementVersion:'2.323[C]',
    sourceExperimentVersion:'2.305',conditions:{difficulty:'악몽',story:'마린포드',durationSeconds:60,
      startingHealthPercent:100,buffPolicy:'cooldown',holdControl:false},
    entries:starters.map((entry,index)=>({rawcode:entry.rawcode,catalogName:entry.displayName,tier:entry.tier,
      rank:index+1,metric:'damage_percent',value:90-index}))};
  const settings = {round:10,wood:0,gold:0,materialStarter:true,
    supportedRecipeIds:current.recipes.map(recipe=>recipe.id)};
  const recommend = data => Strategy.create(current,{}, {},records,{
    recordOnly:true,earlyStoryRecommendations:true,storyRankings:data
  }).recommend({h001:2},settings);

  const result = recommend(rankings);
  assert.equal(result.earlyStory.materialOnly,true);
  assert.equal(result.earlyStory.historicalStoryReference,true);
  assert.equal(result.earlyStory.sourceCandidateCount,8);
  assert.equal(result.earlyStory.evaluatedCandidateCount,2);
  assert.equal(result.earlyStory.relativeQualityFraction,.25);
  assert.equal(result.earlyStory.storyMeasurementVersion,null);
  assert.deepEqual(result.earlyStory.candidates.map(entry=>[entry.rawcode,entry.storyRank]),[['l002',2],['l001',1]]);
  assert(result.earlyStory.candidates.every(entry=>entry.storyDamagePercent===undefined));
  assert.deepEqual(result.craftTargets[0].expectedSteps,[{type:'craft',recipeId:'R002'}]);

  const wrongHash = recommend({...rankings,catalogMapHash:futureHash});
  assert.equal(wrongHash.earlyStory.historicalStoryReference,undefined);
  assert.equal(wrongHash.earlyStory.sourceCandidateCount,8);
  assert(wrongHash.earlyStory.candidates.every(entry=>entry.storyRank===undefined));
  for(const mismatch of [{catalogName:'다른 유닛'},{tier:'히든'},{metric:'completion_seconds'}]){
    const mismatchedUnit = recommend({...rankings,entries:rankings.entries.map(entry=>
      entry.rawcode==='l002'?{...entry,...mismatch}:entry)});
    assert.deepEqual(mismatchedUnit.earlyStory.candidates.map(entry=>entry.rawcode),['l003','l001']);
  }
}

function checkRecordGroups() {
  const Records = require(path.join(web,'nightmare-records.js'));
  const current = {...catalog,units:[...catalog.units,unit('h0C8','전설','레오'),
    unit('h005','초월','초월 유닛'),unit('h006','신비','신비 유닛')]};
  const record = (id, recommendationGroup, codes) => ({id,recommendationGroup,
    sourceProvider:'tmo',sourceOutcome:'public_clear_submission_not_replayed',
    sourceUrl:`https://example.test/clear/${id}`,cohort:'nightmare-regular',
    version:{number:'2.331',numericVerified:true,sourceNumericVersionVerified:true},
    entries:codes.map(rawcode=>({rawcode,count:1,status:'exact_native_identity'})),
    goals:codes.map(rawcode=>({rawcode,count:1}))});
  const data = {catalogMapHash:classicHash,defaultCohort:'nightmare-regular',records:[
    record('untagged',undefined,['h005']), record('path','king_path',['h0C8','h005']),
    record('martial','martial_law',['h0C8']), record('mystery','mysterious',['h006']),
    record('normal','normal',['h005'])
  ]};
  const provider = Records.create(current,{},data,{recordOnly:true});
  const selected = settings => provider.templates({},settings).templates.map(template=>template.recordId).sort();
  assert.deepEqual(selected({}),['normal','untagged']);
  assert.deepEqual(selected({kingPathMode:'off',includeMysteriousDecks:false}),['normal','untagged']);
  assert.deepEqual(selected({kingPathMode:'standard'}),['path','untagged']);
  assert.deepEqual(selected({kingPathMode:'martial_law'}),['martial','untagged']);
  assert.deepEqual(selected({includeMysteriousDecks:true}),['mystery','untagged']);
  assert.deepEqual(selected({kingPathMode:'martial_law',includeMysteriousDecks:true}),['martial','mystery','untagged']);
}

function checkUiTrace() {
  const html = fs.readFileSync(path.join(web,'index.html'),'utf8');
  const messages=[];
  const context = vm.createContext({window:{chrome:{webview:{postMessage:message=>messages.push(message)}}},
    craftUiTraceKeys:new Map(),latestSnapshot:{status:'snapshot_read',sequence:1,timestamp:'2026-11-07T00:00:00Z'}});
  vm.runInContext(html.slice(html.indexOf('function traceCraftUi('),html.indexOf('function requestRecommendedCraft(')),context);
  const ready={scope:'board',enabled:1,total:2,blocked:{'유닛 재료가 부족해요':1}};
  context.traceCraftUi(ready);
  context.latestSnapshot.sequence=2;context.traceCraftUi(ready);
  assert.equal(messages.length,1);
  context.latestSnapshot.status='units_changed';
  context.traceCraftUi({...ready,enabled:0,blocked:{'게임 연결 후 사용할 수 있어요':2}});
  assert.equal(messages.length,2);
  assert.equal(messages[1].snapshotStatus,'units_changed');
  assert.equal(messages[1].enabled,0);
  context.latestSnapshot.status='snapshot_read';context.traceCraftUi(ready);
  assert.equal(messages.length,3);
  assert.equal(messages[2].snapshotSequence,2);
  context.traceCraftUi({scope:'advisor',enabled:0,total:1,blocked:{},calculating:true});
  assert.equal(messages.length,4);
}

async function run() {
  await checkBoard();
  if (fs.existsSync(path.join(web, 'data', 'map-catalog.json'))) {
    await checkCombatInventory();
    await checkAdvisorCombatInventory();
  }
  checkStarter();
  checkHistoricalStarterRanks();
  await checkRecordGroups();
  checkUiTrace();
}

run().catch(error=>{console.error(error);process.exitCode=1;});
