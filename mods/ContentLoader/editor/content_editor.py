"""Nine Lives content editor.

A small local web page for making ContentLoader content packs: item prices and stats,
loot tables and shop stock.

Run:   python content_editor.py              (finds the game folder by itself)
       python content_editor.py --game "D:\\Games\\Ninelives"
Then the editor opens in your browser at http://127.0.0.1:8096

It reads what ContentLoader exported (play once with ContentLoader installed and enter the
world) and saves packs to <game>\\Mods\\Content\\<pack>.json. In game, type "reloadcontent"
in the DevTools command box (F7) to load changes without restarting.
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
PORT = 8096
PACK_NAME = re.compile(r"^[A-Za-z0-9 _\-]{1,60}$")


def find_game(explicit):
    if explicit:
        return os.path.abspath(explicit)
    d = HERE
    for _ in range(6):
        if os.path.exists(os.path.join(d, "Ninelives.exe")):          # installed: <game>\Mods\ContentLoader\editor
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

    def do_GET(self):
        u = urlparse(self.path)
        if u.path in ("/", "/index.html"):
            with open(os.path.join(HERE, "editor.html"), "rb") as f:
                return self.send(200, f.read(), "text/html; charset=utf-8")
        if u.path == "/api/data":
            ql = os.path.join(self.game, "Mods", "QuestLoader", "export")
            packs = sorted(os.path.splitext(f)[0] for f in os.listdir(self.packs) if f.endswith(".json"))
            return self.send_json({
                "game": self.game,
                "items": read_json(os.path.join(self.export, "items.json"), {}),
                "loot": read_json(os.path.join(self.export, "loot.json"), {}),
                "shops": read_json(os.path.join(self.export, "shops.json"), {}),
                "monsters": read_json(os.path.join(self.export, "monsters.json"), []),
                # From QuestLoader, if installed: NPC names (shops are keyed by NPC id) and extra item names.
                "npcs": read_json(os.path.join(ql, "npcs.json"), []),
                "itemNames": read_json(os.path.join(ql, "items.json"), []),
                "packs": packs,
            })
        if u.path == "/api/pack":
            path, name = self.pack_path(u.query)
            if not path:
                return self.send_json({"error": "bad pack name"}, 400)
            return self.send_json(read_json(path, {"name": name}))
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
            if not isinstance(pack, dict):
                raise ValueError("expected a JSON object")
        except Exception as e:
            return self.send_json({"error": "Couldn't read the pack: %s" % e}, 400)
        if os.path.exists(path):
            shutil.copyfile(path, path + ".bak")       # one step of undo
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(pack, f, ensure_ascii=False, indent=2)
        os.replace(tmp, path)
        self.send_json({"saved": path})


def main():
    ap = argparse.ArgumentParser(description="Nine Lives content editor")
    ap.add_argument("--game", help="folder that holds Ninelives.exe")
    ap.add_argument("--no-browser", action="store_true")
    args = ap.parse_args()

    game = find_game(args.game)
    if not game or not os.path.exists(os.path.join(game, "Ninelives.exe")):
        sys.exit("Can't find the game. Run with --game \"path\\to\\Ninelives\" (the folder with Ninelives.exe).")
    Editor.game = game
    Editor.export = os.path.join(game, "Mods", "ContentLoader", "export")
    Editor.packs = os.path.join(game, "Mods", "Content")
    os.makedirs(Editor.packs, exist_ok=True)
    if not os.path.exists(os.path.join(Editor.export, "loot.json")):
        print("Note: no exported data yet. Install ContentLoader, play until you're in the world once, then reload the editor.")

    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Editor)
    url = "http://127.0.0.1:%d/" % PORT
    print("Content editor running at %s  (game: %s)  - Ctrl+C to stop" % (url, game))
    if not args.no_browser:
        webbrowser.open(url)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
