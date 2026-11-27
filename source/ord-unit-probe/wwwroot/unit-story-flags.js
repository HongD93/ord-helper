(function(root,factory){
  const api=factory();
  if(typeof module==="object"&&module.exports)module.exports=api;
  if(root)root.OrdUnitStoryFlags=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(){
  "use strict";
  // 등급 묶음별 스토리 파괴 시간 순위. 묶음 첫 스토리 기준 5위까지 깃발 둘, 10위까지 깃발 하나.
  const hash=value=>typeof value==="string"&&/^[a-f0-9]{64}$/i.test(value);
  const rankOf=(ranks,story,limit)=>{const value=ranks?.[story];return Number.isSafeInteger(value)&&value>=1&&value<=limit?value:null;};

  function normalize(data,catalog){
    const mapHash=catalog?.map?.sha256;
    if(data?.schemaVersion!==1||!hash(mapHash)||data.catalogMapHash!==mapHash||!Array.isArray(data.groups)||!data.groups.length)return null;
    const known=new Map((Array.isArray(catalog.units)?catalog.units:[]).map(unit=>[unit?.rawcode,unit]));
    const entries=new Map();
    for(const group of data.groups){
      const stories=group?.stories,limit=group?.rowCount;
      if(!Array.isArray(stories)||!stories.length||!stories.every(story=>Number.isSafeInteger(story)&&story>0)||
        !Number.isSafeInteger(limit)||limit<1||limit>512||!Array.isArray(group.units)||group.units.length>limit)return null;
      const used=stories.map(()=>new Set());
      for(const unit of group.units){
        if(known.get(unit?.rawcode)?.playable!==true||entries.has(unit.rawcode))return null;
        const ranks=stories.map(story=>rankOf(unit.ranks,story,limit));
        if(ranks.some((rank,index)=>rank===null||used[index].has(rank)))return null;
        ranks.forEach((rank,index)=>used[index].add(rank));
        entries.set(unit.rawcode,{stories:stories.slice(),ranks});
      }
    }
    return entries;
  }
  function marks(entry){
    const rank=entry?.ranks?.[0];
    return !Number.isSafeInteger(rank)?"":rank<=5?"🚩🚩":rank<=10?"🚩":"";
  }
  const oldFlags=/🚩[︎️]?/gu;
  const hearts=/(?:💖|💗|💙|💜|🤍|❤|♥)/u;
  const tidy=value=>value.replace(/\s+/gu," ").trim();
  // 순위가 있는 유닛은 이름에 원래 있던 깃발(참고 이름 표기)을 지우고 순위 깃발을 이름 바로 뒤, 하트·선택지 괄호 앞에 둔다.
  // 순위 자료에 없는 유닛은 이름을 바꾸지 않는다.
  function decorate(name,rawcode,entries){
    if(typeof name!=="string"||!(entries instanceof Map)||!entries.has(rawcode))return name;
    const flags=marks(entries.get(rawcode)),base=tidy(name.replace(oldFlags," "));
    const rank=base.match(/^\s*(?:\([SABCDF][+-]?\)\s*)+/iu),start=rank?.[0].length||0,rest=base.slice(start);
    const positions=[rest.search(hearts),rest.indexOf("(")].filter(index=>index>=0);
    const at=positions.length?start+Math.min(...positions):base.length;
    return tidy([base.slice(0,at).trim(),flags,base.slice(at).trim()].filter(Boolean).join(" "));
  }
  function label(rawcode,entries){
    const entry=entries instanceof Map?entries.get(rawcode):null;
    if(!entry)return "";
    return entry.stories.map((story,index)=>story+"번 "+(index?"":"스토리 ")+entry.ranks[index]+"위").join(" · ");
  }
  return {normalize,marks,decorate,label};
});
