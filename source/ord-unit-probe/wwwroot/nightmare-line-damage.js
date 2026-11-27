(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  root.OrdNightmareLineDamage = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";

  const isPositive = value => Number.isFinite(value) && value > 0;
  const isNonnegative = value => Number.isFinite(value) && value >= 0;
  const defaultScenario = Object.freeze({ averageAreaTargets: 1, attackUptime: 1 });

  function normalize(goals) {
    const counts = new Map();
    for (const goal of Array.isArray(goals) ? goals : []) {
      const rawcode = typeof goal === "string" ? goal : goal?.rawcode;
      const count = typeof goal === "string" ? 1 : goal?.count;
      if (typeof rawcode !== "string" || !Number.isSafeInteger(count) || count <= 0) continue;
      counts.set(rawcode, (counts.get(rawcode) || 0) + count);
    }
    return counts;
  }

  function settings(scenario) {
    const area = scenario?.averageAreaTargets;
    const uptime = scenario?.attackUptime;
    return {
      averageAreaTargets: isPositive(area) ? area : defaultScenario.averageAreaTargets,
      attackUptime: isNonnegative(uptime) && uptime <= 1 ? uptime : defaultScenario.attackUptime,
      target: "normal_line",
      armorAndResistanceIncluded: false
    };
  }

  function averageAttackDamage(attack) {
    if (!isNonnegative(attack?.baseDamage) || !isNonnegative(attack.dice) ||
        !isNonnegative(attack.sides) || !isPositive(attack.intervalSeconds)) return null;
    return attack.baseDamage + attack.dice * (attack.sides + 1) / 2;
  }

  function skillReference(component, attack, scenario) {
    if (component?.target !== "normal_line" || !isNonnegative(component.damagePerHit) ||
        !isPositive(component.hits) || !["single", "area"].includes(component.scope)) return null;
    const interval = attack?.intervalSeconds;
    const chance = component.attackChance;
    const trigger = component.trigger;
    let activationsPerSecond;
    if (trigger === "attack_proc" && isPositive(interval) && isNonnegative(chance) && chance <= 1) {
      activationsPerSecond = chance * scenario.attackUptime / interval;
    } else if (trigger === "attack_after_cooldown" && isPositive(interval) && isPositive(component.cooldownSeconds)) {
      const attacksPerActivation = Math.ceil(component.cooldownSeconds / interval - 1e-9);
      activationsPerSecond = scenario.attackUptime / (Math.max(1, attacksPerActivation) * interval);
    } else if (trigger === "periodic" && isPositive(component.cycleSeconds)) {
      activationsPerSecond = 1 / component.cycleSeconds;
    } else {
      return null;
    }
    const targets = component.scope === "area" ? scenario.averageAreaTargets : 1;
    return {
      scope: component.scope,
      radius: component.radius ?? null,
      activationsPerSecond,
      expectedDamagePerSecond: activationsPerSecond * component.damagePerHit * component.hits * targets
    };
  }

  function create(catalog, profile) {
    const hash = catalog?.map?.sha256;
    const scriptHash = catalog?.map?.scriptSha256;
    const valid = typeof hash === "string" && /^[a-f0-9]{64}$/i.test(hash) &&
      typeof scriptHash === "string" && /^[a-f0-9]{64}$/i.test(scriptHash) &&
      profile?.schemaVersion === 1 && profile.map?.sha256 === hash &&
      profile.map.scriptSha256 === scriptHash && profile.scope === "normal_line_damage_reference" &&
      profile.units != null && typeof profile.units === "object" && !Array.isArray(profile.units);

    function unit(rawcode, scenario) {
      const model = valid && Object.hasOwn(profile.units, rawcode) ? profile.units[rawcode] : null;
      const attack = model?.attack;
      const meanDamage = averageAttackDamage(attack);
      const attackDamagePerSecond = meanDamage === null ? null : meanDamage * scenario.attackUptime / attack.intervalSeconds;
      const skills = (Array.isArray(model?.components) ? model.components : [])
        .map(component => skillReference(component, attack, scenario)).filter(Boolean);
      const skillDamagePerSecond = skills.length ? skills.reduce((sum, skill) => sum + skill.expectedDamagePerSecond, 0) : null;
      const expectedDamagePerSecond = attackDamagePerSecond === null && skillDamagePerSecond === null ? null :
        (attackDamagePerSecond ?? 0) + (skillDamagePerSecond ?? 0);
      const basis = attackDamagePerSecond === null ? (skills.length ? "skills_only" : "unmodeled") :
        (skills.length ? "weapon_and_modeled_skills" : "weapon_only");
      return { rawcode, basis, attackDamagePerSecond, skillDamagePerSecond, expectedDamagePerSecond, skills };
    }

    function summarize(counts, scenario) {
      const units = Array.from(counts, ([rawcode, count]) => ({ ...unit(rawcode, scenario), count }));
      const known = units.filter(row => row.expectedDamagePerSecond !== null);
      const skillRows = units.filter(row => row.skillDamagePerSecond !== null);
      const attacks = units.filter(row => row.attackDamagePerSecond !== null);
      const unknownUnitCount = units.reduce((sum, row) => sum + (row.expectedDamagePerSecond === null ? row.count : 0), 0);
      return {
        enabled: valid,
        scenario,
        complete: false,
        expectedDamagePerSecond: known.length ? known.reduce((sum, row) => sum + row.expectedDamagePerSecond * row.count, 0) : null,
        attackDamagePerSecond: attacks.length ? attacks.reduce((sum, row) => sum + row.attackDamagePerSecond * row.count, 0) : null,
        skillDamagePerSecond: skillRows.length ? skillRows.reduce((sum, row) => sum + row.skillDamagePerSecond * row.count, 0) : null,
        knownUnitCount: known.reduce((sum, row) => sum + row.count, 0),
        unknownUnitCount,
        skillModeledUnitCount: skillRows.reduce((sum, row) => sum + row.count, 0),
        modeledComponents: units.reduce((sum, row) => sum + row.skills.length, 0),
        units
      };
    }

    function assess(goals, scenario) {
      return summarize(normalize(goals), settings(scenario));
    }

    function compare(originalGoals, candidateGoals, input) {
      if (!valid) return { comparable: false, deltaExpectedDamagePerSecond: null };
      const scenario = settings(input);
      const original = normalize(originalGoals);
      const candidate = normalize(candidateGoals);
      const changes = new Map();
      for (const [rawcode, count] of original) changes.set(rawcode, -count);
      for (const [rawcode, count] of candidate) changes.set(rawcode, (changes.get(rawcode) || 0) + count);
      const changedUnits = Array.from(changes).filter(([, change]) => change !== 0)
        .map(([rawcode, change]) => ({ ...unit(rawcode, scenario), change }));
      const bases = new Set(changedUnits.map(row => row.basis));
      // 같은 종류의 확인된 피해만 대조하며, 조합에 공통으로 남는 유닛은 차이에서 상쇄된다.
      const comparable = bases.size <= 1 && !bases.has("unmodeled");
      const deltaExpectedDamagePerSecond = comparable ?
        changedUnits.reduce((sum, row) => sum + row.expectedDamagePerSecond * row.change, 0) : null;
      return { comparable, deltaExpectedDamagePerSecond };
    }

    return { valid, assess, compare };
  }

  return { create };
});
