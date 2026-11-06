"""Nine Lives offline server.

Stands in for the shut-down SmokymonkeyS game server so the game can be played
on this PC. Listens on 127.0.0.1:8080 and stores everything in save.json next
to this script.

Run:  python ninelives_server.py
Then start Ninelives.exe and log in with any email and password.
"""

import base64
import hashlib
import json
import os
import secrets
import sys
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs

import aes

HOST = "127.0.0.1"
PORT = 8080
GAME_VERSION = "0.24.3"           # must match the client exactly
START_GEMS = 100000               # premium "Scarab Gem" balance for new accounts
CHARACTER_LIMIT = 8
KEY = b"RQ82EOnVuZZs1nc2_NZAgr18RfrrNVd7"   # the game's built-in key

HERE = os.path.dirname(os.path.abspath(__file__))
SAVE_PATH = os.path.join(HERE, "save.json")
LOG_PATH = os.path.join(HERE, "server.log")
VERBOSE = "-v" in sys.argv

# Item dock numbers used by the game (ActionDockName enum).
DOCK_BANK = 7
DOCK_SHARE = 11
DIFF_DOCKS = {"statsItems": 1, "bagItems": 2, "actionItems": 4,
              "bankItems": 7, "shareItems": 11}

lock = threading.RLock()


# ---------------------------------------------------------------- storage

def load_db():
    if os.path.exists(SAVE_PATH):
        with open(SAVE_PATH, "r", encoding="utf-8") as f:
            return json.load(f)
    return {"accounts": {}}


def save_db():
    tmp = SAVE_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(DB, f, indent=1)
    os.replace(tmp, SAVE_PATH)


DB = load_db()


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    with open(LOG_PATH, "a", encoding="utf-8") as f:
        f.write(line + "\n")


# ---------------------------------------------------------------- crypto

def decode_request(req):
    """'req' = 40 hex chars + base64(AES(json)); IV is the middle 16 chars."""
    prefix, body = req[:40], req[40:]
    iv = prefix[12:28].encode()
    plain = aes.decrypt_cbc(KEY, iv, base64.b64decode(body))
    return json.loads(plain.decode("utf-8"))


class EncryptedText(str):
    """A handler result sent encrypted as-is (not as JSON)."""


def encode_reply(obj):
    prefix = secrets.token_hex(20)          # 40 chars, fresh every reply
    iv = prefix[12:28].encode()
    if isinstance(obj, EncryptedText):
        data = str(obj).encode("utf-8")
    else:
        data = json.dumps(obj, separators=(",", ":")).encode("utf-8")
    return prefix + base64.b64encode(aes.encrypt_cbc(KEY, iv, data)).decode()


# ---------------------------------------------------------------- helpers

def stable_key(*parts):
    """Session keys that survive a server restart."""
    return hashlib.sha1("|".join(parts).encode()).hexdigest()


def get_account(p, create=False):
    email = (p.get("id") or "").strip().lower()
    if not email:
        email = "player"
    acc = DB["accounts"].get(email)
    if acc is None and create:
        acc = DB["accounts"][email] = {
            "characterLimit": CHARACTER_LIMIT,
            "sharedBankCount": 1,
            "scarabGem": START_GEMS,
            "shared": {},            # account-wide shared bank, slot -> item
            "characters": {},
        }
    return email, acc


def get_character(p):
    email, acc = get_account(p, create=True)
    cs = p.get("csKey", "")
    for name, ch in acc["characters"].items():
        if ch.get("csKey") == cs:
            return acc, ch
    # fall back to the only/last character if the key is unknown
    if acc["characters"]:
        return acc, list(acc["characters"].values())[-1]
    return acc, None


def to_int(v, default=0):
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return default


def to_float(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def dock_store(acc, ch, dock):
    if dock == DOCK_SHARE:
        return acc.setdefault("shared", {})
    return ch.setdefault("docks", {}).setdefault(str(dock), {})


def slots_list(store):
    out = [v for v in store.values() if v.get("itemID", 0)]
    out.sort(key=lambda s: s.get("slotNumber", 0))
    return out


def clean_slot(s):
    return {"slotNumber": to_int(s.get("slotNumber")),
            "itemID": to_int(s.get("itemID")),
            "randomCode": to_int(s.get("randomCode")),
            "itemCount": to_int(s.get("itemCount"), 1)}


def char_summary(ch):
    return {"name": ch["name"], "title": ch.get("title", ""),
            "race": ch["race"], "job": ch["job"], "hair": ch["hair"],
            "hairColor": ch["hairColor"], "facialFileHead": ch["facialFileHead"],
            "lastZoneID": ch["lastZoneID"], "portrait": ch.get("portrait", "")}


CHAR_FIELDS = ["name", "title", "race", "job", "hair", "hireUseDefaultColor",
               "hairColor", "facialFileHead", "facial", "lastZoneID",
               "bagSlotCount", "bankPageCount", "exp", "gold", "killCount",
               "deadCount", "playTime", "flagCode1", "flagCode2", "flagCode3",
               "flagCode4", "flagCode5", "flagCode6", "flagCode7", "flagCode8",
               "skilltreePayExp", "bitCount", "bits1", "bits2", "bits3", "bits4"]


def char_full(ch):
    d = {k: ch.get(k) for k in CHAR_FIELDS}
    d["sessionKey"] = ch["csKey"]
    return d


# ---------------------------------------------------------------- handlers
# Each returns either a dict (sent encrypted) or a plain string.

def h_version(p):
    return "ver:" + GAME_VERSION


def h_login(p):
    email, acc = get_account(p, create=True)
    return {"idHash": stable_key("id", email),
            "sessionKey": stable_key("session", email),
            "characterLimit": acc["characterLimit"],
            "sharedBankCount": acc["sharedBankCount"],
            "scarabGem": acc["scarabGem"]}


h_addAccount = h_login
h_verifyAccount = h_login


def h_characterList(p):
    email, acc = get_account(p, create=True)
    return [char_summary(c) for c in acc["characters"].values()]


def h_newCharacter(p):
    email, acc = get_account(p, create=True)
    name = (p.get("name") or "").strip()
    if not name:
        return "2"
    if any(n.lower() == name.lower() for n in acc["characters"]):
        return "1"                                   # name already used
    if len(acc["characters"]) >= acc["characterLimit"]:
        return "3"
    ch = {
        "name": name, "title": "", "race": to_int(p.get("race")),
        "job": to_int(p.get("job")), "hair": to_int(p.get("hair")),
        "hireUseDefaultColor": str(p.get("hireUseDefaultColor")).lower() == "true",
        "hairColor": to_int(p.get("hairColor")),
        "facialFileHead": p.get("facialFileHead", ""),
        "facial": to_int(p.get("facial")),
        "lastZoneID": 1, "bagSlotCount": 24, "bankPageCount": 1,
        "exp": 0, "gold": 0, "killCount": 0, "deadCount": 0, "playTime": 0.0,
        "skilltreePayExp": 0, "bitCount": 0,
        "bits1": 0, "bits2": 0, "bits3": 0, "bits4": 0,
        "csKey": stable_key("char", email, name, str(time.time())),
        "docks": {}, "quests": [], "alchemy": {}, "portrait": "",
    }
    for i in range(1, 9):
        ch["flagCode%d" % i] = ""
    acc["characters"][name] = ch
    return char_full(ch)


def h_loadCharacter(p):
    email, acc = get_account(p, create=True)
    ch = acc["characters"].get(p.get("name", ""))
    if ch is None:
        return "4"
    return char_full(ch)


def h_removeCharacter(p):
    email, acc = get_account(p, create=True)
    acc["characters"].pop(p.get("name", ""), None)
    return "0"


SAVE_MAP = {"lastZoneID": int, "exp": int, "gold": int, "killCount": int,
            "deadCount": int, "playTime": float, "bitCount": int,
            "bits1": int, "bits2": int, "bits3": int, "bits4": int,
            "bagSlotCount": int, "bankPageCount": int}


def h_saveCharacter(p):
    acc, ch = get_character(p)
    if ch is None:
        return "0"
    for k, v in p.items():
        if k in SAVE_MAP:
            ch[k] = to_float(v) if SAVE_MAP[k] is float else to_int(v)
        elif k.startswith("flagCode"):
            ch[k] = v
        elif k == "payExp":
            ch["skilltreePayExp"] = to_int(v)
    return "0"


h_saveCharacterBagBankCount = h_saveCharacter


def h_saveCharacterPortrait(p):
    acc, ch = get_character(p)
    if ch is not None:
        ch["portrait"] = p.get("portrait", "")
    return "0"


def h_connectionKeep(p):
    return "0"


h_characterConnectionKeep = h_saveCharacter     # also carries lastZoneID / playTime


def h_loadCharacterItems(p):
    acc, ch = get_character(p)
    if ch is None:
        return []
    return slots_list(dock_store(acc, ch, to_int(p.get("dockType"))))


def h_saveCharacterItems(p):
    acc, ch = get_character(p)
    if ch is None:
        return "0"
    store = dock_store(acc, ch, to_int(p.get("dockType")))
    store.clear()
    for s in json.loads(p.get("slots") or "[]") or []:
        s = clean_slot(s)
        store[str(s["slotNumber"])] = s
    return "0"


def h_saveCharacterItemsDiff(p):
    acc, ch = get_character(p)
    if ch is None:
        return "0"
    diff = json.loads(p.get("slots") or "{}") or {}
    for field, dock in DIFF_DOCKS.items():
        for s in diff.get(field) or []:
            s = clean_slot(s)
            store = dock_store(acc, ch, dock)
            if s["itemID"]:
                store[str(s["slotNumber"])] = s
            else:
                store.pop(str(s["slotNumber"]), None)
    return "0"


def h_saveCharacterLoot(p):
    return "0"          # the real server only logged loot


def h_loadCharacterSkill(p):
    acc, ch = get_character(p)
    ch = ch or {}
    return {k: ch.get(k, 0) for k in
            ["bitCount", "skilltreePayExp", "bits1", "bits2", "bits3", "bits4"]}


def h_rmResetSkilltree(p):
    acc, ch = get_character(p)
    if ch is not None:
        for k in ["bitCount", "skilltreePayExp", "bits1", "bits2", "bits3", "bits4"]:
            ch[k] = 0
    return {"pcSG": acc["scarabGem"]}


def h_loadAlchemyMixedItems(p):
    acc, ch = get_character(p)
    al = (ch or {}).get("alchemy", {})
    return [{"recipeID": int(k), "mixCount": v} for k, v in al.items()]


def h_saveAlchemyMixedItem(p):
    acc, ch = get_character(p)
    if ch is not None:
        al = ch.setdefault("alchemy", {})
        rid = str(to_int(p.get("recipeID")))
        al[rid] = al.get(rid, 0) + 1
    return "0"


# Alchemy recipes come from recipes.json (edit it to add or change recipes).
RECIPES_PATH = os.path.join(HERE, "recipes.json")


def load_recipes():
    try:
        with open(RECIPES_PATH, "r", encoding="utf-8") as f:
            return json.load(f).get("recipes", [])
    except FileNotFoundError:
        return []
    except Exception as e:
        log("!! recipes.json could not be read: %s" % e)
        return []


def recipe_for_client(r, vendor):
    return {"isEnabled": True, "isHidden": bool(r.get("hidden", False)),
            "venderID": vendor, "recipeID": r["id"],
            "keyItemID": r["key"]["item"], "keyItemCount": r["key"].get("count", 1),
            "resultItemID": r["result"]["item"], "resultItemCount": r["result"].get("count", 1),
            "mats": [{"id": m["item"], "count": m.get("count", 1)} for m in r.get("materials", [])],
            "orderWeight": r.get("order", r["id"])}


def recipe_vendor_ok(r, vendor):
    vendors = r.get("vendors")          # leave out = every alchemist knows it
    return not vendors or vendor in vendors


def h_loadAlchemyRecipeList(p):
    vendor, key = to_int(p.get("vendorID")), to_int(p.get("keyItemId"))
    found = [recipe_for_client(r, vendor) for r in load_recipes()
             if r["key"]["item"] == key and recipe_vendor_ok(r, vendor)]
    found.sort(key=lambda r: r["orderWeight"])
    return found


def h_loadAlchemyRecipeCode(p):
    """The game checks this code before mixing: md5 of the recipe's fields, see GUI_Alchemy.makeCode."""
    vendor, rid = to_int(p.get("vendorID")), to_int(p.get("recipeID"))
    for r in load_recipes():
        if r["id"] == rid:
            c = recipe_for_client(r, vendor)
            s = "recipe_correct_%d%d%d%d%d%d" % (vendor, c["recipeID"], c["keyItemID"], c["keyItemCount"],
                                               c["resultItemID"], c["resultItemCount"])
            s += "".join("%d%d" % (m["id"], m["count"]) for m in c["mats"])
            return EncryptedText(hashlib.md5(s.encode("utf-8")).hexdigest())
    return EncryptedText("unknown recipe")


def h_loadCharacterQuests(p):
    acc, ch = get_character(p)
    return [{"questID": q} for q in (ch or {}).get("quests", [])]


def h_saveCharacterQuest(p):
    acc, ch = get_character(p)
    if ch is not None:
        q = to_int(p.get("questID"))
        if q not in ch.setdefault("quests", []):
            ch["quests"].append(q)
    return "0"


def h_removeCharacterQuest(p):
    acc, ch = get_character(p)
    if ch is not None:
        q = to_int(p.get("questID"))
        ch["quests"] = [x for x in ch.get("quests", []) if x != q]
    return "0"


def h_completeCharacterQuest(p):
    if str(p.get("isRequestRemove")).lower() == "true":
        return h_removeCharacterQuest(p)
    return "0"


def h_loadInboxItems(p):
    return []


def h_inboxToBag(p):
    return {"resultSet": {"bagSlots": [], "inboxSlots": []}}


def h_sgShopItems(p):
    return []


def free_bag_slot(ch):
    used = {to_int(k) for k, v in dock_store(None, ch, 2).items() if v.get("itemID")}
    for i in range(ch.get("bagSlotCount", 24)):
        if i not in used:
            return i
    return None


def h_sgShopBuy(p):
    acc, ch = get_character(p)
    cost = int(to_int(p.get("price")) * max(to_float(p.get("priceAmp"), 1.0), 0))
    if ch is None or acc["scarabGem"] < cost:
        return "5"
    slot = free_bag_slot(ch)
    if slot is None:
        return "6"
    acc["scarabGem"] -= cost
    s = {"slotNumber": slot, "itemID": to_int(p.get("itemID")),
         "randomCode": to_int(p.get("randomCode")),
         "itemCount": max(to_int(p.get("quantity"), 1), 1)}
    dock_store(acc, ch, 2)[str(slot)] = s
    return {"resultSet": {"scarabGem": acc["scarabGem"], "bagSlots": [s]}}


def h_addCharacterLimit(p):
    email, acc = get_account(p, create=True)
    acc["characterLimit"] += 1
    return {"resultSet": {"characterCountLimit": acc["characterLimit"],
                          "scarabGem": acc["scarabGem"]}}


def h_addSharedBank(p):
    email, acc = get_account(p, create=True)
    acc["sharedBankCount"] += 1
    return {"sharedBank": acc["sharedBankCount"], "scarabGem": acc["scarabGem"]}


def h_bagUnlockBySG(p):
    acc, ch = get_character(p)
    if ch is not None:
        ch["bagSlotCount"] = ch.get("bagSlotCount", 24) + 6
    return {"scarabGem": acc["scarabGem"],
            "bagSlotCount": (ch or {}).get("bagSlotCount", 24)}


def h_bankUnlockBySG(p):
    acc, ch = get_character(p)
    if ch is not None:
        ch["bankPageCount"] = min(ch.get("bankPageCount", 1) + 1, 10)
    return {"scarabGem": acc["scarabGem"],
            "bankPageCount": (ch or {}).get("bankPageCount", 1)}


def h_bankUnlockByItem(p):
    acc, ch = get_character(p)
    if ch is not None:
        ch["bankPageCount"] = min(ch.get("bankPageCount", 1) + 1, 10)
    return {"bankPageCount": (ch or {}).get("bankPageCount", 1), "inboxSlots": []}


HANDLERS = {name[2:]: fn for name, fn in globals().items()
            if name.startswith("h_") and callable(fn)}


# ---------------------------------------------------------------- http

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _reply(self, text, status=200):
        body = text.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _handle(self, raw):
        path = self.path.split("?")[0]
        action = path.rstrip("/").rsplit("/", 1)[-1]
        params = {}
        if raw:
            form = parse_qs(raw.decode("utf-8", "replace"), keep_blank_values=True)
            req = (form.get("req") or [""])[0]
            if req:
                try:
                    params = decode_request(req)
                except Exception as e:
                    log("!! could not decode request for %s: %s" % (action, e))
        fn = HANDLERS.get(action)
        if VERBOSE:
            shown = {k: ("***" if k in ("pass", "password", "verifyCode") else
                         v if len(str(v)) < 120 else str(v)[:120] + "...")
                     for k, v in params.items() if k not in ("ver_major", "ver_minor", "ver_rivision")}
            log("%s %s" % (action, shown))
        else:
            log(action)
        if fn is None:
            log("   (unknown request '%s' - answering OK)" % action)
            return self._reply("0")
        try:
            with lock:
                result = fn(params)
                save_db()
        except Exception:
            log("!! error in %s\n%s" % (action, traceback.format_exc()))
            return self._reply("9")
        plain = isinstance(result, str) and not isinstance(result, EncryptedText)
        self._reply(result if plain else encode_reply(result))

    def do_GET(self):
        self._handle(b"")

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        self._handle(self.rfile.read(n) if n else b"")


def main():
    srv = ThreadingHTTPServer((HOST, PORT), Handler)
    log("Nine Lives offline server running on http://%s:%d  (Ctrl+C to stop)" % (HOST, PORT))
    log("Saves: " + SAVE_PATH)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        log("Stopped.")


if __name__ == "__main__":
    main()
