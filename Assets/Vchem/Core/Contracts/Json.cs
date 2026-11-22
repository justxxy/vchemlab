using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Vchem.Core.Contracts
{
    /// <summary>
    /// 极简 JSON 解析器 / 序列化器。
    /// 为什么不依赖 Newtonsoft 或 System.Text.Json：
    ///   - Core 必须能在纯 dotnet 控制台里编译，且不引用 UnityEngine；
    ///   - 工程约定"内置轻量解析，不引入外部包"。
    /// 只支持本项目契约需要的类型：对象、数组、字符串、数字、布尔、null。
    /// </summary>
    public static class Json
    {
        // ------------------------------ 解析 ------------------------------

        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            Parser p = new Parser(text);
            object result = p.ParseValue();
            p.SkipWhitespace();
            if (!p.AtEnd) throw new FormatException("JSON 末尾有多余内容，位置 " + p.Position);
            return result;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            return AsObject(Parse(text));
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; _i = 0; }

            public bool AtEnd { get { return _i >= _s.Length; } }
            public int Position { get { return _i; } }

            public void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (_i >= _s.Length) throw new FormatException("JSON 意外结束");
                char c = _s[_i];
                if (c == '{') return ParseObjectValue();
                if (c == '[') return ParseArrayValue();
                if (c == '"') return ParseStringValue();
                if (c == 't') return ReadLiteral("true", true);
                if (c == 'f') return ReadLiteral("false", false);
                if (c == 'n') return ReadLiteral("null", null);
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumberValue();
                throw new FormatException("无法识别的 JSON 值，位置 " + _i);
            }

            private object ReadLiteral(string literal, object value)
            {
                if (_i + literal.Length > _s.Length ||
                    string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                    throw new FormatException("非法的 JSON 字面量，位置 " + _i);
                _i += literal.Length;
                return value;
            }

            private Dictionary<string, object> ParseObjectValue()
            {
                _i++; // {
                Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == '}') { _i++; return map; }
                while (true)
                {
                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != '"') throw new FormatException("对象键必须是字符串，位置 " + _i);
                    string key = ParseStringValue();
                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != ':') throw new FormatException("对象键后缺少冒号，位置 " + _i);
                    _i++;
                    object value = ParseValue();
                    map[key] = value;
                    SkipWhitespace();
                    if (_i >= _s.Length) throw new FormatException("对象未闭合");
                    if (_s[_i] == '}') { _i++; break; }
                    if (_s[_i] != ',') throw new FormatException("对象成员后缺少逗号，位置 " + _i);
                    _i++;
                }
                return map;
            }

            private List<object> ParseArrayValue()
            {
                _i++; // [
                List<object> list = new List<object>();
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == ']') { _i++; return list; }
                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    if (_i >= _s.Length) throw new FormatException("数组未闭合");
                    if (_s[_i] == ']') { _i++; break; }
                    if (_s[_i] != ',') throw new FormatException("数组元素后缺少逗号，位置 " + _i);
                    _i++;
                }
                return list;
            }

            private string ParseStringValue()
            {
                _i++; // "
                StringBuilder sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (_i >= _s.Length) throw new FormatException("字符串转义未完成");
                    char e = _s[_i++];
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
                            if (_i + 4 > _s.Length) throw new FormatException("unicode 转义不完整");
                            string hex = _s.Substring(_i, 4);
                            int code;
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw new FormatException("非法的 unicode 转义: " + hex);
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default: throw new FormatException("非法的转义字符: \\" + e);
                    }
                }
                throw new FormatException("字符串未闭合");
            }

            private double ParseNumberValue()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                string token = _s.Substring(start, _i - start);
                double value;
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw new FormatException("非法的数字: " + token);
                return value;
            }
        }

        // ------------------------------ 取值辅助 ------------------------------

        public static Dictionary<string, object> AsObject(object v)
        {
            Dictionary<string, object> m = v as Dictionary<string, object>;
            if (m == null) throw new FormatException("期望 JSON 对象");
            return m;
        }

        public static List<object> AsArray(object v)
        {
            List<object> a = v as List<object>;
            if (a == null) throw new FormatException("期望 JSON 数组");
            return a;
        }

        public static string GetString(Dictionary<string, object> map, string key, string fallback)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return fallback;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double GetNumber(Dictionary<string, object> map, string key, double fallback)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double) return (double)v;
            double d;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
        }

        public static bool GetBool(Dictionary<string, object> map, string key, bool fallback)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            bool b;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b) ? b : fallback;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> map, string key)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<object> GetArray(Dictionary<string, object> map, string key)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v)) return null;
            return v as List<object>;
        }

        // ------------------------------ 序列化 ------------------------------

        public static string Stringify(object value)
        {
            StringBuilder sb = new StringBuilder();
            Write(value, sb);
            return sb.ToString();
        }

        private static void Write(object value, StringBuilder sb)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string) { WriteString((string)value, sb); return; }
            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }
            if (value is double)
            {
                sb.Append(((double)value).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is float)
            {
                sb.Append(((float)value).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is int || value is long || value is short || value is byte)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            IDictionary<string, object> map = value as IDictionary<string, object>;
            if (map != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(kv.Key, sb);
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                return;
            }
            IEnumerable list = value as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(item, sb);
                }
                sb.Append(']');
                return;
            }
            // 兜底：当作字符串
            WriteString(Convert.ToString(value, CultureInfo.InvariantCulture), sb);
        }

        private static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
