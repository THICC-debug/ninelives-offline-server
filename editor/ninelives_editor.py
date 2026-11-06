"""Nine Lives Editor: one local web page for everything you can change in the game.

Tabs: Overview (mods and packs, switch them on/off), Quests (QuestLoader packs), Items / Loot /
Shops (ContentLoader packs) and NPCs (NPCLoader packs).

Run:   python ninelives_editor.py              (finds the game folder by itself)
       python ninelives_editor.py --game "D:\\Games\\Ninelives"
Then the editor opens in your browser at http://127.0.0.1:8090

It reads what the mods export while you play (Mods\\<Mod>\\export) and saves packs to
Mods\\Quests, Mods\\Content and Mods\\NPCs. A pack that's switched off is kept as
<name>.json.disabled, which the mods skip. Python standard library only.
"""

import argparse
import json
import os
import re
import shutil
import socket
import sys
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

HERE = os.path.dirname(os.path.abspath(__file__))
PORT = 8090
PACK_NAME = re.compile(r"^[A-Za-z0-9 _\-]{1,60}$")
OFF = ".disabled"

# kind -> (folder under Mods, the mod that loads it, what's in a pack)
KINDS = {
    "quests": ("Quests", "QuestLoader", "quests"),
    "content": ("Content", "ContentLoader", None),
    "npcs": ("NPCs", "NPCLoader", "npcs"),
}
PAGES = {"/": "index.html", "/index.html": "index.html", "/overview": "overview.html", "/quests": "quests.html",
         "/content": "content.html", "/npcs": "npcs.html", "/style.css": "style.css"}
MODS = ["TravelMod", "QuestLoader", "ContentLoader", "NPCLoader", "DevTools"]


def find_game(explicit):
    if explicit:
        return os.path.abspath(explicit)
    d = HERE
    for _ in range(6):
        if os.path.exists(os.path.join(d, "Ninelives.exe")):          # installed: <game>\Editor
            return d
        sib = os.path.join(d, "Ninelives")
        if os.path.exists(os.path.join(sib, "Ninelives.exe")):        # repo: next to the game folder
            return sib
        d = os.path.dirname(d)
    return None


def read_json(path, default):
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            return json.load(f)
    except (FileNotFoundError, ValueError):
        return default


class Editor(BaseHTTPRequestHandler):
    game = None

    # ------------------------------------------------------------ helpers
    def log_message(self, fmt, *args):
        pass

    def send(self, code, body, ctype="application/json; charset=utf-8"):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def send_json(self, obj, code=200):
        self.send(code, json.dumps(obj, ensure_ascii=False))

    def mods_dir(self, *parts):
        return os.path.join(self.game, "Mods", *parts)

    def export(self, mod, name, default):
        return read_json(self.mods_dir(mod, "export", name), default)

    def folder(self, kind):
        d = self.mods_dir(KINDS[kind][0])
        os.makedirs(d, exist_ok=True)
        return d

    def packs(self, kind):
        """[{name, enabled}] for every pack of a kind, on or off."""
        out = {}
        for f in os.listdir(self.folder(kind)):
            if f.endswith(".json"):
                out[f[:-5]] = True
            elif f.endswith(".json" + OFF):
                out.setdefault(f[:-5 - len(OFF)], False)
        return [{"name": n, "enabled": on} for n, on in sorted(out.items(), key=lambda kv: kv[0].lower())]

    def pack_file(self, kind, name):
        """The pack's file: whichever exists (on or off); a new pack is created switched on."""
        if kind not in KINDS or not PACK_NAME.match(name or ""):
            return None
        base = os.path.join(self.folder(kind), name + ".json")
        if not os.path.exists(base) and os.path.exists(base + OFF):
            return base + OFF
        return base

    def query(self, q, key):
        return (parse_qs(q).get(key) or [""])[0].strip()

    def game_npcs(self):
        """The game's own NPCs (QuestLoader's list also picks up mod NPCs it saw in the world: skip those)."""
        return [n for n in self.export("QuestLoader", "npcs.json", []) if 0 < (n.get("cid") or 0) < 90000]

    def custom_npcs(self):
        out = []
        for p in self.packs("npcs"):
            for n in read_json(self.pack_file("npcs", p["name"]), {}).get("npcs", []) or []:
                out.append({"cid": n.get("id"), "name": n.get("name") or "?", "zone": n.get("zone", ""),
                            "x": n.get("x", 0), "y": n.get("y", 0), "z": n.get("z", 0),
                            "custom": True, "pack": p["name"], "enabled": p["enabled"] and n.get("enabled", True)})
        return out

    def pack_quests(self):
        out = []
        for p in self.packs("quests"):
            for q in read_json(self.pack_file("quests", p["name"]), {}).get("quests", []) or []:
                out.append({"id": q.get("id"), "title": q.get("title"), "giver": q.get("giver"), "ender": q.get("ender"),
                            "pack": p["name"], "enabled": p["enabled"]})
        return out

    def mod_states(self):
        cfg = {}
        try:
            for line in open(self.mods_dir("mods.cfg"), encoding="utf-8-sig"):
                m = re.match(r"\s*([A-Za-z0-9_]+)\s*=\s*(true|false)", line, re.I)
                if m:
                    cfg[m.group(1).lower()] = m.group(2).lower() == "true"
        except FileNotFoundError:
            pass
        return [{"id": m, "installed": os.path.exists(self.mods_dir(m + ".dll")), "enabled": cfg.get(m.lower(), True)} for m in MODS]

    # ------------------------------------------------------------ GET
    def do_GET(self):
        u = urlparse(self.path)
        if u.path in PAGES:
            path = os.path.join(HERE, PAGES[u.path])
            ctype = "text/css; charset=utf-8" if path.endswith(".css") else "text/html; charset=utf-8"
            with open(path, "rb") as f:
                return self.send(200, f.read(), ctype)
        parts = u.path.strip("/").split("/")
        if parts[:1] != ["api"] or len(parts) < 2:
            return self.send_json({"error": "not found"}, 404)

        if parts[1] == "overview":
            return self.send_json({
                "game": self.game,
                "mods": self.mod_states(),
                "packs": {k: [dict(p, size=self.pack_size(k, p["name"])) for p in self.packs(k)] for k in KINDS},
                "exported": {"quests": os.path.exists(self.mods_dir("QuestLoader", "export", "quests.json")),
                             "content": os.path.exists(self.mods_dir("ContentLoader", "export", "loot.json"))},
            })

        kind = parts[1]
        if kind not in KINDS:
            return self.send_json({"error": "not found"}, 404)
        names = [p["name"] for p in self.packs(kind)]
        off = [p["name"] for p in self.packs(kind) if not p["enabled"]]

        if parts[2:] == ["data"]:
            if kind == "quests":
                return self.send_json({
                    "game": self.game, "packs": names, "packsOff": off,
                    "originals": self.export("QuestLoader", "quests.json", []),
                    "items": self.export("QuestLoader", "items.json", []),
                    "npcs": self.game_npcs() + [dict(n, name=n["name"] + " (custom)") for n in self.custom_npcs()],
                })
            if kind == "content":
                return self.send_json({
                    "game": self.game, "packs": names, "packsOff": off,
                    "items": self.export("ContentLoader", "items.json", {}),
                    "loot": self.export("ContentLoader", "loot.json", {}),
                    "variants": self.export("ContentLoader", "variants.json", {}),
                    "shops": self.export("ContentLoader", "shops.json", {}),
                    "monsters": self.export("ContentLoader", "monsters.json", []),
                    "npcs": self.game_npcs() + self.custom_npcs(),
                    "itemNames": self.export("QuestLoader", "items.json", []),
                })
            return self.send_json({
                "game": self.game, "packs": names, "packsOff": off,
                "npcs": self.game_npcs(),
                "custom": self.custom_npcs(),
                "originals": [{"id": q.get("id"), "title": q.get("title"), "giver": q.get("giver"), "ender": q.get("ender")}
                              for q in self.export("QuestLoader", "quests.json", [])],
                "packQuests": self.pack_quests(),
                "shops": sorted((self.export("ContentLoader", "shops.json", {}).get("drops") or {}).keys(), key=int),
            })

        if parts[2:] == ["pack"]:
            name = self.query(u.query, "name")
            path = self.pack_file(kind, name)
            if not path:
                return self.send_json({"error": "bad pack name"}, 400)
            empty = {"name": name}
            if KINDS[kind][2]:
                empty[KINDS[kind][2]] = []
            return self.send_json(read_json(path, empty))
        self.send_json({"error": "not found"}, 404)

    def pack_size(self, kind, name):
        d = read_json(self.pack_file(kind, name), {})
        if kind == "content":
            return len(d.get("items") or {}) + sum(len((d.get(t) or {}).get("drops") or {}) + len((d.get(t) or {}).get("groups") or {})
                                                   for t in ("loot", "shops"))
        return len(d.get(KINDS[kind][2]) or [])

    # ------------------------------------------------------------ POST
    def do_POST(self):
        u = urlparse(self.path)
        parts = u.path.strip("/").split("/")
        if parts[:1] != ["api"] or len(parts) < 3:
            return self.send_json({"error": "not found"}, 404)

        if parts[1:] == ["packs", "toggle"]:
            kind, name, on = self.query(u.query, "kind"), self.query(u.query, "name"), self.query(u.query, "on") == "1"
            path = self.pack_file(kind, name)
            if not path or not os.path.exists(path):
                return self.send_json({"error": "no such pack"}, 404)
            want = os.path.join(self.folder(kind), name + ".json" + ("" if on else OFF))
            if path != want:
                if os.path.exists(want):
                    return self.send_json({"error": "both %s and %s exist; remove one" % (os.path.basename(path), os.path.basename(want))}, 409)
                os.replace(path, want)
            return self.send_json({"ok": True, "enabled": on})

        if parts[1:] == ["mods", "toggle"]:
            mod, on = self.query(u.query, "id"), self.query(u.query, "on") == "1"
            if mod not in MODS:
                return self.send_json({"error": "unknown mod"}, 400)
            cfg = self.mods_dir("mods.cfg")
            lines = open(cfg, encoding="utf-8-sig").read().splitlines() if os.path.exists(cfg) else \
                ["# Which mods load. Set to false to switch a mod off (or use F11 in game). Applies on next start."]
            key, found = mod.lower(), False
            for i, line in enumerate(lines):
                if re.match(r"\s*%s\s*=" % re.escape(key), line, re.I):
                    lines[i], found = "%s = %s" % (key, "true" if on else "false"), True
            if not found:
                lines.append("%s = %s" % (key, "true" if on else "false"))
            with open(cfg, "w", encoding="utf-8") as f:
                f.write("\n".join(lines) + "\n")
            return self.send_json({"ok": True})

        kind = parts[1]
        if kind in KINDS and parts[2:] == ["pack"]:
            name = self.query(u.query, "name")
            path = self.pack_file(kind, name)
            if not path:
                return self.send_json({"error": "Pack names can use letters, numbers, spaces, - and _"}, 400)
            try:
                n = int(self.headers.get("Content-Length") or 0)
                pack = json.loads(self.rfile.read(n).decode("utf-8"))
                if not isinstance(pack, dict):
                    raise ValueError("expected a JSON object")
                list_key = KINDS[kind][2]
                if list_key and not isinstance(pack.get(list_key), list):
                    raise ValueError("expected {name, %s: [...]}" % list_key)
            except Exception as e:
                return self.send_json({"error": "Couldn't read the pack: %s" % e}, 400)
            if os.path.exists(path):
                shutil.copyfile(path, path + ".bak")       # one step of undo
            tmp = path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(pack, f, ensure_ascii=False, indent=2)
            os.replace(tmp, path)
            return self.send_json({"saved": path, "enabled": not path.endswith(OFF),
                                   "count": len(pack.get(KINDS[kind][2]) or []) if KINDS[kind][2] else None})
        self.send_json({"error": "not found"}, 404)



class Server(ThreadingHTTPServer):
    # Python turns on address reuse, which on Windows lets a second copy listen on the same port
    # (requests then go to either one). Off, a second copy fails and says the port is in use.
    allow_reuse_address = False
    daemon_threads = True

    def server_bind(self):
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):    # Windows: no other program may share the port either
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def main():
    ap = argparse.ArgumentParser(description="Nine Lives Editor")
    ap.add_argument("--game", help="folder that holds Ninelives.exe")
    ap.add_argument("--port", type=int, default=PORT)
    ap.add_argument("--no-browser", action="store_true")
    args = ap.parse_args()

    game = find_game(args.game)
    if not game or not os.path.exists(os.path.join(game, "Ninelives.exe")):
        sys.exit("Can't find the game. Run with --game \"path\\to\\Ninelives\" (the folder with Ninelives.exe).")
    Editor.game = game
    try:
        srv = Server(("127.0.0.1", args.port), Editor)
    except OSError:
        url = "http://127.0.0.1:%d/" % args.port
        print("The editor is already running (or port %d is busy). Opening %s" % (args.port, url))
        if not args.no_browser:
            webbrowser.open(url)
        return
    url = "http://127.0.0.1:%d/" % args.port
    print("Nine Lives Editor running at %s  (game: %s)" % (url, game))
    print("Keep this window open while you edit. Close it (or press Ctrl+C) to stop the editor.")
    if not args.no_browser:
        webbrowser.open(url)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
