using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NineLivesEditor
{
    /// <summary>
    /// Test tools that live outside the game: save backups (back up now, restore one with the game closed),
    /// and the update notice (the latest release on GitHub, compared with the version Setup installed).
    /// The live ones (give items, reset a quest, go to an NPC) are the mod loader's /test/... requests.
    /// </summary>
    partial class EditorServer
    {
        const string Repo = "THICC-debug/ninelives-offline-server";

        /// <summary>Where the offline server keeps the save: next to it, or in %LOCALAPPDATA% when the game folder can't be written to.</summary>
        string SaveFile
        {
            get
            {
                var near = Path.Combine(game, "OfflineServer", "save.json");
                var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NineLivesOffline", "save.json");
                if (File.Exists(near) && File.Exists(local)) return File.GetLastWriteTimeUtc(local) > File.GetLastWriteTimeUtc(near) ? local : near;
                return File.Exists(local) ? local : near;
            }
        }
        string BackupDir { get { return Path.Combine(Path.GetDirectoryName(SaveFile), "backups"); } }
        static readonly Regex BackupName = new Regex(@"^save-[0-9]{8}-[0-9]{6}(-[a-z-]{1,30})?\.json$");

        Response TestGet(string[] parts, Dictionary<string, string> q)
        {
            if (parts.Length >= 3 && parts[2] == "backups")
            {
                var list = Directory.Exists(BackupDir)
                    ? new DirectoryInfo(BackupDir).GetFiles("save-*.json").Where(f => BackupName.IsMatch(f.Name)).OrderByDescending(f => f.Name)
                        .Select(f => (object)new Dictionary<string, object> { { "name", f.Name }, { "size", f.Length }, { "time", f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") } }).ToList()
                    : new List<object>();
                return JsonObj(new Dictionary<string, object> { { "save", SaveFile }, { "exists", File.Exists(SaveFile) }, { "backups", list }, { "busy", GameOrServerRunning() } });
            }
            return Error(404, "not found");
        }

        Response TestPost(string[] parts, Dictionary<string, string> q)
        {
            if (parts[2] == "backup")
            {
                if (!File.Exists(SaveFile)) return Error(404, "There's no save yet: play once first.");
                var name = Backup("");
                return JsonObj(new Dictionary<string, object> { { "ok", true }, { "name", name } });
            }
            if (parts[2] == "restore")
            {
                var name = Q(q, "name");
                if (!BackupName.IsMatch(name) || !File.Exists(Path.Combine(BackupDir, name))) return Error(404, "No such backup.");
                if (GameOrServerRunning()) return Error(409, "Close the game first (and its offline server): a running game would write over the restored save.");
                var keep = File.Exists(SaveFile) ? Backup("before-restore") : null;   // the save you had, in case
                File.Copy(Path.Combine(BackupDir, name), SaveFile, true);
                return JsonObj(new Dictionary<string, object> { { "ok", true }, { "kept", keep } });
            }
            if (parts[2] == "deletebackup")
            {
                var name = Q(q, "name");
                if (!BackupName.IsMatch(name) || !File.Exists(Path.Combine(BackupDir, name))) return Error(404, "No such backup.");
                File.Delete(Path.Combine(BackupDir, name));
                return JsonObj(new Dictionary<string, object> { { "ok", true } });
            }
            return Error(404, "not found");
        }

        string Backup(string label)
        {
            Directory.CreateDirectory(BackupDir);
            var name = "save-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (label.Length > 0 ? "-" + label : "") + ".json";
            File.Copy(SaveFile, Path.Combine(BackupDir, name), true);
            return name;
        }

        bool GameOrServerRunning()
        {
            var st = GameRunner.Status(game);
            if (true.Equals(st["running"]) || true.Equals(st["other"]) || true.Equals(st["server"])) return true;
            return false;
        }

        // ------------------------------------------------------------ update notice
        static object updateCache; static DateTime updateAt;
        static readonly object updateGate = new object();

        /// <summary>
        /// {current, latest, newer, url, name}: the latest release on GitHub against the version Setup installed
        /// (OfflineServer\version.txt). Asked at most every 12 hours; nothing about you is sent.
        /// </summary>
        object UpdateInfo(bool force)
        {
            var current = (Json.ReadText(Path.Combine(game, "OfflineServer", "version.txt")) ?? "").Trim();
            lock (updateGate)
            {
                if (!force && updateCache != null && DateTime.UtcNow - updateAt < TimeSpan.FromHours(12)) return With(updateCache, current);
                object result;
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                    var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
                    req.UserAgent = "NineLivesEditor"; req.Accept = "application/vnd.github+json"; req.Timeout = req.ReadWriteTimeout = 8000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        var j = Json.Obj(Json.Parse(r.ReadToEnd()));
                        result = new Dictionary<string, object> {
                            { "latest", (Json.Str(j, "tag_name") ?? "").TrimStart('v', 'V') }, { "name", Json.Str(j, "name") ?? "" },
                            { "url", Json.Str(j, "html_url") ?? "https://github.com/" + Repo + "/releases" }, { "published", Json.Str(j, "published_at") ?? "" } };
                    }
                }
                catch (Exception e) { result = new Dictionary<string, object> { { "error", "Couldn't check for updates: " + e.Message } }; }
                updateCache = result; updateAt = DateTime.UtcNow;
                return With(result, current);
            }
        }

        static object With(object cached, string current)
        {
            var d = new Dictionary<string, object>((Dictionary<string, object>)cached);
            d["current"] = current;
            var latest = Json.Str(d, "latest") ?? "";
            d["newer"] = current.Length > 0 && latest.Length > 0 && CompareVersions(latest, current) > 0;
            return d;
        }
    }
}
