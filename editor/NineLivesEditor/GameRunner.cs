using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace NineLivesEditor
{
    /// <summary>
    /// The editor's 3D view: starts the offline server and the game in the background (no window, no
    /// sound) with -nleditor. The mod loader then logs in with the editor's own account, enters the world
    /// and gives the camera to the editor. Stopped again when the editor closes (only what we started).
    /// </summary>
    static class GameRunner
    {
        static Process game, server;
        static string lastError;
        static bool shown;

        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        const int GWL_STYLE = -16; const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = unchecked((int)0x80000000), WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
        static IntPtr embeddedIn = IntPtr.Zero, gameWindow = IntPtr.Zero;
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

        static bool Alive(Process p) { try { return p != null && !p.HasExited; } catch { return false; } }

        // ------------------------------------------------------------ the game and server go with the editor, even if it crashes
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic; public IO_COUNTERS Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        static IntPtr job = IntPtr.Zero;
        static void KeepWithEditor(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }   // without it, things still work (they just don't go away if the editor is killed)
        }

        /// <summary>A game started some other way (the Play shortcut).</summary>
        static Process OtherGame(string dir)
        {
            foreach (var p in Process.GetProcessesByName("Ninelives"))
            {
                if (game != null && p.Id == game.Id) continue;
                try { if (string.Equals(Path.GetDirectoryName(p.MainModule.FileName), dir, StringComparison.OrdinalIgnoreCase)) return p; } catch { }
            }
            return null;
        }

        static bool ServerUp()
        {
            try { using (var c = new TcpClient()) { var r = c.BeginConnect("127.0.0.1", 8080, null, null); return r.AsyncWaitHandle.WaitOne(300) && c.Connected; } }
            catch { return false; }
        }

        public static Dictionary<string, object> Status(string dir)
        {
            var other = OtherGame(dir);
            return new Dictionary<string, object> {
                { "running", Alive(game) }, { "other", other != null }, { "server", ServerUp() },
                { "shown", shown }, { "docked", Docked }, { "error", lastError } };
        }

        static readonly object gate = new object();

        /// <summary>Start the server and the game (once: two requests at the same moment used to start two games).</summary>
        public static Dictionary<string, object> Start(string dir)
        {
            lock (gate) return StartLocked(dir);
        }

        static Dictionary<string, object> StartLocked(string dir)
        {
            lastError = null;
            if (Alive(game)) return Status(dir);
            if (OtherGame(dir) != null) { lastError = "The game is already running (started with the Play shortcut). Close it first, or edit with it."; return Status(dir); }
            try
            {
                if (!ServerUp())
                {
                    if (Alive(server)) { try { server.Kill(); server.WaitForExit(3000); } catch { } }
                    var py = Path.Combine(dir, "OfflineServer", "python", "python.exe");
                    var script = Path.Combine(dir, "OfflineServer", "ninelives_server.py");
                    if (!File.Exists(py) || !File.Exists(script)) throw new Exception("The offline server isn't installed (run Setup.exe).");
                    server = Process.Start(new ProcessStartInfo(py, "\"" + script + "\" --exit-with-game") {
                        WorkingDirectory = Path.Combine(dir, "OfflineServer"), UseShellExecute = false, CreateNoWindow = true });
                    KeepWithEditor(server);
                    for (int i = 0; i < 30 && !ServerUp(); i++) Thread.Sleep(200);
                    for (int i = 0; i < 40 && !ServerUp() && Alive(server); i++) Thread.Sleep(250);   // a first start (or a virus scan) can be slow
                    if (!ServerUp())
                    {
                        try { if (Alive(server)) server.Kill(); } catch { }
                        server = null;
                        throw new Exception("The offline server didn't start (is Python in OfflineServer\\python?).");
                    }
                }
                var exe = Path.Combine(dir, "Ninelives.exe");
                embeddedIn = IntPtr.Zero; gameWindow = IntPtr.Zero;
                game = Process.Start(new ProcessStartInfo(exe, "-nleditor -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -popupwindow") {
                    WorkingDirectory = dir, UseShellExecute = false });
                shown = false;
                KeepWithEditor(game);
                new Thread(() => KeepHidden(game)) { IsBackground = true, Name = "hide game window" }.Start();
            }
            catch (Exception e) { lastError = e.Message; }
            return Status(dir);
        }

        /// <summary>The game's own window (Unity's), if the editor started the game.</summary>
        static IntPtr GameWindow()
        {
            var g = game;   // (another thread may clear it)
            if (!Alive(g)) return IntPtr.Zero;
            uint id = (uint)g.Id;
            if (gameWindow != IntPtr.Zero)
            {
                uint owner; GetWindowThreadProcessId(gameWindow, out owner);
                if (IsWindow(gameWindow) && owner == id) return gameWindow;
                gameWindow = IntPtr.Zero; embeddedIn = IntPtr.Zero;   // gone or reused: find it again
            }
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != id) return true;
                var sb = new System.Text.StringBuilder(64); GetClassName(h, sb, 64);
                if (sb.ToString() == "UnityWndClass") { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return gameWindow = found;
        }

        /// <summary>
        /// Put the game's window inside the editor (in "host", at x,y size w×h), or take it out of sight.
        /// Returns whether the game is shown there.
        /// </summary>
        public static bool Dock(IntPtr host, int w, int h, bool show)
        {
            var win = GameWindow();
            if (win == IntPtr.Zero) return false;
            if (embeddedIn != host)
            {
                int style = GetWindowLong(win, GWL_STYLE);
                style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD;
                SetWindowLong(win, GWL_STYLE, style);
                SetParent(win, host);
                embeddedIn = host;
            }
            if (show) { MoveWindow(win, 0, 0, Math.Max(16, w), Math.Max(16, h), true); ShowWindow(win, SW_SHOWNOACTIVATE); }
            return true;
        }

        public static bool Docked { get { return embeddedIn != IntPtr.Zero && Alive(game); } }

        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        const int WM_ACTIVATE = 0x0006, WM_ACTIVATEAPP = 0x001C, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008;

        /// <summary>
        /// Inside another program's window the game thinks it's in the background and ignores the keyboard
        /// and mouse. Tell it it's active while the mouse is over the 3D view.
        /// </summary>
        public static void Activate(bool on)
        {
            var win = GameWindow();
            if (win == IntPtr.Zero || embeddedIn == IntPtr.Zero) return;
            // Posted, not sent: a busy (loading) game mustn't freeze the editor's window.
            PostMessage(win, WM_ACTIVATEAPP, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
            PostMessage(win, WM_ACTIVATE, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
            PostMessage(win, on ? WM_SETFOCUS : WM_KILLFOCUS, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Hide the game's windows as they appear (the first minute), unless "show" was asked for.</summary>
        static void KeepHidden(Process p)
        {
            while (Alive(p) && embeddedIn == IntPtr.Zero)   // until it's inside the editor (a slow start too)
            {
                if (!shown) SetShown(p, false);
                Thread.Sleep(150);
            }
        }

        static void SetShown(Process p, bool show)
        {
            if (!Alive(p)) return;
            uint id = (uint)p.Id;
            EnumWindows((h, l) =>
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid == id && (show || IsWindowVisible(h))) ShowWindow(h, show ? SW_SHOWNOACTIVATE : SW_HIDE);
                return true;
            }, IntPtr.Zero);
        }

        public static Dictionary<string, object> Show(string dir, bool show)
        {
            shown = show;
            SetShown(game, show);
            return Status(dir);
        }

        /// <summary>Stop what the editor started (the game, and the server if we started it).</summary>
        public static Dictionary<string, object> Stop(string dir)
        {
            lock (gate) return StopLocked(dir);
        }

        static Dictionary<string, object> StopLocked(string dir)
        {
            try { if (Alive(game)) { game.Kill(); game.WaitForExit(5000); } } catch { }
            try { if (Alive(server)) { server.Kill(); server.WaitForExit(3000); } } catch { }
            game = null; server = null; shown = false; embeddedIn = IntPtr.Zero; gameWindow = IntPtr.Zero;
            return Status(dir);
        }

        public static void StopOnExit() { try { Stop(null); } catch { } }
    }
}
