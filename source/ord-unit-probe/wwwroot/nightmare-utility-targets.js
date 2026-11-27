(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  root.OrdNightmareUtilityTargets = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";

  const ARMOR = 1;
  const AURA_SLOW = 2;
  const TRIGGERED_SLOW = 3;
  const TRIGGERED_ARMOR = 4;
  const TARGETS = Object.freeze({ slow: 102, armorReduction: 211 });

  function normalize(goals) {
    if (Array.isArray(goals)) return goals.map(({ rawcode, count }) => [rawcode, count]);
    if (goals instanceof Map) return goals;
    return Object.entries(goals || {});
  }

  function metric(target, value, complete) {
    return { target, value, deficit: target === null || !complete ? null : Math.max(0, target - value), complete };
  }

  function create(catalog, data) {
    const hash = catalog?.map?.sha256;
    if (!hash || data?.schemaVersion !== 1 || data.scope !== "base_utility_display" || data.map?.sha256 !== hash ||
        !catalog.map.scriptSha256 || data.map?.scriptSha256 !== catalog.map.scriptSha256 || !data.units ||
        !Array.isArray(data.registrations) || !data.registrations.length ||
        data.registrations.length !== data.coverage?.registrations || !Array.isArray(catalog.units) || !catalog.units.length ||
        !Number.isFinite(data.bonuses?.armorReduction) || !Number.isFinite(data.bonuses?.slow)) return null;

    const knownAbilities = new Set();
    const registrations = new Map();
    for (const [order, row] of data.registrations.entries()) {
      if (typeof row?.ability !== "string" || typeof row.group !== "string" || !Number.isFinite(row.value) ||
          !Number.isInteger(row.category) || row.category < ARMOR || row.category > TRIGGERED_ARMOR ||
          !Number.isInteger(row.level) || row.level < 1) return null;
      knownAbilities.add(row.ability);
      if (row.level !== 1) continue;
      if (!registrations.has(row.ability)) registrations.set(row.ability, []);
      registrations.get(row.ability).push({ ...row, order });
    }

    const bindings = new Map();
    for (const { rawcode } of catalog.units) {
      const abilities = Object.hasOwn(data.units, rawcode) ? data.units[rawcode] : null;
      if (Array.isArray(abilities) && abilities.every((ability) => knownAbilities.has(ability))) bindings.set(rawcode, abilities);
    }
    if (!registrations.size || !bindings.size || !Array.from(bindings.values()).some((abilities) => abilities.length)) return null;

    function assess(goals, direction, options = {}) {
      const armorTarget = direction === "physical" || direction === "hybrid" ? TARGETS.armorReduction : null;
      const missingRawcodes = new Set();
      const active = new Set();
      for (const [rawcode, count] of normalize(goals)) {
        if (!Number.isSafeInteger(count) || count <= 0) continue;
        const abilities = bindings.get(rawcode);
        if (!abilities) {
          missingRawcodes.add(rawcode);
          continue;
        }
        for (const ability of abilities) {
          for (const row of registrations.get(ability) || []) {
            if (!row.unit || rawcode === row.unit) active.add(row);
          }
        }
      }

      // 유틸 점수판은 분류별 버프 그룹에 한 항목만 합산한다. 발동형 B00E만 절댓값이 큰 쪽을 선택한다.
      const selected = new Map();
      for (const row of active) {
        const key = row.category + ":" + row.group;
        const previous = selected.get(key);
        if (!previous) { selected.set(key, row); continue; }
        const difference = Math.abs(row.value) - Math.abs(previous.value);
        const preferHigher = row.category === TRIGGERED_ARMOR && row.group === "B00E";
        const wins = preferHigher ? difference > 0 : difference < 0;
        if (wins || (difference === 0 && row.order < previous.order)) selected.set(key, row);
      }

      const values = [0, 0, 0, 0, 0];
      for (const row of selected.values()) values[row.category] += row.value;
      if (options.activeArmorPassive === true) values[ARMOR] += data.bonuses.armorReduction;
      if (options.activeSlowPassive === true) values[AURA_SLOW] += data.bonuses.slow;
      const complete = missingRawcodes.size === 0;
      return {
        basis: "base_utility_display",
        runtimeComplete: false,
        slow: metric(TARGETS.slow, values[AURA_SLOW], complete),
        armorReduction: metric(armorTarget, values[ARMOR], complete),
        triggeredSlow: values[TRIGGERED_SLOW],
        triggeredArmorReduction: values[TRIGGERED_ARMOR],
        missingRawcodes: Array.from(missingRawcodes)
      };
    }

    return { assess };
  }

  return { create, TARGETS };
});
