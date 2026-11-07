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
        readonly string url;
        bool closingChecked;

        public MainForm(string url, string game)
        {
            this.url = url;
            Text = "Nine Lives Editor";
            var icon = Program.GameIcon(game); if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            var screen = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1400, screen.Width - 80), Math.Min(900, screen.Height - 60));
            MinimumSize = new Size(820, 560);
            BackColor = Color.FromArgb(27, 26, 24);
            Controls.Add(web);
            Load += async (s, e) =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Resources.AppData, "WebView"));
                    await web.EnsureCoreWebView2Async(env);
                    var w = web.CoreWebView2;
                    w.Settings.IsStatusBarEnabled = false;
                    w.Settings.AreDevToolsEnabled = false;
                    w.Settings.IsZoomControlEnabled = true;
                    // Links to other sites (GitHub, docs) open in the normal browser.
                    w.NewWindowRequested += (o, a) => { a.Handled = true; Program.OpenInBrowser(a.Uri); };
                    w.NavigationStarting += (o, a) =>
                    {
                        if (!a.Uri.StartsWith(this.url, StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; Program.OpenInBrowser(a.Uri); }
                    };
                    w.DocumentTitleChanged += (o, a) => Text = string.IsNullOrEmpty(w.DocumentTitle) ? "Nine Lives Editor" : w.DocumentTitle;
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
