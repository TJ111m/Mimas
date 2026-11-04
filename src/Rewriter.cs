// Rewrites an Anthropic Messages request body so that image / document blocks
// nested inside tool_result.content travel as sibling content blocks instead.
//
// Background: some Anthropic-compatible relays stringify tool_result.content,
// which turns the base64 image payload into plain text. The upstream then
// tokenizes it as text (~1M tokens for a 1.1MB PNG) and the model never sees
// the image at all. Hoisting the blocks restores both correct vision input and
// a normal token count.
//
// Block ordering constraint: Anthropic requires every tool_result in a user
// message to be contiguous and leading. Hoisted blocks are therefore spliced in
// AFTER the last tool_result, never between tool_results.
using System;
using System.Collections.Generic;

namespace Mimas
{
    public sealed class RewriteResult
    {
        public int BlocksHoisted;
        public bool Changed;
    }

    public static class Rewriter
    {
        static readonly HashSet<string> Hoistable = new HashSet<string> { "image", "document" };

        const string Placeholder = "[image attached in the following content block]";

        public static RewriteResult Rewrite(JNode body)
        {
            RewriteResult result = new RewriteResult();
            if (body == null || !body.IsObject) return result;

            JNode messages = body.Get("messages");
            if (messages == null || !messages.IsArray) return result;

            for (int m = 0; m < messages.Arr.Count; m++)
            {
                JNode msg = messages.Arr[m];
                if (msg == null || !msg.IsObject) continue;

                JNode role = msg.Get("role");
                if (role == null || !role.IsString || role.AsString != "user") continue;

                JNode content = msg.Get("content");
                if (content == null || !content.IsArray) continue;

                List<JNode> collected = new List<JNode>();
                int lastToolResultIdx = -1;

                for (int b = 0; b < content.Arr.Count; b++)
                {
                    JNode block = content.Arr[b];
                    if (block == null || !block.IsObject) continue;

                    JNode type = block.Get("type");
                    if (type == null || !type.IsString) continue;

                    if (type.AsString == "tool_result")
                    {
                        lastToolResultIdx = b;
                        CollectFromToolResult(block, collected);
                    }
                }

                if (collected.Count == 0 || lastToolResultIdx < 0) continue;

                content.Arr.InsertRange(lastToolResultIdx + 1, collected);
                result.BlocksHoisted += collected.Count;
                result.Changed = true;
            }

            return result;
        }

        static void CollectFromToolResult(JNode block, List<JNode> collected)
        {
            JNode content = block.Get("content");
            if (content == null) return;

            if (content.IsArray)
            {
                List<JNode> kept = new List<JNode>();
                int before = collected.Count;
                for (int i = 0; i < content.Arr.Count; i++)
                {
                    JNode part = content.Arr[i];
                    if (part != null && part.IsObject)
                    {
                        JNode t = part.Get("type");
                        if (t != null && t.IsString && Hoistable.Contains(t.AsString))
                        {
                            collected.Add(part);
                            continue;
                        }
                    }
                    kept.Add(part);
                }
                if (collected.Count == before) return;
                if (kept.Count == 0) kept.Add(TextBlock(Placeholder));
                content.Arr.Clear();
                content.Arr.AddRange(kept);
                return;
            }

            if (content.IsString)
            {
                string raw = content.AsString;
                string trimmed = raw.Trim();

                // data URL carried as a bare string
                string mediaType, data;
                if (TryParseDataUrl(trimmed, out mediaType, out data))
                {
                    collected.Add(ImageBlock(mediaType, data));
                    block.Set("content", JNode.NewString(Placeholder));
                    return;
                }

                // some relays hand us the whole content array already serialized
                if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                {
                    JNode parsed;
                    if (!Json.TryParse(trimmed, out parsed)) return;

                    List<JNode> parts = parsed.IsArray ? parsed.Arr : new List<JNode> { parsed };
                    List<JNode> kept = new List<JNode>();
                    int extracted = 0;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        JNode part = parts[i];
                        if (part != null && part.IsObject)
                        {
                            JNode t = part.Get("type");
                            if (t != null && t.IsString && Hoistable.Contains(t.AsString))
                            {
                                collected.Add(part);
                                extracted++;
                                continue;
                            }
                        }
                        kept.Add(part);
                    }
                    if (extracted == 0) return;

                    List<string> texts = new List<string>();
                    for (int i = 0; i < kept.Count; i++)
                    {
                        JNode part = kept[i];
                        if (part != null && part.IsObject)
                        {
                            JNode t = part.Get("type");
                            JNode txt = part.Get("text");
                            if (t != null && t.IsString && t.AsString == "text" && txt != null && txt.IsString)
                                texts.Add(txt.AsString);
                        }
                    }
                    string joined = string.Join("\n", texts.ToArray()).Trim();
                    block.Set("content", JNode.NewString(joined.Length > 0 ? joined : Placeholder));
                }
            }
        }

        static JNode TextBlock(string text)
        {
            JNode n = JNode.NewObject();
            n.Set("type", JNode.NewString("text"));
            n.Set("text", JNode.NewString(text));
            return n;
        }

        static JNode ImageBlock(string mediaType, string data)
        {
            JNode source = JNode.NewObject();
            source.Set("type", JNode.NewString("base64"));
            source.Set("media_type", JNode.NewString(mediaType));
            source.Set("data", JNode.NewString(data));

            JNode n = JNode.NewObject();
            n.Set("type", JNode.NewString("image"));
            n.Set("source", source);
            return n;
        }

        static bool TryParseDataUrl(string s, out string mediaType, out string data)
        {
            mediaType = null;
            data = null;
            if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
            int comma = s.IndexOf(',');
            if (comma < 0) return false;

            string meta = s.Substring(5, comma - 5);
            string[] bits = meta.Split(';');
            if (bits.Length < 2) return false;
            if (bits[bits.Length - 1].Trim().ToLowerInvariant() != "base64") return false;

            string mt = bits[0].Trim().ToLowerInvariant();
            if (!mt.StartsWith("image/")) return false;

            string payload = s.Substring(comma + 1);
            if (payload.Length == 0) return false;

            mediaType = mt;
            data = payload;
            return true;
        }
    }
}
