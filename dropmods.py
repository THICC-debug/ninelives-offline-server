"""Mods dropped into the game's Mods folder: unpack them into packs.

A mod made with the Nine Lives Editor (Packs & mods > Export a mod) is one .zip of packs. Drop it into
<game>\\Mods and this puts its packs where the mods read them (Mods\\Quests, Mods\\NPCs, ...), then moves
the zip to Mods\\Installed with a note of what came from it. The packs are then ordinary packs: they
load in game and can be opened and changed in the editor.

Runs when the offline server starts (so before the game), and the editor does the same when it opens.

Only pack files (.json in the six pack folders) are ever unpacked, never code: a zip with a .dll or .exe
in it is left where it is (install those mods with Setup.exe).

    python dropmods.py "<game folder>"      (by hand)
"""

import json
import os
import re
import shutil
import sys
import time
import zipfile

KINDS = {  # folder -> list key in a pack (None: content packs)
    "Quests": "quests", "Content": None, "NPCs": "npcs", "Enemies": "enemies", "World": "objects", "Scripts": "rules",
}
ID_LISTS = {"Quests": "quests", "NPCs": "npcs", "Enemies": "enemies"}  # things whose ids must be unique across packs
MANIFEST = "nine-lives-mod.json"
MAX_PACK = 16 * 1024 * 1024
PACK_RE = re.compile(r"(?:^|/)(?:Mods/)?(Quests|Content|NPCs|Enemies|World|Scripts)/([^/]+)\.json(?:\.disabled)?$", re.I)
CODE_RE = re.compile(r"\.(dll|exe|bat|cmd|ps1|vbs|js|scr|msi)$", re.I)


def _folder(name):
    return next(k for k in KINDS if k.lower() == name.lower())


def _tidy(name):
    n = re.sub(r"[^A-Za-z0-9 _\-]", "-", name).strip(" -")[:60]
    return n or "imported"


def _ids(folder, data):
    key = ID_LISTS.get(folder)
    items = data.get(key) if key and isinstance(data, dict) else None
    out = set()
    for it in items if isinstance(items, list) else []:
        if isinstance(it, dict) and isinstance(it.get("id"), (int, float)) and it["id"]:
            out.add(int(it["id"]))
    return out


def _load(path):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except Exception:
        return None


def _write(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
    os.replace(tmp, path)


def read_zip(path):
    """(manifest or None, [(folder, name, data or None, problem or None)], [skipped entries], has_code)"""
    manifest, packs, skipped, code = None, [], [], False
    with zipfile.ZipFile(path) as z:
        for info in z.infolist():
            if info.is_dir():
                continue
            name = info.filename.replace("\\", "/").lstrip("/")
            if name.lower() == MANIFEST or name.lower().endswith("/" + MANIFEST):
                try:
                    manifest = json.loads(z.read(info).decode("utf-8-sig"))
                except Exception:
                    pass
                continue
            if CODE_RE.search(name):
                code = True
            m = PACK_RE.search(name)
            if not m:
                if name.lower() != "readme.txt":
                    skipped.append(name)
                continue
            folder, pname = _folder(m.group(1)), _tidy(m.group(2))
            if any(p[0] == folder and p[1].lower() == pname.lower() for p in packs):
                continue
            if info.file_size > MAX_PACK:
                packs.append((folder, pname, None, "too big (over 16 MB)"))
                continue
            try:
                data = json.loads(z.read(info).decode("utf-8-sig"))
                if not isinstance(data, dict):
                    raise ValueError("expected a JSON object")
                key = KINDS[folder]
                if key and not isinstance(data.get(key), list):
                    raise ValueError("expected {name, %s: [...]}" % key)
                packs.append((folder, pname, data, None))
            except Exception as e:
                packs.append((folder, pname, None, "has a mistake in it: %s" % e))
    return (manifest if isinstance(manifest, dict) else None), packs, skipped, code

# ---------------------------------------------------------------- renumbering, so mods don't clash
# Everyone's editor numbers custom things from the same place (NPCs 90001+, quests 900000001+, quest flags
# 9000+), so two mods from different people nearly always share some numbers, and then one of them breaks
# in game. So on install, a number the mod brings that one of your other packs already uses gets a free
# one, and every place in the mod that points at it follows (quest givers, script and dialogue steps,
# shop lists, Lua code). The numbers it got are kept in its note in Mods\Installed, and an update of the
# same mod gets the same ones again (players keep their quest progress).
NPC_MIN, QUEST_MIN, FLAG_MIN, FLAG_MAX = 90000, 900000000, 9000, 9999


def _num(v):
    return int(v) if isinstance(v, (int, float)) and not isinstance(v, bool) and v == int(v) else None


def _steps(pack):
    """Every script/dialogue step in a script pack: rule when/if/then, dialogue node do and choice do/if."""
    for r in pack.get("rules") or []:
        if isinstance(r, dict):
            if isinstance(r.get("when"), dict):
                yield r["when"]
            for k in ("if", "then"):
                for s in r.get(k) or []:
                    if isinstance(s, dict):
                        yield s
    for d in pack.get("dialogs") or []:
        if not isinstance(d, dict):
            continue
        for n in d.get("nodes") or []:
            if not isinstance(n, dict):
                continue
            for s in n.get("do") or []:
                if isinstance(s, dict):
                    yield s
            for c in n.get("choices") or []:
                if isinstance(c, dict):
                    for k in ("do", "if"):
                        for s in c.get(k) or []:
                            if isinstance(s, dict):
                                yield s


def defined(folder, data):
    """What a pack brings of its own: {"npc": {ids}, "quest": .., "flag": .., "enemy": .., "world": {names}}."""
    out = {"npc": set(), "quest": set(), "flag": set(), "enemy": set(), "world": set()}
    if not isinstance(data, dict):
        return out
    flag = lambda v: (_num(v) is not None and FLAG_MIN <= _num(v) <= FLAG_MAX) and out["flag"].add(_num(v))
    if folder == "NPCs":
        for n in data.get("npcs") or []:
            v = _num(n.get("id")) if isinstance(n, dict) else None
            if v is not None and v >= NPC_MIN:
                out["npc"].add(v)
    elif folder == "Quests":
        for q in data.get("quests") or []:
            if not isinstance(q, dict):
                continue
            v = _num(q.get("id"))
            if v is not None and v > QUEST_MIN:   # (an original quest's id replaces that quest on purpose)
                out["quest"].add(v)
            flag(q.get("clearFlag"))
            for f in q.get("setFlagsOnClear") or []:
                if isinstance(f, dict):
                    flag(f.get("flag"))
    elif folder == "Enemies":
        for e in data.get("enemies") or []:
            v = _num(e.get("id")) if isinstance(e, dict) else None
            if v:
                out["enemy"].add(v)
    elif folder == "World":
        for o in data.get("objects") or []:
            if isinstance(o, dict) and isinstance(o.get("id"), str) and o["id"]:
                out["world"].add(o["id"])
    elif folder == "Scripts":
        for s in _steps(data):
            if s.get("type") == "setFlag":
                flag(s.get("flag"))
    return out


def _free(kind, used, taken):
    """The next number (or name) nobody uses."""
    if kind == "flag":
        for v in range(FLAG_MIN, FLAG_MAX + 1):
            if v not in used and v not in taken:
                return v
        return None
    start = {"npc": NPC_MIN + 1, "quest": QUEST_MIN + 1, "enemy": 1}[kind]
    v = max([start - 1] + [x for x in used | taken if isinstance(x, int)]) + 1
    return v


def _free_name(base, used, taken):
    stem = re.sub(r"-\d+$", "", base) or base
    i = 2
    while "%s-%d" % (stem, i) in used or "%s-%d" % (stem, i) in taken:
        i += 1
    return "%s-%d" % (stem, i)


def plan(incoming, used, previous=None):
    """{kind: {old: new}} for the numbers the mod brings that are already used. incoming: [(folder, data)].
    used: {kind: set} from your other packs. previous: the mapping this mod got last time (kept if still free)."""
    previous = previous or {}
    mine = {k: set() for k in used}
    for folder, data in incoming:
        for k, vals in defined(folder, data).items():
            mine[k] |= vals
    maps = {}
    for kind in ("npc", "quest", "flag", "enemy", "world"):
        m, taken = {}, set()
        for old in sorted(mine[kind], key=str):
            prev = previous.get(kind, {}).get(str(old))
            if prev is not None:   # what it got last time, if nobody else has it now
                prev = prev if kind == "world" else _num(prev)
                if prev is not None and prev not in used[kind] and prev not in taken and (prev == old or prev not in mine[kind]):
                    if prev != old:
                        m[old] = prev
                    taken.add(prev)
                    continue
            if old not in used[kind]:
                taken.add(old)
                continue
            new = _free_name(old, used[kind] | mine[kind], taken) if kind == "world" else _free(kind, used[kind] | mine[kind], taken)
            if new is None:
                continue   # (no free flag left: left as it is, and warned about)
            m[old] = new
            taken.add(new)
        if m:
            maps[kind] = m
    return maps


def _lua(code, maps):
    """Numbers and quoted world names in Lua code."""
    if not isinstance(code, str):
        return code
    nums = {}
    for kind in ("npc", "quest", "flag"):
        nums.update({str(k): str(v) for k, v in maps.get(kind, {}).items()})
    if nums:
        code = re.sub(r"(?<![\w.])(\d{4,10})(?![\w.])", lambda m: nums.get(m.group(1), m.group(1)), code)
    for old, new in maps.get("world", {}).items():
        code = re.sub(r"([\"'])%s\1" % re.escape(old), lambda m: m.group(1) + new + m.group(1), code)
    return code


def apply(folder, data, maps):
    """Renumber one pack of the mod in place, with every reference to the renumbered things."""
    if not maps or not isinstance(data, dict):
        return data
    get = lambda kind, v: maps.get(kind, {}).get(_num(v) if kind != "world" else v, v)
    def fix(d, key, kind):
        if isinstance(d, dict) and key in d and (_num(d[key]) is not None or kind == "world"):
            d[key] = get(kind, d[key])
    if folder == "NPCs":
        for n in data.get("npcs") or []:
            fix(n, "id", "npc")
    elif folder == "Quests":
        for q in data.get("quests") or []:
            if not isinstance(q, dict):
                continue
            fix(q, "id", "quest"); fix(q, "giver", "npc"); fix(q, "ender", "npc"); fix(q, "clearFlag", "flag")
            for f in q.get("setFlagsOnClear") or []:
                fix(f, "flag", "flag")
            req = q.get("requires") if isinstance(q.get("requires"), dict) else {}
            for k in ("flagsAll", "flagsAny"):
                for f in req.get(k) or []:
                    fix(f, "flag", "flag")
    elif folder == "Enemies":
        for e in data.get("enemies") or []:
            fix(e, "id", "enemy")
    elif folder == "World":
        for o in data.get("objects") or []:
            fix(o, "id", "world")
    elif folder == "Scripts":
        for s in _steps(data):
            fix(s, "npc", "npc"); fix(s, "quest", "quest"); fix(s, "flag", "flag")
            if isinstance(s.get("thing"), str):
                s["thing"] = get("world", s["thing"])
            if "code" in s:
                s["code"] = _lua(s["code"], maps)
        for d in data.get("dialogs") or []:
            fix(d, "npc", "npc")
        for sc in data.get("scripts") or []:
            if isinstance(sc, dict) and "code" in sc:
                sc["code"] = _lua(sc["code"], maps)
    elif folder == "Content":
        shops = data.get("shops") if isinstance(data.get("shops"), dict) else None
        drops = shops.get("drops") if shops and isinstance(shops.get("drops"), dict) else None
        if drops and maps.get("npc"):   # a shop NPC's stock is keyed by its id
            shops["drops"] = {str(maps["npc"].get(int(k), k)) if k.isdigit() else k: v for k, v in drops.items()}
    return data


def used_by_others(mods_dir, skip):
    """What your packs use already, leaving out the packs this mod replaces (skip: {(folder, name lower)})."""
    used = {"npc": set(), "quest": set(), "flag": set(), "enemy": set(), "world": set()}
    files = set()
    for folder in KINDS:
        d = os.path.join(mods_dir, folder)
        if not os.path.isdir(d):
            continue
        for f in os.listdir(d):
            m = re.match(r"(.+)\.json(\.disabled)?$", f, re.I)
            if not m or (folder, m.group(1).lower()) in skip:
                continue
            files.add(m.group(1).lower() + ".json")
            for k, vals in defined(folder, _load(os.path.join(d, f)) or {}).items():
                used[k] |= vals
    # quest numbers the game has already seen from a pack (a player's save may still hold them)
    known = _load(os.path.join(mods_dir, "QuestLoader", "export", "known_quests.json"))
    if isinstance(known, dict):
        for qid, pack in known.items():
            if str(qid).isdigit() and not (("Quests", str(pack).lower()[:-5]) in skip):
                used["quest"].add(int(qid))
    return used


def previous_mapping(mods_dir, mod_name, zname):
    """The numbers this mod got when it was installed before (its note in Mods\\Installed)."""
    inst = os.path.join(mods_dir, "Installed")
    if not os.path.isdir(inst):
        return {}
    for f in os.listdir(inst):
        if f.lower().endswith(".json"):
            n = _load(os.path.join(inst, f))
            if isinstance(n, dict) and (n.get("name") == mod_name or n.get("zip") == zname) and isinstance(n.get("renumbered"), dict):
                return n["renumbered"]
    return {}


KIND_WORDS = {"npc": ("NPC", "NPCs"), "quest": ("quest", "quests"), "flag": ("quest flag", "quest flags"),
              "enemy": ("enemy spawn", "enemy spawns"), "world": ("world thing", "world things")}


def install_zip(mods_dir, path, log=print):
    """Unpack one dropped zip. Returns the note written to Mods\\Installed, or None when it was left alone."""
    zname = os.path.basename(path)
    try:
        manifest, packs, skipped, code = read_zip(path)
    except zipfile.BadZipFile:
        log("Mods: %s isn't a zip file - left as it is" % zname)
        return None
    if code:
        log("Mods: %s has programs in it (not just packs) - left as it is. Install code mods with Setup.exe." % zname)
        return None
    if not packs:
        log("Mods: %s has no packs in it - left as it is" % zname)
        return None
    mod_name = (manifest or {}).get("name") or os.path.splitext(zname)[0]
    done, warnings, info = [], [], []
    # numbers your other packs already use: the mod's get free ones (the same as last time for an update)
    valid = [(f, n, d) for f, n, d, p in packs if not p]
    replaces = {(f, n.lower()) for f, n, d in valid
                if os.path.exists(os.path.join(mods_dir, f, n + ".json")) or os.path.exists(os.path.join(mods_dir, f, n + ".json.disabled"))}
    maps = plan([(f, d) for f, n, d in valid], used_by_others(mods_dir, replaces), previous_mapping(mods_dir, mod_name, zname))
    for f, n, d in valid:
        apply(f, d, maps)
    if maps:
        info.append("Renumbered " + ", ".join("%d %s" % (len(m), KIND_WORDS[k][len(m) != 1]) for k, m in maps.items())
                        + " so they don't clash with your other packs (everything in the mod that points at them was changed to match)")
    for folder, name, data, problem in packs:
        if problem:
            done.append({"folder": folder, "name": name, "result": "skipped", "problem": problem})
            warnings.append("%s\\%s %s, so it was left out" % (folder, name, problem))
            continue
        d = os.path.join(mods_dir, folder)
        os.makedirs(d, exist_ok=True)
        on = os.path.join(d, name + ".json")
        off = on + ".disabled"
        mine = on if os.path.exists(on) else off if os.path.exists(off) else None
        if mine and _load(mine) == data:
            done.append({"folder": folder, "name": name, "result": "same"})
            continue
        if mine:  # a newer version of it (or another pack with that name): yours is kept as .bak
            shutil.copy2(mine, on + ".bak")
            if mine == off:
                os.remove(off)
        _write(on, data)
        done.append({"folder": folder, "name": name, "result": "replaced" if mine else "added"})
        # ids another of your packs already uses
        ids = _ids(folder, data)
        if ids:
            for f in sorted(os.listdir(d)):
                m = re.match(r"(.+)\.json$", f)
                if not m or m.group(1).lower() == name.lower():
                    continue
                same = sorted(ids & _ids(folder, _load(os.path.join(d, f)) or {}))
                if same:
                    warnings.append("%s\\%s uses the same id%s as your pack %s (%s): one of them won't work in game while both are on"
                                    % (folder, name, "s" if len(same) > 1 else "", m.group(1), ", ".join(map(str, same[:6]))))
    # keep the zip and a note of what came from it
    inst = os.path.join(mods_dir, "Installed")
    os.makedirs(inst, exist_ok=True)
    base = os.path.splitext(zname)[0]
    note = {
        "name": mod_name, "author": (manifest or {}).get("author", ""), "description": (manifest or {}).get("description", ""),
        "made": (manifest or {}).get("created", ""), "madeWith": (manifest or {}).get("mods", {}),
        "zip": zname, "installed": time.strftime("%Y-%m-%dT%H:%M:%S"), "packs": done, "warnings": warnings, "info": info,
        "notUnpacked": skipped[:20],
        "renumbered": {k: {str(o): n for o, n in m.items()} for k, m in maps.items()},
    }
    _write(os.path.join(inst, base + ".json"), note)
    dest = os.path.join(inst, zname)
    if os.path.exists(dest):
        os.remove(dest)
    shutil.move(path, dest)
    log("Mods: installed \"%s\" from %s: %s" % (mod_name, zname, ", ".join("%s\\%s (%s)" % (p["folder"], p["name"], p["result"]) for p in done)))
    for i in info:
        log("Mods:   " + i)
    for w in warnings:
        log("Mods:   ! " + w)
    return note


def install_dropped(game_dir, log=print):
    """Every .zip dropped straight into <game>\\Mods. Never raises: a problem is logged and the zip left."""
    mods_dir = os.path.join(game_dir, "Mods")
    if not os.path.isdir(mods_dir):
        return []
    notes = []
    for f in sorted(os.listdir(mods_dir)):
        if not f.lower().endswith(".zip"):
            continue
        try:
            n = install_zip(mods_dir, os.path.join(mods_dir, f), log)
            if n:
                notes.append(n)
        except PermissionError:
            log("Mods: couldn't install %s - the Mods folder can't be written to" % f)
        except Exception as e:
            log("Mods: couldn't install %s: %s" % (f, e))
    return notes


if __name__ == "__main__":
    install_dropped(sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
