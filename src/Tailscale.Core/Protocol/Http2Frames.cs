using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Tailscale.Core.Protocol
{
    /// <summary>Errore di protocollo HTTP/2: il peer ha mandato qualcosa che non sta in piedi.</summary>
    public class Http2Exception : Exception
    {
        public Http2Exception(string message) : base(message) { }

        /// <summary>
        /// Con la causa: un guasto di lettura (socket chiuso, lettura interrotta dal
        /// sistema) va distinto da un protocollo violato, e chi deve decidere se
        /// rifare il canale non puo' farlo se la causa e' stata buttata via qui.
        /// </summary>
        public Http2Exception(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Tipi di frame usati (RFC 9113 §6). Gli altri non ci interessano.</summary>
    public enum Http2FrameType
    {
        Data = 0,
        Headers = 1,
        Priority = 2,
        RstStream = 3,
        Settings = 4,
        PushPromise = 5,
        Ping = 6,
        GoAway = 7,
        WindowUpdate = 8,
        Continuation = 9
    }

    /// <summary>
    /// Un frame HTTP/2: 9 byte di testa (lunghezza a 24 bit, tipo, flag, stream id
    /// a 31 bit) piu' il corpo.
    ///
    /// Il bit piu' alto dello stream id e' riservato: in lettura va ignorato (un
    /// peer puo' mandarlo a 1), in scrittura va a zero. Trattarlo come parte
    /// dell'id sposterebbe lo stream di 2 miliardi e la risposta non arriverebbe
    /// mai.
    /// </summary>
    public sealed class Http2Frame
    {
        public const int HeaderSize = 9;
        public const int DefaultMaxFrameSize = 16384;

        public const byte FlagEndStream = 0x1;
        public const byte FlagAck = 0x1;
        public const byte FlagEndHeaders = 0x4;
        public const byte FlagPadded = 0x8;
        public const byte FlagPriority = 0x20;

        private readonly Http2FrameType _type;
        private readonly byte _flags;
        private readonly int _streamId;
        private readonly byte[] _payload;

        public Http2Frame(Http2FrameType type, byte flags, int streamId, byte[] payload)
        {
            if (streamId < 0) throw new ArgumentOutOfRangeException("streamId");
            _type = type;
            _flags = flags;
            _streamId = streamId;
            _payload = payload ?? new byte[0];
        }

        public Http2FrameType Type { get { return _type; } }
        public byte Flags { get { return _flags; } }
        public int StreamId { get { return _streamId; } }
        public byte[] Payload { get { return _payload; } }

        public bool Has(byte flag) { return (_flags & flag) != 0; }

        /// <summary>Legge un frame intero dallo stream. Lancia su stream chiuso a meta'.</summary>
        public static Http2Frame Read(Stream stream)
        {
            byte[] header = new byte[HeaderSize];
            int read = 0;
            while (read < HeaderSize)
            {
                int n = stream.Read(header, read, HeaderSize - read);
                if (n <= 0)
                {
                    if (read == 0) throw new EndOfStreamException("canale chiuso dal peer");
                    throw new Http2Exception("frame HTTP/2 troncato: " + read + " dei "
                        + HeaderSize + " byte di testa");
                }
                read += n;
            }

            int length = (header[0] << 16) | (header[1] << 8) | header[2];
            Http2FrameType type = (Http2FrameType)header[3];
            byte flags = header[4];
            int streamId = ((header[5] & 0x7F) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];

            if (length > DefaultMaxFrameSize)
                throw new Http2Exception("frame di " + length + " byte oltre il massimo dichiarato ("
                    + DefaultMaxFrameSize + "), tipo " + (int)type);

            byte[] payload = new byte[length];
            int got = 0;
            while (got < length)
            {
                int n = stream.Read(payload, got, length - got);
                if (n <= 0)
                    throw new Http2Exception("frame " + (int)type + " troncato: " + got + " dei "
                        + length + " byte di corpo");
                got += n;
            }

            return new Http2Frame(type, flags, streamId, payload);
        }

        public byte[] ToBytes()
        {
            // Scrittura in un colpo solo: due scritture sulla stessa connessione
            // possono intrecciarsi con quelle di un altro thread.
            byte[] bytes = new byte[HeaderSize + _payload.Length];
            bytes[0] = (byte)((_payload.Length >> 16) & 0xFF);
            bytes[1] = (byte)((_payload.Length >> 8) & 0xFF);
            bytes[2] = (byte)(_payload.Length & 0xFF);
            bytes[3] = (byte)_type;
            bytes[4] = _flags;
            bytes[5] = (byte)((_streamId >> 24) & 0x7F);
            bytes[6] = (byte)((_streamId >> 16) & 0xFF);
            bytes[7] = (byte)((_streamId >> 8) & 0xFF);
            bytes[8] = (byte)(_streamId & 0xFF);
            Array.Copy(_payload, 0, bytes, HeaderSize, _payload.Length);
            return bytes;
        }

        /// <summary>Le coppie di un SETTINGS (RFC 9113 §6.5).</summary>
        public List<KeyValuePair<int, int>> ReadSettings()
        {
            if (_type != Http2FrameType.Settings)
                throw new Http2Exception("il frame non e' un SETTINGS ma " + _type);
            if (_payload.Length % 6 != 0)
                throw new Http2Exception("SETTINGS di " + _payload.Length + " byte (non multiplo di 6)");

            List<KeyValuePair<int, int>> settings = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < _payload.Length; i += 6)
            {
                int id = (_payload[i] << 8) | _payload[i + 1];
                int value = (_payload[i + 2] << 24) | (_payload[i + 3] << 16)
                            | (_payload[i + 4] << 8) | _payload[i + 5];
                settings.Add(new KeyValuePair<int, int>(id, value));
            }
            return settings;
        }

        public int ReadWindowUpdateIncrement()
        {
            if (_type != Http2FrameType.WindowUpdate)
                throw new Http2Exception("il frame non e' un WINDOW_UPDATE ma " + _type);
            if (_payload.Length != 4)
                throw new Http2Exception("WINDOW_UPDATE di " + _payload.Length + " byte invece di 4");
            return ((_payload[0] & 0x7F) << 24) | (_payload[1] << 16) | (_payload[2] << 8) | _payload[3];
        }

        public int ReadGoAwayErrorCode()
        {
            if (_type != Http2FrameType.GoAway)
                throw new Http2Exception("il frame non e' un GOAWAY ma " + _type);
            if (_payload.Length < 8)
                throw new Http2Exception("GOAWAY di " + _payload.Length + " byte invece di almeno 8");
            return (_payload[4] << 24) | (_payload[5] << 16) | (_payload[6] << 8) | _payload[7];
        }

        public int ReadGoAwayLastStreamId()
        {
            if (_type != Http2FrameType.GoAway) return 0;
            if (_payload.Length < 4) return 0;
            return ((_payload[0] & 0x7F) << 24) | (_payload[1] << 16) | (_payload[2] << 8) | _payload[3];
        }

        public int ReadRstStreamErrorCode()
        {
            if (_type != Http2FrameType.RstStream) return 0;
            if (_payload.Length < 4) return 0;
            return (_payload[0] << 24) | (_payload[1] << 16) | (_payload[2] << 8) | _payload[3];
        }

        public override string ToString()
        {
            return _type + " (stream " + _streamId.ToString(CultureInfo.InvariantCulture)
                   + ", flag " + _flags + ", " + _payload.Length + " byte)";
        }
    }
}
