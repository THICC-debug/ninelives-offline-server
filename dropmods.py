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
    done, warnings = [], []
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
        "zip": zname, "installed": time.strftime("%Y-%m-%dT%H:%M:%S"), "packs": done, "warnings": warnings,
        "notUnpacked": skipped[:20],
    }
    _write(os.path.join(inst, base + ".json"), note)
    dest = os.path.join(inst, zname)
    if os.path.exists(dest):
        os.remove(dest)
    shutil.move(path, dest)
    log("Mods: installed \"%s\" from %s: %s" % (mod_name, zname, ", ".join("%s\\%s (%s)" % (p["folder"], p["name"], p["result"]) for p in done)))
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
