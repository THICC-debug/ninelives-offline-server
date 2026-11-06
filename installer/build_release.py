"""Build the player release: installer/release/NineLivesOffline-<version>.zip

    python installer/build_release.py [--game "path\\to\\Ninelives"]

Needs the .NET 8 SDK and an install of the game (the loader and mods compile against its code;
nothing is written into the game folder). Downloads the official Python embeddable package
from python.org once (cached in installer/.cache) and checks its SHA-256.

The zip holds Setup.exe and its files\\ folder: the mod loader, the server, Python, and one
release zip per mod. No game files go in it.
"""

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

PYTHON_VERSION = "3.13.17"
PYTHON_URL = "https://www.python.org/ftp/python/%s/python-%s-embed-win32.zip" % (PYTHON_VERSION, PYTHON_VERSION)

# The mods in the release. default = ticked on a fresh install.
# (id, default, description, editor name, editor script)
MODS = [
    ("TravelMod", True, "F10 menu to jump to the binding stone, shops and other places", "", ""),
    ("QuestLoader", True, "Adds quests from quest packs (Mods\\Quests). Comes with a quest editor.",
     "Quest Editor", "Mods\\QuestLoader\\editor\\quest_editor.py"),
    ("ContentLoader", True, "Change item prices and stats, loot and shop stock with content packs. Comes with an editor.",
     "Content Editor", "Mods\\ContentLoader\\editor\\content_editor.py"),
    ("NPCLoader", False, "Add your own NPCs (quest givers, vendors) from NPC packs (Mods\\NPCs). New.", "", ""),
    ("DevTools", False, "F7 command box for testing: teleport, give items and gold, set flags. Cheats!", "", ""),
]

SERVER_FILES = ["ninelives_server.py", "aes.py", "recipes.json", "README.md", "LICENSE", "PROTOCOL.md", "ITEMS.md", "MODDING.md"]


def run(cmd):
    print(">", " ".join(cmd))
    r = subprocess.run(cmd, cwd=REPO)
    if r.returncode != 0:
        sys.exit("Failed: " + " ".join(cmd))


def csproj_version(path):
    m = re.search(r"<Version>([^<]+)</Version>", open(path, encoding="utf-8-sig").read())
    return m.group(1)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def get_python(stage):
    cache = os.path.join(HERE, ".cache")
    os.makedirs(cache, exist_ok=True)
    zpath = os.path.join(cache, os.path.basename(PYTHON_URL))
    if not os.path.exists(zpath):
        print("Downloading", PYTHON_URL)
        urllib.request.urlretrieve(PYTHON_URL, zpath + ".part")
        os.replace(zpath + ".part", zpath)
    pin = os.path.join(HERE, "python-embed.sha256")
    got = sha256(zpath)
    if os.path.exists(pin):
        want = open(pin).read().split()[0]
        if got != want:
            sys.exit("Python download doesn't match python-embed.sha256 (%s). Delete installer/.cache and retry." % got)
    else:
        with open(pin, "w") as f:
            f.write("%s  %s\n" % (got, os.path.basename(zpath)))
        print("Pinned Python SHA-256 in", pin)
    dest = os.path.join(stage, "python")
    with zipfile.ZipFile(zpath) as z:
        z.extractall(dest)
    # The embeddable Python only looks in the folders its ._pth file lists, not the script's folder.
    # Add the server folder (python\..) so ninelives_server.py can import aes.py.
    pth = next(f for f in os.listdir(dest) if f.endswith("._pth"))
    with open(os.path.join(dest, pth), "a", encoding="utf-8") as f:
        f.write("\n# Nine Lives Offline: the server folder (for aes.py)\n..\n")
    return dest


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=os.path.join(os.path.dirname(REPO), "Ninelives"),
                    help="a Nine Lives install to compile against (default: ..\\Ninelives)")
    args = ap.parse_args()
    game = os.path.abspath(args.game)
    if not os.path.exists(os.path.join(game, "Ninelives.exe")):
        sys.exit("Can't find the game at %s. Pass --game." % game)

    setup_proj = os.path.join(HERE, "Setup", "Setup.csproj")
    version = csproj_version(setup_proj)
    out_dir = os.path.join(HERE, "release")
    stage = os.path.join(HERE, "obj", "release", "NineLivesOffline-" + version)
    if os.path.exists(stage):
        shutil.rmtree(stage)
    files = os.path.join(stage, "files")
    os.makedirs(files)
    gd = "-p:GameDir=" + game

    # Mod loader (+ Harmony).
    run(["dotnet", "build", "-c", "Release", "-v", "q", "-nologo", "modloader/NineLivesModLoader", gd])
    loader_out = os.path.join(REPO, "modloader", "NineLivesModLoader", "bin", "Release", "net35")
    os.makedirs(os.path.join(files, "loader"))
    for f in ("NineLivesModLoader.dll", "0Harmony.dll"):
        shutil.copy2(os.path.join(loader_out, f), os.path.join(files, "loader", f))

    # Mods: each one's own release zip (the same zip that's released on its own).
    os.makedirs(os.path.join(files, "mods"))
    lines = ["# id | version | zip | default | description | editor name | editor script"]
    for mid, default, desc, ed_name, ed_script in MODS:
        proj = os.path.join(REPO, "mods", mid, mid + ".csproj")
        ver = csproj_version(proj)
        run(["dotnet", "build", "-c", "Release", "-v", "q", "-nologo", "-t:Package", proj, gd, "-p:NoInstall=true"])
        zname = "%s-%s.zip" % (mid, ver)
        shutil.copy2(os.path.join(REPO, "mods", mid, "release", zname), os.path.join(files, "mods", zname))
        lines.append(" | ".join([mid, ver, zname, "1" if default else "0", desc, ed_name, ed_script]))
    with open(os.path.join(files, "mods.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    # Server and docs.
    os.makedirs(os.path.join(files, "server"))
    for f in SERVER_FILES:
        shutil.copy2(os.path.join(REPO, f), os.path.join(files, "server", f))

    get_python(files)

    # Setup.exe
    run(["dotnet", "build", "-c", "Release", "-v", "q", "-nologo", setup_proj])
    shutil.copy2(os.path.join(HERE, "Setup", "bin", "Release", "net48", "Setup.exe"), os.path.join(stage, "Setup.exe"))
    shutil.copy2(os.path.join(HERE, "README.txt"), os.path.join(stage, "README.txt"))

    # Nothing from the game may end up in the release.
    for root, _, names in os.walk(stage):
        for n in names:
            low = n.lower()
            if low.startswith("assembly-") or low.startswith("unity") or low.startswith("save") and low.endswith(".json") or low.endswith(".log"):
                sys.exit("Refusing to package %s" % os.path.join(root, n))

    os.makedirs(out_dir, exist_ok=True)
    zpath = os.path.join(out_dir, "NineLivesOffline-%s.zip" % version)
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, names in os.walk(stage):
            for n in names:
                full = os.path.join(root, n)
                z.write(full, os.path.relpath(full, os.path.dirname(stage)))
    print("\nRelease: %s (%.1f MB)" % (zpath, os.path.getsize(zpath) / 1e6))


if __name__ == "__main__":
    main()
