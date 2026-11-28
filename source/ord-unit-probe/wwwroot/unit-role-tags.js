(function(root,factory){
  const api=factory();
  if(typeof module==="object"&&module.exports)module.exports=api;
  if(root)root.OrdUnitRoleTags=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(){
  "use strict";
  // 유닛 이름 뒤 효과 표기. 역할 자료(data/nightmare-role-profile-2.331.json)의 units[rawcode].tags를 "(1.5스턴 이감80 폭뎀증10)"처럼 붙이고,
  // 같은 유닛에서 특강하면 바뀌는 점(traitTags)은 "(특강 +1.2스턴)"처럼 따로 붙인다.
  // 맵 해시가 같은 자료일 때만 쓰며, 이름에 남은 이전 효과 표기 괄호(스턴·이감·깍 등)는 새 표기가 없어도 지운다.
  const oldTag=/\s*\((?=[^()]*(?:스턴|이감|깍|깎|공증|공속|암브|뎀증|댐증|마젠|보잡|체젠|체마|방무|마방|받피|특강|단일|끝딜|발동))[^()]*\)/gu;
  const hash=value=>typeof value==="string"&&/^[0-9a-f]{64}$/i.test(value);
  const own=(value,key)=>value!==null&&typeof value==="object"&&Object.prototype.hasOwnProperty.call(value,key);
  const usable=(data,catalogMapHash)=>hash(catalogMapHash)&&data?.schemaVersion===1&&data.catalogMapHash===catalogMapHash&&data.units!==null&&typeof data.units==="object";
  const strings=value=>Array.isArray(value)?value.filter(tag=>typeof tag==="string"&&tag.trim()).map(tag=>tag.trim()):[];
  function tagsOf(rawcode,data,catalogMapHash){
    if(typeof rawcode!=="string"||!usable(data,catalogMapHash)||!own(data.units,rawcode))return [];
    return strings(data.units[rawcode]?.tags);
  }
  function traitTagsOf(rawcode,data,catalogMapHash){
    if(typeof rawcode!=="string"||!usable(data,catalogMapHash)||!own(data.units,rawcode))return [];
    return strings(data.units[rawcode]?.traitTags);
  }
  function decorate(name,rawcode,data,catalogMapHash){
    if(typeof name!=="string"||!usable(data,catalogMapHash))return name;
    const tags=tagsOf(rawcode,data,catalogMapHash),traits=traitTagsOf(rawcode,data,catalogMapHash);
    // 이름 앞 "(S) "·"(A) " 같은 등급 글자도 예전 표시 자료(2.323)의 평가라 지운다.
    const base=name.replace(oldTag,"").replace(/^\s*(?:\([SABCDF][+-]?\)\s*)+/u,"").replace(/\s{2,}/g," ").trim();
    const parts=[base,tags.length?"("+tags.join(" ")+")":"",traits.length?"(특강 "+traits.join(" ")+")":""].filter(Boolean);
    return parts.join(" ");
  }
  return {tagsOf,traitTagsOf,decorate};
});
