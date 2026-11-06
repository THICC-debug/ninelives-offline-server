using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using NineLivesPatch;

namespace NineLivesSetup
{
    /// <summary>The setup window. All the work is in Installer; this only collects choices and shows progress.</summary>
    class SetupForm : Form
    {
        const string RepoUrl = "https://github.com/THICC-debug/ninelives-offline-server";

        readonly TextBox gameBox = new TextBox();
        readonly Label status = new Label();
        readonly ListView modList = new ListView();
        readonly CheckBox shortcut = new CheckBox();
        readonly Button installBtn = new Button(), uninstallBtn = new Button(), closeBtn = new Button(), browseBtn = new Button();
        readonly TextBox logBox = new TextBox();
        readonly List<ModEntry> mods = Installer.LoadMods();
        bool busy;

        public SetupForm(string game)
        {
            Text = "Nine Lives Offline " + Installer.Version + " - Setup";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(760, 600);
            MinimumSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 10, 14, 10) };
            Controls.Add(layout);

            var title = new Label { Text = "Nine Lives Offline", Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
            var sub = new Label
            {
                Text = "Play Nine Lives 0.24.3 without the official servers. " +
                       "Setup patches the game (keeping a backup), installs the offline server and the mod loader (and any mods you pick). You can undo it all with Uninstall.",
                AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(0, 0, 0, 10),
            };
            layout.Controls.Add(title);
            layout.Controls.Add(sub);

            layout.Controls.Add(new Label { Text = "Game folder (the one with Ninelives.exe):", AutoSize = true });
            var row = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            gameBox.Dock = DockStyle.Fill;
            gameBox.TextChanged += (s, e) => Refresh_();
            browseBtn.Text = "Browse...";
            browseBtn.AutoSize = true;
            browseBtn.Click += (s, e) => Browse();
            row.Controls.Add(gameBox, 0, 0);
            row.Controls.Add(browseBtn, 1, 0);
            layout.Controls.Add(row);
            status.AutoSize = true;
            status.MaximumSize = new Size(720, 0);
            status.Margin = new Padding(0, 4, 0, 10);
            layout.Controls.Add(status);

            var modsLabel = new Label { Text = "Mods (each one can be turned on or off later by running Setup again):", AutoSize = true };
            layout.Controls.Add(modsLabel);
            modList.View = View.Details;
            modList.CheckBoxes = true;
            modList.FullRowSelect = true;
            modList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            modList.Dock = DockStyle.Fill;
            modList.Columns.Add("Mod", 120);
            modList.Columns.Add("Version", 60);
            modList.Columns.Add("What it does", 400);
            modList.ShowItemToolTips = true;
            foreach (var m in mods)
                modList.Items.Add(new ListViewItem(new[] { m.Id, m.Version, m.Description }) { Tag = m, ToolTipText = m.Description });
            layout.Controls.Add(modList);
            if (mods.Count == 0)
            {
                // A release without mods (the loader is still installed, mods come later).
                layout.Controls.Remove(modsLabel);
                layout.Controls.Remove(modList);
            }
            // The description column takes the rest of the width.
            EventHandler fit = (s, e) => modList.Columns[2].Width = Math.Max(150, modList.ClientSize.Width - modList.Columns[0].Width - modList.Columns[1].Width - 4);
            modList.Resize += fit;
            Shown += fit;

            shortcut.Text = "Put a \"Nine Lives (Offline)\" shortcut on the desktop";
            shortcut.Checked = true;
            shortcut.AutoSize = true;
            shortcut.Margin = new Padding(0, 6, 0, 6);
            layout.Controls.Add(shortcut);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            installBtn.Text = "Install";
            uninstallBtn.Text = "Uninstall...";
            closeBtn.Text = "Close";
            foreach (var b in new[] { installBtn, uninstallBtn, closeBtn }) { b.AutoSize = true; b.MinimumSize = new Size(96, 30); buttons.Controls.Add(b); }
            installBtn.Click += (s, e) => DoInstall();
            uninstallBtn.Click += (s, e) => DoUninstall();
            closeBtn.Click += (s, e) => Close();
            layout.Controls.Add(buttons);

            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Dock = DockStyle.Fill;
            logBox.Font = new Font("Consolas", 8.5f);
            logBox.BackColor = SystemColors.Window;
            layout.Controls.Add(logBox);

            var foot = new LinkLabel
            {
                Text = "Made with AI (Anthropic's Claude) and tested by a human player. A fan project, not affiliated with SmokymonkeyS. Source and help: GitHub",
                AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(0, 8, 0, 0), ForeColor = SystemColors.GrayText,
            };
            foot.LinkArea = new LinkArea(foot.Text.Length - "GitHub".Length, "GitHub".Length);
            foot.LinkClicked += (s, e) => { try { Process.Start(RepoUrl); } catch { } };
            layout.Controls.Add(foot);

            layout.RowStyles.Clear();
            for (int i = 0; i < layout.Controls.Count; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (mods.Count > 0) layout.RowStyles[layout.Controls.GetChildIndex(modList)] = new RowStyle(SizeType.Percent, 55);
            else ClientSize = new Size(760, 470);
            layout.RowStyles[layout.Controls.GetChildIndex(logBox)] = new RowStyle(SizeType.Percent, 45);

            var problem = Installer.PayloadProblem();
            if (problem != null)
            {
                Log(problem);
                installBtn.Enabled = false;
            }
            gameBox.Text = game ?? Installer.FindGame() ?? "";
            if (gameBox.Text.Length == 0) Log("Couldn't find the game by itself. Click Browse and pick the folder with Ninelives.exe.");
            Refresh_();
            SetChecks();
            ActiveControl = installBtn;
            gameBox.SelectionStart = gameBox.Text.Length;
        }

        string Game { get { return gameBox.Text.Trim().Trim('"'); } }

        void Browse()
        {
            using (var d = new FolderBrowserDialog { Description = "Pick the Nine Lives folder (the one with Ninelives.exe)", ShowNewFolderButton = false })
            {
                if (Directory.Exists(Game)) d.SelectedPath = Game;
                if (d.ShowDialog(this) == DialogResult.OK) { gameBox.Text = d.SelectedPath; SetChecks(); }
            }
        }

        /// <summary>Tick the installed mods (updating) or the release's defaults (fresh install).</summary>
        void SetChecks()
        {
            bool ok = GamePatcher.LooksLikeGame(Game);
            bool any = ok && mods.Any(m => m.InstalledIn(Game));
            foreach (ListViewItem it in modList.Items)
            {
                var m = (ModEntry)it.Tag;
                it.Checked = any ? m.InstalledIn(Game) : m.Default;
            }
        }

        void Refresh_()
        {
            bool ok = GamePatcher.LooksLikeGame(Game);
            status.Text = Game.Length == 0 ? "" : Installer.Describe(Game);
            status.ForeColor = ok ? SystemColors.ControlText : Color.Firebrick;
            bool installed = ok && (Installer.InstalledVersion(Game) != null || GamePatcher.IsPatched(Game));
            installBtn.Text = ok && Installer.InstalledVersion(Game) != null ? "Update" : "Install";
            installBtn.Enabled = !busy && ok && Installer.PayloadProblem() == null;
            uninstallBtn.Enabled = !busy && installed;
            browseBtn.Enabled = gameBox.Enabled = modList.Enabled = !busy;
        }

        void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), line); return; }
            logBox.AppendText(line + Environment.NewLine);
        }

        bool ReadyToChange()
        {
            if (Installer.GameRunning())
            {
                MessageBox.Show(this, "Nine Lives is running. Close the game first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (!Installer.CanWrite(Game))
            {
                var r = MessageBox.Show(this, "Setup can't write to the game folder (it may be under Program Files). Restart Setup as administrator?",
                                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--game \"" + Game + "\"") { Verb = "runas", UseShellExecute = true });
                        Close();
                    }
                    catch { }   // they said no to the UAC prompt
                }
                return false;
            }
            return true;
        }

        void Run(string what, Action work)
        {
            busy = true;
            Refresh_();
            UseWaitCursor = true;
            new Thread(() =>
            {
                string error = null;
                try { work(); }
                catch (Exception e) { error = e.Message; Log("Error: " + e.Message); }
                BeginInvoke(new Action(() =>
                {
                    busy = false;
                    UseWaitCursor = false;
                    Refresh_();
                    SetChecks();
                    if (error != null) MessageBox.Show(this, what + " didn't finish:\n\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else MessageBox.Show(this, what + " finished.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            }) { IsBackground = true }.Start();
        }

        void DoInstall()
        {
            if (!ReadyToChange()) return;
            var ids = modList.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((ModEntry)i.Tag).Id).ToList();
            var game = Game;
            bool sc = shortcut.Checked;
            logBox.Clear();
            Run(installBtn.Text, () => Installer.Install(game, ids, sc, Log));
        }

        void DoUninstall()
        {
            if (!ReadyToChange()) return;
            var r = MessageBox.Show(this,
                "This puts the original game files back and removes the offline server and the mods.\n\n" +
                "Keep your offline save and your mod packs?\n\n" +
                "Yes = keep them (you can install again later and carry on)\n" +
                "No = delete them too",
                "Uninstall Nine Lives Offline", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (r == DialogResult.Cancel) return;
            bool delete = r == DialogResult.No;
            if (delete && MessageBox.Show(this, "Delete your offline save (OfflineServer\\save.json) and everything in the Mods folder? This can't be undone.",
                                          "Uninstall Nine Lives Offline", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                return;
            var game = Game;
            logBox.Clear();
            Run("Uninstall", () => Installer.Uninstall(game, delete, Log));
        }
    }
}
