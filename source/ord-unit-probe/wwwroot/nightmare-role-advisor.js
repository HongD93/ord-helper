(function(root,factory){
  const api=factory();
  if(typeof module==="object"&&module.exports)module.exports=api;
  if(root)root.OrdNightmareRoleAdvisor=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(){
  "use strict";
  // 보유 패 기준 역할 추천(2.331 악몽 65라운드). 자료: data/nightmare-role-profile-2.331.json.
  // 팀 처치 시간은 분석 폴더 team-calc-2331.cjs와 같은 계산이다.
  // - 팀 방깎 = 범위 방깎(상시 + 발동형 x 가동률) + 아머브레이크 평균 중첩 + 징베 표식 조건부 25(자료 breakMarkerUnits).
  // - 현기증처럼 같은 감속 버프를 쓰는 발동형 이감(효과 6번째 값)은 겹치지 않으므로 하나라도 걸린 시간 비율 x 빈도 가중 감속으로 센다.
  // - 피해 증폭은 팀에서 가장 높은 대상 상태를 유닛 자신이 건 상태와 비교해 피해 종류별 비중에 곱한다.
  // - 범위 스턴은 전설 이상만 세고, 이감과 합친 남은 이동 비율 = (1 - 이감) x (1 - 스턴 가동률)이다.
  const upperTiers=new Set(["초월","불멸","영원","제한","신비","왜곡","변화","세라핌"]);
  const targets=["line","boss","enraged"];
  const finite=x=>typeof x==="number"&&Number.isFinite(x);
  const countsOf=value=>{
    const out=new Map();
    const rows=value instanceof Map?Array.from(value):Array.isArray(value)?value.map(row=>Array.isArray(row)?row:[row?.rawcode,row?.count]):Object.entries(value||{});
    for(const [code,count] of rows)if(typeof code==="string"&&Number.isSafeInteger(count)&&count>0)out.set(code,(out.get(code)||0)+count);
    return out;
  };

  function create(data,catalog){
    if(data?.schemaVersion!==1||!catalog?.map?.sha256||data.catalogMapHash!==catalog.map.sha256)return null;
    const elders=Array.isArray(data.elders)?data.elders:[],levels=Array.isArray(data.armorLevels)?data.armorLevels:[];
    if(elders.length!==3||levels.length<2||!data.units||!data.enemies||!data.thresholds)return null;
    const units=data.units,stunTiers=new Set(data.stunTiers||[]),known=new Map((catalog.units||[]).map(unit=>[unit.rawcode,unit]));
    const tierOf=code=>known.get(code)?.tier??units[code]?.tier??null;
    const breakMarkerUnits=Array.isArray(data.breakMarkerUnits)?data.breakMarkerUnits:["H09A"];
    const groupedSlow=list=>{
      let rate=0,weighted=0;
      for(const {value,uptime,count} of list){const r=-Math.log(1-Math.min(.9999,uptime))*count;rate+=r;weighted+=value*r;}
      return rate>0?(1-Math.exp(-rate))*weighted/rate:0;
    };

    // 처치 시간 표 보간(처리 속도 기준)과 가장 가까운 방깎 단계의 역할 기록.
    function rateAt(seconds,reduction){
      const rates=levels.map((level,index)=>seconds?.[index]?1/seconds[index]:0);
      const value=Math.max(levels[0],Math.min(levels[levels.length-1],reduction));
      const upper=levels.findIndex(level=>level>=value);
      if(upper<=0)return rates[0];
      const lower=upper-1,share=(value-levels[lower])/(levels[upper]-levels[lower]);
      return rates[lower]+(rates[upper]-rates[lower])*share;
    }
    function roleAt(rows,reduction){
      let nearest=0;
      levels.forEach((level,index)=>{if(Math.abs(level-reduction)<Math.abs(levels[nearest]-reduction))nearest=index;});
      const row=rows?.[nearest];
      return Array.isArray(row)?{share:row.slice(0,5),atHit:row.slice(5,10),overTime:row.slice(10,15)}:null;
    }
    // 덱 버프로 바뀐 발동 가동률: 효과 7번째 값(빈도 모형)과 유닛의 공속 배율(as)·초당 공격(ps)·마나 회복(mr)으로 다시 계산한다.
    // 분석 폴더 team-calc-2331.cjs의 rescaledUptime과 같은 계산이다. 공속 배율 상한 5.
    function rescaled(seconds,uptime,model,unit,boost){
      if(!Array.isArray(model)||!(seconds>0)||!(unit?.as>0)||!(unit?.ps>0))return uptime;
      const faster=Math.max(.1,Math.min(5,unit.as+boost.attackSpeed))/unit.as,perSecond=unit.ps*faster,regen=unit.mr||0;
      const [type,first,second,third,wait,regular]=model;
      let frequency=type==="a"?first*faster:type==="m"?(perSecond+regen+boost.manaRegen)/first:(second+boost.healthRegen+third*perSecond)/first;
      if(wait)frequency=1/(1/frequency+wait);
      if(type==="a"&&second)frequency=Math.min(frequency,(perSecond+regen+boost.manaRegen)/second);
      return Number((regular?Math.min(1,frequency*seconds):1-Math.exp(-frequency*seconds)).toFixed(4));
    }
    // 덱의 아군 공속(다른 유닛 것)·마젠·체젠 합과 나스쥬로 공속 -15%.
    function boostsOf(counts,elder){
      const speed=new Map();let manaRegen=0,healthRegen=0;
      for(const [code,count] of counts){
        const unit=units[code];if(!unit?.effects)continue;
        for(const [kind,value,,uptime,aura] of unit.effects){
          const weight=aura?1:finite(uptime)&&uptime>0?1-Math.pow(1-uptime,count):0;
          if(kind==="allyAttackSpeed")speed.set(code,(speed.get(code)||0)+value*weight);
          else if(kind==="allyManaRegen")manaRegen+=value*weight;
          else if(kind==="allyHealthRegen")healthRegen+=value*weight;
        }
      }
      const penalty=elder==="나스쥬로"?-.15:0;
      return code=>({attackSpeed:Array.from(speed).filter(([other])=>other!==code).reduce((sum,[,value])=>sum+value,0)/100+penalty,manaRegen,healthRegen});
    }
    // 덱의 범위·아군 효과(같은 유닛 여러 기: 상시 효과는 한 번, 발동형은 가동률 1 - (1 - u)^n). 가동률은 덱 버프·오로성을 반영한다.
    function areaEffects(counts,elder){
      const total={armor:0,slow:0,stunMiss:1,stunUnits:0},sources={allyAttackDamage:new Map(),allyAttackSpeed:new Map()},grouped=[];
      const boostOf=boostsOf(counts,elders.includes(elder)?elder:null);
      for(const [code,count] of counts){
        const unit=units[code];if(!unit?.effects)continue;
        let stun=false;const boost=boostOf(code);
        for(const [kind,value,seconds,stored,aura,group,model] of unit.effects){
          const uptime=model&&(kind==="stun"||kind==="slow"||kind==="armorReduction")?rescaled(seconds,stored,model,unit,boost):stored;
          const weight=aura?1:finite(uptime)&&uptime>0?1-Math.pow(1-uptime,count):0;
          if(!weight)continue;
          if(kind==="armorReduction")total.armor+=value*weight;
          else if(kind==="slow"&&group&&!aura)grouped.push({value,uptime,count});
          else if(kind==="slow")total.slow+=value*weight;
          else if(kind==="stun"&&stunTiers.has(unit.tier)&&finite(uptime)&&uptime>0){total.stunMiss*=Math.pow(1-uptime,count);stun=true;}
          else if(sources[kind])sources[kind].set(code,(sources[kind].get(code)||0)+value*weight);
        }
        if(stun)total.stunUnits+=count;
      }
      total.slow+=groupedSlow(grouped);
      return {...total,stunUptime:1-total.stunMiss,sources};
    }

    function teamTime(target,elder,input,options){
      const counts=countsOf(input),amplification=options?.amplification!==false,armorBreak=options?.armorBreak!==false;
      const enemy=data.enemies?.[target]?.[elder];if(!enemy||!targets.includes(target))return null;
      const members=Array.from(counts).filter(([code])=>units[code]?.[target]);
      const effects=areaEffects(counts,elder),jinbe=breakMarkerUnits.some(code=>counts.has(code)),lifetimeSolo=seconds=>target==="line"?seconds*.5:seconds;
      let lifetime=30,result=null;
      for(let pass=0;pass<3;pass+=1){
        // 아머브레이크 쌓는 속도(대상 하나당 초당 중첩): 50중첩 도달 시간, 못 닿으면 시간 평균 중첩 x 2 / 단독 생존 시간.
        let stackRate=0;
        if(armorBreak)for(const [code,count] of members){
          const entry=units[code][target],reach=entry.b?.[elder],over=roleAt(entry.r?.[elder],0)?.overTime,seconds=entry.s?.[elder]?.[0];
          if(reach&&reach[0]>0&&reach[1]>.5)stackRate+=count*50/reach[0];
          else if(over&&over[4]>0&&seconds)stackRate+=count*2*over[4]/lifetimeSolo(seconds);
        }
        const reachStacks=stackRate*lifetime;
        const averageBreak=!(stackRate>0)?0:reachStacks<=75?reachStacks/2:75-75*75/(2*reachStacks);
        const reduction=effects.armor+averageBreak+(jinbe&&averageBreak>=10?25:0);
        const best=enemy.base.slice();
        if(amplification)for(const [code] of members){
          const over=roleAt(units[code][target].r?.[elder],reduction)?.overTime;if(!over)continue;
          best[0]=Math.max(best[0],Math.min(25,over[0]));best[1]=Math.max(best[1],Math.min(25,over[1]));
          best[2]=Math.max(best[2],over[2]);best[3]=Math.max(best[3],Math.min(46,over[3]));
        }
        let rate=0;
        for(const [code,count] of members){
          const entry=units[code][target];
          let unitRate=count*rateAt(entry.s?.[elder],reduction);
          const role=roleAt(entry.r?.[elder],reduction);
          if(amplification&&role){
            const own=role.atHit,share=role.share;
            const magic=(.9+.01*best[0])/(.9+.01*own[0])*(1+.01*best[1])/(1+.01*own[1]);
            const explosive=(.79+.01*best[3])/(.79+.01*own[3]);
            const taken=best[2]/Math.max(1e-9,own[2]);
            const others=kind=>Array.from(effects.sources[kind]).filter(([other])=>other!==code).reduce((sum,[,value])=>sum+value,0);
            const attackDamage=1+others("allyAttackDamage")/100;
            const speedNow=units[code].speed;
            const speedFactor=speedNow?Math.min(5,speedNow+others("allyAttackSpeed")/100)/speedNow:1;
            const mix=share[0]*attackDamage+share[1]+share[2]*Math.max(1,magic)+share[3]*Math.max(1,explosive)+share[4];
            unitRate*=mix*Math.max(1,taken)*speedFactor;
          }
          rate+=unitRate;
        }
        const net=rate-enemy.regen/enemy.hp;
        result={seconds:net>0?1/net:null,reduction,averageBreak,stunUptime:effects.stunUptime,slow:effects.slow};
        lifetime=result.seconds?(target==="line"?result.seconds*.5:result.seconds):lifetime;
      }
      return result;
    }

    const soloLine=code=>{
      const seconds=elders.map(elder=>units[code]?.line?.s?.[elder]?.[levels.indexOf(158)]).filter(finite);
      return seconds.length===elders.length?seconds.reduce((sum,value)=>sum+value,0)/seconds.length:null;
    };
    // 주딜은 초월·불멸·영원·제한급 최상위(자료 mainTiers, 신비는 신비함을 켠 경우만 후보로 들어온다)에서 단독 라인 시간이 기준 이하인 유닛이다.
    const mainTiers=new Set(Array.isArray(data.mainTiers)&&data.mainTiers.length?data.mainTiers:upperTiers);
    const mainEligible=code=>mainTiers.has(tierOf(code))&&finite(soloLine(code))&&soloLine(code)<=(data.mainLineSecondsLimit||360);
    // 덱 방향: 방깎 158 라인 처리 속도를 물딜·마딜로 나눠 큰 쪽.
    function directionOf(counts){
      let physical=0,magic=0;
      for(const [code,count] of counts){const seconds=soloLine(code);if(!seconds)continue;if(units[code].direction==="physical")physical+=count/seconds;else if(units[code].direction==="magic")magic+=count/seconds;}
      return physical>=magic?"physical":"magic";
    }
    const deficit={time:(value,target)=>finite(target)&&target>0?(finite(value)?Math.min(2,Math.max(0,value/target-1)):2):0,
      amount:(value,target)=>finite(target)&&target>0?Math.min(1,Math.max(0,1-value/target)):0};

    // 진단: 오로성이 판별되면 그 오로성, 아니면 세 오로성 평균. 기준은 같은 방향 클리어 기록 중앙값이다.
    function assess(input,options){
      const counts=countsOf(input),elder=elders.includes(options?.elder)?options.elder:null,used=elder?[elder]:elders;
      const direction=options?.direction==="physical"||options?.direction==="magic"?options.direction:directionOf(counts);
      const threshold=(elder?data.thresholds.byElder?.[elder]?.[direction]:null)||data.thresholds[direction];
      const average=(target,key)=>{const values=used.map(item=>teamTime(target,item,counts)?.[key]);return values.every(finite)?values.reduce((sum,value)=>sum+value,0)/values.length:null;};
      const effects=areaEffects(counts,elder),main=options?.main??null;
      const metrics={line:average("line","seconds"),boss:average("boss","seconds"),enraged:average("enraged","seconds"),armor:average("line","reduction"),
        slow:effects.slow,stunUptime:effects.stunUptime,stunUnits:effects.stunUnits,remainingMove:(1-Math.min(95,effects.slow)/100)*(1-effects.stunUptime),
        // 보잡도 스턴과 같이 전설 이상만 센다(희귀 이하는 65라운드 보스·광폭화를 거의 잡지 못한다).
        bossKillers:Array.from(counts).filter(([code])=>units[code]?.bossKiller&&stunTiers.has(units[code].tier)).reduce((sum,[,count])=>sum+count,0),
        // 끝딜(전체체력 N% 피해)·단일(단일 대상 퍼센트 피해) 유닛 수. 전설 이상만 센다.
        finishers:Array.from(counts).filter(([code])=>units[code]?.finisher&&stunTiers.has(units[code].tier)).reduce((sum,[,count])=>sum+count,0),
        finisherOrSingle:Array.from(counts).filter(([code])=>(units[code]?.finisher||units[code]?.single)&&stunTiers.has(units[code].tier)).reduce((sum,[,count])=>sum+count,0)};
      const gaps={line:deficit.time(metrics.line,threshold?.line?.median),boss:deficit.time(metrics.boss,threshold?.boss?.median),enraged:deficit.time(metrics.enraged,threshold?.enraged?.median),
        slow:deficit.amount(metrics.slow,threshold?.slow?.median),stun:deficit.amount(metrics.stunUptime,threshold?.stunUptime?.median),
        armor:direction==="physical"?deficit.amount(metrics.armor,threshold?.armor?.median):0,
        bossKiller:metrics.bossKillers>=1||main&&units[main]?.bossKiller?0:1};
      return {elder,direction,metrics,threshold,gaps,score:Object.values(gaps).reduce((sum,value)=>sum+value,0)};
    }

    // 추천: 주딜을 정하고 부족분을 줄이는 양 / 조합 비용이 큰 유닛을 하나씩 더한다.
    // candidates: [{rawcode,cost}] (cost는 지금 패 기준 남은 조합 비용, 호출 쪽 계산). 이미 가진 유닛은 counts에 있다.
    function recommend(options){
      const counts=countsOf(options?.counts),candidates=(options?.candidates||[]).filter(row=>typeof row?.rawcode==="string"&&finite(row.cost)&&row.cost>=0&&units[row.rawcode]);
      const upperLimit=Number.isSafeInteger(options?.upperLimit)?options.upperLimit:Infinity,maxPicks=Number.isSafeInteger(options?.maxPicks)?options.maxPicks:8;
      // 유닛 하나는 덱 자리(인구)를 차지하므로 조합 비용에 자리 비용을 더하고, 부족분을 조금만 줄이는 유닛은 고르지 않는다.
      const slotCost=finite(options?.slotCost)&&options.slotCost>=0?options.slotCost:8,minimumGain=finite(options?.minimumGain)&&options.minimumGain>=0?options.minimumGain:.05;
      const upperCount=deck=>Array.from(deck).filter(([code])=>upperTiers.has(tierOf(code))).reduce((sum,[,count])=>sum+count,0);
      // 묶음당 최대 수(예: 초월 영웅과 니카는 덱 전체 1기). 이미 가진 수까지 센다.
      const groups=(options?.limitedGroups||[]).map(group=>({set:new Set(group?.rawcodes||[]),maximum:Number.isSafeInteger(group?.maximum)?group.maximum:Infinity}));
      const groupAllows=(deck,code)=>groups.every(group=>!group.set.has(code)||Array.from(deck).filter(([other])=>group.set.has(other)).reduce((sum,[,count])=>sum+count,0)+1<=group.maximum);
      let main=options?.main&&units[options.main]?options.main:null,mainReason=main?"chosen":null;
      if(!main){
        const owned=Array.from(counts.keys()).filter(mainEligible).sort((a,b)=>soloLine(a)-soloLine(b));
        if(owned.length){main=owned[0];mainReason="owned";}
      }
      if(!main){
        const ranked=candidates.filter(row=>mainEligible(row.rawcode)&&groupAllows(counts,row.rawcode)&&(upperTiers.has(tierOf(row.rawcode))?upperCount(counts)+1<=upperLimit:true)).sort((a,b)=>a.cost-b.cost||soloLine(a.rawcode)-soloLine(b.rawcode));
        if(ranked.length){main=ranked[0].rawcode;mainReason="fastest";}
      }
      const deck=new Map(counts);
      if(main&&!deck.has(main))deck.set(main,1);
      // 방향은 주딜을 따른다(역할 유닛을 더해도 물딜·마딜 기준이 바뀌지 않게).
      const settings={elder:options?.elder,main,direction:main?units[main]?.direction:undefined};
      const before=assess(counts,settings);
      let current=assess(deck,settings);
      const picks=[];
      for(let step=0;step<maxPicks&&current.score>1e-9;step+=1){
        let best=null;
        for(const row of candidates){
          if(deck.has(row.rawcode)||picks.some(pick=>pick.rawcode===row.rawcode))continue;
          if(upperTiers.has(tierOf(row.rawcode))&&upperCount(deck)+1>upperLimit)continue;
          if(!groupAllows(deck,row.rawcode))continue;
          const trial=new Map(deck);trial.set(row.rawcode,1);
          const after=assess(trial,settings),gain=current.score-after.score;
          if(!(gain>=minimumGain))continue;
          // 단일·끝딜은 필수 역할이 아니다. 마딜 덱에 아직 없으면 같은 조건에서 단일·끝딜 유닛을 조금 먼저 고른다(마딜 보조에 단일·끝딜이 많다).
          const prefer=current.direction==="magic"&&!(current.metrics.finisherOrSingle>0)&&(units[row.rawcode]?.finisher||units[row.rawcode]?.single)&&stunTiers.has(tierOf(row.rawcode))?1.15:1;
          const value=gain*prefer/(row.cost+slotCost);
          if(!best||value>best.value)best={rawcode:row.rawcode,cost:row.cost,gain,value,after};
        }
        if(!best)break;
        const roles=Object.keys(current.gaps).filter(key=>best.after.gaps[key]<current.gaps[key]-1e-9);
        picks.push({rawcode:best.rawcode,cost:best.cost,gain:Number(best.gain.toFixed(4)),roles});
        deck.set(best.rawcode,1);current=best.after;
      }
      const goals=[...(main&&!counts.has(main)?[{rawcode:main,count:1,role:"main"}]:[]),...picks.map(pick=>({rawcode:pick.rawcode,count:1,role:pick.roles[0]||"support"}))];
      return {main,mainReason,direction:current.direction,before,after:current,picks,goals};
    }

    return {teamTime,assess,recommend,areaEffects,mainEligible,soloLine,unit:code=>units[code]||null};
  }
  return {create,upperTiers};
});
