using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace YushuAfterSales.Reporting
{
    /// <summary>Small dependency-free JSON writer for the .NET Framework 4.8 report format.</summary>
    public static class ReportJson
    {
        /// <summary>Reads the report JSON format emitted by this class, including nested models and collections.</summary>
        public static ReportDocument DeserializeReport(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) throw new FormatException("Report JSON is empty.");
            if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new FormatException("Report JSON exceeds the 16 MiB limit.");
            object parsed = new JsonParser(json).Parse();
            var root = parsed as IDictionary<string, object>;
            if (root == null) throw new FormatException("Report JSON root must be an object.");
            var report = (ReportDocument)ConvertValue(root, typeof(ReportDocument), 0);
            if (report == null || String.IsNullOrWhiteSpace(report.SchemaVersion) || String.IsNullOrWhiteSpace(report.ReportId))
                throw new FormatException("Report JSON is missing schemaVersion or reportId.");
            if (report.Inventory == null) report.Inventory = new List<InventoryEntry>();
            if (report.Findings == null) report.Findings = new List<FindingRecord>();
            if (report.Actions == null) report.Actions = new List<ActionRecord>();
            return report;
        }

        private static object ConvertValue(object value, Type targetType, int depth)
        {
            if (depth > 64) throw new FormatException("Report JSON nesting is too deep.");
            if (value == null) return null;
            Type nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null) targetType = nullableType;
            if (targetType.IsAssignableFrom(value.GetType())) return value;
            if (targetType == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            if (targetType.IsPrimitive || targetType == typeof(decimal)) return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var source = value as IList<object>;
                if (source == null) throw new FormatException("Expected a JSON array for " + targetType.Name + ".");
                Type itemType = targetType.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(targetType);
                foreach (object item in source) list.Add(ConvertValue(item, itemType, depth + 1));
                return list;
            }

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var source = value as IDictionary<string, object>;
                if (source == null) throw new FormatException("Expected a JSON object for " + targetType.Name + ".");
                Type[] arguments = targetType.GetGenericArguments();
                var dictionary = (IDictionary)Activator.CreateInstance(targetType);
                foreach (KeyValuePair<string, object> item in source)
                {
                    object key = ConvertValue(item.Key, arguments[0], depth + 1);
                    object entry = ConvertValue(item.Value, arguments[1], depth + 1);
                    dictionary.Add(key, entry);
                }
                return dictionary;
            }

            var properties = value as IDictionary<string, object>;
            if (properties == null) throw new FormatException("Unexpected JSON value for " + targetType.Name + ".");
            object instance = Activator.CreateInstance(targetType);
            foreach (PropertyInfo property in targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite || property.GetIndexParameters().Length != 0) continue;
                object raw;
                if (!TryGetProperty(properties, property.Name, out raw)) continue;
                property.SetValue(instance, ConvertValue(raw, property.PropertyType, depth + 1), null);
            }
            return instance;
        }

        private static bool TryGetProperty(IDictionary<string, object> values, string name, out object value)
        {
            foreach (KeyValuePair<string, object> item in values)
            {
                if (String.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = item.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        private sealed class JsonParser
        {
            private readonly string _text;
            private int _position;

            public JsonParser(string text) { _text = text; }

            public object Parse()
            {
                SkipWhitespace();
                object value = ParseValue(0);
                SkipWhitespace();
                if (_position != _text.Length) Fail("Unexpected trailing data");
                return value;
            }

            private object ParseValue(int depth)
            {
                if (depth > 64) Fail("JSON nesting is too deep");
                SkipWhitespace();
                if (_position >= _text.Length) Fail("Unexpected end of JSON");
                char c = _text[_position];
                if (c == '{') return ParseObject(depth + 1);
                if (c == '[') return ParseArray(depth + 1);
                if (c == '"') return ParseString();
                if (c == 't') { ReadLiteral("true"); return true; }
                if (c == 'f') { ReadLiteral("false"); return false; }
                if (c == 'n') { ReadLiteral("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                Fail("Invalid JSON value");
                return null;
            }

            private IDictionary<string, object> ParseObject(int depth)
            {
                _position++;
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                SkipWhitespace();
                if (Take('}')) return result;
                while (true)
                {
                    SkipWhitespace();
                    if (_position >= _text.Length || _text[_position] != '"') Fail("Expected object property name");
                    string key = ParseString();
                    SkipWhitespace();
                    if (!Take(':')) Fail("Expected ':' after property name");
                    object value = ParseValue(depth);
                    if (result.ContainsKey(key)) Fail("Duplicate object property");
                    result.Add(key, value);
                    SkipWhitespace();
                    if (Take('}')) return result;
                    if (!Take(',')) Fail("Expected ',' or '}' in object");
                }
            }

            private IList<object> ParseArray(int depth)
            {
                _position++;
                var result = new List<object>();
                SkipWhitespace();
                if (Take(']')) return result;
                while (true)
                {
                    result.Add(ParseValue(depth));
                    SkipWhitespace();
                    if (Take(']')) return result;
                    if (!Take(',')) Fail("Expected ',' or ']' in array");
                }
            }

            private string ParseString()
            {
                if (!Take('"')) Fail("Expected string");
                var result = new StringBuilder();
                while (_position < _text.Length)
                {
                    char c = _text[_position++];
                    if (c == '"') return result.ToString();
                    if (c < 0x20) Fail("Control character in string");
                    if (c != '\\') { result.Append(c); continue; }
                    if (_position >= _text.Length) Fail("Incomplete string escape");
                    char escape = _text[_position++];
                    switch (escape)
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            if (_position + 4 > _text.Length) Fail("Incomplete unicode escape");
                            int code;
                            if (!Int32.TryParse(_text.Substring(_position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) Fail("Invalid unicode escape");
                            result.Append((char)code);
                            _position += 4;
                            break;
                        default: Fail("Invalid string escape"); break;
                    }
                }
                Fail("Unterminated string");
                return null;
            }

            private object ParseNumber()
            {
                int start = _position;
                if (Take('-') && _position >= _text.Length) Fail("Incomplete number");
                if (Take('0'))
                {
                    if (_position < _text.Length && Char.IsDigit(_text[_position])) Fail("Leading zero in number");
                }
                else ReadDigits();
                bool fractional = false;
                if (Take('.')) { fractional = true; ReadDigits(); }
                if (Take('e') || Take('E'))
                {
                    fractional = true;
                    if (!Take('+')) Take('-');
                    ReadDigits();
                }
                string token = _text.Substring(start, _position - start);
                if (!fractional)
                {
                    long integer;
                    if (Int64.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
                }
                double number;
                if (!Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || Double.IsInfinity(number) || Double.IsNaN(number)) Fail("Invalid number");
                return number;
            }

            private void ReadDigits()
            {
                int start = _position;
                while (_position < _text.Length && _text[_position] >= '0' && _text[_position] <= '9') _position++;
                if (start == _position) Fail("Expected digit");
            }

            private void ReadLiteral(string literal)
            {
                if (_position + literal.Length > _text.Length || String.CompareOrdinal(_text, _position, literal, 0, literal.Length) != 0) Fail("Invalid literal");
                _position += literal.Length;
            }

            private bool Take(char c)
            {
                if (_position < _text.Length && _text[_position] == c) { _position++; return true; }
                return false;
            }

            private void SkipWhitespace()
            {
                while (_position < _text.Length && (_text[_position] == ' ' || _text[_position] == '\t' || _text[_position] == '\r' || _text[_position] == '\n')) _position++;
            }

            private void Fail(string message) { throw new FormatException(message + " at character " + _position.ToString(CultureInfo.InvariantCulture) + "."); }
        }

        public static string Serialize(object value)
        {
            return Serialize(value, true);
        }

        public static string SerializeCompact(object value)
        {
            return Serialize(value, false);
        }

        public static string Serialize(object value, bool pretty)
        {
            var writer = new JsonWriter(pretty);
            writer.WriteValue(value, 0);
            return writer.ToString();
        }

        private sealed class JsonWriter
        {
            private readonly StringBuilder _buffer = new StringBuilder();
            private readonly bool _pretty;

            public JsonWriter(bool pretty) { _pretty = pretty; }
            public override string ToString() { return _buffer.ToString(); }

            public void WriteValue(object value, int depth)
            {
                if (value == null) { _buffer.Append("null"); return; }
                if (value is string || value is char || value is Guid)
                { WriteString(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
                if (value is bool) { _buffer.Append((bool)value ? "true" : "false"); return; }
                if (value is DateTime) { WriteString(((DateTime)value).ToString("o", CultureInfo.InvariantCulture)); return; }
                if (value is DateTimeOffset) { WriteString(((DateTimeOffset)value).ToString("o", CultureInfo.InvariantCulture)); return; }
                if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal)
                { _buffer.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }

                var dictionary = value as IDictionary;
                if (dictionary != null)
                {
                    WriteDictionary(dictionary, depth);
                    return;
                }
                var enumerable = value as IEnumerable;
                if (enumerable != null)
                {
                    WriteArray(enumerable, depth);
                    return;
                }
                WriteObject(value, depth);
            }

            private void WriteObject(object value, int depth)
            {
                var properties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                _buffer.Append('{');
                for (int i = 0; i < properties.Count; i++)
                {
                    if (i > 0) _buffer.Append(',');
                    WriteNewLine(depth + 1);
                    WriteString(ToCamelCase(properties[i].Name));
                    _buffer.Append(_pretty ? ": " : ":");
                    WriteValue(properties[i].GetValue(value, null), depth + 1);
                }
                if (properties.Count > 0) WriteNewLine(depth);
                _buffer.Append('}');
            }

            private void WriteDictionary(IDictionary dictionary, int depth)
            {
                var items = new List<KeyValuePair<string, object>>();
                foreach (DictionaryEntry entry in dictionary)
                    items.Add(new KeyValuePair<string, object>(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), entry.Value));
                items.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
                _buffer.Append('{');
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) _buffer.Append(',');
                    WriteNewLine(depth + 1);
                    WriteString(ToCamelCase(items[i].Key));
                    _buffer.Append(_pretty ? ": " : ":");
                    WriteValue(items[i].Value, depth + 1);
                }
                if (items.Count > 0) WriteNewLine(depth);
                _buffer.Append('}');
            }

            private void WriteArray(IEnumerable values, int depth)
            {
                var list = new List<object>();
                foreach (object value in values) list.Add(value);
                _buffer.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) _buffer.Append(',');
                    WriteNewLine(depth + 1);
                    WriteValue(list[i], depth + 1);
                }
                if (list.Count > 0) WriteNewLine(depth);
                _buffer.Append(']');
            }

            private void WriteNewLine(int depth)
            {
                if (!_pretty) return;
                _buffer.Append('\n');
                _buffer.Append(' ', depth * 2);
            }

            private void WriteString(string value)
            {
                _buffer.Append('"');
                if (value != null)
                {
                    foreach (char c in value)
                    {
                        switch (c)
                        {
                            case '\\': _buffer.Append("\\\\"); break;
                            case '"': _buffer.Append("\\\""); break;
                            case '\r': _buffer.Append("\\r"); break;
                            case '\n': _buffer.Append("\\n"); break;
                            case '\t': _buffer.Append("\\t"); break;
                            case '\b': _buffer.Append("\\b"); break;
                            case '\f': _buffer.Append("\\f"); break;
                            default:
                                if (c < 32) _buffer.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                                else _buffer.Append(c);
                                break;
                        }
                    }
                }
                _buffer.Append('"');
            }

            private static string ToCamelCase(string name)
            {
                if (String.IsNullOrEmpty(name) || Char.IsLower(name[0])) return name;
                if (name.Length == 1) return name.ToLowerInvariant();
                return Char.ToLowerInvariant(name[0]) + name.Substring(1);
            }
        }
    }
}
