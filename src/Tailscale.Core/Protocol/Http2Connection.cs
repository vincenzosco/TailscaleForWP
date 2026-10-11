using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Tailscale.Core.Protocol
{
    /// <summary>La risposta di una richiesta: stato, header e corpo (uno stream).</summary>
    public sealed class Http2Response
    {
        private readonly int _statusCode;
        private readonly List<HpackHeader> _headers;
        private readonly Stream _body;

        public Http2Response(int statusCode, List<HpackHeader> headers, Stream body)
        {
            _statusCode = statusCode;
            _headers = headers;
            _body = body;
        }

        public int StatusCode { get { return _statusCode; } }
        public List<HpackHeader> Headers { get { return _headers; } }
        public Stream Body { get { return _body; } }

        /// <summary>Il valore di un header di risposta, o null se non c'e'.</summary>
        public string GetHeader(string name)
        {
            for (int i = 0; i < _headers.Count; i++)
            {
                if (string.Equals(_headers[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return _headers[i].Value;
            }
            return null;
        }

        public string ContentType { get { return GetHeader("content-type"); } }
    }

    /// <summary>
    /// Client HTTP/2 sopra uno stream gia' cifrato (il trasporto Noise).
    ///
    /// Perche' scritto a mano: il control plane di Tailscale parla HTTP/2 dentro
    /// Noise (`SetUnencryptedHTTP2` nel client Go), e su WP8.1 non c'e'
    /// System.Net.Http con un transport dirottabile. Non e' un client HTTP/2
    /// generico: fa richieste POST piccole, una per volta, e sa leggere una
    /// risposta che puo' non finire mai (la netmap), che e' il motivo per cui il
    /// corpo e' uno Stream e non una stringa.
    ///
    /// Cosa non copre, perche' non serve qui: priorita' degli stream, push,
    /// stream concorrenti, compressione dei frame (padding e PRIORITY in lettura
    /// sono comunque gestiti), e la contabilita' della finestra di *invio* (i
    /// nostri corpi stanno sotto la finestra iniziale e la superano solo con un
    /// hostinfo enorme, che viene segnalato nei log).
    /// </summary>
    public sealed class Http2Connection : IDisposable
    {
        private const string DefaultHost = "controlplane.tailscale.com";
        private const int InitialWindowSize = 65535;
        private const int ReceiveWindowTarget = 1 << 20;
        private const int WindowUpdateThreshold = 32 * 1024;
        private const int MaxBufferedResponse = 8 << 20;

        private readonly Stream _stream;
        private readonly ILogSink _log;
        private readonly string _host;
        private readonly HpackEncoder _encoder = new HpackEncoder(true);
        private readonly HpackDecoder _decoder;
        private readonly object _writeGate = new object();
        private readonly Dictionary<int, StreamState> _streams = new Dictionary<int, StreamState>();
        private readonly int _receiveWindowTarget;

        private bool _prefaceSent;
        private bool _earlyChecked;
        private bool _closed;
        private int _nextStreamId = 1;
        private int _peerMaxFrameSize = Http2Frame.DefaultMaxFrameSize;

        public Http2Connection(Stream stream, ILogSink log) : this(stream, log, DefaultHost) { }

        public Http2Connection(Stream stream, ILogSink log, string host)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            _stream = stream;
            _log = log ?? NullLogSink.Instance;
            _host = host ?? DefaultHost;
            _decoder = new HpackDecoder(_log);
            // La finestra iniziale e' 65535: si allarga con un WINDOW_UPDATE, cosi'
            // il server non si ferma a meta' di un messaggio grosso.
            _receiveWindowTarget = ReceiveWindowTarget;
        }

        public Stream Transport { get { return _stream; } }

        /// <summary>
        /// Manda una richiesta e legge la testa della risposta. Il corpo resta uno
        /// stream: chi non lo legge tutto (la netmap) non blocca la connessione.
        /// </summary>
        public Http2Response Post(string path, byte[] body, List<KeyValuePair<string, string>> headers)
        {
            byte[] payload = body ?? new byte[0];

            List<HpackHeader> request = new List<HpackHeader>();
            request.Add(new HpackHeader(":method", "POST"));
            request.Add(new HpackHeader(":scheme", "https"));
            request.Add(new HpackHeader(":authority", _host));
            request.Add(new HpackHeader(":path", path));
            request.Add(new HpackHeader("content-type", "application/json"));
            if (payload.Length > 0)
                request.Add(new HpackHeader("content-length", payload.Length.ToString(CultureInfo.InvariantCulture)));
            if (headers != null)
            {
                for (int i = 0; i < headers.Count; i++)
                {
                    // RFC 9113 §8.2.1: i nomi degli header vanno in minuscolo.
                    request.Add(new HpackHeader(headers[i].Key.ToLowerInvariant(), headers[i].Value));
                }
            }

            if (payload.Length > InitialWindowSize)
            {
                _log.Log("http2: corpo di " + payload.Length + " byte sopra la finestra iniziale ("
                         + InitialWindowSize + "): la finestra di invio non e' contabilizzata");
            }

            int streamId;
            lock (_writeGate)
            {
                SendPreface();

                streamId = _nextStreamId;
                _nextStreamId += 2;

                StreamState state = new StreamState(streamId);
                _streams[streamId] = state;

                byte[] block = _encoder.Encode(request);
                byte flags = Http2Frame.FlagEndHeaders;
                if (payload.Length == 0) flags |= Http2Frame.FlagEndStream;

                _log.Log("http2: -> stream " + streamId + " POST " + path + " ("
                         + payload.Length + " byte, header block " + block.Length + " byte)");
                WriteFrame(new Http2Frame(Http2FrameType.Headers, flags, streamId, block));

                if (payload.Length > 0)
                {
                    int offset = 0;
                    while (offset < payload.Length)
                    {
                        int count = Math.Min(_peerMaxFrameSize, payload.Length - offset);
                        bool last = offset + count == payload.Length;
                        byte[] chunk = new byte[count];
                        Array.Copy(payload, offset, chunk, 0, count);
                        WriteFrame(new Http2Frame(Http2FrameType.Data,
                            last ? Http2Frame.FlagEndStream : (byte)0, streamId, chunk));
                        offset += count;
                    }
                }
            }

            StreamState response = _streams[streamId];
            Pump(response);
            if (!response.HeadersDone)
            {
                throw new Http2Exception("stream " + response.Id + " terminato senza header di risposta"
                    + (response.Ended ? " (END_STREAM senza HEADERS)" : ""));
            }

            int status = 0;
            string statusText = null;
            for (int i = 0; i < response.Headers.Count; i++)
            {
                if (response.Headers[i].Name == ":status") statusText = response.Headers[i].Value;
            }
            if (statusText == null || !int.TryParse(statusText, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out status))
            {
                throw new Http2Exception("risposta senza :status valido (\"" + statusText + "\")");
            }

            _log.Log("http2: <- stream " + response.Id + " " + status + " ("
                     + response.Headers.Count + " header)");
            return new Http2Response(status, response.Headers, new Http2ResponseStream(this, response));
        }

        /// <summary>Legge tutto il corpo di una risposta, con un tetto di sicurezza.</summary>
        public static string ReadBody(Stream body, int maxBytes)
        {
            if (body == null) return null;

            // I byte si accumulano e si convertono una volta sola. Due motivi:
            // i corpi sono UTF-8 (un nome con accenti sono due byte per carattere,
            // e leggerli come caratteri li storpierebbe), e decodificare a pezzi
            // spezzerebbe in due un carattere multibyte a cavallo di due Read.
            MemoryStream buffered = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                int read = body.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                if (buffered.Length + read > maxBytes)
                    throw new Http2Exception("corpo della risposta oltre " + maxBytes + " byte");
                buffered.Write(buffer, 0, read);
            }
            return Text.FromUtf8(buffered.ToArray());
        }

        /// <summary>
        /// Prefazione + SETTINGS iniziali. Il preface e' quello che fa capire al
        /// server che parliamo HTTP/2: senza, qualunque cosa scriviamo e' rumore.
        /// </summary>
        private void SendPreface()
        {
            if (_prefaceSent) return;
            _prefaceSent = true;

            byte[] preface = new byte[24];
            string text = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";
            for (int i = 0; i < text.Length; i++) preface[i] = (byte)text[i];
            WriteRaw(preface);

            byte[] settings = new byte[12];
            // 2 = ENABLE_PUSH (0: non vogliamo push), 4 = INITIAL_WINDOW_SIZE.
            settings[1] = 2;
            WriteUInt32(settings, 2, 0);
            settings[7] = 4;
            WriteUInt32(settings, 8, _receiveWindowTarget);
            WriteFrame(new Http2Frame(Http2FrameType.Settings, 0, 0, settings));

            int increment = _receiveWindowTarget - InitialWindowSize;
            if (increment > 0)
            {
                byte[] update = new byte[4];
                WriteUInt32(update, 0, increment);
                WriteFrame(new Http2Frame(Http2FrameType.WindowUpdate, 0, 0, update));
            }

            _log.Log("http2: prefazione inviata (finestra di ricezione " + _receiveWindowTarget + " byte)");
        }

        /// <summary>
        /// Legge una volta sola l'eventuale early payload, alla *prima lettura* del
        /// canale e non alla costruzione dell'oggetto.
        ///
        /// L'ordine conta: l'early payload e' opzionale, e un server che non lo manda
        /// non manda nulla finche' non ha ricevuto la nostra prefazione HTTP/2. Se la
        /// lettura dei 9 byte di testa avvenisse prima di scrivere, il client
        /// resterebbe appeso per sempre; la prefazione invece e' gia' partita quando
        /// si arriva qui, esattamente come nel client Go (readHeader chiamato dalla
        /// prima Read).
        /// </summary>
        private void CheckEarlyPayload()
        {
            if (_earlyChecked) return;
            _earlyChecked = true;

            // Solo il canale del control plane ha un early payload davanti ai frame:
            // su uno stream qualsiasi (i test) non c'e' niente da guardare.
            PushbackStream pushback = _stream as PushbackStream;
            if (pushback == null) return;

            string json;
            if (EarlyPayload.TryConsume(pushback, _log, out json))
            {
                _log.Log("http2: early payload consumato prima del primo frame ("
                         + (json == null ? 0 : json.Length) + " byte di JSON)");
            }
        }

        /// <summary>Legge e gestisce frame finche' lo stream non ha qualcosa da consegnare.</summary>
        private void Pump(StreamState state)
        {
            while (!state.Ended && state.Offset >= state.Buffer.Length && !state.HeadersDone)
            {
                Http2Frame frame = ReadFrame();
                Dispatch(frame, state);
            }
        }

        private void PumpForData(StreamState state)
        {
            while (!state.Ended && state.Offset >= state.Buffer.Length)
            {
                Http2Frame frame = ReadFrame();
                Dispatch(frame, state);
            }
        }

        private Http2Frame ReadFrame()
        {
            if (_closed) throw new Http2Exception("connessione HTTP/2 gia' chiusa");
            CheckEarlyPayload();

            Http2Frame frame;
            try
            {
                frame = Http2Frame.Read(_stream);
            }
            catch (EndOfStreamException)
            {
                throw;
            }
            catch (IOException ex)
            {
                throw new Http2Exception("lettura di un frame HTTP/2 fallita: " + ex.Message, ex);
            }

            _log.Log("http2: <- " + frame);
            return frame;
        }

        private void Dispatch(Http2Frame frame, StreamState state)
        {
            switch (frame.Type)
            {
                case Http2FrameType.Settings:
                    HandleSettings(frame);
                    return;

                case Http2FrameType.Ping:
                    if (!frame.Has(Http2Frame.FlagAck))
                    {
                        WriteFrame(new Http2Frame(Http2FrameType.Ping, Http2Frame.FlagAck,
                            frame.StreamId, frame.Payload));
                    }
                    return;

                case Http2FrameType.WindowUpdate:
                    _log.Log("http2: finestra aggiornata di " + frame.ReadWindowUpdateIncrement()
                             + " sullo stream " + frame.StreamId);
                    return;

                case Http2FrameType.GoAway:
                    throw new Http2Exception("GOAWAY dal server: codice " + frame.ReadGoAwayErrorCode()
                        + ", ultimo stream accettato " + frame.ReadGoAwayLastStreamId());

                case Http2FrameType.Headers:
                    if (frame.StreamId != state.Id)
                    {
                        _log.Log("http2: header su uno stream inatteso (" + frame.StreamId + "), ignorati");
                        return;
                    }
                    HandleHeaders(frame, state);
                    return;

                case Http2FrameType.Continuation:
                    if (frame.StreamId != state.Id) return;
                    HandleContinuation(frame, state);
                    return;

                case Http2FrameType.Data:
                    if (frame.StreamId != state.Id)
                    {
                        _log.Log("http2: dati su uno stream inatteso (" + frame.StreamId + "), ignorati");
                        return;
                    }
                    HandleData(frame, state);
                    return;

                case Http2FrameType.RstStream:
                    if (frame.StreamId != state.Id) return;
                    throw new Http2Exception("RST_STREAM dal server sullo stream " + frame.StreamId
                        + ": codice " + frame.ReadRstStreamErrorCode());

                default:
                    _log.Log("http2: frame non gestito (" + frame.Type + "), ignorato");
                    return;
            }
        }

        private void HandleSettings(Http2Frame frame)
        {
            if (frame.Has(Http2Frame.FlagAck))
            {
                _log.Log("http2: SETTINGS confermati dal server");
                return;
            }

            List<KeyValuePair<int, int>> settings = frame.ReadSettings();
            for (int i = 0; i < settings.Count; i++)
            {
                switch (settings[i].Key)
                {
                    case 5:
                        // RFC 9113 §6.5.2: il valore ammesso va da 16384 a 16777215.
                        // Fuori da quel range (0 compreso) il valore non si usa: con 0
                        // il ciclo di invio del corpo non avanzerebbe piu'.
                        if (settings[i].Value < Http2Frame.DefaultMaxFrameSize
                            || settings[i].Value > 16777215)
                        {
                            _log.Log("http2: MAX_FRAME_SIZE fuori range (" + settings[i].Value
                                     + "): lo ignoro e resto a " + _peerMaxFrameSize);
                            break;
                        }
                        _peerMaxFrameSize = settings[i].Value;
                        _log.Log("http2: il server accetta frame fino a " + _peerMaxFrameSize + " byte");
                        break;
                    case 1:
                        // Quanto il *server* tiene per il suo decoder HPACK: la
                        // nostra tabella dinamica resta in ogni caso dentro i limiti.
                        _log.Log("http2: il server dichiara una tabella HPACK di "
                                 + settings[i].Value + " byte");
                        break;
                    default:
                        _log.Log("http2: SETTINGS " + settings[i].Key + " = " + settings[i].Value);
                        break;
                }
            }

            WriteFrame(new Http2Frame(Http2FrameType.Settings, Http2Frame.FlagAck, 0, new byte[0]));
        }

        private void HandleHeaders(Http2Frame frame, StreamState state)
        {
            if (frame.StreamId != state.Id) return;

            byte[] block = StripPadding(frame);
            if (state.HeadersDone)
            {
                // Un secondo HEADERS sono i trailer: se chiudono lo stream, il corpo e' finito.
                if (frame.Has(Http2Frame.FlagEndStream)) state.Ended = true;
                List<HpackHeader> trailers = _decoder.Decode(block);
                _log.Log("http2: trailer (" + trailers.Count + " header)");
                return;
            }

            if (frame.Has(Http2Frame.FlagEndHeaders))
            {
                state.Headers = _decoder.Decode(block);
                state.HeadersDone = true;
                if (frame.Has(Http2Frame.FlagEndStream)) state.Ended = true;
                return;
            }

            // Blocco spezzato: si accumula e si decodifica alla fine. Decodificare
            // ora significherebbe leggere un blocco a meta' e dissezionare la
            // tabella dinamica per il resto della connessione.
            state.PartialHeaderBlock = block;
            return;
        }

        private void HandleContinuation(Http2Frame frame, StreamState state)
        {
            if (state.PartialHeaderBlock == null)
                throw new Http2Exception("CONTINUATION senza HEADERS sullo stream " + state.Id);

            byte[] joined = new byte[state.PartialHeaderBlock.Length + frame.Payload.Length];
            Array.Copy(state.PartialHeaderBlock, 0, joined, 0, state.PartialHeaderBlock.Length);
            Array.Copy(frame.Payload, 0, joined, state.PartialHeaderBlock.Length, frame.Payload.Length);
            state.PartialHeaderBlock = joined;

            if (frame.Has(Http2Frame.FlagEndHeaders))
            {
                state.Headers = _decoder.Decode(state.PartialHeaderBlock);
                state.HeadersDone = true;
                state.PartialHeaderBlock = null;
            }
        }

        private void HandleData(Http2Frame frame, StreamState state)
        {
            if (frame.Has(Http2Frame.FlagEndStream))
            {
                // Un DATA vuoto con END_STREAM chiude soltanto.
                state.Ended = true;
                _log.Log("http2: <- fine del corpo dello stream " + state.Id);
            }

            byte[] payload = StripPadding(frame);
            if (payload.Length == 0) return;

            int remaining = state.Buffer.Length - state.Offset;
            if (remaining > 0)
            {
                // Compattazione prima di accodare: senza, un corpo lungo
                // accumulerebbe ogni chunk letto fin qui.
                byte[] compacted = new byte[remaining + payload.Length];
                Array.Copy(state.Buffer, state.Offset, compacted, 0, remaining);
                Array.Copy(payload, 0, compacted, remaining, payload.Length);
                state.Buffer = compacted;
                state.Offset = 0;
            }
            else
            {
                state.Buffer = payload;
                state.Offset = 0;
            }

            if (state.Buffer.Length - state.Offset > MaxBufferedResponse)
            {
                _log.Log("http2: attenzione, " + (state.Buffer.Length - state.Offset)
                         + " byte di risposta non consumati: chi legge e' piu' lento del server");
            }
        }

        /// <summary>Toglie il padding (RFC 9113 §6.1) e i campi che non ci riguardano.</summary>
        private static byte[] StripPadding(Http2Frame frame)
        {
            byte[] payload = frame.Payload;
            int start = 0;
            int end = payload.Length;

            // L'ordine dei campi e' quello della RFC 9113 §6.2: prima Pad Length,
            // poi (solo per HEADERS) i 5 byte di priorita', poi il blocco di header.
            // Invertirlo sposta la lettura di 5 byte e disallinea la tabella HPACK
            // per il resto della connessione, non solo per questa risposta.
            if (frame.Has(Http2Frame.FlagPadded))
            {
                if (end <= start) throw new Http2Exception("frame con PADDED ma senza byte di padding");
                int padding = payload[start];
                start++;
                if (padding > end - start)
                    throw new Http2Exception("padding di " + padding + " byte oltre il frame");
                end -= padding;
            }

            if (frame.Type == Http2FrameType.Headers && frame.Has(Http2Frame.FlagPriority))
            {
                if (end - start < 5)
                    throw new Http2Exception("frame HEADERS con PRIORITY ma senza i 5 byte di priorita'");
                start += 5;
            }

            if (start == 0 && end == payload.Length) return payload;

            byte[] result = new byte[end - start];
            Array.Copy(payload, start, result, 0, result.Length);
            return result;
        }

        /// <summary>
        /// Restituisce credito alla finestra per i byte consumati da chi legge.
        /// Restituirlo all'arrivo invece che al consumo farebbe crescere la finestra
        /// senza limite e il server potrebbe riempirci la memoria.
        /// </summary>
        private void CreditWindow(StreamState state)
        {
            // Si accredita quello che il lettore ha *consumato*: e' la sola
            // grandezza che dice se la finestra puo' riaprirsi. Accreditare
            // all'arrivo (come faceva prima) tiene la finestra sempre aperta e
            // lascia al server la scelta di quanta memoria occuparci.
            if (state.ConsumedSinceCredit < WindowUpdateThreshold) return;

            int increment = state.ConsumedSinceCredit;
            state.ConsumedSinceCredit = 0;

            byte[] update = new byte[4];
            WriteUInt32(update, 0, increment);
            lock (_writeGate)
            {
                WriteFrame(new Http2Frame(Http2FrameType.WindowUpdate, 0, 0, update));
                WriteFrame(new Http2Frame(Http2FrameType.WindowUpdate, 0, state.Id, update));
            }
            _log.Log("http2: -> WINDOW_UPDATE di " + increment + " byte (stream " + state.Id + ")");
        }

        internal int ReadFromStream(StreamState state, byte[] buffer, int offset, int count)
        {
            while (state.Offset >= state.Buffer.Length)
            {
                if (state.Ended) return 0;
                PumpForData(state);
            }

            int available = Math.Min(count, state.Buffer.Length - state.Offset);
            Array.Copy(state.Buffer, state.Offset, buffer, offset, available);
            state.Offset += available;
            state.ConsumedSinceCredit += available;
            CreditWindow(state);
            return available;
        }

        private void WriteRaw(byte[] bytes)
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        private void WriteFrame(Http2Frame frame)
        {
            // Il lock e' ricorsivo sullo stesso thread, quindi chi gia' tiene il
            // gate per una sequenza di frame non si blocca qui; chi scrive un
            // frame singolo (un ACK, un WINDOW_UPDATE) resta comunque protetto.
            lock (_writeGate)
            {
                WriteRaw(frame.ToBytes());
            }
        }

        private static void WriteUInt32(byte[] target, int offset, int value)
        {
            target[offset] = (byte)((value >> 24) & 0xFF);
            target[offset + 1] = (byte)((value >> 16) & 0xFF);
            target[offset + 2] = (byte)((value >> 8) & 0xFF);
            target[offset + 3] = (byte)(value & 0xFF);
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;

            foreach (KeyValuePair<int, StreamState> entry in _streams)
                entry.Value.Ended = true;

            _log.Log("http2: chiusura della connessione");
            try { _stream.Dispose(); }
            catch (Exception ex) { _log.Log("http2: errore in chiusura: " + ex.Message); }
        }

        /// <summary>
        /// Stato di uno stream lato risposta: header decodificati e buffer dei dati.
        /// Uno per richiesta; con una richiesta per volta non serve altro.
        /// </summary>
        internal sealed class StreamState
        {
            public readonly int Id;
            public List<HpackHeader> Headers = new List<HpackHeader>();
            public bool HeadersDone;
            public bool Ended;
            public byte[] Buffer = new byte[0];
            public int Offset;

            /// <summary>Byte consumati dal lettore e non ancora accreditati al server.</summary>
            public int ConsumedSinceCredit;

            public byte[] PartialHeaderBlock;

            public StreamState(int id)
            {
                Id = id;
            }
        }
    }

    /// <summary>
    /// Il corpo di una risposta come Stream: legge i frame DATA man mano che
    /// servono. E' un Stream e non una stringa perche' la netmap arriva per ore e
    /// non finisce mai, quindi "leggere il corpo" e' una cosa che non si puo' fare.
    /// </summary>
    internal sealed class Http2ResponseStream : Stream
    {
        private readonly Http2Connection _connection;
        private readonly Http2Connection.StreamState _state;
        private bool _disposed;

        public Http2ResponseStream(Http2Connection connection, Http2Connection.StreamState state)
        {
            _connection = connection;
            _state = state;
        }

        public bool AtEnd { get { return _state.Ended && _state.Offset >= _state.Buffer.Length; } }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException("il corpo di una risposta non ha lunghezza nota"); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException("Http2ResponseStream");
            if (count == 0) return 0;
            return _connection.ReadFromStream(_state, buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
