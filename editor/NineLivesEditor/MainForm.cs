using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NineLivesEditor
{
    /// <summary>The editor's window: the pages, shown with Edge WebView2.</summary>
    class MainForm : Form
    {
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
        // The game's own window sits in this, over the 3D tab's view area (see GameRunner.Dock).
        readonly Panel gameHost = new Panel { Visible = false, BackColor = Color.Black };
        readonly Timer inputTimer = new Timer { Interval = 50 };
        bool overGame; Point lastCursor; volatile bool sending; float pendingYaw, pendingPitch;
        static readonly System.Net.Http.HttpClient live = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        readonly string url;
        bool closingChecked;

        public MainForm(string url, string game)
        {
            this.url = url;
            Text = "Nine Lives Editor";
            var icon = Program.AppIcon(); if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            var screen = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1400, screen.Width - 80), Math.Min(900, screen.Height - 60));
            MinimumSize = new Size(820, 560);
            BackColor = Color.FromArgb(27, 26, 24);
            Controls.Add(gameHost);
            Controls.Add(web);
            gameHost.BringToFront();
            Load += async (s, e) =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Resources.AppData, "WebView"));
                    await web.EnsureCoreWebView2Async(env);
                    var w = web.CoreWebView2;
                    w.Settings.IsStatusBarEnabled = false;
                    w.Settings.AreDevToolsEnabled = false;
                    w.Settings.IsZoomControlEnabled = false;   // page zoom would put the docked game in the wrong place
                    // The editor's own pages open in windows of their own (the ⧉ on a tab); links to other
                    // sites (GitHub, docs) open in the normal browser.
                    w.NewWindowRequested += async (o, a) =>
                    {
                        if (!a.Uri.StartsWith(this.url, StringComparison.OrdinalIgnoreCase)) { a.Handled = true; Program.OpenInBrowser(a.Uri); return; }
                        var deferral = a.GetDeferral();
                        try
                        {
                            var pop = new PopoutForm(this.url, a.WindowFeatures);
                            pop.Show();
                            await pop.Web.EnsureCoreWebView2Async(env);
                            pop.Setup();
                            a.NewWindow = pop.Web.CoreWebView2;   // keeps window.opener: the page talks to this window
                            a.Handled = true;
                        }
                        catch { a.Handled = true; }
                        finally { deferral.Complete(); }
                    };
                    w.NavigationStarting += (o, a) =>
                    {
                        if (!a.Uri.StartsWith(this.url, StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; Program.OpenInBrowser(a.Uri); }
                    };
                    w.DocumentTitleChanged += (o, a) => Text = string.IsNullOrEmpty(w.DocumentTitle) ? "Nine Lives Editor" : w.DocumentTitle;
                    w.WebMessageReceived += (o, a) => OnPageMessage(a.WebMessageAsJson);
                    // The game's window sits over the 3D view, on top of the page: WebView2's own question boxes
                    // (confirm, alert, prompt) open centred over the page and end up hidden behind it. Ask with
                    // the app's own boxes instead, which always come on top.
                    w.Settings.AreDefaultScriptDialogsEnabled = false;
                    w.ScriptDialogOpening += (o, a) => Dialogs.Handle(this, a);
                    w.Navigate(this.url);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("The editor window couldn't start (" + ex.Message + "). It will open in your web browser instead.",
                                    "Nine Lives Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Program.OpenInBrowser(this.url);
                }
            };
            FormClosing += OnClosing;
            inputTimer.Tick += (o, e) => { try { GameInput(); } catch { } };   // never crash the window over this
            inputTimer.Start();
        }

        /// <summary>
        /// While the mouse is over the docked game: tell the game it's active, and also send it the flying
        /// keys and right-drag look ourselves (in case the game still doesn't read input inside our window).
        /// </summary>
        bool viewHasKeys;
        void GameInput()
        {
            bool over = false;
            try
            {
                over = gameHost.Visible && GetForegroundWindow() == Handle &&
                       gameHost.RectangleToScreen(gameHost.ClientRectangle).Contains(Cursor.Position);
            }
            catch { }
            if (over != overGame) { overGame = over; GameRunner.Activate(over); lastCursor = Cursor.Position; }
            Func<int, bool> down = k => (GetAsyncKeyState(k) & 0x8000) != 0;
            // The arrow keys belong to the 3D view after you click in it, and to the page after you click there
            // (so moving the caret in a box doesn't fly the camera).
            bool click = down(0x01) || down(0x02) || down(0x04);
            if (click && GetForegroundWindow() == Handle) viewHasKeys = over;
            if (!over) return;
            // Like Unity: WASD / Q E fly only while the right mouse button is held (otherwise they're the build
            // tools in the game); the arrow keys fly any time.
            bool rmb = down(0x02);
            bool arrows = viewHasKeys || rmb;
            int mx = ((rmb && down(0x44)) || (arrows && down(0x27)) ? 1 : 0) - ((rmb && down(0x41)) || (arrows && down(0x25)) ? 1 : 0);   // D/right, A/left
            int mz = ((rmb && down(0x57)) || (arrows && down(0x26)) ? 1 : 0) - ((rmb && down(0x53)) || (arrows && down(0x28)) ? 1 : 0);   // W/up, S/down
            int my = (rmb && (down(0x45) || down(0x20)) ? 1 : 0) - (rmb && down(0x51) ? 1 : 0);                   // E/space, Q
            bool fast = down(0x10);
            var c = Cursor.Position;
            float yaw = 0, pitch = 0;
            if (down(0x02)) { yaw = (c.X - lastCursor.X) * 0.12f; pitch = (c.Y - lastCursor.Y) * 0.12f; }   // right mouse button
            lastCursor = c;
            if (mx == 0 && my == 0 && mz == 0 && yaw == 0 && pitch == 0) return;
            if (sending) { pendingYaw += yaw; pendingPitch += pitch; return; }   // one at a time: a slow game mustn't queue up old moves
            yaw += pendingYaw; pitch += pendingPitch; pendingYaw = pendingPitch = 0;
            var body = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{\"mx\":{0},\"my\":{1},\"mz\":{2},\"yaw\":{3},\"pitch\":{4},\"fast\":{5},\"fromEditor\":1}}", mx, my, mz, yaw, pitch, fast ? 1 : 0);
            sending = true;
            var content = new System.Net.Http.StringContent(body);
            try
            {
                live.PostAsync("http://127.0.0.1:8099/editor/cam", content).ContinueWith(t =>
                {
                    try { if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) t.Result.Dispose(); } catch { }
                    content.Dispose(); sending = false;
                });
            }
            catch { content.Dispose(); sending = false; }
        }

        /// <summary>
        /// The 3D tab says where its view is ({cmd:"gameRect", x, y, w, h, show} in screen pixels inside the
        /// page): put the game's window there. Answers {cmd:"dockState", docked} so the page can fall back
        /// to pictures when the game wasn't started by the editor.
        /// </summary>
        void OnPageMessage(string json)
        {
            try
            {
                if (!json.Contains("\"gameRect\"")) return;
                Func<string, int> num = k => { var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + k + "\"\\s*:\\s*(-?[0-9.]+)"); return m.Success ? (int)Math.Round(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) : 0; };
                bool show = json.Contains("\"show\":true");
                int x = num("x"), y = num("y"), w = num("w"), h = num("h");
                bool docked = false;
                if (show && w > 20 && h > 20)
                {
                    gameHost.Bounds = new Rectangle(web.Left + x, web.Top + y, w, h);
                    docked = GameRunner.Dock(gameHost.Handle, w, h, true);
                    gameHost.Visible = docked;
                    if (docked) gameHost.BringToFront();
                }
                else gameHost.Visible = false;
                web.CoreWebView2.PostWebMessageAsJson("{\"cmd\":\"dockState\",\"docked\":" + (docked ? "true" : "false") + "}");
            }
            catch { }
        }

        // Unsaved changes in any tab: ask before closing (the page keeps track; see anyDirty() in index.html).
        async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (closingChecked || web.CoreWebView2 == null) return;
            e.Cancel = true;
            string dirty = "false";
            try { dirty = await web.CoreWebView2.ExecuteScriptAsync("typeof anyDirty === 'function' ? anyDirty() : false"); } catch { }
            if (dirty == "true" &&
                MessageBox.Show(this, "You have unsaved changes. Close the editor anyway?", "Nine Lives Editor",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            closingChecked = true;
            Close();
        }
    }
}
