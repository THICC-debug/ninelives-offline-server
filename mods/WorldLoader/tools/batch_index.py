"""
Index the game's pre-baked scenery: for every baked part in every scene, which pieces (sub-meshes) of
its baked mesh are its own. Unity 5.3 doesn't expose this while the game runs, but the scene files
store it (MeshRenderer.m_SubsetIndices). WorldLoader uses it to draw a copy's own pieces at the copy.

Writes only names, positions and piece numbers (no models, textures or other game content).

    pip install UnityPy
    python batch_index.py "<game folder>" [output folder]

Output: one <SceneName>.json per scene, {"name|x|y|z": [piece, ...]} (position = the part's place in the
world, rounded to 1 cm).
"""
import json, math, os, sys
import UnityPy

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
            aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz)

def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    # v' = q v q*
    ix = w*vx + y*vz - z*vy; iy = w*vy + z*vx - x*vz; iz = w*vz + x*vy - y*vx; iw = -x*vx - y*vy - z*vz
    return (ix*w + iw*-x + iy*-z - iz*-y, iy*w + iw*-y + iz*-x - ix*-z, iz*w + iw*-z + ix*-y - iy*-x)

def main():
    game = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "batches")
    data = os.path.join(game, "Ninelives_Data")
    os.makedirs(out, exist_ok=True)
    scenes = []
    for o in UnityPy.load(os.path.join(data, "globalgamemanagers")).objects:
        if o.type.name == "BuildSettings":
            scenes = o.read_typetree()["scenes"]
    for i, path in enumerate(scenes):
        name = os.path.splitext(os.path.basename(path))[0]
        f = os.path.join(data, "level%d" % i)
        if not os.path.exists(f): continue
        env = UnityPy.load(f)
        tr, go_tr, rend = {}, {}, []
        names = {}
        for o in env.objects:
            t = o.type.name
            if t == "Transform" or t == "RectTransform":
                d = o.read_typetree()
                tr[o.path_id] = d
                go_tr[d["m_GameObject"]["m_PathID"]] = o.path_id
            elif t == "GameObject":
                names[o.path_id] = o.read_typetree()["m_Name"]
            elif t == "MeshRenderer":
                d = o.read_typetree()
                if d.get("m_SubsetIndices"): rend.append(d)
        world = {}
        def pos(pid):
            if pid in world: return world[pid]
            d = tr[pid]; lp = d["m_LocalPosition"]; lr = d["m_LocalRotation"]; ls = d["m_LocalScale"]
            p = (lp["x"], lp["y"], lp["z"]); q = (lr["x"], lr["y"], lr["z"], lr["w"]); s = (ls["x"], ls["y"], ls["z"])
            father = d["m_Father"]["m_PathID"]
            if father and father in tr:
                fp, fq, fs = pos(father)
                sp = (p[0]*fs[0], p[1]*fs[1], p[2]*fs[2])
                r = qrot(fq, sp)
                res = ((fp[0]+r[0], fp[1]+r[1], fp[2]+r[2]), qmul(fq, q), (fs[0]*s[0], fs[1]*s[1], fs[2]*s[2]))
            else:
                res = (p, q, s)
            world[pid] = res
            return res
        index = {}
        for d in rend:
            gid = d["m_GameObject"]["m_PathID"]
            if gid not in go_tr: continue
            p = pos(go_tr[gid])[0]
            key = "%s|%.2f|%.2f|%.2f" % (names.get(gid, ""), p[0], p[1], p[2])
            index[key] = list(d["m_SubsetIndices"])
        with open(os.path.join(out, name + ".json"), "w", encoding="utf-8") as h:
            json.dump(index, h, separators=(",", ":"))
        print("%-32s %6d baked part(s)" % (name, len(index)))

if __name__ == "__main__":
    sys.setrecursionlimit(10000)
    main()
