using System;
using System.Globalization;

namespace Tailscale.Core
{
    /// <summary>
    /// Il tipo e il valore di una voce di registro.
    ///
    /// Solo due tipi, quelli che servono davvero: sz e dword. Il valore resta
    /// testo anche per il dword (in decimale), cosi' il manifest e' una riga sola
    /// e sono le implementazioni a decidere come scriverlo. La forma
    /// `tipo:valore` e' quella che si legge nel manifest e in riga di comando.
    /// </summary>
    public sealed class RegValue
    {
        public const string Sz = "sz";
        public const string Dword = "dword";

        /// <summary>Ci si scrive dentro quando un valore non esiste: il backup dice l'assenza.</summary>
        public const string Absent = "<assente>";

        private readonly string _kind;
        private readonly string _text;

        public RegValue(string kind, string text)
        {
            if (kind == null) throw new ArgumentNullException("kind");
            if (text == null) throw new ArgumentNullException("text");
            string normalized = kind.Trim().ToLowerInvariant();
            if (normalized != Sz && normalized != Dword)
                throw new ArgumentException("tipo di valore sconosciuto: " + kind);
            _kind = normalized;
            _text = normalized == Dword ? NormalizeDword(text) : text;
        }

        /// <summary>
        /// Il dword diventa sempre decimale. `reg query` lo restituisce in
        /// esadecimale (`0x1`), il manifest lo scrive in decimale (`1`): senza una
        /// forma sola, la rilettura che conferma una scrittura direbbe "diverso"
        /// per lo stesso identico valore, e il collegamento risulterebbe non
        /// confermato su un canale mentre e' confermato sull'altro.
        /// </summary>
        private static string NormalizeDword(string text)
        {
            string trimmed = text.Trim();
            uint number;
            bool ok;
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                ok = uint.TryParse(trimmed.Substring(2), NumberStyles.HexNumber,
                                   CultureInfo.InvariantCulture, out number);
            else
                ok = uint.TryParse(trimmed, NumberStyles.Integer,
                                   CultureInfo.InvariantCulture, out number);
            if (!ok) throw new ArgumentException("valore dword non numerico: " + text);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        public string Kind { get { return _kind; } }

        public string Text { get { return _text; } }

        /// <summary>La forma del manifest: "sz:testo" oppure "dword:1".</summary>
        public static RegValue Parse(string field)
        {
            if (field == null) throw new ArgumentNullException("field");
            int colon = field.IndexOf(':');
            if (colon <= 0)
                throw new FormatException("valore atteso nella forma tipo:testo, trovato: " + field);
            // "sz:" e' il testo vuoto: ToString() lo produce e Parse deve
            // restituirlo identico, o un ripristino con un valore vuoto salta
            // con un'eccezione a meta' del lavoro.
            if (colon == field.Length - 1
                && !field.Substring(0, colon).Trim().Equals(Sz, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("valore atteso nella forma tipo:testo, trovato: " + field);
            return new RegValue(field.Substring(0, colon), field.Substring(colon + 1));
        }

        public override string ToString()
        {
            return _kind + ":" + _text;
        }

        public override bool Equals(object obj)
        {
            RegValue other = obj as RegValue;
            return other != null && other._kind == _kind && other._text == _text;
        }

        public override int GetHashCode()
        {
            return _kind.GetHashCode() ^ _text.GetHashCode();
        }
    }

    /// <summary>
    /// Una voce di registro: dove sta e cosa vale. Il nome completo serve ai log,
    /// e nei log va **il nome, mai il valore** (piano 3: nessun segreto nel
    /// registro dell'app).
    /// </summary>
    public sealed class RegistryEntry
    {
        public RegistryEntry(string hive, string path, string name, RegValue value)
        {
            if (hive == null) throw new ArgumentNullException("hive");
            if (path == null) throw new ArgumentNullException("path");
            if (name == null) throw new ArgumentNullException("name");
            if (value == null) throw new ArgumentNullException("value");
            Hive = hive;
            Path = path;
            Name = name;
            Value = value;
        }

        public string Hive { get; private set; }
        public string Path { get; private set; }
        public string Name { get; private set; }
        public RegValue Value { get; private set; }

        /// <summary>HKLM\SOFTWARE\TailscaleWP\VpnLink: il nome, non il valore.</summary>
        public string FullName
        {
            get { return Hive + "\\" + Path + "\\" + Name; }
        }
    }

    /// <summary>
    /// Le quattro operazioni di registro che il core sa chiedere. Dietro ci sono
    /// implementazioni diverse (P/Invoke sul telefono, reg.exe via telnet per CMD
    /// Injector, memoria nei test) e il core non sa quale stia usando: e' il
    /// motivo per cui l'accesso e' un'interfaccia e non una chiamata diretta.
    /// </summary>
    public interface IRegistryApi
    {
        /// <summary>Il valore, o false se il valore non esiste. Un errore di accesso o una risposta illeggibile sono un'eccezione.</summary>
        bool TryRead(string hive, string path, string name, out RegValue value);

        void Write(string hive, string path, string name, RegValue value);

        void Delete(string hive, string path, string name);
    }
}