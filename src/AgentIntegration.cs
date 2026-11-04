// Integration with the agents Mimas sits in front of.
//
// Mimas never stores API keys. It only rewrites the *base URL* an agent points
// at, so the agent keeps its own key and Mimas stays credential-free.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mimas
{
    public enum AgentKind { ZCode, ClaudeCode }

    public sealed class AgentTarget
    {
        public AgentKind Kind;
        public string DisplayName;
        public string Detail;        // where the setting lives
        public bool Configured;      // currently pointed at Mimas
        public bool Present;         // the agent appears to be installed
        public string StatusText;
    }

    public static class AgentIntegration
    {
        public const string LocalBaseUrl = "http://127.0.0.1:8817";

        public static string ZCodeConfigPath
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(Path.Combine(Path.Combine(home, ".zcode"), "v2"), "provider_config.json");
            }
        }

        public static string ZCodeBackupPath
        {
            get { return ZCodeConfigPath + ".bak-before-mimas"; }
        }

        public static List<AgentTarget> Detect(int port)
        {
            List<AgentTarget> list = new List<AgentTarget>();

            string local = "http://127.0.0.1:" + port;

            // ---- ZCode ----
            AgentTarget z = new AgentTarget();
            z.Kind = AgentKind.ZCode;
            z.DisplayName = "ZCode";
            z.Detail = ZCodeConfigPath;
            z.Present = File.Exists(ZCodeConfigPath);
            if (z.Present)
            {
                try
                {
                    string text = File.ReadAllText(ZCodeConfigPath);
                    z.Configured = text.IndexOf(local, StringComparison.OrdinalIgnoreCase) >= 0;
                    z.StatusText = z.Configured ? "已指向 Mimas" : "未指向 Mimas";
                }
                catch (Exception ex) { z.StatusText = "读取失败：" + ex.Message; }
            }
            else z.StatusText = "未找到配置文件";
            list.Add(z);

            // ---- Claude Code ----
            AgentTarget c = new AgentTarget();
            c.Kind = AgentKind.ClaudeCode;
            c.DisplayName = "Claude Code";
            c.Detail = "环境变量 ANTHROPIC_BASE_URL";
            c.Present = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"))
                || HasClaudeOnPath();
            string env = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
            c.Configured = !string.IsNullOrEmpty(env) && env.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0;
            c.StatusText = c.Configured ? "已指向 Mimas" : (string.IsNullOrEmpty(env) ? "未设置" : "未指向 Mimas");
            list.Add(c);

            return list;
        }

        static bool HasClaudeOnPath()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] dirs = path.Split(Path.PathSeparator);
            for (int i = 0; i < dirs.Length; i++)
            {
                try
                {
                    if (File.Exists(Path.Combine(dirs[i].Trim(), "claude.cmd"))
                        || File.Exists(Path.Combine(dirs[i].Trim(), "claude.exe")))
                        return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// Points every provider rule in ZCode's personal config at the local
        /// proxy. The original file is backed up first.
        ///
        /// The rewrite is textual, not a re-serialisation: only the string
        /// values of "baseUrl" are replaced, so the file's indentation, key
        /// order and every other field stay byte-identical. ZCode rewrites this
        /// file itself on exit, and a re-serialised document would show up as a
        /// spurious diff.
        /// </summary>
        public static string PointZCodeAtMimas(int port)
        {
            return PointConfigAt(ZCodeConfigPath, ZCodeBackupPath, port);
        }

        /// <summary>
        /// The rewrite itself, parameterised by path so it can be exercised by
        /// the offline tests without touching a real ZCode install.
        /// </summary>
        public static string PointConfigAt(string path, string backupPath, int port)
        {
            if (!File.Exists(path)) return "找不到 " + path;

            string original;
            try { original = File.ReadAllText(path); }
            catch (Exception ex) { return "读取失败：" + ex.Message; }

            // Back up once; never overwrite an existing backup.
            try
            {
                if (!File.Exists(backupPath))
                    File.WriteAllText(backupPath, original, new UTF8Encoding(false));
            }
            catch (Exception ex) { return "备份失败：" + ex.Message; }

            JNode root;
            if (!Json.TryParse(original, out root)) return "配置文件不是合法 JSON，未做修改";

            string local = "http://127.0.0.1:" + port;

            // Collect the distinct baseUrl values we are replacing.
            List<string> oldUrls = new List<string>();
            CollectBaseUrls(root, oldUrls);

            if (oldUrls.Count == 0) return "配置里没有可改写的 baseUrl";

            string updated = original;
            int changed = 0;
            for (int i = 0; i < oldUrls.Count; i++)
            {
                string oldUrl = oldUrls[i];
                if (oldUrl == local) continue;

                string needle = "\"baseUrl\": \"" + oldUrl + "\"";
                string replacement = "\"baseUrl\": \"" + local + "\"";

                int at = updated.IndexOf(needle, StringComparison.Ordinal);
                while (at >= 0)
                {
                    updated = updated.Substring(0, at) + replacement + updated.Substring(at + needle.Length);
                    changed++;
                    at = updated.IndexOf(needle, at + replacement.Length, StringComparison.Ordinal);
                }
            }

            if (changed == 0) return "已经指向 " + local;

            try { File.WriteAllText(path, updated, new UTF8Encoding(false)); }
            catch (Exception ex) { return "写入失败：" + ex.Message; }

            return "已改写 " + changed + " 个供应商的 baseUrl → " + local;
        }

        static void CollectBaseUrls(JNode node, List<string> into)
        {
            if (node == null) return;

            if (node.IsObject)
            {
                for (int i = 0; i < node.Obj.Count; i++)
                {
                    KeyValuePair<string, JNode> kv = node.Obj[i];
                    if (kv.Key == "baseUrl" && kv.Value != null && kv.Value.IsString)
                    {
                        if (!into.Contains(kv.Value.AsString)) into.Add(kv.Value.AsString);
                    }
                    else CollectBaseUrls(kv.Value, into);
                }
                return;
            }

            if (node.IsArray)
            {
                for (int i = 0; i < node.Arr.Count; i++) CollectBaseUrls(node.Arr[i], into);
            }
        }

        public static string RestoreZCode()
        {
            string backup = ZCodeBackupPath;
            if (!File.Exists(backup)) return "找不到备份文件 " + backup;
            try
            {
                File.Copy(backup, ZCodeConfigPath, true);
                return "已从备份恢复 " + ZCodeConfigPath;
            }
            catch (Exception ex) { return "恢复失败：" + ex.Message; }
        }

        /// <summary>
        /// Sets ANTHROPIC_BASE_URL for the current user. A new terminal (or a
        /// restarted agent) is required for it to take effect.
        /// </summary>
        public static string PointClaudeCodeAtMimas(int port)
        {
            string local = "http://127.0.0.1:" + port;
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", true))
                {
                    if (k == null) return "无法打开 HKCU\\Environment";
                    k.SetValue("ANTHROPIC_BASE_URL", local, Microsoft.Win32.RegistryValueKind.String);
                }
                Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", local);
                return "已设置 ANTHROPIC_BASE_URL=" + local + "（重启终端/agent 后生效）";
            }
            catch (Exception ex) { return "设置失败：" + ex.Message; }
        }

        public static string ClearClaudeCode()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", true))
                {
                    if (k != null) k.DeleteValue("ANTHROPIC_BASE_URL", false);
                }
                Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", null);
                return "已清除 ANTHROPIC_BASE_URL";
            }
            catch (Exception ex) { return "清除失败：" + ex.Message; }
        }
    }
}
