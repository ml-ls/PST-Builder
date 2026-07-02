using System;
using System.Collections.Generic;
using System.Text;

namespace PstBuilder.Pim
{
    /// <summary>
    /// One unfolded content line of a vCard/iCalendar stream: NAME(;PARAM=VALUE…):VALUE.
    /// Shared by the vCard and iCalendar parsers (both use RFC 5545/6350 line syntax).
    /// </summary>
    internal sealed class ContentLine
    {
        public string Name { get; }
        public string Value { get; }
        private readonly Dictionary<string, string> _params;

        private ContentLine(string name, string value, Dictionary<string, string> parameters)
        {
            Name = name;
            Value = value;
            _params = parameters;
        }

        /// <summary>Gets a parameter value (case-insensitive), or null.</summary>
        public string? Param(string key) => _params.TryGetValue(key, out var v) ? v : null;

        /// <summary>True when a parameter contains the given token (e.g. TYPE contains "CELL").</summary>
        public bool TypeContains(string token)
        {
            var t = Param("TYPE");
            if (t == null) return false;
            foreach (var part in t.Split(','))
                if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Parses text into unfolded content lines.</summary>
        public static IEnumerable<ContentLine> Parse(string text)
        {
            foreach (var raw in Unfold(text))
            {
                int colon = IndexOfValueColon(raw);
                if (colon < 0) continue;
                string head = raw.Substring(0, colon);
                string value = Unescape(raw.Substring(colon + 1));

                var pieces = head.Split(';');
                string name = pieces[0].Trim().ToUpperInvariant();
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < pieces.Length; i++)
                {
                    int eq = pieces[i].IndexOf('=');
                    if (eq > 0) parameters[pieces[i].Substring(0, eq).Trim()] = pieces[i].Substring(eq + 1).Trim();
                }
                yield return new ContentLine(name, value, parameters);
            }
        }

        // Lines folded per RFC: a CRLF followed by a space or tab continues the previous line.
        private static IEnumerable<string> Unfold(string text)
        {
            var sb = new StringBuilder();
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    sb.Append(line.Substring(1));
                }
                else
                {
                    if (sb.Length > 0) yield return sb.ToString();
                    sb.Clear();
                    sb.Append(line);
                }
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        // The ':' that starts the value is the first ':' not inside a quoted parameter value.
        private static int IndexOfValueColon(string line)
        {
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') inQuotes = !inQuotes;
                else if (c == ':' && !inQuotes) return i;
            }
            return -1;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 'n' || n == 'N' ? '\n' : n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
