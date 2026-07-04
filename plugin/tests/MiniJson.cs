// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Minimal JSON reader for the machine-generated golden manifests under
// testdata/ (frames/ was written by the Rust dumper retired at M11;
// frames-plugin/ by PluginGoldenDumper). Deliberately tiny and strict:
// objects, arrays, strings, integer numbers, booleans and null — exactly
// the grammar the dumpers emit. Test-only; keeps the test project free of
// new NuGet dependencies.

using System;
using System.Collections.Generic;
using System.Text;

namespace Uniflag.Tests
{
    internal static class MiniJson
    {
        /// <summary>
        /// Parse a JSON document. Objects become
        /// <c>Dictionary&lt;string, object&gt;</c>, arrays
        /// <c>List&lt;object&gt;</c>, strings <c>string</c>, numbers
        /// <c>long</c>, booleans <c>bool</c>, null <c>null</c>.
        /// </summary>
        public static object Parse(string text)
        {
            int pos = 0;
            object value = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
            {
                throw new FormatException($"trailing characters at offset {pos}");
            }
            return value;
        }

        private static object ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length)
            {
                throw new FormatException("unexpected end of input");
            }
            switch (s[pos])
            {
                case '{':
                    return ParseObject(s, ref pos);
                case '[':
                    return ParseArray(s, ref pos);
                case '"':
                    return ParseString(s, ref pos);
                case 't':
                    Expect(s, ref pos, "true");
                    return true;
                case 'f':
                    Expect(s, ref pos, "false");
                    return false;
                case 'n':
                    Expect(s, ref pos, "null");
                    return null;
                default:
                    return ParseNumber(s, ref pos);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int pos)
        {
            var result = new Dictionary<string, object>();
            pos++;
            SkipWhitespace(s, ref pos);
            if (Peek(s, pos) == '}')
            {
                pos++;
                return result;
            }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (Peek(s, pos) != '"')
                {
                    throw new FormatException($"expected object key at offset {pos}");
                }
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (Peek(s, pos) != ':')
                {
                    throw new FormatException($"expected ':' at offset {pos}");
                }
                pos++;
                result[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                char c = Peek(s, pos);
                pos++;
                if (c == '}')
                {
                    return result;
                }
                if (c != ',')
                {
                    throw new FormatException($"expected ',' or '}}' at offset {pos - 1}");
                }
            }
        }

        private static List<object> ParseArray(string s, ref int pos)
        {
            var result = new List<object>();
            pos++;
            SkipWhitespace(s, ref pos);
            if (Peek(s, pos) == ']')
            {
                pos++;
                return result;
            }
            while (true)
            {
                result.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                char c = Peek(s, pos);
                pos++;
                if (c == ']')
                {
                    return result;
                }
                if (c != ',')
                {
                    throw new FormatException($"expected ',' or ']' at offset {pos - 1}");
                }
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length)
                {
                    throw new FormatException("unterminated string");
                }
                char c = s[pos++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (pos >= s.Length)
                {
                    throw new FormatException("unterminated escape");
                }
                char e = s[pos++];
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
                        if (pos + 4 > s.Length)
                        {
                            throw new FormatException("truncated \\u escape");
                        }
                        sb.Append((char)Convert.ToUInt16(s.Substring(pos, 4), 16));
                        pos += 4;
                        break;
                    default:
                        throw new FormatException($"bad escape '\\{e}' at offset {pos - 1}");
                }
            }
        }

        private static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (Peek(s, pos) == '-')
            {
                pos++;
            }
            while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9')
            {
                pos++;
            }
            if (pos == start || (pos == start + 1 && s[start] == '-'))
            {
                throw new FormatException($"expected a number at offset {start}");
            }
            if (pos < s.Length && (s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E'))
            {
                // The golden manifests are integer-only; anything else is a
                // format drift worth failing loudly on.
                throw new FormatException($"non-integer number at offset {start}");
            }
            return long.Parse(s.Substring(start, pos - start), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || s.Substring(pos, literal.Length) != literal)
            {
                throw new FormatException($"expected '{literal}' at offset {pos}");
            }
            pos += literal.Length;
        }

        private static char Peek(string s, int pos)
        {
            if (pos >= s.Length)
            {
                throw new FormatException("unexpected end of input");
            }
            return s[pos];
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r'))
            {
                pos++;
            }
        }
    }
}
