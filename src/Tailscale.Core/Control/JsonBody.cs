using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tailscale.Core.Control
{
    /// <summary>
    /// Un parser JSON minimale ma completo (oggetti, array, stringhe, numeri,
    /// booleani, null), con i numeri conservati anche come testo.
    ///
    /// Perche' scritto a mano: il profilo WP8.1 non offre un serializer e non
    /// vogliamo una dipendenza esterna; e soprattutto il control plane aggiunge
    /// campi spesso, quindi serve un lettore che ignori con calma cio' che non
    /// conosciamo invece di lanciare. I numeri restano testo perché gli ID di
    /// Tailscale sono int64 e passarli per un double li arrotonderebbe.
    ///
    /// Un errore di sintassi riporta la posizione: un JSON troncato da un proxy e'
    /// un caso che si vede davvero.
    /// </summary>
    public sealed class JsonValue
    {
        public enum ValueKind { Object, Array, String, Number, Boolean, Null }

        private readonly ValueKind _kind;
        private Dictionary<string, JsonValue> _members;
        private List<JsonValue> _items;
        private string _text;
        private bool _boolean;

        public JsonValue(ValueKind kind)
        {
            _kind = kind;
        }

        public ValueKind Kind { get { return _kind; } }

        internal Dictionary<string, JsonValue> Members
        {
            set { _members = value; }
        }

        internal List<JsonValue> Items
        {
            set { _items = value; }
        }

        internal string Text
        {
            get { return _text; }
            set { _text = value; }
        }

        internal bool Boolean
        {
            get { return _boolean; }
            set { _boolean = value; }
        }

        public bool IsObject { get { return _kind == ValueKind.Object; } }
        public bool IsArray { get { return _kind == ValueKind.Array; } }
        public bool IsString { get { return _kind == ValueKind.String; } }
        public bool IsNumber { get { return _kind == ValueKind.Number; } }
        public bool IsBoolean { get { return _kind == ValueKind.Boolean; } }
        public bool IsNull { get { return _kind == ValueKind.Null; } }

        /// <summary>Campo di un oggetto, o null se assente. Non lancia mai.</summary>
        public JsonValue this[string name]
        {
            get
            {
                if (_members == null) return null;
                JsonValue value;
                return _members.TryGetValue(name, out value) ? value : null;
            }
        }

        public int Count
        {
            get
            {
                if (_members != null) return _members.Count;
                if (_items != null) return _items.Count;
                return 0;
            }
        }

        public IEnumerable<JsonValue> ArrayItems
        {
            get { return _items ?? new List<JsonValue>(); }
        }

        public IEnumerable<KeyValuePair<string, JsonValue>> ObjectMembers
        {
            get
            {
                return _members ?? new Dictionary<string, JsonValue>();
            }
        }

        public string AsString(string fallback)
        {
            return _kind == ValueKind.String ? _text : fallback;
        }

        public bool AsBool(bool fallback)
        {
            return _kind == ValueKind.Boolean ? _boolean : fallback;
        }

        public int AsInt(int fallback)
        {
            if (_kind != ValueKind.Number) return fallback;
            int value;
            return int.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        public long AsLong(long fallback)
        {
            if (_kind != ValueKind.Number) return fallback;
            long value;
            return long.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        public override string ToString()
        {
            switch (_kind)
            {
                case ValueKind.Null: return "null";
                case ValueKind.Boolean: return _boolean ? "true" : "false";
                case ValueKind.String: return _text;
                case ValueKind.Number: return _text;
                case ValueKind.Object: return "{" + Count + " campi}";
                default: return "[" + Count + " elementi]";
            }
        }
    }

    /// <summary>Parser JSON. Non lancia mai su input ben formato; su input rotto lancia <see cref="JsonParseException"/>.</summary>
    public static class JsonBody
    {
        public static JsonValue Parse(string text)
        {
            int consumed;
            JsonValue value = ParsePrefix(text, out consumed);
            if (consumed != text.Length)
                throw new JsonParseException("testo in eccesso dopo il valore, alla posizione " + consumed);
            return value;
        }

        /// <summary>
        /// Interpreta il primo valore e dice quanti caratteri ha consumato.
        ///
        /// Serve allo stream della netmap: il corpo della risposta map sono piu'
        /// valori JSON uno dopo l'altro, e chi legge deve sapere dove finisce il
        /// primo per non perdere l'inizio del successivo.
        /// </summary>
        public static JsonValue ParsePrefix(string text, out int consumed)
        {
            if (text == null) throw new ArgumentNullException("text");
            int position = 0;
            SkipWhitespace(text, ref position);
            JsonValue value = ParseValue(text, ref position, 0);
            SkipWhitespace(text, ref position);
            consumed = position;
            return value;
        }

        private const int MaxDepth = 64;

        private static JsonValue ParseValue(string text, ref int position, int depth)
        {
            if (depth > MaxDepth)
                throw new JsonParseException("annidamento oltre " + MaxDepth + " livelli");
            if (position >= text.Length)
                throw new JsonParseException("input troncato: manca un valore");

            char c = text[position];
            switch (c)
            {
                case '{': return ParseObject(text, ref position, depth);
                case '[': return ParseArray(text, ref position, depth);
                case '"':
                    {
                        JsonValue value = new JsonValue(JsonValue.ValueKind.String);
                        value.Text = ParseString(text, ref position);
                        return value;
                    }
                case 't': Expect(text, ref position, "true"); return MakeBoolean(true);
                case 'f': Expect(text, ref position, "false"); return MakeBoolean(false);
                case 'n': Expect(text, ref position, "null"); return new JsonValue(JsonValue.ValueKind.Null);
                default: return ParseNumber(text, ref position);
            }
        }

        private static JsonValue ParseObject(string text, ref int position, int depth)
        {
            position++; // {
            Dictionary<string, JsonValue> members = new Dictionary<string, JsonValue>();
            SkipWhitespace(text, ref position);
            if (position < text.Length && text[position] == '}') { position++; return MakeObject(members); }

            while (true)
            {
                SkipWhitespace(text, ref position);
                if (position >= text.Length || text[position] != '"')
                    throw new JsonParseException("atteso il nome di un campo alla posizione " + position);
                string name = ParseString(text, ref position);

                SkipWhitespace(text, ref position);
                if (position >= text.Length || text[position] != ':')
                    throw new JsonParseException("atteso ':' dopo il campo \"" + name + "\"");
                position++;

                SkipWhitespace(text, ref position);
                JsonValue value = ParseValue(text, ref position, depth + 1);
                // Un campo ripetuto non e' un errore: l'ultimo vince.
                members[name] = value;

                SkipWhitespace(text, ref position);
                if (position >= text.Length)
                    throw new JsonParseException("oggetto non chiuso");
                if (text[position] == ',') { position++; continue; }
                if (text[position] == '}') { position++; break; }
                throw new JsonParseException("attesi ',' o '}' alla posizione " + position);
            }
            return MakeObject(members);
        }

        private static JsonValue ParseArray(string text, ref int position, int depth)
        {
            position++; // [
            List<JsonValue> items = new List<JsonValue>();
            SkipWhitespace(text, ref position);
            if (position < text.Length && text[position] == ']') { position++; return MakeArray(items); }

            while (true)
            {
                SkipWhitespace(text, ref position);
                items.Add(ParseValue(text, ref position, depth + 1));

                SkipWhitespace(text, ref position);
                if (position >= text.Length)
                    throw new JsonParseException("array non chiuso");
                if (text[position] == ',') { position++; continue; }
                if (text[position] == ']') { position++; break; }
                throw new JsonParseException("attesi ',' o ']' alla posizione " + position);
            }
            return MakeArray(items);
        }

        private static string ParseString(string text, ref int position)
        {
            position++; // virgoletta aperta
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            while (true)
            {
                if (position >= text.Length)
                    throw new JsonParseException("stringa non chiusa");
                char c = text[position++];

                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }

                if (position >= text.Length)
                    throw new JsonParseException("sequenza di escape troncata");
                char escape = text[position++];
                switch (escape)
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
                        if (position + 4 > text.Length)
                            throw new JsonParseException("escape unicode troncato");
                        string hex = text.Substring(position, 4);
                        position += 4;
                        int code;
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            throw new JsonParseException("escape unicode non valido: \\u" + hex);
                        sb.Append((char)code);
                        break;
                    default:
                        throw new JsonParseException("escape sconosciuto: \\" + escape);
                }
            }
            return sb.ToString();
        }

        private static JsonValue ParseNumber(string text, ref int position)
        {
            int start = position;
            if (position < text.Length && (text[position] == '-' || text[position] == '+')) position++;
            while (position < text.Length && IsNumberChar(text[position])) position++;

            if (position == start)
                throw new JsonParseException("carattere inatteso '" + text[position] + "' alla posizione " + position);

            string raw = text.Substring(start, position - start);
            double parsed;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                throw new JsonParseException("numero non valido: " + raw);

            JsonValue value = new JsonValue(JsonValue.ValueKind.Number);
            value.Text = raw;
            return value;
        }

        private static bool IsNumberChar(char c)
        {
            return (c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+';
        }

        private static void Expect(string text, ref int position, string literal)
        {
            if (position + literal.Length > text.Length ||
                string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
                throw new JsonParseException("atteso \"" + literal + "\" alla posizione " + position);
            position += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int position)
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') position++;
                else break;
            }
        }

        private static JsonValue MakeBoolean(bool value)
        {
            JsonValue result = new JsonValue(JsonValue.ValueKind.Boolean);
            result.Boolean = value;
            return result;
        }

        private static JsonValue MakeObject(Dictionary<string, JsonValue> members)
        {
            JsonValue result = new JsonValue(JsonValue.ValueKind.Object);
            result.Members = members;
            return result;
        }

        private static JsonValue MakeArray(List<JsonValue> items)
        {
            JsonValue result = new JsonValue(JsonValue.ValueKind.Array);
            result.Items = items;
            return result;
        }
    }

    /// <summary>JSON malformato, con la posizione del problema.</summary>
    public class JsonParseException : Exception
    {
        public JsonParseException(string message) : base(message) { }
    }
}
