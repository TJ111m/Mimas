using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Mimas
{
    public sealed class AppConfig
    {
        public string Upstream = "https://ai.hajimi.yun";
        public int Port = 8817;
        public string Mode = "always";          // "always" | "watch"
        public List<string> WatchProcesses = new List<string> { "ZCode" };
        public bool StartWithWindows = false;
        public bool Verbose = false;
        public bool SetupCompleted = false;     // first-run wizard dismissed

        // True when the config file already carried a "setupCompleted" key.
        // An old install (v1.0.0) has no such key and must not be treated as
        // an unfinished setup.
        public bool HasSetupFlag = false;

        public static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
        public static string ExeDir { get { return Path.GetDirectoryName(ExePath); } }
        public static string ConfigPath { get { return Path.Combine(ExeDir, "mimas.config.json"); } }
        public static string LogPath { get { return Path.Combine(ExeDir, "mimas.log"); } }

        public static AppConfig Load()
        {
            AppConfig cfg = new AppConfig();
            if (!File.Exists(ConfigPath)) { cfg.Save(); return cfg; }

            try
            {
                JNode n = Json.Parse(File.ReadAllText(ConfigPath));
                JNode v;

                v = n.Get("upstream");
                if (v != null && v.IsString && v.AsString.Trim().Length > 0) cfg.Upstream = v.AsString.Trim();

                v = n.Get("port");
                if (v != null && v.Kind == JKind.Number)
                {
                    int p;
                    if (int.TryParse(v.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p > 0 && p < 65536)
                        cfg.Port = p;
                }

                v = n.Get("mode");
                if (v != null && v.IsString)
                {
                    string m = v.AsString.Trim().ToLowerInvariant();
                    if (m == "always" || m == "watch") cfg.Mode = m;
                }

                v = n.Get("watchProcesses");
                if (v != null && v.IsArray)
                {
                    cfg.WatchProcesses = new List<string>();
                    for (int i = 0; i < v.Arr.Count; i++)
                        if (v.Arr[i] != null && v.Arr[i].IsString && v.Arr[i].AsString.Trim().Length > 0)
                            cfg.WatchProcesses.Add(v.Arr[i].AsString.Trim());
                }

                v = n.Get("startWithWindows");
                if (v != null && v.Kind == JKind.Bool) cfg.StartWithWindows = v.BoolValue;

                v = n.Get("verbose");
                if (v != null && v.Kind == JKind.Bool) cfg.Verbose = v.BoolValue;

                v = n.Get("setupCompleted");
                if (v != null && v.Kind == JKind.Bool)
                {
                    cfg.HasSetupFlag = true;
                    cfg.SetupCompleted = v.BoolValue;
                }
            }
            catch { }

            return cfg;
        }

        public void Save()
        {
            JNode n = JNode.NewObject();
            n.Set("upstream", JNode.NewString(Upstream));
            n.Set("port", JNode.NewNumber(Port.ToString(CultureInfo.InvariantCulture)));

            JNode arr = JNode.NewArray();
            for (int i = 0; i < WatchProcesses.Count; i++) arr.Arr.Add(JNode.NewString(WatchProcesses[i]));

            n.Set("mode", JNode.NewString(Mode));
            n.Set("watchProcesses", arr);
            n.Set("startWithWindows", JNode.NewBool(StartWithWindows));
            n.Set("verbose", JNode.NewBool(Verbose));
            n.Set("setupCompleted", JNode.NewBool(SetupCompleted));

            File.WriteAllText(ConfigPath, PrettyPrint(Json.Serialize(n)));
        }

        static string PrettyPrint(string compact)
        {
            // Small cosmetic pass; the file is hand-edited by users.
            System.Text.StringBuilder sb = new System.Text.StringBuilder(compact.Length + 64);
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < compact.Length; i++)
            {
                char c = compact[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < compact.Length) { sb.Append(compact[++i]); continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        sb.Append(c).Append("\r\n").Append(new string(' ', (indent + 1) * 2));
                        indent++;
                        break;
                    case '}':
                    case ']':
                        indent--;
                        sb.Append("\r\n").Append(new string(' ', indent * 2)).Append(c);
                        break;
                    case ',':
                        sb.Append(c).Append("\r\n").Append(new string(' ', indent * 2));
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
