// Monsters as ContentLoader 1.4 exports them (Mods\ContentLoader\export\monsters.json): shared by the
// Quests and Enemies pages. Needs the page's esc().

const zoneName = z => String(z || "").replace(/^Zone_/, "").replace(/([a-z])([A-Z])/g, "$1 $2");
const fmt = v => Math.abs(v) >= 100 ? String(Math.round(v)) : String(+(+v).toFixed(1));

/** One entry per name in a zone (kills count by name; a few kinds have more than one loot table).
 *  Stats are the basic kind's (the lowest loot table), the one EnemyLoader spawns. */
function mergeMonsters(list) {
  const by = {};
  (list || []).filter(m => m && m.name && m.zone).forEach(m => {
    const k = m.zone + "|" + m.name, o = by[k];
    if (!o) { by[k] = { ...m, drops: [m.dropId], spots: (m.spots || []).slice(), _base: m.stats ? m.dropId : Infinity }; return; }
    o.drops.push(m.dropId); o.seen = Math.max(o.seen || 0, m.seen || 0);
    const have = new Set(o.spots.map(p => p + ""));
    (m.spots || []).forEach(p => { if (!have.has(p + "")) o.spots.push(p); });
    if (m.stats && m.dropId < o._base) { o.stats = m.stats; o._base = m.dropId; if (m.cid > 0) o.cid = m.cid; }
    if (!o.pic && m.pic) o.pic = m.pic;
    if (!o.title && m.title) o.title = m.title;
    if (m.shownAs) o.shownAs = m.shownAs;
    if (!(o.cid > 0) && m.cid > 0) o.cid = m.cid;
  });
  return Object.values(by);
}

function damageRange(s) {
  const w = s.weaponDamage > 0, lo = w ? s.weaponDamage : (s.damage || 0), rnd = w ? (s.weaponDamageRandom || 0) : (s.damageRandom || 0);
  return lo > 0 ? [lo, lo + rnd] : null;
}
function damageOf(s) { const d = damageRange(s); return d ? (d[1] > d[0] ? `${fmt(d[0])}–${fmt(d[1])}` : fmt(d[0])) : null; }

const STATROWS = [
  ["Health", s => s.health && fmt(s.health)], ["Damage", damageOf], ["Attack every", s => s.weaponSpeed && fmt(1 / s.weaponSpeed) + " s"],
  ["Attack power", s => s.attackPower && fmt(s.attackPower)], ["Willpower", s => s.willpower && fmt(s.willpower)],
  ["Defense", s => s.defense && fmt(s.defense)], ["Resistance", s => s.resistance && fmt(s.resistance)],
  ["Dodge", s => s.avoidance && fmt(s.avoidance)], ["Critical", s => s.critRate && fmt(s.critRate)],
  ["Heals", s => s.healthRecovery && fmt(s.healthRecovery) + "/s"], ["Speed", s => s.moveSpeed && fmt(s.moveSpeed)],
  ["Notices you at", s => s.sightRange && fmt(s.sightRange) + " m"], ["Reach", s => s.attackRange && fmt(s.attackRange) + " m"],
  ["Exp", s => s.exp && fmt(s.exp)], ["Gold", s => s.gold && fmt(s.gold)],
];
function shortStats(m) {
  const s = m.stats || {}, bits = [];
  if (s.health) bits.push("❤ " + fmt(s.health));
  const d = damageOf(s); if (d) bits.push("⚔ " + d);
  if (s.exp) bits.push(fmt(s.exp) + " exp");
  return bits.join(" · ") || "no stats yet";
}
const mpic = (m, big) => m && m.pic ? `<img class="mpic${big ? " big" : ""}" src="/api/monster/${esc(m.pic)}" alt="" loading="lazy">`
  : `<span class="mpic none${big ? " big" : ""}" title="${esc(noPicWhy(m))}">${esc(((m && m.name) || "?").charAt(0))}${big ? `<small>${esc(noPicWhy(m))}</small>` : ""}</span>`;
// Pictures are taken in game, of monsters near you, in the zone you're in.
const noPicWhy = m => m && m.zone ? `No picture yet: it's taken next time you're near one in ${zoneName(m.zone)}` : "No picture yet";

// Where they mostly are: the busiest 40 m cells (with their neighbours), at least 150 m apart.
function hotspots(spots) {
  if (!spots || !spots.length) return [];
  const C = 40, cells = {};
  spots.forEach(([x, z]) => { const k = Math.floor(x / C) + "," + Math.floor(z / C); (cells[k] = cells[k] || []).push([x, z]); });
  const around = k => { const [cx, cz] = k.split(",").map(Number), pts = []; for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) pts.push(...(cells[(cx + dx) + "," + (cz + dz)] || [])); return pts; };
  const ranked = Object.keys(cells).map(k => ({ k, pts: around(k) })).sort((a, b) => b.pts.length - a.pts.length);
  const out = [];
  for (const c of ranked) {
    if (out.length && c.pts.length < ranked[0].pts.length * 0.35) break;
    const x = c.pts.reduce((a, p) => a + p[0], 0) / c.pts.length, z = c.pts.reduce((a, p) => a + p[1], 0) / c.pts.length;
    if (out.some(o => Math.hypot(o.x - x, o.z - z) < 150)) continue;
    const r = Math.max(20, Math.sqrt(c.pts.reduce((a, p) => a + (p[0] - x) ** 2 + (p[1] - z) ** 2, 0) / c.pts.length) * 1.4);
    out.push({ x, z, r, n: c.pts.length });
    if (out.length === 3) break;
  }
  return out;
}

// ---------------------------------------------------------------- how tough is it
// The game has no levels (exp buys skills), so this is our own scale: how hard a fight is, from how much
// it can take (health, defense) and how hard it hits (damage per second), placed among all the game's
// monsters. Level 1 = the weakest the game has, 30 = the toughest; tweaked ones can go past 30.
function powerOf(s) {
  if (!s || !(s.health > 0)) return 0;
  const d = damageRange(s), avg = d ? (d[0] + d[1]) / 2 : 1;
  const every = s.weaponSpeed > 0 ? 1 / s.weaponSpeed : 2;   // weaponSpeed = swings per second
  const tough = s.health * (1 + Math.max(0, s.defense || 0) / 50);
  return Math.sqrt(tough * (avg / every));
}
let _scale = null;
function levelScale(mons) {
  const ps = mons.map(m => powerOf(m.stats)).filter(p => p > 0).sort((a, b) => a - b);
  if (ps.length < 2) return null;
  return { lo: Math.log(ps[Math.floor(ps.length * 0.03)]), hi: Math.log(ps[ps.length - 1]) };
}
function levelOf(s, mons) {
  const p = powerOf(s); if (!p) return null;
  if (!_scale || _scale.n !== mons.length) _scale = Object.assign(levelScale(mons) || { lo: 0, hi: 1 }, { n: mons.length });
  const t = (Math.log(p) - _scale.lo) / Math.max(0.01, _scale.hi - _scale.lo);
  return Math.max(1, Math.round(1 + 29 * t));
}
const TIERS = [[5, "Beginner"], [11, "Easy"], [18, "Medium"], [24, "Hard"], [30, "Very hard"], [Infinity, "Beyond anything in the game"]];
const tierOf = lv => lv == null ? "" : TIERS.find(t => lv <= t[0])[1];
/** Known monsters about as tough (by power), nearest first. */
function similarTo(s, mons, n = 3, skipName) {
  const p = powerOf(s); if (!p) return [];
  const seen = new Set();
  return mons.filter(m => powerOf(m.stats) > 0 && m.name !== skipName)
    .sort((a, b) => Math.abs(Math.log(powerOf(a.stats) / p)) - Math.abs(Math.log(powerOf(b.stats) / p)))
    .filter(m => !seen.has(m.name) && seen.add(m.name)).slice(0, n);
}
const levelBadge = (lv, small) => lv == null ? `<span class="lvl none" title="No stats yet">Lv ?</span>`
  : `<span class="lvl t${TIERS.findIndex(t => lv <= t[0])}${small ? " sm" : ""}" title="${esc(tierOf(lv))} — suggested fighting level (our own 1–30 scale)">Lv ${lv}</span>`;
