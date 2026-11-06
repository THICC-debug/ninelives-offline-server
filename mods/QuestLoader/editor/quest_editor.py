"""Nine Lives quest editor.

A small local web page for making quest packs for the QuestLoader mod.

Run:   python quest_editor.py              (finds the game folder by itself)
       python quest_editor.py --game "D:\\Games\\Ninelives"
Then the editor opens in your browser at http://127.0.0.1:8095

It reads what QuestLoader exported (play the game once with QuestLoader installed)
and saves packs to <game>\\Mods\\Quests\\<pack>.json. Restart the game to load them.
Python standard library only.
"""

import argparse
import json
import os
import re
import shutil
import sys
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

HERE = os.path.dirname(os.path.abspath(__file__))
PORT = 8095
PACK_NAME = re.compile(r"^[A-Za-z0-9 _\-]{1,60}$")


def find_game(explicit):
    if explicit:
        return os.path.abspath(explicit)
    d = HERE
    for _ in range(6):
        if os.path.exists(os.path.join(d, "Ninelives.exe")):          # installed: <game>\Mods\QuestLoader\editor
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
    except FileNotFoundError:
        return default


class Editor(BaseHTTPRequestHandler):
    game = export = packs = None

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

    def pack_path(self, q):
        name = (parse_qs(q).get("name") or [""])[0].strip()
        if not PACK_NAME.match(name):
            return None, name
        return os.path.join(self.packs, name + ".json"), name

    def custom_npcs(self):
        """NPCs added with the NPCLoader mod (Mods\\NPCs\\*.json), so they show in the NPC picker right away."""
        out, folder = [], os.path.join(self.game, "Mods", "NPCs")
        if not os.path.isdir(folder):
            return out
        for f in sorted(os.listdir(folder)):
            if not f.endswith(".json"):
                continue
            try:
                for n in read_json(os.path.join(folder, f), {}).get("npcs", []):
                    out.append({"cid": n.get("id"), "name": (n.get("name") or "?") + " (custom)", "zone": n.get("zone", ""),
                                "x": n.get("x", 0), "y": n.get("y", 0), "z": n.get("z", 0)})
            except Exception:
                pass
        return out

    def do_GET(self):
        u = urlparse(self.path)
        if u.path in ("/", "/index.html"):
            with open(os.path.join(HERE, "editor.html"), "rb") as f:
                return self.send(200, f.read(), "text/html; charset=utf-8")
        if u.path == "/api/data":
            packs = sorted(os.path.splitext(f)[0] for f in os.listdir(self.packs) if f.endswith(".json"))
            return self.send_json({
                "game": self.game,
                "originals": read_json(os.path.join(self.export, "quests.json"), []),
                "items": read_json(os.path.join(self.export, "items.json"), []),
                "npcs": read_json(os.path.join(self.export, "npcs.json"), []) + self.custom_npcs(),
                "packs": packs,
            })
        if u.path == "/api/pack":
            path, name = self.pack_path(u.query)
            if not path:
                return self.send_json({"error": "bad pack name"}, 400)
            return self.send_json(read_json(path, {"name": name, "quests": []}))
        self.send_json({"error": "not found"}, 404)

    def do_POST(self):
        u = urlparse(self.path)
        if u.path != "/api/pack":
            return self.send_json({"error": "not found"}, 404)
        path, name = self.pack_path(u.query)
        if not path:
            return self.send_json({"error": "Pack names can use letters, numbers, spaces, - and _"}, 400)
        try:
            n = int(self.headers.get("Content-Length") or 0)
            pack = json.loads(self.rfile.read(n).decode("utf-8"))
            if not isinstance(pack, dict) or not isinstance(pack.get("quests"), list):
                raise ValueError("expected {name, quests: [...]}")
        except Exception as e:
            return self.send_json({"error": "Couldn't read the pack: %s" % e}, 400)
        if os.path.exists(path):
            shutil.copyfile(path, path + ".bak")       # one step of undo
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(pack, f, ensure_ascii=False, indent=2)
        os.replace(tmp, path)
        self.send_json({"saved": path, "quests": len(pack["quests"])})


def main():
    ap = argparse.ArgumentParser(description="Nine Lives quest editor")
    ap.add_argument("--game", help="folder that holds Ninelives.exe")
    ap.add_argument("--no-browser", action="store_true")
    args = ap.parse_args()

    game = find_game(args.game)
    if not game or not os.path.exists(os.path.join(game, "Ninelives.exe")):
        sys.exit("Can't find the game. Run with --game \"path\\to\\Ninelives\" (the folder with Ninelives.exe).")
    Editor.game = game
    Editor.export = os.path.join(game, "Mods", "QuestLoader", "export")
    Editor.packs = os.path.join(game, "Mods", "Quests")
    os.makedirs(Editor.packs, exist_ok=True)
    if not os.path.exists(os.path.join(Editor.export, "quests.json")):
        print("Note: no exported quests yet. Install QuestLoader, play until you're in the world once, then reload the editor.")

    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Editor)
    url = "http://127.0.0.1:%d/" % PORT
    print("Quest editor running at %s  (game: %s)  - Ctrl+C to stop" % (url, game))
    if not args.no_browser:
        webbrowser.open(url)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
