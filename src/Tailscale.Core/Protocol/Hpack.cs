using System;
using System.Collections.Generic;
using System.Text;

namespace Tailscale.Core.Protocol
{
    /// <summary>Un header HTTP/2 decodificato (nome e valore gia' come testo).</summary>
    public sealed class HpackHeader
    {
        private readonly string _name;
        private readonly string _value;

        public HpackHeader(string name, string value)
        {
            if (name == null) throw new ArgumentNullException("name");
            _name = name;
            _value = value ?? "";
        }

        public string Name { get { return _name; } }
        public string Value { get { return _value; } }

        public override string ToString()
        {
            return _name + ": " + _value;
        }
    }

    /// <summary>
    /// Errore di codifica HPACK: il blocco non e' interpretabile come il RFC
    /// prescrive. Non e' un errore di rete, quindi non si ritenta: si dice cosa
    /// non andava e a che byte, perche' un blocco corrotto che viene letto come
    /// se fosse valido fa divergere la tabella dinamica e da li' in poi *ogni*
    /// header e' sbagliato senza che nulla lo segnali.
    /// </summary>
    public class HpackException : Exception
    {
        public HpackException(string message) : base(message) { }
    }

    /// <summary>
    /// Decoder HPACK (RFC 7541).
    ///
    /// E' un decoder *stateful*: la tabella dinamica e' condivisa da tutti i
    /// blocchi della stessa connessione, quindi ne serve uno per connessione e
    /// non uno per blocco. Perdere un blocco scarta la connessione, perche' da
    /// quel punto in poi gli indici non significano piu' la stessa cosa.
    ///
    /// I due casi che contano piu' di tutti: un indice fuori tabella e un
    /// Huffman che non torna sono *errori*, non valori vuoti.
    /// </summary>
    public sealed class HpackDecoder
    {
        private const int StaticTableLength = 61;
        private const int EntryOverhead = 32;

        private readonly ILogSink _log;
        private readonly List<HpackHeader> _dynamic = new List<HpackHeader>();
        private readonly int _maxAllowedSize;
        private int _maxSize;
        private int _size;

        public HpackDecoder(ILogSink log) : this(log, 4096, 4096) { }

        public HpackDecoder(ILogSink log, int maxSize, int maxAllowedSize)
        {
            _log = log ?? NullLogSink.Instance;
            if (maxSize < 0 || maxSize > maxAllowedSize) throw new ArgumentOutOfRangeException("maxSize");
            _maxSize = maxSize;
            _maxAllowedSize = maxAllowedSize;
        }

        /// <summary>Dimensione corrente della tabella dinamica in byte (RFC 7541 §4.2).</summary>
        public int DynamicTableSize { get { return _size; } }

        /// <summary>Numero di voci nella tabella dinamica (per i test e i log).</summary>
        public int DynamicTableCount { get { return _dynamic.Count; } }

        public List<HpackHeader> Decode(byte[] block)
        {
            if (block == null) throw new ArgumentNullException("block");

            List<HpackHeader> headers = new List<HpackHeader>();
            int position = 0;
            while (position < block.Length)
            {
                byte first = block[position];

                if ((first & 0x80) != 0)
                {
                    // 6.1 - campo indicizzato: la voce esiste in una delle due tabelle.
                    int index = ReadInteger(block, ref position, 7);
                    HpackHeader entry = GetIndexed(index, position);
                    HpackHeader header = new HpackHeader(entry.Name, entry.Value);
                    headers.Add(header);
                    continue;
                }

                if ((first & 0xC0) == 0x40)
                {
                    // 6.2.1 - literal con indicizzazione incrementale: entra in tabella.
                    int nameIndex = ReadInteger(block, ref position, 6);
                    HpackHeader header = ReadLiteral(block, ref position, nameIndex);
                    headers.Add(header);
                    AddToDynamicTable(header);
                    continue;
                }

                if ((first & 0xE0) == 0x20)
                {
                    // 6.3 - aggiornamento della dimensione massima della tabella.
                    int newSize = ReadInteger(block, ref position, 5);
                    if (newSize > _maxAllowedSize)
                        throw new HpackException("aggiornamento di dimensione a " + newSize
                            + " oltre il massimo concordato " + _maxAllowedSize);
                    _maxSize = newSize;
                    Evict();
                    continue;
                }

                // 6.2.2 (0001xxxx, mai indicizzato) e 6.2.3 (0000xxxx, senza
                // indicizzazione): per il decoder sono la stessa cosa, cambia solo
                // se il campo puo' essere indicizzato da un proxy.
                int prefix = (first & 0xF0) == 0x10 ? 4 : 4;
                int literalNameIndex = ReadInteger(block, ref position, prefix);
                headers.Add(ReadLiteral(block, ref position, literalNameIndex));
            }

            _log.Log("hpack: " + block.Length + " byte -> " + headers.Count + " header (tabella dinamica "
                     + _dynamic.Count + " voci, " + _size + " byte)");
            return headers;
        }

        /// <summary>Legge nome e valore di un campo literal (RFC 7541 §6.2).</summary>
        private HpackHeader ReadLiteral(byte[] block, ref int position, int nameIndex)
        {
            string name;
            if (nameIndex == 0)
            {
                // Indice 0 significa "il nome e' una stringa qui": non esiste una
                // voce 0 in nessuna delle due tabelle.
                name = ReadString(block, ref position);
            }
            else
            {
                name = GetIndexed(nameIndex, position).Name;
            }

            string value = ReadString(block, ref position);
            return new HpackHeader(name, value);
        }

        /// <summary>Risolve un indice nelle due tabelle (RFC 7541 §2.3.3).</summary>
        private HpackHeader GetIndexed(int index, int position)
        {
            if (index == 0)
                throw new HpackException("indice HPACK 0 (byte " + position + "): non esiste alcuna voce 0");

            if (index <= StaticTableLength)
                return new HpackHeader(HpackTables.StaticNames[index], HpackTables.StaticValues[index]);

            int dynamicIndex = index - StaticTableLength - 1;
            if (dynamicIndex >= _dynamic.Count)
                throw new HpackException("indice HPACK " + index + " oltre la tabella dinamica ("
                    + _dynamic.Count + " voci, byte " + position + ")");

            return _dynamic[dynamicIndex];
        }

        private void AddToDynamicTable(HpackHeader header)
        {
            int size = HeaderSize(header);
            if (size > _maxSize)
            {
                // Un campo piu' grande della tabella svuota la tabella e non entra
                // (RFC 7541 §4.4): non e' un errore.
                _dynamic.Clear();
                _size = 0;
                return;
            }

            _dynamic.Insert(0, header);
            _size += size;
            Evict();
        }

        private void Evict()
        {
            while (_size > _maxSize && _dynamic.Count > 0)
            {
                HpackHeader last = _dynamic[_dynamic.Count - 1];
                _dynamic.RemoveAt(_dynamic.Count - 1);
                _size -= HeaderSize(last);
            }
        }

        private static int HeaderSize(HpackHeader header)
        {
            // I nomi e i valori qui sono testo latin-1: un carattere, un byte.
            return header.Name.Length + header.Value.Length + EntryOverhead;
        }

        /// <summary>Intero con prefisso e continuazione (RFC 7541 §5.1).</summary>
        private static int ReadInteger(byte[] block, ref int position, int prefixBits)
        {
            if (position >= block.Length)
                throw new HpackException("blocco HPACK troncato: manca l'intero");

            int mask = (1 << prefixBits) - 1;
            int value = block[position] & mask;
            position++;

            if (value < mask) return value;

            int shift = 0;
            while (true)
            {
                if (position >= block.Length)
                    throw new HpackException("intero HPACK troncato");
                int next = block[position];
                position++;

                value += (next & 0x7F) << shift;
                if ((next & 0x80) == 0) return value;

                shift += 7;
                if (shift > 28)
                    throw new HpackException("intero HPACK oltre i 32 bit");
            }
        }

        /// <summary>Stringa con bit di Huffman e lunghezza a 7 bit (RFC 7541 §5.2).</summary>
        private static string ReadString(byte[] block, ref int position)
        {
            if (position >= block.Length)
                throw new HpackException("blocco HPACK troncato: manca la stringa");

            bool huffman = (block[position] & 0x80) != 0;
            int length = ReadInteger(block, ref position, 7);

            if (length < 0 || position + length > block.Length)
                throw new HpackException("stringa HPACK di " + length + " byte oltre il blocco ("
                    + (block.Length - position) + " disponibili)");

            byte[] raw = new byte[length];
            Array.Copy(block, position, raw, 0, length);
            position += length;

            return huffman ? HpackHuffman.Decode(raw) : Latin1(raw);
        }

        /// <summary>
        /// I byte di un header HTTP sono latin-1 per definizione: passarli per
        /// Ascii mangerebbe i valori non ASCII, e un carattere mangiato e' un
        /// valore sbagliato che nessuno segnala.
        /// </summary>
        private static string Latin1(byte[] bytes)
        {
            char[] chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }
    }

    /// <summary>
    /// Decodifica di Huffman di HPACK (RFC 7541 §5.2 e Appendice B).
    ///
    /// I codici sono liberi da prefissi, quindi si accumulano bit finche' quello
    /// che si ha in mano non e' esattamente un codice della tabella: il primo
    /// che combacia e' il simbolo. La tabella di ricerca si costruisce una volta
    /// sola, alla prima decodifica.
    /// </summary>
    internal static class HpackHuffman
    {
        private const int Eos = 256;
        private const int MaxCodeBits = 30;

        private static Dictionary<long, int> _lookup;

        public static string Decode(byte[] bytes)
        {
            Dictionary<long, int> lookup = Lookup();

            StringBuilder text = new StringBuilder(bytes.Length);
            int code = 0;
            int bits = 0;

            for (int i = 0; i < bytes.Length; i++)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    code = (code << 1) | ((bytes[i] >> bit) & 1);
                    bits++;

                    int symbol;
                    if (lookup.TryGetValue(Key(bits, code), out symbol))
                    {
                        if (symbol == Eos)
                            throw new HpackException("stringa HPACK: simbolo di fine (EOS) dentro i dati");
                        text.Append((char)symbol);
                        code = 0;
                        bits = 0;
                        continue;
                    }

                    if (bits > MaxCodeBits)
                        throw new HpackException("stringa HPACK: codice di Huffman non valido dopo "
                            + (i + 1) + " byte");
                }
            }

            // Il riempimento finale e' il prefisso del codice di EOS (tutti uno)
            // e deve essere piu' corto di un byte (RFC 7541 §5.2).
            if (bits > 7)
                throw new HpackException("stringa HPACK: riempimento di " + bits + " bit (massimo 7)");
            if (bits > 0 && code != (1 << bits) - 1)
                throw new HpackException("stringa HPACK: riempimento non tutto a uno");

            return text.ToString();
        }

        private static long Key(int bits, int code)
        {
            return ((long)bits << 32) | (uint)code;
        }

        private static Dictionary<long, int> Lookup()
        {
            if (_lookup != null) return _lookup;

            Dictionary<long, int> table = new Dictionary<long, int>(257);
            for (int symbol = 0; symbol <= Eos; symbol++)
            {
                int bits = HpackTables.HuffmanLengths[symbol];
                int code = HpackTables.HuffmanCodes[symbol];
                table[Key(bits, code)] = symbol;
            }

            _lookup = table;
            return table;
        }
    }
}
