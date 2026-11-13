const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const sha=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
const hash=value=>typeof value==='string'&&/^[a-f0-9]{64}$/.test(value);
const runId=value=>typeof value==='string'&&/^auto-\d{8}T\d{13}Z-[a-f0-9]{16}$/.test(value);
const list=value=>Array.isArray(value)?value:[];
const canonicalInventory=value=>{
 if(!Array.isArray(value)||!value.length)return null;
 const rows=value.map(row=>({rawcode:row?.rawcode,count:row?.count}));
 if(rows.some(row=>typeof row.rawcode!=='string'||!/^[A-Za-z0-9]{4}$/.test(row.rawcode)||!Number.isSafeInteger(row.count)||row.count<1||row.count>1000000)||new Set(rows.map(row=>row.rawcode)).size!==rows.length)return null;
 return rows.sort((a,b)=>a.rawcode.localeCompare(b.rawcode));
};
const sameInventory=(left,right)=>left!==null&&right!==null&&JSON.stringify(left)===JSON.stringify(right);
const parse=file=>{const info=fs.lstatSync(file);if(!info.isFile()||info.isSymbolicLink()||info.size>32*1024*1024)throw Error('unsupported_local_source_file');const bytes=fs.readFileSync(file);return {bytes,data:JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/,''))};};
const validProgression=(value,player,ended)=>value?.status==='confirmed'&&value.localPlayer===player&&value.round===65&&value.activePlayer===true&&value.difficulty==='nightmare'&&value.difficultyValue===6&&value.gameEnded===ended&&value.victory===ended;
const sameInstant=(a,b)=>typeof a==='string'&&typeof b==='string'&&Number.isFinite(Date.parse(a))&&Date.parse(a)===Date.parse(b);

function importLocalClear(root,file,catalog,slotCosts={}){
 const rejected=reason=>({excluded:'local_'+reason,sourceId:path.basename(file)});
 try{
  const directory=path.resolve(root,'data/local-runs'),resolved=path.resolve(file);
  if(path.dirname(resolved)!==directory||!/^clear-run-[A-Za-z0-9_-]+\.json$/.test(path.basename(resolved)))return rejected('invalid_record_path');
  const realDirectory=fs.realpathSync(directory),realFile=fs.realpathSync(resolved);
  if(path.dirname(realFile)!==realDirectory)return rejected('record_path_escapes_directory');
  const saved=parse(resolved),record=saved.data,id=record.canonicalRunId,mapMatch=String(catalog.map?.version||'').match(/^(\d+\.\d{3})(?:\[([A-Za-z0-9]+)\])?$/);
  if(record.schemaVersion!==1||record.recordKind!=='clear_run'||!runId(id))return rejected('invalid_record_identity');
  if(!mapMatch||record.map?.version!==mapMatch[1]||record.map.sha256!==catalog.map.sha256||record.map.variant!==mapMatch[2])return rejected('map_mismatch');
  if(record.mode?.difficulty!=='nightmare'||record.mode.difficultyValue!==6||record.outcome?.result!=='clear'||record.outcome.nativeVictoryConfirmed!==true||record.outcome.nativeOutcome!=='victory'||record.outcome.nativeRound!==65||record.outcome.round!==65)return rejected('victory_not_confirmed');
  const expected='data/local-runs/auto/'+id+'.json',evidence=record.evidence;
  if(evidence?.automaticRunRelativePath!==expected||!hash(evidence.automaticRunSha256))return rejected('automatic_source_not_bound');
  const automaticPath=path.resolve(root,...expected.split('/')),realAuto=fs.realpathSync(automaticPath);
  if(path.dirname(realAuto)!==path.join(realDirectory,'auto'))return rejected('automatic_path_escapes_directory');
  const original=parse(automaticPath),automatic=original.data;
  if(sha(original.bytes)!==evidence.automaticRunSha256)return rejected('automatic_hash_mismatch');
  if(automatic.schemaVersion!==1||automatic.recordKind!=='automatic_run'||automatic.id!==id||automatic.mapSha256!==catalog.map.sha256||!Number.isInteger(automatic.localPlayer)||automatic.localPlayer<0||automatic.localPlayer>11)return rejected('automatic_identity_mismatch');
  const terminal=automatic.terminalObservation,active=automatic.lastActiveSnapshot,outcome=automatic.outcome;
  if(automatic.collectionStatus!=='ended'||outcome?.result!=='victory'||outcome.endedRound!==65||!validProgression(terminal?.progression,automatic.localPlayer,true)||!validProgression(active?.progression,automatic.localPlayer,false)||!validProgression(record.nativeTerminalProgression,automatic.localPlayer,true))return rejected('native_victory_mismatch');
  if(!sameInstant(terminal.timestamp,outcome.endedObservedAtUtc)||!sameInstant(terminal.timestamp,record.outcome.nativeObservedAtUtc)||!sameInstant(active.timestamp,automatic.finalInventoryObservedAtUtc)||!sameInstant(active.timestamp,outcome.lastActiveObservedAtUtc))return rejected('snapshot_timestamp_mismatch');
  const gap=Date.parse(terminal.timestamp)-Date.parse(active.timestamp);
  if(!Number.isFinite(gap)||gap<0||gap>2000)return rejected('final_inventory_too_old');
  const nativeInventory=canonicalInventory(active.counts),finalInventory=canonicalInventory(automatic.finalInventory),manualInventory=canonicalInventory(record.finalInventory);
  if(!sameInventory(nativeInventory,finalInventory)||!sameInventory(nativeInventory,manualInventory))return rejected('final_inventory_mismatch');
  const units=new Map(list(catalog.units).map(unit=>[unit.rawcode,unit])),tiers=new Set(['전설','히든','초월','불멸','영원','제한','신비','왜곡','변화','특수','세라핌','랜덤전용']);
  const temporary=new Set(['h076','h04T','h04Y','h05D','h0CA']);
  const selected=nativeInventory.filter(row=>{const unit=units.get(row.rawcode);return unit?.playable===true&&unit.isSummon!==true&&tiers.has(unit.tier)&&!temporary.has(row.rawcode);});
  if(!selected.length)return rejected('no_final_goals');
  const slots=selected.reduce((sum,row)=>sum+row.count*(slotCosts[row.rawcode]??Number(units.get(row.rawcode).tier==='초월'&&/^H[A-Za-z0-9]{3}$/.test(row.rawcode))),0);
  if(slots>1)return rejected('too_many_transcendence');
  const proof={schemaVersion:1,kind:'verified_native_clear',canonicalRunId:id,automaticRunSha256:sha(original.bytes),clearRecordSha256:sha(saved.bytes),finalInventorySha256:sha(JSON.stringify(nativeInventory)),catalogMapHash:catalog.map.sha256,versionNumber:mapMatch[1],variant:mapMatch[2]||null,nativeVictoryConfirmed:true,terminalRound:65,terminalObservedAt:terminal.timestamp,lastActiveObservedAt:active.timestamp,roster:selected};
  const quantityEvidence=row=>({kind:'verified_native_last_active_quantity',sourceProvider:'local-native',sourceCountVerified:true,canonicalRunId:id,automaticRunSha256:proof.automaticRunSha256,finalInventorySha256:proof.finalInventorySha256,catalogMapHash:proof.catalogMapHash,rawcode:row.rawcode,count:row.count});
  const targetCodes=new Set(selected.map(row=>row.rawcode)),entries=nativeInventory.map(row=>{
   const unit=units.get(row.rawcode),native=list(active.counts).find(item=>item.rawcode===row.rawcode),contextOnly=!targetCodes.has(row.rawcode);
   return {sourceName:unit?.displayName||native?.name||row.rawcode,sourceTier:unit?.tier||null,sourceRawcode:row.rawcode,rawcode:row.rawcode,reportedCount:row.count,count:row.count,countMeaning:'verified_native_quantity',countVerified:true,quantityKnown:true,status:contextOnly?'context_only':'exact_native_identity',contextOnly,mappingStatus:'native_map_and_last_active_quantity_verified',quantityEvidence:quantityEvidence(row)};
  });
  const sourceId='local-'+id;
  return {record:{id:sourceId,displayId:sourceId,sourceProvider:'local-native',sourceUrl:null,sourceUrls:[],sourceRecordIds:[sourceId],createdAt:terminal.timestamp,version:{number:mapMatch[1],variant:mapMatch[2]||null,numericVerified:true,sourceNumericVersionVerified:true,variantVerified:true,source:'verified_native_loaded_map',assumedByUser:false,datePolicyApplied:false,minimumPossibleVersion:mapMatch[1],possibleSourceVersions:[mapMatch[1]]},versionEvidence:{method:'verified_native_loaded_map',sourceNumericVersionVerified:true,catalogMapHash:catalog.map.sha256,automaticRunSha256:proof.automaticRunSha256},cohort:'nightmare-regular',difficulty:'악몽',solo:record.mode.soloConfirmed===true?true:null,mode:null,otherworldMode:null,cohortEvidence:{difficultyRaw:'nightmare',classificationRule:'confirmed_same_map_native_nightmare_victory',soloConfirmed:record.mode.soloConfirmed===true},environment:{elder:active.elder?.name||null,navigation:null,navigationNode:null,enhancements:null,items:active.items||null},reportedRemainingEnemyCount:null,imageObservedRemainingEnemyCount:null,remainingEnemyCountUsedForRanking:false,rosterComplete:true,rosterCompleteness:'verified_last_active_owned_snapshot_catalog_projection',entries,goals:selected,mappingComplete:true,sourceMappingComplete:true,unresolved:[],sourceOutcome:'local_native_victory_confirmed',exactListedCountsVerified:true,fullRosterCountsVerified:true,sourceUnitCount:nativeInventory.length,localNativeProof:proof,observedInventory:list(active.counts).map(row=>({rawcode:row.rawcode,name:row.name??null,count:row.count,nativeIsCharacter:row.isCharacter??null})),sourceRecordRelativePath:path.relative(root,resolved).split(path.sep).join('/')},canonicalRunId:id};
 }catch(error){return rejected('source_unavailable_or_invalid');}
}

function loadLocalClears(root,catalog,slotCosts={}){
 const directory=path.join(root,'data/local-runs'),records=[],excluded=[],byRun=new Map();
 if(!fs.existsSync(directory))return {records,excluded};
 for(const name of fs.readdirSync(directory).filter(name=>/^clear-run-[A-Za-z0-9_-]+\.json$/.test(name)).sort()){
  const result=importLocalClear(root,path.join(directory,name),catalog,slotCosts);
  if(!result.record){excluded.push(result);continue;}
  if(byRun.has(result.canonicalRunId)){excluded.push({excluded:'local_duplicate_canonical_run',sourceId:name,canonicalRunId:result.canonicalRunId});continue;}
  byRun.set(result.canonicalRunId,result.record);records.push(result.record);
 }
 return {records,excluded};
}
module.exports={importLocalClear,loadLocalClears,canonicalInventory};
