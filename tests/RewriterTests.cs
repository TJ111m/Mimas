// Offline tests for the request rewriter. No network, no upstream required.
//
//   scripts\test.cmd
//
// Covers the block-ordering rule that makes or breaks this proxy: hoisted
// blocks must land after the LAST tool_result, never between two of them.
using System;
using System.Collections.Generic;
using Mimas;

namespace MimasTests
{
    static class Program
    {
        static int _passed;
        static int _failed;

        static void Main()
        {
            TextOnlyUntouched();
            NoToolResultUntouched();
            ImageInsideArrayIsHoisted();
            HoistedBlockSitsAfterLastToolResult();
            ImageOnlyResultGetsPlaceholder();
            TwoImagesBothHoisted();
            TextPlusImageKeepsText();
            DataUrlStringIsHoisted();
            SerializedArrayIsHoisted();
            NonImageBlocksStayPut();
            MessageOrderPreserved();
            NumbersKeepOriginalText();

            Console.WriteLine();
            Console.WriteLine("passed " + _passed + ", failed " + _failed);
            Environment.Exit(_failed == 0 ? 0 : 1);
        }

        // ---------- tests ----------

        static void TextOnlyUntouched()
        {
            string body = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}]}";
            JNode n = Json.Parse(body);
            RewriteResult r = Rewriter.Rewrite(n);
            Check("text-only body is untouched", !r.Changed && Json.Serialize(n) == body);
        }

        static void NoToolResultUntouched()
        {
            string body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"AAA\"}}]}]}";
            JNode n = Json.Parse(body);
            RewriteResult r = Rewriter.Rewrite(n);
            Check("top-level image is untouched", !r.Changed && Json.Serialize(n) == body);
        }

        static void ImageInsideArrayIsHoisted()
        {
            JNode n = Json.Parse(ToolResultBody(Img("AAA"), null));
            RewriteResult r = Rewriter.Rewrite(n);

            JNode content = n.Get("messages").Arr[2].Get("content");
            Check("nested image is hoisted", r.Changed && r.BlocksHoisted == 1);
            Check("user message now has 2 blocks", content.Arr.Count == 2);
            Check("first block is the tool_result", TypeOf(content.Arr[0]) == "tool_result");
            Check("second block is the image", TypeOf(content.Arr[1]) == "image");
            Check("tool_result kept a text placeholder", ToolResultText(content.Arr[0]).Length > 0);
        }

        static void HoistedBlockSitsAfterLastToolResult()
        {
            // two tool_results, image only in the first one
            string body = "{\"messages\":[{\"role\":\"user\",\"content\":["
                + "{\"type\":\"tool_result\",\"tool_use_id\":\"a\",\"content\":[" + Img("AAA") + "]},"
                + "{\"type\":\"tool_result\",\"tool_use_id\":\"b\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}"
                + "]}]}";

            JNode n = Json.Parse(body);
            Rewriter.Rewrite(n);

            JNode content = n.Get("messages").Arr[0].Get("content");
            Check("both tool_results stay contiguous", TypeOf(content.Arr[0]) == "tool_result" && TypeOf(content.Arr[1]) == "tool_result");
            Check("image lands after the last tool_result", TypeOf(content.Arr[2]) == "image");
        }

        static void ImageOnlyResultGetsPlaceholder()
        {
            JNode n = Json.Parse(ToolResultBody(Img("AAA"), null));
            Rewriter.Rewrite(n);
            JNode tr = n.Get("messages").Arr[2].Get("content").Arr[0];
            JNode inner = tr.Get("content");
            Check("image-only result becomes a text placeholder", inner.IsArray && inner.Arr.Count == 1 && TypeOf(inner.Arr[0]) == "text");
        }

        static void TwoImagesBothHoisted()
        {
            JNode n = Json.Parse(ToolResultBody(Img("AAA") + "," + Img("BBB"), null));
            RewriteResult r = Rewriter.Rewrite(n);
            JNode content = n.Get("messages").Arr[2].Get("content");
            Check("two images both hoisted", r.BlocksHoisted == 2 && content.Arr.Count == 3);
        }

        static void TextPlusImageKeepsText()
        {
            JNode n = Json.Parse(ToolResultBody("{\"type\":\"text\",\"text\":\"screenshot below\"}," + Img("AAA"), null));
            Rewriter.Rewrite(n);
            JNode tr = n.Get("messages").Arr[2].Get("content").Arr[0];
            Check("original text is preserved inside tool_result", ToolResultText(tr) == "screenshot below");
        }

        static void DataUrlStringIsHoisted()
        {
            string body = "{\"messages\":[{\"role\":\"user\",\"content\":["
                + "{\"type\":\"tool_result\",\"tool_use_id\":\"a\",\"content\":\"data:image/png;base64,QUJD\"}"
                + "]}]}";
            JNode n = Json.Parse(body);
            RewriteResult r = Rewriter.Rewrite(n);

            JNode content = n.Get("messages").Arr[0].Get("content");
            Check("data URL string is hoisted", r.BlocksHoisted == 1 && TypeOf(content.Arr[1]) == "image");
            JNode source = content.Arr[1].Get("source");
            Check("data URL keeps its media type", source.Get("media_type").AsString == "image/png");
            Check("data URL payload preserved", source.Get("data").AsString == "QUJD");
        }

        static void SerializedArrayIsHoisted()
        {
            // the relay's own failure shape: whole content array handed over as a string
            string inner = "[" + Img("AAA").Replace("\"", "\\\"") + "]";
            string body = "{\"messages\":[{\"role\":\"user\",\"content\":["
                + "{\"type\":\"tool_result\",\"tool_use_id\":\"a\",\"content\":\"" + inner + "\"}"
                + "]}]}";

            JNode n = Json.Parse(body);
            RewriteResult r = Rewriter.Rewrite(n);
            Check("stringified block array is hoisted", r.Changed && r.BlocksHoisted == 1);
        }

        static void NonImageBlocksStayPut()
        {
            JNode n = Json.Parse(ToolResultBody("{\"type\":\"text\",\"text\":\"plain\"}", null));
            RewriteResult r = Rewriter.Rewrite(n);
            Check("text-only tool_result is untouched", !r.Changed);
        }

        static void MessageOrderPreserved()
        {
            JNode n = Json.Parse(ToolResultBody(Img("AAA"), null));
            Rewriter.Rewrite(n);
            JNode msgs = n.Get("messages");
            Check("message count unchanged", msgs.Arr.Count == 3);
            Check("assistant message untouched", msgs.Arr[1].Get("role").AsString == "assistant");
        }

        static void NumbersKeepOriginalText()
        {
            string body = "{\"a\":1.0,\"b\":1e10,\"c\":-0.50}";
            JNode n = Json.Parse(body);
            Check("number formatting survives round-trip", Json.Serialize(n) == body);
        }

        // ---------- helpers ----------

        static string Img(string data)
        {
            return "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + data + "\"}}";
        }

        static string ToolResultBody(string inner, string unused)
        {
            return "{\"messages\":["
                + "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"read it\"}]},"
                + "{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{}}]},"
                + "{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":[" + inner + "]}]}"
                + "]}";
        }

        static string TypeOf(JNode n)
        {
            JNode t = n.Get("type");
            return t == null ? null : t.AsString;
        }

        static string ToolResultText(JNode tr)
        {
            JNode c = tr.Get("content");
            if (c == null) return "";
            if (c.IsString) return c.AsString;
            if (c.IsArray && c.Arr.Count > 0) return c.Arr[0].Get("text").AsString;
            return "";
        }

        static void Check(string name, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine("  ok   " + name); }
            else { _failed++; Console.WriteLine("  FAIL " + name); }
        }
    }
}
