using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NineLivesEditor
{
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        /// <summary>
        /// NineLivesEditor.exe [--game DIR] [--port N] [--browser] [--server-only]
        ///   --browser      open in your web browser instead of the app window
        ///   --server-only  just run the server (prints its address), for testing
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            Resources.HookAssemblies();     // before anything touches WebView2
            return Run(args);
        }

        static int Run(string[] args)
        {
            System.Net.ServicePointManager.DefaultConnectionLimit = 16;   // several pages talk to the game at once (the default is 2)
            Func<string, string> opt = n => { int i = Array.FindIndex(args, a => a.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; };
            Func<string, bool> has = n => args.Any(a => a.Equals(n, StringComparison.OrdinalIgnoreCase));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (opt("--web") != null && Directory.Exists(opt("--web"))) Resources.WebDir = Path.GetFullPath(opt("--web"));   // working on the pages
            var game = FindGame(opt("--game"));
            if (game == null)
            {
                if (has("--server-only")) { AttachConsole(-1); Console.WriteLine("Can't find the game. Pass --game DIR."); return 1; }
                game = AskForGame();
                if (game == null) return 1;
            }

            EditorServer server;
            int port = 0;
            int.TryParse(opt("--port") ?? "0", out port);
            try { server = new EditorServer(game, port); }
            catch (Exception e)
            {
                Fail("Couldn't start the editor's local server" + (port > 0 ? " on port " + port : "") + ":\n\n" + e.Message, has("--server-only"));
                return 1;
            }
            Remember(game);

            if (has("--server-only"))
            {
                AttachConsole(-1);
                Console.WriteLine("Nine Lives Editor running at " + server.Url + "  (game: " + game + ")");
                Thread.Sleep(Timeout.Infinite);
            }
            Application.ApplicationExit += (o, e) => GameRunner.StopOnExit();   // the 3D view goes with the editor
            if (has("--browser") || !WebViewAvailable())
                Application.Run(new BrowserForm(server.Url, game, !has("--browser")));
            else
                Application.Run(new MainForm(server.Url, game));
            return 0;
        }

        static void Fail(string msg, bool console)
        {
            if (console) { AttachConsole(-1); Console.WriteLine(msg); }
            else MessageBox.Show(msg, "Nine Lives Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static bool WebViewAvailable()
        {
            try
            {
                Resources.PrepareNativeLoader();
                return !string.IsNullOrEmpty(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ finding the game
        static bool IsGame(string d) { return !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "Ninelives.exe")); }

        static string LastGameFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NineLivesOffline", "last-game.txt"); }
        }

        /// <summary>Next to the exe (installed in the game folder), a parent or a sibling "Ninelives", or the one Setup last used.</summary>
        static string FindGame(string explicit_)
        {
            if (explicit_ != null) return IsGame(explicit_) ? Path.GetFullPath(explicit_) : null;
            var d = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(d); i++)
            {
                if (IsGame(d)) return d;
                if (IsGame(Path.Combine(d, "Ninelives"))) return Path.Combine(d, "Ninelives");
                d = Path.GetDirectoryName(d);
            }
            try { var last = File.ReadAllText(LastGameFile).Trim(); if (IsGame(last)) return last; } catch { }
            return null;
        }

        static void Remember(string game)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(LastGameFile)); File.WriteAllText(LastGameFile, game); } catch { }
        }

        static string AskForGame()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Where is Nine Lives? Pick the folder with Ninelives.exe.", ShowNewFolderButton = false })
                while (dlg.ShowDialog() == DialogResult.OK)
                {
                    if (IsGame(dlg.SelectedPath)) return dlg.SelectedPath;
                    MessageBox.Show("Ninelives.exe isn't in that folder.", "Nine Lives Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            return null;
        }

        /// <summary>The editor's own "9E" icon (built into the exe).</summary>
        public static Icon AppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return null; }
        }

        public static void OpenInBrowser(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }

    /// <summary>When WebView2 isn't there (or --browser): the editor opens in the web browser, and this small window keeps it running.</summary>
    class BrowserForm : Form
    {
        public BrowserForm(string url, string game, bool noWebView)
        {
            Text = "Nine Lives Editor";
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(470, 170);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            var icon = Program.AppIcon(); if (icon != null) Icon = icon;
            var text = new Label
            {
                Text = (noWebView ? "Microsoft Edge WebView2 isn't installed, so the editor opens in your web browser.\n\n" : "The editor is open in your web browser.\n\n") +
                       "Keep this window open while you edit. Closing it stops the editor.",
                Location = new Point(16, 16), Size = new Size(440, 80),
            };
            var open = new Button { Text = "Open the editor", Location = new Point(16, 116), Size = new Size(140, 32) };
            var close = new Button { Text = "Close the editor", Location = new Point(170, 116), Size = new Size(140, 32) };
            open.Click += (s, e) => Program.OpenInBrowser(url);
            close.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { text, open, close });
            Shown += (s, e) => Program.OpenInBrowser(url);
        }
    }
}
