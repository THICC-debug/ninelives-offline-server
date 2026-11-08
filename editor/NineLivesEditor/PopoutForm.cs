using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NineLivesEditor
{
    /// <summary>
    /// One of the editor's pages in a window of its own (the ⧉ on a tab): it can go anywhere, on another
    /// screen too, be maximised, or go full screen (F11 or the page's ⛶ button). The page talks to the main
    /// window through window.opener, so saving, links between pages and docking back into Build all work.
    /// </summary>
    class PopoutForm : Form
    {
        public readonly WebView2 Web = new WebView2 { Dock = DockStyle.Fill };
        readonly string url;
        bool closingChecked, full;
        FormWindowState wasState; FormBorderStyle wasBorder; Rectangle wasBounds;

        public PopoutForm(string url, CoreWebView2WindowFeatures features)
        {
            this.url = url;
            Text = "Nine Lives Editor";
            var icon = Program.AppIcon(); if (icon != null) Icon = icon;
            BackColor = Color.FromArgb(27, 26, 24);
            MinimumSize = new Size(420, 320);
            StartPosition = FormStartPosition.Manual;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int w = features != null && features.HasSize ? (int)features.Width : 1000, h = features != null && features.HasSize ? (int)features.Height : 760;
            int x = features != null && features.HasPosition ? (int)features.Left : area.Left + (area.Width - w) / 2;
            int y = features != null && features.HasPosition ? (int)features.Top : area.Top + (area.Height - h) / 2;
            var r = new Rectangle(x, y, Math.Max(420, w), Math.Max(320, h));
            // a remembered spot on a screen that's gone: back onto this one
            bool onScreen = false; foreach (var s in Screen.AllScreens) if (s.WorkingArea.IntersectsWith(r)) onScreen = true;
            if (!onScreen) r.Location = new Point(area.Left + 40, area.Top + 40);
            Bounds = r;
            Controls.Add(Web);
            FormClosing += OnClosing;
        }

        /// <summary>After the WebView is ready (and before the page loads in it).</summary>
        public void Setup()
        {
            var w = Web.CoreWebView2;
            w.Settings.IsStatusBarEnabled = false;
            w.Settings.AreDevToolsEnabled = false;
            w.Settings.IsZoomControlEnabled = false;
            w.Settings.AreDefaultScriptDialogsEnabled = false;
            w.ScriptDialogOpening += (o, a) => Dialogs.Handle(this, a);
            w.NewWindowRequested += (o, a) => { a.Handled = true; Program.OpenInBrowser(a.Uri); };
            w.NavigationStarting += (o, a) => { if (!a.Uri.StartsWith(url, StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; Program.OpenInBrowser(a.Uri); } };
            w.DocumentTitleChanged += (o, a) => Text = string.IsNullOrEmpty(w.DocumentTitle) ? "Nine Lives Editor" : w.DocumentTitle;
            w.WindowCloseRequested += (o, a) => { closingChecked = true; BeginInvoke((Action)Close); };   // window.close(): it already asked
            w.ContainsFullScreenElementChanged += (o, a) => FullScreen(w.ContainsFullScreenElement);
        }

        void FullScreen(bool on)
        {
            if (on == full) return;
            full = on;
            if (on)
            {
                wasState = WindowState; wasBorder = FormBorderStyle; wasBounds = Bounds;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
            }
            else
            {
                FormBorderStyle = wasBorder;
                Bounds = wasBounds;
                WindowState = wasState;
            }
        }

        // Unsaved changes in the page: ask before closing (popout.html keeps track).
        async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (closingChecked || Web.CoreWebView2 == null) return;
            e.Cancel = true;
            string dirty = "false";
            try { dirty = await Web.CoreWebView2.ExecuteScriptAsync("typeof popDirty === 'function' ? popDirty() : false"); } catch { }
            if (dirty == "true" &&
                MessageBox.Show(this, "This page has unsaved changes. Close it anyway?", "Nine Lives Editor",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            closingChecked = true;
            Close();
        }
    }

    /// <summary>The pages' confirm/alert/prompt boxes, as the app's own (always on top of the game's window).</summary>
    static class Dialogs
    {
        public static void Handle(Form owner, CoreWebView2ScriptDialogOpeningEventArgs e)
        {
            var deferral = e.GetDeferral();
            owner.BeginInvoke((Action)(() =>
            {
                try
                {
                    const string title = "Nine Lives Editor";
                    switch (e.Kind)
                    {
                        case CoreWebView2ScriptDialogKind.Alert:
                            MessageBox.Show(owner, e.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                            e.Accept(); break;
                        case CoreWebView2ScriptDialogKind.Confirm:
                            if (MessageBox.Show(owner, e.Message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) e.Accept();
                            break;
                        case CoreWebView2ScriptDialogKind.Prompt:
                            var answer = Microsoft.VisualBasic.Interaction.InputBox(e.Message, title, e.DefaultText ?? "");
                            if (!string.IsNullOrEmpty(answer)) { e.ResultText = answer; e.Accept(); }
                            break;
                        case CoreWebView2ScriptDialogKind.Beforeunload:
                            if (MessageBox.Show(owner, "Leave this page? Your unsaved changes on it will be lost.", title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes) e.Accept();
                            break;
                    }
                }
                catch { }
                finally { deferral.Complete(); }
            }));
        }
    }
}
