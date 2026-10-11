using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tailscale.Core;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Test del layer HTTP/2. Il server e' finto ma la sua parte di filo e' vera:
    /// i frame sono costruiti con `Http2Frame` e il blocco di header con l'encoder
    /// gia' verificato byte per byte contro hpack, poi il client li legge dallo
    /// stesso stream su cui scrive. Cosi' i due lati non possono essere
    /// d'accordo su un formato sbagliato senza che si veda qui.
    ///
    /// Il caso che questi test difendono per primo e' la risposta che *non
    /// finisce*: la netmap e' uno stream aperto per ore, e un client che aspetta
    /// la fine del corpo la aspetta per sempre.
    /// </summary>
    public static class Http2Tests
    {
        private const string Preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";

        public static void Run()
        {
            Case("frame", ParsesFrames);
            Case("richiesta", WritesRequestAndReadsResponse);
            Case("continuation", ReassemblesContinuation);
            Case("stream senza fine", StreamsWithoutWaitingForEnd);
            Case("finestra", SendsAckWindowUpdatesAndPing);
            Case("goaway", GoAwayIsAnError);
            Case("frame troppo grande", RejectsOversizedFrame);
        }

        /// <summary>
        /// Ogni caso ha un nome: un'eccezione in un caso non deve oscurare gli
        /// altri, e il messaggio deve dire *quale* caso e' caduto.
        /// </summary>
        private static void Case(string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                T.Failures++;
                Console.WriteLine("FAIL http2/" + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ParsesFrames()
        {
            // SETTINGS vuoto, cosi' come lo scrive un server all'apertura.
            MemoryStream settings = new MemoryStream(T.FromHex("000000040000000000"));
            Http2Frame frame = Http2Frame.Read(settings);
            T.Eq("Settings", frame.Type.ToString(), "SETTINGS: tipo");
            T.Eq(0, frame.StreamId, "SETTINGS: stream 0");
            T.Eq(0, frame.Payload.Length, "SETTINGS: corpo vuoto");
            T.Eq(0, frame.ReadSettings().Count, "SETTINGS: nessuna coppia");

            // HEADERS con END_HEADERS|END_STREAM (0x5) sullo stream 1, un byte di corpo.
            MemoryStream headers = new MemoryStream(T.FromHex("000001010500000001fe"));
            Http2Frame parsed = Http2Frame.Read(headers);
            T.Eq("Headers", parsed.Type.ToString(), "HEADERS: tipo");
            T.Eq(1, parsed.StreamId, "HEADERS: stream 1");
            T.True(parsed.Has(Http2Frame.FlagEndHeaders), "HEADERS: END_HEADERS");
            T.True(parsed.Has(Http2Frame.FlagEndStream), "HEADERS: END_STREAM");
            T.Eq("fe", T.ToHex(parsed.Payload), "HEADERS: corpo");

            // Il bit piu' alto dello stream id e' riservato: se finisse nell'id, lo
            // stream diventerebbe 2147483649 e la risposta non arriverebbe mai.
            MemoryStream reserved = new MemoryStream(T.FromHex("000001010500000001fe".Replace("00000001", "80000001")));
            T.Eq(1, Http2Frame.Read(reserved).StreamId, "HEADERS: bit riservato ignorato");

            // E il giro completo: quello che scriviamo lo rileggiamo uguale.
            Http2Frame written = new Http2Frame(Http2FrameType.Data, Http2Frame.FlagEndStream, 3, T.FromHex("0102"));
            MemoryStream roundTrip = new MemoryStream(written.ToBytes());
            Http2Frame back = Http2Frame.Read(roundTrip);
            T.Eq("Data", back.Type.ToString(), "scrittura: tipo conservato");
            T.Eq(3, back.StreamId, "scrittura: stream conservato");
            T.Eq("0102", T.ToHex(back.Payload), "scrittura: corpo conservato");

            T.Throws(delegate { frame.ReadWindowUpdateIncrement(); }, "WINDOW_UPDATE su un SETTINGS");
        }

        private static void WritesRequestAndReadsResponse()
        {
            ScriptedStream stream = new ScriptedStream();
            stream.Script(ServerSettings());
            stream.Script(ResponseHeaders(0x4, ":status", "200", "content-type", "application/json"));
            stream.Script(Data("hello", false));
            stream.Script(Data("", true));

            List<string> lines = new List<string>();
            Http2Connection connection = new Http2Connection(stream, new DelegateLogSink(lines.Add));
            Http2Response response = connection.Post("/machine/register", T.FromHex("7b7d"),
                new List<KeyValuePair<string, string>>());

            T.Eq(200, response.StatusCode, "richiesta: stato 200");
            T.Eq("application/json", response.ContentType, "richiesta: content-type");
            T.Eq("hello", Http2Connection.ReadBody(response.Body, 4096), "richiesta: corpo letto fino a END_STREAM");

            // La traccia dei frame e' quello che si legge nei log quando una
            // richiesta va storta: deve esserci e deve dire cosa e' arrivato.
            T.True(HasLine(lines, "<- stream 1 200"), "richiesta: il log dice la risposta ricevuta");
            T.True(HasLine(lines, "-> stream 1 POST /machine/register"), "richiesta: il log dice la richiesta inviata");

            // La prefazione e' i primi 24 byte scritti: senza, il server non sa
            // nemmeno che stiamo parlando HTTP/2.
            T.Eq(Preface, AsciiOf(stream.Written.ToArray(), 0, 24), "richiesta: prefazione HTTP/2");

            List<Http2Frame> frames = ParseWritten(stream, 24);
            T.Eq("Settings", frames[0].Type.ToString(), "richiesta: il primo frame sono i SETTINGS");
            T.Eq("WindowUpdate", frames[1].Type.ToString(), "richiesta: il secondo frame allarga la finestra");

            Http2Frame request = Find(frames, Http2FrameType.Headers);
            T.True(request != null, "richiesta: HEADERS scritti");
            T.True(request.Has(Http2Frame.FlagEndHeaders), "richiesta: HEADERS con END_HEADERS");

            HpackDecoder decoder = new HpackDecoder(NullLogSink.Instance);
            List<HpackHeader> sent = decoder.Decode(request.Payload);
            T.Eq("/machine/register", HeaderValue(sent, ":path"), "richiesta: :path");
            T.Eq("POST", HeaderValue(sent, ":method"), "richiesta: :method");
            T.Eq("controlplane.tailscale.com", HeaderValue(sent, ":authority"), "richiesta: :authority");
            T.Eq("2", HeaderValue(sent, "content-length"), "richiesta: content-length del corpo");

            // Il corpo piccolo sta in un DATA con END_STREAM in coda.
            Http2Frame data = Find(frames, Http2FrameType.Data);
            T.True(data != null, "richiesta: DATA scritti");
            T.True(data.Has(Http2Frame.FlagEndStream), "richiesta: DATA con END_STREAM");
            T.Eq("7b7d", T.ToHex(data.Payload), "richiesta: corpo dei DATA");
        }

        private static void ReassemblesContinuation()
        {
            // Il server puo' spezzare il blocco di header: se lo decodificassimo a
            // meta', la tabella dinamica andrebbe fuori sincrono per il resto della
            // connessione e *tutti* gli header successivi sarebbero sbagliati.
            byte[] block = HeaderBlock(":status", "200", "content-type", "application/json");
            byte[] first = new byte[4];
            byte[] second = new byte[block.Length - 4];
            Array.Copy(block, 0, first, 0, 4);
            Array.Copy(block, 4, second, 0, second.Length);

            ScriptedStream stream = new ScriptedStream();
            stream.Script(ServerSettings());
            stream.Script(new Http2Frame(Http2FrameType.Headers, 0, 1, first).ToBytes());
            stream.Script(new Http2Frame(Http2FrameType.Continuation,
                Http2Frame.FlagEndHeaders, 1, second).ToBytes());
            stream.Script(Data("", true));

            Http2Connection connection = new Http2Connection(stream, NullLogSink.Instance);
            Http2Response response = connection.Post("/machine/map", T.FromHex("7b7d"),
                new List<KeyValuePair<string, string>>());

            T.Eq(200, response.StatusCode, "continuation: stato 200");
            T.Eq("application/json", response.ContentType, "continuation: header decodificati interi");
        }

        private static void StreamsWithoutWaitingForEnd()
        {
            // La netmap: header, un pezzo di corpo e *niente* END_STREAM. Chi legge
            // deve ricevere quel pezzo subito; se aspettasse la fine del corpo,
            // aspetterebbe per sempre e il login non mostrerebbe mai il primo peer.
            ScriptedStream stream = new ScriptedStream();
            stream.Script(ServerSettings());
            stream.Script(ResponseHeaders(0x4, ":status", "200", "content-type", "application/json"));
            stream.Script(Data("primo", false));

            Http2Connection connection = new Http2Connection(stream, NullLogSink.Instance);
            Http2Response response = connection.Post("/machine/map", T.FromHex("7b7d"),
                new List<KeyValuePair<string, string>>());

            byte[] buffer = new byte[5];
            int read = response.Body.Read(buffer, 0, 5);
            T.Eq(5, read, "stream: cinque byte letti senza attendere la fine");
            T.Eq("primo", AsciiOf(buffer, 0, 5), "stream: i byte sono quelli arrivati");
        }

        private static void SendsAckWindowUpdatesAndPing()
        {
            // Un corpo grande piu' un PING dal server: il credito della finestra va
            // restituito *dopo* che il chiamante ha consumato, altrimenti il server
            // puo' riempirci la memoria; e il PING va confermato, se no il server
            // puo' chiudere la connessione per inattivita'.
            // 40000 byte in tre DATA, perche' un server non manda un frame piu'
            // grande di MAX_FRAME_SIZE (16384): 16384 + 16384 + 7232.
            byte[] big = new byte[40000];
            for (int i = 0; i < big.Length; i++) big[i] = (byte)'x';

            ScriptedStream stream = new ScriptedStream();
            stream.Script(ServerSettings());
            stream.Script(ResponseHeaders(0x4, ":status", "200", "content-type", "application/json"));
            stream.Script(new Http2Frame(Http2FrameType.Ping, 0, 0, T.FromHex("0102030405060708")).ToBytes());
            for (int offset = 0; offset < big.Length; offset += 16384)
            {
                int count = Math.Min(16384, big.Length - offset);
                byte[] chunk = new byte[count];
                Array.Copy(big, offset, chunk, 0, count);
                stream.Script(new Http2Frame(Http2FrameType.Data, 0, 1, chunk).ToBytes());
            }
            stream.Script(Data("", true));

            Http2Connection connection = new Http2Connection(stream, NullLogSink.Instance);
            Http2Response response = connection.Post("/machine/map", T.FromHex("7b7d"),
                new List<KeyValuePair<string, string>>());
            string body = Http2Connection.ReadBody(response.Body, 100000);
            T.Eq(40000, body.Length, "finestra: corpo grande letto tutto");

            List<Http2Frame> frames = ParseWritten(stream, 24);

            Http2Frame ack = null;
            Http2Frame ping = null;
            int credited = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].Type == Http2FrameType.Settings && frames[i].Has(Http2Frame.FlagAck)) ack = frames[i];
                if (frames[i].Type == Http2FrameType.Ping && frames[i].Has(Http2Frame.FlagAck)) ping = frames[i];
                if (frames[i].Type == Http2FrameType.WindowUpdate && frames[i].StreamId == 1)
                    credited += frames[i].ReadWindowUpdateIncrement();
            }

            T.True(ack != null, "finestra: SETTINGS confermati con ACK");
            T.True(ping != null, "finestra: PING confermato con ACK");
            T.Eq("0102030405060708", T.ToHex(ping.Payload), "finestra: il PING riporta lo stesso payload");
            // Il credito torna a blocchi da 32 KB man mano che il chiamante
            // consuma: 32768 dopo i primi due DATA, e i 7232 rimanenti non
            // arrivano alla soglia (la finestra non e' in sofferenza, quindi non
            // serve altro credito). Mai piu' dei byte consumati.
            T.True(credited >= 32768 && credited <= 40000,
                   "finestra: credito fra 32768 e i 40000 byte consumati (era " + credited + ")");
        }

        private static void GoAwayIsAnError()
        {
            byte[] payload = new byte[8];
            payload[3] = 1;    // ultimo stream accettato
            payload[7] = 0x2;  // PROTOCOL_ERROR

            ScriptedStream stream = new ScriptedStream();
            stream.Script(ServerSettings());
            stream.Script(new Http2Frame(Http2FrameType.GoAway, 0, 0, payload).ToBytes());

            Http2Connection connection = new Http2Connection(stream, NullLogSink.Instance);
            try
            {
                connection.Post("/machine/map", T.FromHex("7b7d"), new List<KeyValuePair<string, string>>());
                T.True(false, "GOAWAY: doveva lanciare");
            }
            catch (Http2Exception ex)
            {
                T.True(ex.Message.Contains("GOAWAY"), "GOAWAY: il messaggio lo dice");
                T.True(ex.Message.Contains("codice 2"), "GOAWAY: il messaggio porta il codice del server");
            }
        }

        private static void RejectsOversizedFrame()
        {
            // Lunghezza 20000 (> 16384): un frame cosi' non e' ammesso e non va
            // allocato "per sicurezza" prima di sapere se e' plausibile.
            MemoryStream stream = new MemoryStream(T.FromHex("004e200400000000"));
            T.Throws(delegate { Http2Frame.Read(stream); }, "frame oltre 16384 byte");
        }

        // --- attrezzi del finto server ---

        private static byte[] ServerSettings()
        {
            byte[] payload = new byte[6];
            payload[1] = 5;                      // MAX_FRAME_SIZE
            payload[2] = 0;
            payload[3] = 0;
            payload[4] = 0x40;
            payload[5] = 0x00;                   // 16384
            return new Http2Frame(Http2FrameType.Settings, 0, 0, payload).ToBytes();
        }

        private static byte[] ResponseHeaders(byte flags, string name, string value)
        {
            byte[] block = HeaderBlock(name, value);
            return new Http2Frame(Http2FrameType.Headers, (byte)(flags | Http2Frame.FlagEndHeaders), 1, block).ToBytes();
        }

        private static byte[] ResponseHeaders(byte flags, string name, string value, string name2, string value2)
        {
            List<HpackHeader> headers = new List<HpackHeader>();
            headers.Add(new HpackHeader(name, value));
            headers.Add(new HpackHeader(name2, value2));
            byte[] block = new HpackEncoder(true).Encode(headers);
            return new Http2Frame(Http2FrameType.Headers, (byte)(flags | Http2Frame.FlagEndHeaders), 1, block).ToBytes();
        }

        private static byte[] HeaderBlock(string name, string value)
        {
            List<HpackHeader> headers = new List<HpackHeader>();
            headers.Add(new HpackHeader(name, value));
            return new HpackEncoder(true).Encode(headers);
        }

        private static byte[] HeaderBlock(string name, string value, string name2, string value2)
        {
            List<HpackHeader> headers = new List<HpackHeader>();
            headers.Add(new HpackHeader(name, value));
            headers.Add(new HpackHeader(name2, value2));
            return new HpackEncoder(true).Encode(headers);
        }

        private static byte[] Data(string text, bool endStream)
        {
            byte[] payload = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) payload[i] = (byte)text[i];
            return new Http2Frame(Http2FrameType.Data,
                endStream ? Http2Frame.FlagEndStream : (byte)0, 1, payload).ToBytes();
        }

        private static List<Http2Frame> ParseWritten(ScriptedStream stream, int skip)
        {
            byte[] all = stream.Written.ToArray();
            MemoryStream memory = new MemoryStream(all);
            memory.Position = skip;
            List<Http2Frame> frames = new List<Http2Frame>();
            while (memory.Position < memory.Length) frames.Add(Http2Frame.Read(memory));
            return frames;
        }

        private static Http2Frame Find(List<Http2Frame> frames, Http2FrameType type)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].Type == type) return frames[i];
            }
            return null;
        }

        private static string HeaderValue(List<HpackHeader> headers, string name)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                if (headers[i].Name == name) return headers[i].Value;
            }
            return null;
        }

        private static bool HasLine(List<string> lines, string fragment)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static string AsciiOf(byte[] bytes, int offset, int count)
        {
            char[] chars = new char[count];
            for (int i = 0; i < count; i++) chars[i] = (char)bytes[offset + i];
            return new string(chars);
        }

        /// <summary>
        /// Uno stream che si legge da una sceneggiatura (quello che il server
        /// manda) e si scrive in un buffer (quello che il client manda). Il test
        /// puo' cosi' guardare i byte in uscita senza un server vero e senza
        /// thread, e resta deterministico.
        /// </summary>
        private sealed class ScriptedStream : Stream
        {
            public readonly List<byte> Written = new List<byte>();
            private readonly MemoryStream _incoming = new MemoryStream();

            /// <summary>
            /// Accoda quello che il server manda, senza spostare il cursore di
            /// lettura: il client deve leggere i frame nell'ordine in cui sono
            /// stati scritti, dal primo all'ultimo.
            /// </summary>
            public void Script(byte[] bytes)
            {
                long reading = _incoming.Position;
                _incoming.Position = _incoming.Length;
                _incoming.Write(bytes, 0, bytes.Length);
                _incoming.Position = reading;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_incoming.Position >= _incoming.Length)
                    throw new EndOfStreamException("la sceneggiatura del server e' finita");
                return _incoming.Read(buffer, offset, count);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i++) Written.Add(buffer[offset + i]);
            }
        }
    }
}
