(function(root,factory){
  const api=factory();
  if(typeof module==="object"&&module.exports)module.exports=api;
  if(root)root.OrdUnitResourceSkills=api;
})(typeof globalThis!=="undefined"?globalThis:this,function(){
  "use strict";
  const hash=value=>typeof value==="string"&&/^[a-f0-9]{64}$/i.test(value);
  const own=(value,key)=>value!==null&&typeof value==="object"&&Object.prototype.hasOwnProperty.call(value,key);
  const hearts=/(?:💖|💗|💙|🤍|❤|♥)[\uFE0E\uFE0F]?/gu;
  const tidy=value=>value.replace(/\s+/gu," ").trim();
  const join=(before,marks,after)=>tidy([before.trim(),marks,after.trim()].filter(Boolean).join(" "));

  function decorate(name,rawcode,nativeDescriptions,catalogMapHash){
    const resource=nativeDescriptions?.resourceSkills;
    if(typeof name!=="string"||typeof rawcode!=="string"||!hash(catalogMapHash)||
      nativeDescriptions?.catalogMapHash!==catalogMapHash||resource?.catalogMapHash!==catalogMapHash||
      resource.schemaVersion!==1||!own(resource.units,rawcode))return name;
    const unit=resource.units[rawcode];
    if(!own(unit,"sourceVerified")||unit.sourceVerified!==true||!own(unit,"health")||!own(unit,"mana")||
      typeof unit.health!=="boolean"||typeof unit.mana!=="boolean")return name;
    const marks=(unit.health?"💖":"")+(unit.mana?"💙":"");
    hearts.lastIndex=0;
    const first=hearts.exec(name);
    hearts.lastIndex=0;
    if(!marks)return tidy(name.replace(hearts,""));
    if(first){
      const before=name.slice(0,first.index);
      const after=name.slice(first.index).replace(hearts,"");
      return join(before,marks,after);
    }
    const rank=name.match(/^\s*(?:\([SABCDF][+-]?\)\s*)+/iu);
    const option=name.indexOf("(",rank?.[0].length||0);
    return option<0?join(name,marks,""):join(name.slice(0,option),marks,name.slice(option));
  }
  return {decorate};
});
