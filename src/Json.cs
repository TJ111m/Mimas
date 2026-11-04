// Minimal JSON DOM that preserves number formatting and key order.
//
// The proxy must re-serialize request bodies byte-faithfully apart from the
// blocks it moves, so a generic serializer (which normalizes 1.0 -> 1 and
// reorders keys) is not good enough. Numbers keep their original text here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mimas
{
    public enum JKind { Null, Bool, Number, String, Array, Object }

    public sealed class JNode
    {
        public JKind Kind;
        public bool BoolValue;
        public string Raw;      // number: original text; string: decoded value
        public List<JNode> Arr;
        public List<KeyValuePair<string, JNode>> Obj;

        public static JNode NewNull() { JNode n = new JNode(); n.Kind = JKind.Null; return n; }
        public static JNode NewBool(bool v) { JNode n = new JNode(); n.Kind = JKind.Bool; n.BoolValue = v; return n; }
        public static JNode NewString(string v) { JNode n = new JNode(); n.Kind = JKind.String; n.Raw = v; return n; }
        public static JNode NewNumber(string raw) { JNode n = new JNode(); n.Kind = JKind.Number; n.Raw = raw; return n; }
        public static JNode NewArray() { JNode n = new JNode(); n.Kind = JKind.Array; n.Arr = new List<JNode>(); return n; }
        public static JNode NewObject() { JNode n = new JNode(); n.Kind = JKind.Object; n.Obj = new List<KeyValuePair<string, JNode>>(); return n; }

        public bool IsObject { get { return Kind == JKind.Object; } }
        public bool IsArray { get { return Kind == JKind.Array; } }
        public bool IsString { get { return Kind == JKind.String; } }

        public string AsString { get { return Kind == JKind.String ? Raw : null; } }

        public JNode Get(string key)
        {
            if (Kind != JKind.Object) return null;
            for (int i = 0; i < Obj.Count; i++)
                if (Obj[i].Key == key) return Obj[i].Value;
            return null;
        }

        public void Set(string key, JNode value)
        {
            if (Kind != JKind.Object) return;
            for (int i = 0; i < Obj.Count; i++)
            {
                if (Obj[i].Key == key) { Obj[i] = new KeyValuePair<string, JNode>(key, value); return; }
            }
            Obj.Add(new KeyValuePair<string, JNode>(key, value));
        }
    }

    public static class Json
    {
        public static JNode Parse(string text)
        {
            int i = 0;
            JNode root = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("trailing data at " + i);
            return root;
        }

        public static bool TryParse(string text, out JNode node)
        {
            try { node = Parse(text); return true; }
            catch { node = null; return false; }
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }

        static JNode ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return JNode.NewString(ParseString(s, ref i));
            if (c == 't') { Expect(s, ref i, "true"); return JNode.NewBool(true); }
            if (c == 'f') { Expect(s, ref i, "false"); return JNode.NewBool(false); }
            if (c == 'n') { Expect(s, ref i, "null"); return JNode.NewNull(); }
            return ParseNumber(s, ref i);
        }

        static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0)
                throw new FormatException("bad literal at " + i);
            i += lit.Length;
        }

        static JNode ParseObject(string s, ref int i)
        {
            JNode n = JNode.NewObject();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return n; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected key at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected : at " + i);
                i++;
                JNode v = ParseValue(s, ref i);
                n.Obj.Add(new KeyValuePair<string, JNode>(key, v));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return n; }
                throw new FormatException("expected , or } at " + i);
            }
        }

        static JNode ParseArray(string s, ref int i)
        {
            JNode n = JNode.NewArray();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return n; }
            while (true)
            {
                JNode v = ParseValue(s, ref i);
                n.Arr.Add(v);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return n; }
                throw new FormatException("expected , or ] at " + i);
            }
        }

        static string ParseString(string s, ref int i)
        {
            i++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("unterminated escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape \\" + e);
                }
            }
        }

        static JNode ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') i++;
                else break;
            }
            if (i == start) throw new FormatException("expected value at " + start);
            return JNode.NewNumber(s.Substring(start, i - start));
        }

        public static string Serialize(JNode n)
        {
            StringBuilder sb = new StringBuilder(4096);
            Write(sb, n);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, JNode n)
        {
            switch (n.Kind)
            {
                case JKind.Null: sb.Append("null"); break;
                case JKind.Bool: sb.Append(n.BoolValue ? "true" : "false"); break;
                case JKind.Number: sb.Append(n.Raw); break;
                case JKind.String: WriteString(sb, n.Raw); break;
                case JKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < n.Arr.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Write(sb, n.Arr[i]);
                    }
                    sb.Append(']');
                    break;
                case JKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < n.Obj.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(sb, n.Obj[i].Key);
                        sb.Append(':');
                        Write(sb, n.Obj[i].Value);
                    }
                    sb.Append('}');
                    break;
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x2028 || c == 0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
