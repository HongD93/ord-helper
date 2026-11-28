'use strict';
// 보유 패 기준 역할 추천 모듈(nightmare-role-advisor.js) 검사. 자료는 data/nightmare-role-profile-2.331.json이다.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const web=path.resolve(__dirname,'../wwwroot'),Advisor=require(path.join(web,'nightmare-role-advisor.js'));
const read=name=>JSON.parse(fs.readFileSync(path.join(web,'data',name),'utf8'));
// 실행용 맵 목록(map-catalog.json) 없이 저장소에 있는 역할 자료만으로 목록을 만든다.
const data=read('nightmare-role-profile-2.331.json');
const catalog={map:{sha256:data.catalogMapHash,version:data.mapVersion},units:Object.entries(data.units).map(([rawcode,unit])=>({rawcode,tier:unit.tier,playable:true}))};
const advisor=Advisor.create(data,catalog),tests=[];
function test(name,fn){try{fn();tests.push({name,passed:true});}catch(error){tests.push({name,passed:false,error:String(error.stack)});}}
const deck=rows=>new Map(Object.entries(rows));
const elders=['나스쥬로','워큐리','새턴'];

test('the profile only loads for the same map catalog',()=>{
  assert(advisor);
  assert.equal(Advisor.create(data,{...catalog,map:{...catalog.map,sha256:'a'.repeat(64)}}),null);
  assert.equal(Advisor.create({...data,schemaVersion:2},catalog),null);
});
test('a team with no damage cannot beat regeneration and more copies kill faster',()=>{
  assert.equal(advisor.teamTime('line','새턴',deck({}))?.seconds,null);
  const one=advisor.teamTime('line','새턴',deck({H0B5:1})).seconds,two=advisor.teamTime('line','새턴',deck({H0B5:2})).seconds;
  assert(Number.isFinite(one)&&Number.isFinite(two)&&two<one);
});
test('another unit\'s magic amplification speeds up a magic main',()=>{
  const plain=advisor.teamTime('line','새턴',deck({H0B5:1,h0A1:1}),{amplification:false}).seconds;
  const amplified=advisor.teamTime('line','새턴',deck({H0B5:1,h0A1:1})).seconds;
  assert(amplified<plain);
});
test('armor break is added by stacking speed and stays below the cap',()=>{
  const without=advisor.teamTime('line','나스쥬로',deck({H09F:1})),withBreak=advisor.teamTime('line','나스쥬로',deck({H09F:1,h081:1}));
  assert.equal(without.averageBreak,0);
  assert(withBreak.averageBreak>0&&withBreak.averageBreak<75);
  assert(withBreak.reduction>without.reduction);
});
test('area stuns count only from legend or higher units',()=>{
  const rare=advisor.areaEffects(deck({h01V:1})),legend=advisor.areaEffects(deck({h03O:1}));
  assert.equal(advisor.unit('h01V').tier,'희귀');
  assert(advisor.unit('h01V').effects.some(effect=>effect[0]==='stun'));
  assert.equal(rare.stunUptime,0);assert.equal(rare.stunUnits,0);
  assert(legend.stunUptime>0);assert.equal(legend.stunUnits,1);
});
test('assessment uses the detected elder or the three-elder average',()=>{
  const counts=deck({H0B5:1}),known=advisor.assess(counts,{elder:'워큐리'}),average=advisor.assess(counts,{});
  assert.equal(known.elder,'워큐리');assert.equal(average.elder,null);
  assert.equal(known.direction,'magic');
  assert.equal(known.threshold,data.thresholds.byElder['워큐리'].magic);
  assert.equal(average.threshold,data.thresholds.magic);
  const mean=elders.map(elder=>advisor.teamTime('line',elder,counts).seconds).reduce((sum,value)=>sum+value,0)/3;
  assert(Math.abs(average.metrics.line-mean)<1e-9);
  assert.equal(known.gaps.armor,0);
});
test('a main without boss damage leaves a boss killer gap until one is added',()=>{
  assert.equal(advisor.unit('H0B5').bossKiller,undefined);
  assert.equal(advisor.assess(deck({H0B5:1}),{main:'H0B5'}).gaps.bossKiller,1);
  assert.equal(advisor.assess(deck({H0B5:1,h045:1}),{main:'H0B5'}).gaps.bossKiller,0);
});
test('without an owned main only the fastest main candidates are offered, no role units',()=>{
  assert.equal(advisor.mainEligible('h05W'),false);
  const step=advisor.nextStep({counts:{},candidates:[{rawcode:'h05W',cost:1},{rawcode:'H09F',cost:9},{rawcode:'H0B5',cost:4},{rawcode:'h045',cost:0}]});
  assert.equal(step.phase,'main');
  assert.deepEqual(step.mainCandidates.map(row=>row.rawcode),['H0B5','H09F']);
  assert.equal(step.pick,null);assert.deepEqual(step.pool,[]);
});
test('with an owned main the next step fills the largest gap with the cheapest meaningful unit',()=>{
  const candidates=['h045','h03V','h03O','h0A1','h081','H09F','H0B4'].map((rawcode,index)=>({rawcode,cost:2+index}));
  const step=advisor.nextStep({counts:{H0B5:1},candidates,elder:'새턴'});
  assert.equal(step.phase,'role');assert.deepEqual(step.mains,['H0B5']);
  const largest=Math.max(...Object.values(step.current.gaps));
  assert.equal(step.current.gaps[step.role],largest);
  assert(step.after.gaps[step.role]<step.current.gaps[step.role]);
  assert.deepEqual(step.pick.roles,[step.role]);assert(step.pick.gain>=.05);
  assert.equal(step.pool[0].rawcode,step.pick.rawcode);assert(step.pool.length<=3);
  // 부족분을 의미 있게 줄이는 후보 중에서는 남은 조합 비용이 낮은 쪽이 먼저다.
  for(let index=1;index<step.pool.length;index+=1)assert(step.pool[index-1].cost<=step.pool[index].cost);
  const limited=advisor.nextStep({counts:{H0B5:1},candidates,elder:'새턴',upperLimit:1});
  assert(!Advisor.upperTiers.has(data.units[limited.pick.rawcode].tier));
});
test('several mains can be owned and another main-tier dealer can be the next pick for line damage',()=>{
  const both=advisor.nextStep({counts:{H09F:1,H0B5:1},candidates:[{rawcode:'h045',cost:2}],elder:'새턴'});
  assert.deepEqual(new Set(both.mains),new Set(['H09F','H0B5']));
  const all=Object.keys(data.units).filter(code=>!data.units[code].skinOf).map(rawcode=>({rawcode,cost:10}));
  const step=advisor.nextStep({counts:{H0B5:1},candidates:all,elder:'새턴'});
  assert.equal(step.role,'line');assert(advisor.mainEligible(step.pick.rawcode));
});
test('damage roles only take dealers of the deck direction',()=>{
  assert.equal(advisor.unit('H09A').direction,'physical');
  const all=Object.keys(data.units).filter(code=>!data.units[code].skinOf).map(rawcode=>({rawcode,cost:10}));
  const step=advisor.nextStep({counts:{H09A:1},candidates:all,elder:'나스쥬로'});
  assert.equal(step.current.direction,'physical');
  assert(['line','boss','enraged'].includes(step.role),'the largest gap of a lone main is a damage role');
  for(const row of step.pool)assert.notEqual(data.units[row.rawcode].direction,'magic',row.rawcode);
});
test('the physical or magic standard follows the owned mains',()=>{
  assert.equal(advisor.unit('H09F').direction,'physical');
  const candidates=['H0B4','H08X','h0A1','h03O'].map(rawcode=>({rawcode,cost:1}));
  const step=advisor.nextStep({counts:{H09F:1},candidates,elder:'나스쥬로'});
  assert.equal(step.current.direction,'physical');assert.equal(step.after.direction,'physical');
  assert.equal(step.current.threshold,data.thresholds.byElder['나스쥬로'].physical);
});
test('candidates without a finite craft cost are never picked',()=>{
  const step=advisor.nextStep({counts:{H0B5:1},candidates:[{rawcode:'h045',cost:Infinity},{rawcode:'h03V',cost:null}]});
  assert.equal(step.pick,null);assert.equal(step.phase,'done');
});
test('a limited group such as one transcendence hero is never exceeded',()=>{
  const heroes=Object.keys(data.units).filter(code=>/^H/.test(code)&&data.units[code].tier==='초월');
  const candidates=heroes.slice(0,12).map(rawcode=>({rawcode,cost:1}));
  const step=advisor.nextStep({counts:{H09F:1},candidates,elder:'새턴',limitedGroups:[{rawcodes:heroes,maximum:1}]});
  assert.equal(step.pick,null);
  const empty=advisor.nextStep({counts:{},candidates,elder:'새턴',limitedGroups:[{rawcodes:heroes,maximum:1}]});
  assert(empty.mainCandidates.length>=1&&empty.mainCandidates.every(row=>heroes.includes(row.rawcode)));
});
test('a pick must reduce its role gap enough',()=>{
  const candidates=[{rawcode:'h0A1',cost:40},{rawcode:'h03O',cost:20}];
  const loose=advisor.nextStep({counts:{H0B5:1},candidates,elder:'새턴',minimumGain:0,slotCost:0});
  const strict=advisor.nextStep({counts:{H0B5:1},candidates,elder:'새턴',minimumGain:10});
  assert(loose.pick);assert.equal(strict.pick,null);
});
test('the main comes from the top tiers only and special units count like legend for stuns',()=>{
  assert.equal(advisor.unit('h09Y').tier,'세라핌');
  assert(advisor.soloLine('h09Y')<=data.mainLineSecondsLimit);
  assert.equal(advisor.mainEligible('h09Y'),false);
  const picked=advisor.nextStep({counts:{},candidates:[{rawcode:'h09Y',cost:1},{rawcode:'H0B5',cost:50}]});
  assert.deepEqual(picked.mainCandidates.map(row=>row.rawcode),['H0B5']);
  assert(data.stunTiers.includes('특수'));
  const special=Object.keys(data.units).find(code=>data.units[code].tier==='특수');
  const copy=JSON.parse(JSON.stringify(data));copy.units[special].effects=[['stun',2,2,.5,0]];
  const local=Advisor.create(copy,catalog);
  assert.equal(local.areaEffects(new Map([[special,1]])).stunUnits,1);
});
test('boss killers below legend (other than special) do not fill the boss killer role',()=>{
  const low=Object.keys(data.units).find(code=>data.units[code].tier==='희귀');
  const copy=JSON.parse(JSON.stringify(data));copy.units[low].bossKiller=true;
  const local=Advisor.create(copy,catalog);
  assert.equal(local.assess(deck({H0B5:1,[low]:1}),{main:'H0B5'}).gaps.bossKiller,1);
  const special=Object.keys(data.units).find(code=>data.units[code].tier==='특수');
  copy.units[special].bossKiller=true;
  assert.equal(Advisor.create(copy,catalog).assess(deck({H0B5:1,[special]:1}),{main:'H0B5'}).gaps.bossKiller,0);
});
test('slows sharing one buff do not stack and the break marker covers every unit with it',()=>{
  const [first,second]=Object.keys(data.units).filter(code=>/^h0/.test(code)&&!data.units[code].effects);
  const copy=JSON.parse(JSON.stringify(data));
  copy.units[first].effects=[['slow',50,3,.5,0,'Bslo']];copy.units[second].effects=[['slow',50,3,.5,0,'Bslo']];
  const local=Advisor.create(copy,catalog);
  assert(Math.abs(local.areaEffects(deck({[first]:1})).slow-25)<1e-9);
  assert(Math.abs(local.areaEffects(deck({[first]:1,[second]:1})).slow-37.5)<1e-9);
  copy.units[second].effects=[['slow',50,3,.5,0]];
  assert(Math.abs(Advisor.create(copy,catalog).areaEffects(deck({[first]:1,[second]:1})).slow-50)<1e-9);
  for(const code of ['H09A','H0AY','h03G'])assert(data.breakMarkerUnits.includes(code),code);
});
test('unit names get the effect tags with the stun equivalent rounded to one decimal',()=>{
  const Tags=require(path.join(web,'unit-role-tags.js')),hash=data.catalogMapHash;
  const tagged=Object.entries(data.units).filter(([,unit])=>Array.isArray(unit.tags));
  assert(tagged.length>100);
  for(const [,unit] of tagged)for(const tag of unit.tags.filter(tag=>/스턴$/.test(tag)))assert.match(tag,/^\d+\.\d스턴$/);
  assert.equal(data.units.h02W.tags[0],'0.9스턴');
  assert(data.units.h02W.tags.includes('이감10')&&data.units.h02W.tags.includes('깎10'));
  assert.equal(Tags.decorate('드래곤 (0.9스턴 이감10 깍10 공속5 공증25) (전설 [스턴])','h02W',data,hash),'드래곤 ('+data.units.h02W.tags.join(' ')+')');
  assert.equal(Tags.decorate('(C) 드래곤 💙','h04D',data,hash),'드래곤 💙 ('+data.units.h04D.tags.join(' ')+')');
  assert.equal(Tags.decorate('드래곤','h02W',data,'a'.repeat(64)),'드래곤');
  assert.equal(Tags.decorate('이름','__proto__',data,hash),'이름');
  assert.deepEqual(Tags.tagsOf('h02W',{...data,schemaVersion:2},hash),[]);
  // 발동형 버프는 툴팁 값, 체젠·마젠은 초당 회복량, 단일·끝딜은 수치 없이.
  assert(data.units.h08Q.tags.includes('발동공증33'));
  assert(data.units.H08Y.tags.includes('마젠5'));
  assert(data.units.h0CI.tags.includes('체젠1')&&data.units.h0CI.tags.includes('마젠1'));
  assert(data.units.h057.tags.includes('끝딜'));
  assert(data.units.H09F.tags.includes('단일'));
  for(const [,unit] of tagged)for(const tag of unit.tags)assert(!/^(?:단일|끝딜)\d/.test(tag),tag);
  // 단일 방깎·단일 암브는 범위 방깎·암브와 따로.
  assert(data.units.H0BX.tags.includes('단일깎40'));
  assert(data.units.h0A3.tags.includes('단일암브')&&!data.units.h0A3.tags.includes('암브'));
  assert(data.units.h039.tags.includes('암브')&&!data.units.h039.tags.includes('단일암브'));
  assert(!(data.units.H08X.tags||[]).some(tag=>/^단일깎/.test(tag)),'armor break step notes are not flat armor reduction');
  // 스킨(맵의 스킨 적용 유닛)은 표기·묶음에서 빠지고 원래 유닛만 표시한다.
  for(const code of ['h0BQ','h0B7','h09U','H0AY','H0C1'])assert(data.units[code]?.skinOf&&!data.units[code].tags&&!data.units[code].board,code);
  assert(data.units.h05C.tags&&data.units.h05C.board);
});
test('trait changes inside the same unit are shown apart and old effect tags never remain',()=>{
  const Tags=require(path.join(web,'unit-role-tags.js')),hash=data.catalogMapHash;
  const traited=Object.keys(data.units).filter(code=>Array.isArray(data.units[code].traitTags));
  assert(traited.length>=5);
  assert(data.units.H0AA.traitTags.some(tag=>/^\+[\d.]+스턴$/.test(tag)),'Vegapunk gains York stun with the trait');
  assert(!(data.units.H0AA.tags||[]).some(tag=>/스턴$/.test(tag)),'the base Vegapunk has no stun');
  assert(data.units.h07M.traitTags.includes('이감60→30'));
  const code=traited[0],shown=Tags.decorate('이름',code,data,hash);
  assert(shown.endsWith('(특강 '+data.units[code].traitTags.join(' ')+')'));
  const plain=Object.keys(data.units).find(code=>!data.units[code].tags&&!data.units[code].traitTags);
  assert.equal(Tags.decorate('요크 💙 (스턴)',plain,data,hash),'요크 💙');
  assert.equal(Tags.decorate(Tags.decorate('이름',code,data,hash),code,data,hash),shown,'decorating twice keeps one set of tags');
});
test('deck buffs and the elder change trigger rates, and a finisher is a role like the boss killer',()=>{
  // 마나로 묶인 스턴(아오키지 초월 A0ZG)은 마젠으로, 확률 스턴은 아군 공속으로 가동률이 오르고, 나스쥬로 공속 감소로 내려간다.
  const alone=advisor.areaEffects(deck({H0B1:1})).stunUptime;
  assert(advisor.areaEffects(deck({H0B1:1,H08Y:1})).stunUptime>alone,'Franky mana regen speeds up Aokiji');
  assert(advisor.areaEffects(deck({H0B1:1}),'나스쥬로').stunUptime<alone,'Nasujuro attack speed penalty');
  assert(advisor.areaEffects(deck({h039:1,H0AA:1})).stunUptime>advisor.areaEffects(deck({h039:1})).stunUptime,'ally attack speed');
  // 단일·끝딜은 필수 역할이 아니다. 마딜 덱에 없으면 같은 조건의 후보 중 단일·끝딜 유닛을 먼저 고른다.
  const noFinisher=Object.keys(data.units).find(code=>data.units[code].direction==='magic'&&data.units[code].line&&!data.units[code].finisher&&!data.units[code].single&&['초월','불멸','영원'].includes(data.units[code].tier));
  assert(noFinisher,'a magic main without finisher exists');
  assert.equal(advisor.assess(deck({[noFinisher]:1}),{main:noFinisher}).gaps.finisher,undefined);
  const copy=JSON.parse(JSON.stringify(data));
  copy.units.zzz1={...JSON.parse(JSON.stringify(data.units.h03Q)),finisher:true};copy.units.zzz2=JSON.parse(JSON.stringify(data.units.h03Q));delete copy.units.zzz2.finisher;delete copy.units.zzz2.single;
  const local=Advisor.create(copy,{...catalog,units:[...catalog.units,{rawcode:'zzz1',tier:'히든',playable:true},{rawcode:'zzz2',tier:'히든',playable:true}]});
  const picked=local.nextStep({counts:{[noFinisher]:1},candidates:[{rawcode:'zzz2',cost:10},{rawcode:'zzz1',cost:10}]});
  assert.equal(picked.pick?.rawcode,'zzz1');
});

const report={passed:tests.filter(row=>row.passed).length,total:tests.length,tests};console.log(JSON.stringify(report,null,2));if(report.passed!==report.total)process.exitCode=1;
