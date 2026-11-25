using System;
using System.Collections.Generic;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Net.Ip
{
    /// <summary>Le dipendenze di TcpConnection, tutte iniettate: niente orologio ne' rete dentro il core.</summary>
    public sealed class TcpConnectionOptions
    {
        /// <summary>L'indirizzo IPv4 del tailnet di questo terminatore.</summary>
        public uint SourceIp;

        /// <summary>Per la porta sorgente e l'ISN.</summary>
        public IRandomSource Random;

        /// <summary>L'attesa (iniettata: su WP8.1 il ciclo resta sincrono).</summary>
        public Action<int> Sleep;

        /// <summary>L'orologio (iniettato): i deadline passano da qui.</summary>
        public Func<DateTime> Clock;

        /// <summary>Per spedire il pacchetto IP verso il tunnel.</summary>
        public Action<byte[]> SendIpPacket;

        public int PollMilliseconds = 5;
    }

    /// <summary>
    /// Una connessione TCP in uscita, RFC 793 essenziale: SYN/SYN-ACK/ACK,
    /// stop-and-wait con retransmission a timeout raddoppiato (200 ms fino a
    /// 6 s), finestra di ricezione 64 KB, PSH e URG trattati come ACK, niente
    /// SACK. Il tempo e il sonno arrivano dalle opzioni: il core non legge
    /// l'orologio e non dorme da solo.
    /// </summary>
    public sealed class TcpConnection
    {
        private enum State
        {
            Closed,
            SynSent,
            Established,
            CloseWait,
            FinWait1
        }

        private sealed class Inflight
        {
            public uint Seq;
            public byte Flags;
            public byte[] Payload;
            public DateTime RetransmitAt;
            public int DelayMs;
        }

        private const int Mss = IpV4.TunnelMtu - 40;
        private const int AdvertisedWindow = 65535;
        private const int RetransmitStartMs = 200;
        private const int RetransmitMaxMs = 6000;
        private const int WriteTimeoutMs = 30000;

        private readonly object _gate = new object();
        private readonly TcpConnectionOptions _options;

        private State _state = State.Closed;
        private bool _reset;
        private bool _finReceived;
        private bool _finSent;

        private uint _dstIp;
        private ushort _dstPort;
        private ushort _srcPort;

        private uint _sndNxt;
        private uint _sndUna;
        private uint _rcvNxt;
        private int _peerWindow = AdvertisedWindow;

        private readonly List<byte> _toSend = new List<byte>();
        private readonly List<byte> _recvQueue = new List<byte>();
        private Inflight _inflight;

        /// <summary>Il prossimo contatore di invio: diagnostica e test.</summary>
        public uint SendNxt
        {
            get { lock (_gate) return _sndNxt; }
        }

        public bool IsConnected
        {
            get { lock (_gate) return _state == State.Established; }
        }

        public bool IsClosed
        {
            get { lock (_gate) return _state == State.Closed; }
        }

        /// <summary>Scatta quando la connessione muore (RST o chiusura completa).</summary>
        public event Action Closed;

        public TcpConnection(TcpConnectionOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (options.Random == null) throw new ArgumentException("serve una sorgente casuale", "options");
            if (options.Sleep == null) throw new ArgumentException("serve una funzione di attesa", "options");
            if (options.Clock == null) throw new ArgumentException("serve un orologio", "options");
            if (options.SendIpPacket == null) throw new ArgumentException("serve il canale verso il tunnel", "options");
            _options = options;
        }

        /// <summary>
        /// Apre la connessione e aspetta l'handshake. TimeoutException se non
        /// risponde entro il tempo; InvalidOperationException se il peer
        /// risponde con RST.
        /// </summary>
        public void Connect(uint dstIp, ushort dstPort, TimeSpan timeout)
        {
            byte[] portSeed = new byte[2];
            byte[] isnSeed = new byte[4];
            _options.Random.NextBytes(portSeed);
            _options.Random.NextBytes(isnSeed);

            DateTime deadline = _options.Clock() + timeout;
            lock (_gate)
            {
                if (_state != State.Closed)
                    throw new InvalidOperationException("la connessione e' gia' aperta");
                _reset = false;
                _finReceived = false;
                _finSent = false;
                _dstIp = dstIp;
                _dstPort = dstPort;
                _srcPort = (ushort)(40000 + (((portSeed[0] << 8) | portSeed[1]) % 20000));
                uint isn = ((uint)isnSeed[0] << 24) | ((uint)isnSeed[1] << 16)
                         | ((uint)isnSeed[2] << 8) | isnSeed[3];
                _sndUna = isn;
                _sndNxt = isn + 1;
                _rcvNxt = 0;
                _state = State.SynSent;

                Inflight syn = new Inflight();
                syn.Seq = isn;
                syn.Flags = TcpSegment.FlagSyn;
                syn.Payload = new byte[0];
                _inflight = syn;
                ResetRetransmitTimer(syn);
                SendSegment(syn.Seq, syn.Flags, syn.Payload);
            }

            while (true)
            {
                lock (_gate)
                {
                    if (_state == State.Established) return;
                    if (_state != State.SynSent)
                        throw new InvalidOperationException("connessione rifiutata (RST dal peer)");
                    TickLocked();
                }
                if (_options.Clock() >= deadline)
                    throw new TimeoutException("handshake TCP scaduto verso " + dstIp + ":" + dstPort);
                _options.Sleep(_options.PollMilliseconds);
            }
        }

        /// <summary>
        /// Spedisce i dati: ogni byte viene messo almeno una volta sul filo e
        /// il retransmit pensa alle conferme. Per i dati oltre il segmento
        /// aspetta gli ack del peer per mandare avanti la coda.
        /// TimeoutException a tempo scaduto; InvalidOperationException se la
        /// connessione e' morta.
        /// </summary>
        public void Write(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            DateTime deadline = _options.Clock() + TimeSpan.FromMilliseconds(WriteTimeoutMs);
            lock (_gate)
            {
                RequireWritable();
                for (int i = 0; i < data.Length; i++) _toSend.Add(data[i]);
            }

            while (true)
            {
                lock (_gate)
                {
                    RequireWritable();
                    PumpLocked();
                    if (_toSend.Count == 0) return;
                }
                if (_options.Clock() >= deadline)
                    throw new TimeoutException("scrittura TCP scaduta dopo " + WriteTimeoutMs + " ms");
                _options.Sleep(_options.PollMilliseconds);
            }
        }

        /// <summary>
        /// Legge cio' che c'e', anche parziale. 0 = fine del flusso (FIN).
        /// TimeoutException se non arriva nulla entro il tempo;
        /// InvalidOperationException se la connessione e' stata interrotta.
        /// </summary>
        public int Read(byte[] buffer, TimeSpan timeout)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (buffer.Length == 0) return 0;
            DateTime deadline = _options.Clock() + timeout;
            while (true)
            {
                lock (_gate)
                {
                    if (_reset)
                        throw new InvalidOperationException("connessione interrotta (RST dal peer)");
                    if (_recvQueue.Count > 0)
                    {
                        int count = Math.Min(buffer.Length, _recvQueue.Count);
                        for (int i = 0; i < count; i++) buffer[i] = _recvQueue[i];
                        _recvQueue.RemoveRange(0, count);
                        return count;
                    }
                    if (_finReceived) return 0;
                }
                if (_options.Clock() >= deadline)
                    throw new TimeoutException("lettura TCP scaduta dopo " + timeout.TotalMilliseconds + " ms");
                _options.Sleep(_options.PollMilliseconds);
            }
        }

        /// <summary>Chiude: spedisce FIN e, alla conferma, segnala Closed.</summary>
        public void Close()
        {
            lock (_gate)
            {
                if (_state == State.Closed) return;
                if (!_finSent)
                {
                    _finSent = true;
                    Inflight fin = new Inflight();
                    fin.Seq = _sndNxt;
                    fin.Flags = TcpSegment.FlagFin | TcpSegment.FlagAck;
                    fin.Payload = new byte[0];
                    _sndNxt++;
                    _inflight = fin;
                    ResetRetransmitTimer(fin);
                    SendSegment(fin.Seq, fin.Flags, fin.Payload);
                    _state = _finReceived ? State.Closed : State.FinWait1;
                }
                if (_state == State.Closed) RaiseClosed();
            }
        }

        /// <summary>
        /// Il tunnel chiama qui con i pacchetti IP in ingresso. Tutto cio' che
        /// non e' per questa connessione o non si apre viene scartato senza
        /// cambiare stato.
        /// </summary>
        public void OnIpPacket(byte[] ipPacket)
        {
            lock (_gate)
            {
                if (_state == State.Closed) return;
                IpV4Header header;
                byte[] tcpBytes;
                if (!IpV4.Parse(ipPacket, out header, out tcpBytes)) return;
                if (header.Protocol != 6) return;
                if (IpV4.ToUInt32(header.Src) != _dstIp) return;
                if (IpV4.ToUInt32(header.Dst) != _options.SourceIp) return;

                TcpSegment seg;
                if (!TcpSegment.TryParse(tcpBytes, _options.SourceIp, _dstIp, out seg)) return;
                if (seg == null) return;
                if (seg.SrcPort != _dstPort || seg.DstPort != _srcPort) return;

                _peerWindow = seg.Window;

                if ((seg.Flags & TcpSegment.FlagRst) != 0)
                {
                    _reset = true;
                    _state = State.Closed;
                    _toSend.Clear();
                    _inflight = null;
                    RaiseClosed();
                    return;
                }

                if ((seg.Flags & TcpSegment.FlagAck) != 0)
                {
                    // Solo in avanti: un ack duplicato non fa avanzare nulla.
                    if (seg.Ack > _sndUna && seg.Ack <= _sndNxt)
                    {
                        _sndUna = seg.Ack;
                        if (_sndUna == _sndNxt) _inflight = null;
                    }
                }

                if (_state == State.SynSent)
                {
                    if ((seg.Flags & (TcpSegment.FlagSyn | TcpSegment.FlagAck))
                            == (TcpSegment.FlagSyn | TcpSegment.FlagAck)
                        && seg.Ack == _sndNxt)
                    {
                        _rcvNxt = seg.Seq + 1;
                        _state = State.Established;
                        SendSegment(_sndNxt, TcpSegment.FlagAck, new byte[0]);
                    }
                    return;
                }

                if (seg.Payload.Length > 0)
                {
                    if (seg.Seq == _rcvNxt)
                    {
                        for (int i = 0; i < seg.Payload.Length; i++) _recvQueue.Add(seg.Payload[i]);
                        _rcvNxt += (uint)seg.Payload.Length;
                        SendSegment(_sndNxt, TcpSegment.FlagAck, new byte[0]);
                    }
                    else
                    {
                        // Fuori ordine o duplicato: si riconferma, senza reassembly.
                        SendSegment(_sndNxt, TcpSegment.FlagAck, new byte[0]);
                    }
                }

                if ((seg.Flags & TcpSegment.FlagFin) != 0 && seg.Seq == _rcvNxt)
                {
                    _rcvNxt++;
                    _finReceived = true;
                    SendSegment(_sndNxt, TcpSegment.FlagAck, new byte[0]);
                    if (_finSent || _state == State.FinWait1)
                    {
                        _state = State.Closed;
                        _inflight = null;
                        RaiseClosed();
                    }
                    else
                    {
                        _state = State.CloseWait;
                    }
                }
            }
        }

        /// <summary>Pompa i retransmit: il proprietario lo chiama, il timer non e' mio.</summary>
        public void Tick()
        {
            lock (_gate) TickLocked();
        }

        private void TickLocked()
        {
            if (_inflight == null) return;
            if (_options.Clock() < _inflight.RetransmitAt) return;
            SendSegment(_inflight.Seq, _inflight.Flags, _inflight.Payload);
            _inflight.DelayMs = Math.Min(_inflight.DelayMs * 2, RetransmitMaxMs);
            _inflight.RetransmitAt = _options.Clock() + TimeSpan.FromMilliseconds(_inflight.DelayMs);
        }

        private void PumpLocked()
        {
            if (_inflight != null) return;
            if (_toSend.Count == 0) return;
            if (_peerWindow == 0) return; // la SendWindow del peer si rispetta

            int length = Math.Min(Mss, _toSend.Count);
            byte[] payload = new byte[length];
            for (int i = 0; i < length; i++) payload[i] = _toSend[i];
            _toSend.RemoveRange(0, length);

            Inflight inflight = new Inflight();
            inflight.Seq = _sndNxt;
            inflight.Flags = TcpSegment.FlagAck | TcpSegment.FlagPsh;
            inflight.Payload = payload;
            _sndNxt += (uint)length;
            _inflight = inflight;
            ResetRetransmitTimer(inflight);
            SendSegment(inflight.Seq, inflight.Flags, inflight.Payload);
        }

        private void ResetRetransmitTimer(Inflight inflight)
        {
            inflight.DelayMs = RetransmitStartMs;
            inflight.RetransmitAt = _options.Clock() + TimeSpan.FromMilliseconds(inflight.DelayMs);
        }

        private void SendSegment(uint seq, byte flags, byte[] payload)
        {
            TcpSegment seg = new TcpSegment();
            seg.SrcPort = _srcPort;
            seg.DstPort = _dstPort;
            seg.Seq = seq;
            seg.Ack = _rcvNxt;
            seg.Flags = flags;
            seg.Window = AdvertisedWindow;
            seg.Payload = payload;
            byte[] tcp = TcpSegment.Encode(seg, _options.SourceIp, _dstIp);
            byte[] ip = IpV4.Build(IpV4.ToBytes(_options.SourceIp), IpV4.ToBytes(_dstIp), 6, tcp);
            _options.SendIpPacket(ip);
        }

        private void RequireWritable()
        {
            if (_reset)
                throw new InvalidOperationException("connessione interrotta (RST dal peer)");
            if (_state != State.Established && _state != State.CloseWait)
                throw new InvalidOperationException("la connessione non e' stabilita");
        }

        private void RaiseClosed()
        {
            Action handler = Closed;
            if (handler != null) handler();
        }
    }
}
