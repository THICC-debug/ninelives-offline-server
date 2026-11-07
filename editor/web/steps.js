// Rules, conditions and actions: shared by the Scripts and Dialogue tabs.
// Uses the page's globals: D (data), names, packName, esc(), parseId().

// ---------------------------------------------------------------- what rules can do
const WHEN = {
  zone: ["Entering a zone", "Every time you arrive in the zone (or any zone)."],
  enterArea: ["Walking into an area", "A circle around a spot. Stand there in game and use “Use where I'm standing”."],
  leaveArea: ["Walking out of an area", ""],
  talk: ["Talking to an NPC", "Clicking the NPC when close enough to talk."],
  questAccepted: ["Accepting a quest", ""],
  questDone: ["Finishing a quest", ""],
  kill: ["Defeating a monster", "By name (or any monster)."],
  flag: ["A flag turns on or off", "Flags are the game's progress markers. Quests set them when finished."],
  item: ["Having enough of an item", "When the bag reaches that many."],
  timer: ["Every few seconds", "While you're in the world (and in that zone, if you pick one)."],
  key: ["Pressing a key", ""],
};
const IF = { flag: "A flag is on / off", item: "Has an item", zone: "Is in a zone", gold: "Has gold", quest: "Has a quest in progress" };
const THEN = {
  message: "Show a message", wait: "Wait", giveItem: "Give an item", takeItem: "Take an item", gold: "Give or take gold",
  exp: "Give experience", setFlag: "Set a flag", startQuest: "Start a quest", teleport: "Teleport the player",
  showNpc: "Show or hide one of your NPCs", showThing: "Show or hide a placed thing", hidePath: "Hide part of the game's world", dialog: "Start a dialogue", lua: "Run Lua",
};
// [field, label, kind] kinds: text, num, npc, item, quest, zone, monster, bool, key, thing, code, flag
const FIELDS = {
  zone: [["zone", "Zone (blank = any)", "zone"]],
  enterArea: [["zone", "Zone", "zone"], ["x", "X", "num"], ["y", "Y", "num"], ["z", "Z", "num"], ["radius", "Radius (m)", "num"]],
  leaveArea: [["zone", "Zone", "zone"], ["x", "X", "num"], ["y", "Y", "num"], ["z", "Z", "num"], ["radius", "Radius (m)", "num"]],
  talk: [["npc", "NPC (blank = any)", "npc"]],
  questAccepted: [["quest", "Quest (blank = any)", "quest"]],
  questDone: [["quest", "Quest (blank = any)", "quest"]],
  kill: [["monster", "Monster name (blank = any)", "monster"]],
  flag: [["flag", "Flag", "flag"], ["on", "Turns on (untick: off)", "bool"]],
  item: [["item", "Item", "item"], ["count", "How many", "num"]],
  timer: [["seconds", "Every (seconds)", "num"], ["zone", "Only in zone (blank = any)", "zone"]],
  key: [["key", "Key", "key"]],
  // conditions
  "if:flag": [["flag", "Flag", "flag"], ["on", "is on (untick: off)", "bool"]],
  "if:item": [["item", "Item", "item"], ["count", "At least", "num"]],
  "if:zone": [["zone", "Zone", "zone"]],
  "if:gold": [["amount", "At least", "num"]],
  "if:quest": [["quest", "Quest", "quest"]],
  // actions
  message: [["text", "Text ({name} = player's name)", "text"], ["seconds", "Seconds on screen", "num"]],
  wait: [["seconds", "Seconds", "num"]],
  giveItem: [["item", "Item", "item"], ["count", "How many", "num"]],
  takeItem: [["item", "Item", "item"], ["count", "How many", "num"]],
  gold: [["amount", "Amount (minus to take)", "num"]],
  exp: [["amount", "Amount", "num"]],
  setFlag: [["flag", "Flag", "flag"], ["on", "On (untick: off)", "bool"]],
  startQuest: [["quest", "Quest", "quest"]],
  teleport: [["x", "X", "num"], ["y", "Y", "num"], ["z", "Z", "num"]],
  showNpc: [["npc", "NPC (one of yours)", "npc"], ["show", "Show (untick: hide)", "bool"]],
  showThing: [["thing", "Placed thing (World tab)", "thing"], ["show", "Show (untick: hide)", "bool"]],
  hidePath: [["path", "Path (from the World tab's Pick)", "text"], ["hide", "Hide (untick: show again)", "bool"]],
  dialog: [["dialog", "Dialogue (Dialogue tab)", "dialog"]],
  lua: [["code", "Lua (event = what happened)", "code"]],
};
const DEFAULTS = { radius: 5, count: 1, seconds: 4, on: true, show: true, hide: true, amount: 100 };
const KEYS = ["F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12", ..."ABCDEFGHIJKLMNOPQRSTUVWXYZ".split(""), ..."0123456789".split("").map(d => "Alpha" + d), "Space", "Return", "Tab"];

const pick = (kind, id) => id ? `${id} · ${names[kind][id] || "?"}` : "";

function field(obj, f) {
  const [k, label, kind] = f, v = obj[k];
  const common = `data-f="${k}" data-kind="${kind}"`;
  if (kind === "bool") return `<label class="checks" style="display:flex;align-items:end"><label><input type="checkbox" ${common} ${v !== false ? "checked" : ""}>${esc(label)}</label></label>`;
  if (kind === "code") return `<label style="grid-column:1/-1">${esc(label)}<textarea class="snippet" ${common}>${esc(v || "")}</textarea></label>`;
  if (kind === "num") return `<label>${esc(label)}<input type="number" step="any" ${common} value="${v ?? ""}"></label>`;
  if (kind === "dialog") return `<label>${esc(label)}<select ${common}><option value=""></option>${(D.dialogs || []).map(t => `<option value="${esc(t.id)}" ${t.id === v ? "selected" : ""}>${esc(t.name || t.id)}${t.pack !== packName ? " (pack " + esc(t.pack) + ")" : ""}</option>`).join("")}</select></label>`;
  if (kind === "thing") return `<label>${esc(label)}<select ${common}><option value=""></option>${(D.things || []).map(t => `<option value="${esc(t.id)}" ${t.id === v ? "selected" : ""}>${esc(t.name || t.id)} (${esc(zoneName(t.zone))})</option>`).join("")}</select></label>`;
  const list = { npc: "npcList", item: "itemList", quest: "questList", monster: "monsterList", zone: "zoneList", key: "keyList" }[kind];
  const shown = ["npc", "item", "quest"].includes(kind) ? pick(kind, v) : (v ?? "");
  return `<label ${kind === "text" ? 'style="grid-column:1/-1"' : ""}>${esc(label)}<input ${list ? `list="${list}"` : ""} ${common} value="${esc(shown)}" ${kind === "flag" ? 'type="number"' : ""}></label>`;
}
function readField(obj, el) {
  const k = el.dataset.f, kind = el.dataset.kind;
  if (kind === "bool") obj[k] = el.checked;
  else if (kind === "num" || kind === "flag") { if (el.value === "") delete obj[k]; else obj[k] = +el.value; }
  else if (["npc", "item", "quest"].includes(kind)) { const id = parseId(el.value); if (id) obj[k] = id; else delete obj[k]; }
  else { if (el.value === "") delete obj[k]; else obj[k] = el.value; }
}
