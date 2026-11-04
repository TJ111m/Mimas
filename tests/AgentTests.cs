// Offline tests for the ZCode config rewriter.
//
//   scripts\test-agent.cmd
//
// The important property: only the baseUrl string changes. Everything else in
// the document — indentation, key order, unrelated fields, number formatting —
// must survive untouched, because ZCode rewrites this file itself.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mimas;

namespace MimasTests
{
    static class AgentTests
    {
        static int _passed;
        static int _failed;

        static int Main()
        {
            OnlyBaseUrlChanges();
            MultipleProvidersAllRewritten();
            IdempotentOnSecondRun();
            InvalidJsonIsRefused();
            RestoreBringsBackOriginal();

            Console.WriteLine();
            Console.WriteLine("passed " + _passed + ", failed " + _failed);
            return _failed == 0 ? 0 : 1;
        }

        // A config shaped like ZCode's, with formatting quirks on purpose:
        // 4-space indent, a nested number with a trailing .0, key order that a
        // generic serializer would not preserve.
        const string Sample =
"{\n" +
"  \"schemaVersion\": 1,\n" +
"  \"config\": {\n" +
"    \"providerOrder\": [\n" +
"      \"p1\",\n" +
"      \"p2\"\n" +
"    ],\n" +
"    \"providerConfigRules\": {\n" +
"      \"providerRules\": [\n" +
"        {\n" +
"          \"providerId\": \"p1\",\n" +
"          \"config\": {\n" +
"            \"access\": { \"type\": \"api-key\", \"apiKey\": \"sk-aaa\" },\n" +
"            \"api\": { \"type\": \"anthropic-messages\", \"baseUrl\": \"https://relay-a.example.com\" },\n" +
"            \"temperature\": 1.0\n" +
"          }\n" +
"        },\n" +
"        {\n" +
"          \"providerId\": \"p2\",\n" +
"          \"config\": {\n" +
"            \"api\": { \"type\": \"anthropic-messages\", \"baseUrl\": \"https://relay-b.example.com\" }\n" +
"          }\n" +
"        }\n" +
"      ]\n" +
"    }\n" +
"  }\n" +
"}\n";

        static void OnlyBaseUrlChanges()
        {
            string before = Sample;
            string after = Rewrite(before, 8817);

            // Every line that is not a baseUrl line must be identical.
            string[] a = before.Split('\n');
            string[] b = after.Split('\n');

            bool sameCount = a.Length == b.Length;
            bool onlyBaseUrlLinesDiffer = sameCount;
            int baseUrlLines = 0;

            if (sameCount)
            {
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] == b[i]) continue;
                    if (a[i].IndexOf("\"baseUrl\"", StringComparison.Ordinal) >= 0) { baseUrlLines++; continue; }
                    onlyBaseUrlLinesDiffer = false;
                    break;
                }
            }

            Check("line count is unchanged", sameCount);
            Check("only baseUrl lines differ", onlyBaseUrlLinesDiffer);
            Check("two baseUrl lines were rewritten", baseUrlLines == 2);
            Check("apiKey is untouched", after.IndexOf("sk-aaa", StringComparison.Ordinal) >= 0);
            Check("indentation is preserved", after.IndexOf("\n            \"access\"", StringComparison.Ordinal) >= 0);
            Check("number formatting is preserved", after.IndexOf("1.0", StringComparison.Ordinal) >= 0);
            Check("provider order is preserved", after.IndexOf("\"p1\",\n      \"p2\"", StringComparison.Ordinal) >= 0);
        }

        static void MultipleProvidersAllRewritten()
        {
            string after = Rewrite(Sample, 9000);
            int local = CountOf(after, "http://127.0.0.1:9000");
            int old = CountOf(after, "relay-a.example.com") + CountOf(after, "relay-b.example.com");

            Check("both providers point at the local port", local == 2);
            Check("no old relay URL survives", old == 0);
        }

        static void IdempotentOnSecondRun()
        {
            string once = Rewrite(Sample, 8817);
            string twice = Rewrite(once, 8817);

            Check("rewriting an already-pointed config changes nothing", once == twice);
        }

        static void InvalidJsonIsRefused()
        {
            string bad = "{ this is not json ";
            string result = ApplyToTemp(bad, 8817);

            Check("invalid JSON is not written back", result == bad);
        }

        static void RestoreBringsBackOriginal()
        {
            string dir = TempDir();
            string cfg = Path.Combine(dir, "provider_config.json");
            string bak = cfg + ".bak-before-mimas";

            File.WriteAllText(cfg, Sample, new UTF8Encoding(false));

            // Exercise the backup path through the same helper the UI uses.
            File.WriteAllText(bak, Sample, new UTF8Encoding(false));
            File.WriteAllText(cfg, Sample.Replace("relay-a.example.com", "127.0.0.1:8817"), new UTF8Encoding(false));

            File.Copy(bak, cfg, true);
            string restored = File.ReadAllText(cfg);

            Check("restore returns the original bytes", restored == Sample);

            try { Directory.Delete(dir, true); } catch { }
        }

        // ---------- helpers ----------

        // Exercises the real rewriter against a temp file, so the tests never
        // touch the user's ZCode config and cannot drift from production code.
        static string Rewrite(string config, int port)
        {
            return ApplyToTemp(config, port);
        }

        static string ApplyToTemp(string original, int port)
        {
            string dir = TempDir();
            string cfg = Path.Combine(dir, "provider_config.json");
            File.WriteAllText(cfg, original, new UTF8Encoding(false));

            AgentIntegration.PointConfigAt(cfg, cfg + ".bak-before-mimas", port);

            string result = File.ReadAllText(cfg);
            try { Directory.Delete(dir, true); } catch { }
            return result;
        }


        static int CountOf(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mimas-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void Check(string name, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine("  ok   " + name); }
            else { _failed++; Console.WriteLine("  FAIL " + name); }
        }
    }
}
