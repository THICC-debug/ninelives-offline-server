// The game view: the game's camera as a live picture inside a tab, that you can also build in.
// Click one of your placed things or NPCs and drag it; the mouse wheel turns what you're dragging;
// Alt+click anything else to pick it. Uses WorldLoader's build mode (the same as F8 in the game).
// A page includes this script and calls GameView.mount(element).
(function () {
  const css = `
  .gv { border: 1px solid var(--accent); border-radius: 10px; overflow: hidden; margin-bottom: 14px; background: #111; }
  .gv .bar { display: flex; gap: 8px; align-items: center; padding: 6px 10px; background: var(--panel); border-bottom: 1px solid var(--line); font-size: 13px; }
  .gv .bar b { color: var(--accent); }
  .gv .bar .grow { flex: 1; color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .gv .bar button { padding: 3px 10px; font-size: 12px; }
  .gv .pic { position: relative; cursor: crosshair; user-select: none; outline: none; }
  .gv .pic:focus { box-shadow: inset 0 0 0 2px var(--accent); }
  .gv .pic.drag { cursor: grabbing; }
  .gv img { display: block; width: 100%; height: auto; -webkit-user-drag: none; }
  .gv .msg { position: absolute; inset: 0; display: grid; place-items: center; color: #bbb; text-align: center; padding: 20px; }
  .gv.closed .pic { display: none; }`;
  const st = document.createElement("style"); st.textContent = css; document.head.appendChild(st);

  let pic = null;   // (module-level: the once-only listeners below always use the current one)
  let box = null, img = null, on = true, busy = false, down = false, lastMove = 0, timer = null, building = false;
  let camBusy = false;
  const keys = new Set(), taps = new Set(); let look = null, lookDx = 0, lookDy = 0, flying = false, hover = false, wasFlying = false;
  // Send the flight controls to the game ~16 times a second while something is held.
  setInterval(() => {
    const k = c => keys.has(c) || taps.has(c) ? 1 : 0;   // a quick tap still counts once
    const mx = k("d") + k("arrowright") - k("a") - k("arrowleft"), mz = k("w") + k("arrowup") - k("s") - k("arrowdown"), my = k("e") - k("q");
    const yaw = lookDx * 0.25, pitch = lookDy * 0.25; lookDx = lookDy = 0;
    taps.clear();
    flying = !!(mx || my || mz || yaw || pitch);
    if (flying !== wasFlying) { wasFlying = flying; hint(flying ? "Flying… (Shift for faster)" : "WASD fly · Q/E down/up · Shift faster · right-drag look · drag your things · wheel turns · Alt+click picks"); }
    if (!flying || !box || !box.isConnected) return;
    if (camBusy) { lookDx += yaw / 0.25; lookDy += pitch / 0.25; return; }   // one at a time (a loading game answers slowly): keep the look for the next one
    camBusy = true;
    fetch("/api/live/editor/cam", { method: "POST", body: JSON.stringify({ mx, my, mz, yaw, pitch, fast: keys.has("shift") ? 1 : 0 }) })
      .then(r => r.json()).then(r => { if (r.error) hint(r.error); }).catch(() => {}).then(() => { camBusy = false; });
    refresh();
  }, 60);
  let open = (() => { try { return localStorage.getItem("nle.gameview") !== "0"; } catch (e) { return true; } })();

  async function get(p) { try { return await (await fetch("/api/live" + p)).json(); } catch (e) { return { connected: false }; } }
  async function post(p) { try { return await (await fetch("/api/live" + p, { method: "POST", body: "{}" })).json(); } catch (e) { return { connected: false }; } }

  let globalsAdded = false;
  function mount(el) {
    if (box && box.isConnected && el.contains(box)) return;
    const addGlobal = !globalsAdded; globalsAdded = true;   // window/document listeners: once, not on every mount
    el.insertAdjacentHTML("afterbegin", `<div class="gv${open ? "" : " closed"}">
      <div class="bar"><b>Game view</b><span class="grow" id="gvHint">Mouse over the picture: WASD fly · Q/E down/up · Shift faster · right-drag look · drag your things · wheel turns · Alt+click picks</span>
        <button id="gvBuild" title="Build mode in the game (F8)">Build mode: off</button>
        <button id="gvToggle">${open ? "Hide" : "Show"}</button></div>
      <div class="pic" tabindex="0"><img alt=""><div class="msg">Connecting to the game…</div></div></div>`);
    box = el.querySelector(".gv"); img = box.querySelector("img");
    pic = box.querySelector(".pic");
    box.querySelector("#gvToggle").onclick = () => {
      open = !open; box.classList.toggle("closed", !open);
      box.querySelector("#gvToggle").textContent = open ? "Hide" : "Show";
      try { localStorage.setItem("nle.gameview", open ? "1" : "0"); } catch (e) {}
      if (open) refresh();
    };
    box.querySelector("#gvBuild").onclick = async () => { const r = await post("/world/build?on=" + (building ? 0 : 1)); if (r.on != null) setBuilding(r.on); };
    const uv = e => { const r = img.getBoundingClientRect(); return { u: ((e.clientX - r.left) / r.width).toFixed(4), v: ((e.clientY - r.top) / r.height).toFixed(4) }; };
    // flying (editor mode): keys while the picture has focus, right-drag to look
    pic.addEventListener("contextmenu", e => e.preventDefault());
    // Keys count while the mouse is over the picture or it has focus (not while typing in a box).
    pic.addEventListener("mouseenter", () => {
      hover = true;
      // Take the keyboard (the editor shell around this page usually has it), unless you're typing somewhere.
      let typing = false;
      try {
        let a = (parent.document || document).activeElement;
        while (a && a.tagName === "IFRAME" && a.contentDocument) a = a.contentDocument.activeElement;   // focus inside a page of the shell
        typing = !!a && (/INPUT|SELECT|TEXTAREA/.test(a.tagName) || a.isContentEditable);
      } catch (e) {}
      if (!typing) { try { window.focus(); } catch (e) {} pic.focus({ preventScroll: true }); }
    });
    pic.addEventListener("mouseleave", () => { hover = false; });
    const flyKey = e => { const k = e.key === "Shift" ? "shift" : e.key.toLowerCase(); return (k.length === 1 && "wasdqe".includes(k)) || k === "shift" || k.startsWith("arrow") ? k : null; };
    const active = () => (hover || document.activeElement === pic) && !/INPUT|SELECT|TEXTAREA/.test((document.activeElement || {}).tagName || "");
    if (addGlobal) document.addEventListener("keydown", e => {
      const k = flyKey(e); if (!k || !active()) return;
      e.preventDefault();
      if (!keys.has(k)) { keys.add(k); taps.add(k); }
    }, true);
    if (addGlobal) document.addEventListener("keyup", e => { const k = flyKey(e); if (k) keys.delete(k); }, true);
    // Keys the editor shell passes on (when it has the keyboard).
    if (addGlobal) window.addEventListener("message", e => {
      const m = e.data || {}; if (m.cmd !== "key" || (m.down && !hover)) return;   // (a key let go always counts, even off the picture)
      const k = m.key === "Shift" ? "shift" : String(m.key || "").toLowerCase();
      if (!((k.length === 1 && "wasdqe".includes(k)) || k === "shift" || k.startsWith("arrow"))) return;
      if (m.down) { if (!keys.has(k)) { keys.add(k); taps.add(k); } } else keys.delete(k);
    });
    if (addGlobal) window.addEventListener("blur", () => { keys.clear(); look = null; if (down) { down = false; pic.classList.remove("drag"); post("/world/view/pointer?act=up"); } });
    pic.addEventListener("mousedown", e => { pic.focus(); if (e.button === 2) { look = { x: e.clientX, y: e.clientY }; e.preventDefault(); } });
    if (addGlobal) window.addEventListener("mousemove", e => { if (look && !(e.buttons & 2)) look = null; /* (a missed button-up) */ if (!look) return; lookDx += e.clientX - look.x; lookDy += e.clientY - look.y; look = { x: e.clientX, y: e.clientY }; });
    if (addGlobal) window.addEventListener("mouseup", e => { if (e.button === 2) look = null; });
    pic.addEventListener("mousedown", async e => {
      if (e.button !== 0) return;
      e.preventDefault(); down = true; pic.classList.add("drag");
      const p = uv(e);
      const r = await post(`/world/view/pointer?act=down&u=${p.u}&v=${p.v}&alt=${e.altKey ? 1 : 0}`);
      setBuilding(true);
      hint(r.picked ? "Picked " + r.picked + ": drag to move, wheel turns" : (e.altKey ? "Picked: see the inspector" : "Nothing of yours there (Alt+click picks anything)"));
      refresh();
    });
    if (addGlobal) window.addEventListener("mousemove", e => {
      if (down && !(e.buttons & 1)) { down = false; pic.classList.remove("drag"); post("/world/view/pointer?act=up").then(refresh); return; }   // (a missed button-up)
      if (!down) return;
      const now = Date.now(); if (now - lastMove < 60) return; lastMove = now;
      const p = uv(e);
      post(`/world/view/pointer?act=move&u=${p.u}&v=${p.v}`).then(refresh);
    });
    if (addGlobal) window.addEventListener("mouseup", async () => {
      if (!down) return; down = false; pic.classList.remove("drag");
      await post("/world/view/pointer?act=up"); refresh();
    });
    pic.addEventListener("wheel", e => { e.preventDefault(); post(`/world/view/pointer?act=turn&deg=${e.deltaY > 0 ? -15 : 15}`).then(refresh); }, { passive: false });
    refresh();
  }
  function hint(t) { const h = box && box.querySelector("#gvHint"); if (h) h.textContent = t; }
  function setBuilding(v) { building = !!v; const b = box && box.querySelector("#gvBuild"); if (b) { b.textContent = "Build mode: " + (building ? "on" : "off"); b.classList.toggle("primary", building); } }

  async function refresh() {
    if (!box || !open || busy || document.hidden || !box.isConnected) return;
    busy = true;
    try {
      const r = await get("/world/view?w=" + Math.min(1280, Math.max(320, Math.round(box.clientWidth / 64) * 64)));
      const msg = box.querySelector(".msg");
      if (r.jpg) { img.src = "data:image/jpeg;base64," + r.jpg; msg.style.display = "none"; setBuilding(r.build); }
      else { img.removeAttribute("src"); msg.style.display = ""; msg.textContent = r.connected === false ? "The game isn't running." : (r.error || "No picture: is WorldLoader 1.1 installed?"); }
    } finally { busy = false; }
  }
  // A few pictures a second while it's open and the tab is visible.
  setInterval(() => { if (box && box.isConnected && box.offsetParent !== null) refresh(); }, 250);
  window.addEventListener("message", e => { const m = e.data || {}; if (m.cmd === "buildMode") setBuilding(m.on); });

  window.GameView = { mount, refresh, setBuilding };
})();
