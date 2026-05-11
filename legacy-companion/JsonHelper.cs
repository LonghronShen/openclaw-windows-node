using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenClaw
{
    /// <summary>
    /// Minimal JSON serializer/deserializer for .NET Framework 2.0.
    /// No external dependencies. Handles the subset of JSON used by JSON-RPC 2.0.
    ///
    /// Supported types:
    ///   - string, int, long, double, float, bool, null
    ///   - Dictionary&lt;string, object&gt; (JSON objects)
    ///   - List&lt;object&gt; (JSON arrays)
    ///
    /// Escaped strings and nested objects are fully handled.
    /// </summary>
    public static class JsonHelper
    {
        // ───────────────────── Public API ─────────────────────

        /// <summary>
        /// Serialize a .NET object to a JSON string.
        /// </summary>
        public static string Serialize(object obj)
        {
            StringBuilder sb = new StringBuilder(4096);
            SerializeValue(sb, obj);
            return sb.ToString();
        }

        /// <summary>
        /// Parse a JSON string into .NET objects.
        /// Returns Dictionary&lt;string, object&gt;, List&lt;object&gt;, or a primitive.
        /// </summary>
        public static object Parse(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException("json");
            }

            int pos = 0;
            object result = ParseValue(json, ref pos);
            return result;
        }

        // ───────────────────── Serialization ─────────────────────

        private static void SerializeValue(StringBuilder sb, object obj)
        {
            if (obj == null)
            {
                sb.Append("null");
                return;
            }

            // Check by type
            if (obj is string)
            {
                SerializeString(sb, (string)obj);
                return;
            }

            if (obj is bool)
            {
                sb.Append((bool)obj ? "true" : "false");
                return;
            }

            if (obj is int)
            {
                sb.Append(((int)obj).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (obj is long)
            {
                sb.Append(((long)obj).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (obj is double)
            {
                double d = (double)obj;
                if (double.IsInfinity(d) || double.IsNaN(d))
                {
                    sb.Append("null");
                }
                else
                {
                    sb.Append(d.ToString("0.0########", CultureInfo.InvariantCulture));
                }
                return;
            }

            if (obj is float)
            {
                float f = (float)obj;
                if (float.IsInfinity(f) || float.IsNaN(f))
                {
                    sb.Append("null");
                }
                else
                {
                    sb.Append(f.ToString("0.0########", CultureInfo.InvariantCulture));
                }
                return;
            }

            if (obj is Dictionary<string, object>)
            {
                SerializeObject(sb, (Dictionary<string, object>)obj);
                return;
            }

            if (obj is List<object>)
            {
                SerializeArray(sb, (List<object>)obj);
                return;
            }

            // Fallback: convert using ToString
            SerializeString(sb, obj.ToString());
        }

        private static void SerializeObject(StringBuilder sb, Dictionary<string, object> dict)
        {
            sb.Append('{');

            bool first = true;
            foreach (KeyValuePair<string, object> entry in dict)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                SerializeString(sb, entry.Key);
                sb.Append(':');
                SerializeValue(sb, entry.Value);

                first = false;
            }

            sb.Append('}');
        }

        private static void SerializeArray(StringBuilder sb, List<object> list)
        {
            sb.Append('[');

            bool first = true;
            foreach (object item in list)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                SerializeValue(sb, item);
                first = false;
            }

            sb.Append(']');
        }

        private static void SerializeString(StringBuilder sb, string str)
        {
            if (str == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');

            for (int i = 0; i < str.Length; i++)
            {
                char c = str[i];

                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u" + ((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            sb.Append('"');
        }

        // ───────────────────── Parsing ─────────────────────

        /// <summary>
        /// Skip whitespace characters (space, tab, newline, carriage return).
        /// </summary>
        private static void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    pos++;
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Parse the next JSON value starting at position pos.
        /// Advances pos past the parsed value.
        /// </summary>
        private static object ParseValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);

            if (pos >= json.Length)
            {
                throw new ArgumentException("Unexpected end of JSON input");
            }

            char c = json[pos];

            if (c == '"')
            {
                return ParseString(json, ref pos);
            }

            if (c == '{')
            {
                return ParseObject(json, ref pos);
            }

            if (c == '[')
            {
                return ParseArray(json, ref pos);
            }

            if (c == 't')
            {
                ExpectLiteral(json, ref pos, "true");
                return true;
            }

            if (c == 'f')
            {
                ExpectLiteral(json, ref pos, "false");
                return false;
            }

            if (c == 'n')
            {
                ExpectLiteral(json, ref pos, "null");
                return null;
            }

            if (c == '-' || (c >= '0' && c <= '9'))
            {
                return ParseNumber(json, ref pos);
            }

            throw new ArgumentException("Unexpected character '" + c + "' at position " + pos);
        }

        /// <summary>
        /// Parse a JSON string (including escape sequences).
        /// </summary>
        private static string ParseString(string json, ref int pos)
        {
            if (pos >= json.Length || json[pos] != '"')
            {
                throw new ArgumentException("Expected '\"' at position " + pos);
            }

            pos++; // Skip opening quote

            StringBuilder sb = new StringBuilder(256);

            while (pos < json.Length)
            {
                char c = json[pos];

                if (c == '"')
                {
                    pos++; // Skip closing quote
                    return sb.ToString();
                }

                if (c == '\\')
                {
                    pos++; // Skip backslash
                    if (pos >= json.Length)
                    {
                        throw new ArgumentException("Unexpected end in string escape");
                    }

                    char escaped = json[pos];
                    switch (escaped)
                    {
                        case '"':
                            sb.Append('"');
                            break;
                        case '\\':
                            sb.Append('\\');
                            break;
                        case '/':
                            sb.Append('/');
                            break;
                        case 'b':
                            sb.Append('\b');
                            break;
                        case 'f':
                            sb.Append('\f');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case 'u':
                            // Parse 4-digit hex unicode
                            if (pos + 4 >= json.Length)
                            {
                                throw new ArgumentException("Unexpected end in \\u escape");
                            }

                            string hex = json.Substring(pos + 1, 4);
                            int codePoint = int.Parse(hex, NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture);
                            sb.Append((char)codePoint);
                            pos += 4; // Skip the 4 hex digits
                            break;
                        default:
                            sb.Append(escaped);
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }

                pos++;
            }

            throw new ArgumentException("Unterminated string");
        }

        /// <summary>
        /// Parse a JSON number (int or double).
        /// </summary>
        private static object ParseNumber(string json, ref int pos)
        {
            int start = pos;

            // Optional negative sign
            if (pos < json.Length && json[pos] == '-')
            {
                pos++;
            }

            // Integer part
            if (pos < json.Length && json[pos] == '0')
            {
                pos++;
            }
            else if (pos < json.Length && json[pos] >= '1' && json[pos] <= '9')
            {
                pos++;
                while (pos < json.Length && json[pos] >= '0' && json[pos] <= '9')
                {
                    pos++;
                }
            }
            else
            {
                throw new ArgumentException("Invalid number at position " + start);
            }

            bool isDouble = false;

            // Optional fractional part
            if (pos < json.Length && json[pos] == '.')
            {
                isDouble = true;
                pos++;
                if (pos >= json.Length || json[pos] < '0' || json[pos] > '9')
                {
                    throw new ArgumentException("Expected digit after '.' at position " + pos);
                }

                while (pos < json.Length && json[pos] >= '0' && json[pos] <= '9')
                {
                    pos++;
                }
            }

            // Optional exponent part
            if (pos < json.Length && (json[pos] == 'e' || json[pos] == 'E'))
            {
                isDouble = true;
                pos++;
                if (pos < json.Length && (json[pos] == '+' || json[pos] == '-'))
                {
                    pos++;
                }

                if (pos >= json.Length || json[pos] < '0' || json[pos] > '9')
                {
                    throw new ArgumentException("Expected digit in exponent at position " + pos);
                }

                while (pos < json.Length && json[pos] >= '0' && json[pos] <= '9')
                {
                    pos++;
                }
            }

            string numberStr = json.Substring(start, pos - start);

            if (isDouble)
            {
                return double.Parse(numberStr, CultureInfo.InvariantCulture);
            }
            else
            {
                return int.Parse(numberStr, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Expect and skip a specific literal string (true, false, null).
        /// </summary>
        private static void ExpectLiteral(string json, ref int pos, string literal)
        {
            for (int i = 0; i < literal.Length; i++)
            {
                if (pos >= json.Length || json[pos] != literal[i])
                {
                    throw new ArgumentException("Expected '" + literal + "' at position " + pos);
                }

                pos++;
            }
        }

        /// <summary>
        /// Parse a JSON object: {...}
        /// </summary>
        private static Dictionary<string, object> ParseObject(string json, ref int pos)
        {
            if (pos >= json.Length || json[pos] != '{')
            {
                throw new ArgumentException("Expected '{' at position " + pos);
            }

            pos++; // Skip opening brace

            Dictionary<string, object> result = new Dictionary<string, object>();

            SkipWhitespace(json, ref pos);

            // Empty object?
            if (pos < json.Length && json[pos] == '}')
            {
                pos++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(json, ref pos);

                // Expect string key
                if (pos >= json.Length || json[pos] != '"')
                {
                    throw new ArgumentException("Expected string key at position " + pos);
                }

                string key = ParseString(json, ref pos);

                SkipWhitespace(json, ref pos);

                // Expect ':'
                if (pos >= json.Length || json[pos] != ':')
                {
                    throw new ArgumentException("Expected ':' at position " + pos);
                }

                pos++; // Skip ':'

                SkipWhitespace(json, ref pos);

                // Parse value
                object value = ParseValue(json, ref pos);
                result[key] = value;

                SkipWhitespace(json, ref pos);

                if (pos >= json.Length)
                {
                    throw new ArgumentException("Unexpected end of object");
                }

                if (json[pos] == '}')
                {
                    pos++;
                    return result;
                }

                if (json[pos] != ',')
                {
                    throw new ArgumentException("Expected ',' or '}' at position " + pos);
                }

                pos++; // Skip ','
            }
        }

        /// <summary>
        /// Parse a JSON array: [...]
        /// </summary>
        private static List<object> ParseArray(string json, ref int pos)
        {
            if (pos >= json.Length || json[pos] != '[')
            {
                throw new ArgumentException("Expected '[' at position " + pos);
            }

            pos++; // Skip opening bracket

            List<object> result = new List<object>();

            SkipWhitespace(json, ref pos);

            // Empty array?
            if (pos < json.Length && json[pos] == ']')
            {
                pos++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(json, ref pos);

                object value = ParseValue(json, ref pos);
                result.Add(value);

                SkipWhitespace(json, ref pos);

                if (pos >= json.Length)
                {
                    throw new ArgumentException("Unexpected end of array");
                }

                if (json[pos] == ']')
                {
                    pos++;
                    return result;
                }

                if (json[pos] != ',')
                {
                    throw new ArgumentException("Expected ',' or ']' at position " + pos);
                }

                pos++; // Skip ','
            }
        }
    }
}
