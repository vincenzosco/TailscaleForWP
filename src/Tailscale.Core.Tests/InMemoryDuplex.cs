using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Due stream collegati in memoria, con letture bloccanti come quelle di un
    /// socket: servono a esercitare l'handshake su due thread senza toccare la rete.
    /// </summary>
    public static class InMemoryDuplex
    {
        public static void Create(out Stream client, out Stream server)
        {
            Channel clientToServer = new Channel();
            Channel serverToClient = new Channel();
            client = new ChannelStream(clientToServer, serverToClient);
            server = new ChannelStream(serverToClient, clientToServer);
        }

        private sealed class Channel
        {
            private readonly object _gate = new object();
            private readonly List<byte> _data = new List<byte>();
            private int _readPosition;
            private bool _closed;

            public void Write(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    for (int i = 0; i < count; i++) _data.Add(buffer[offset + i]);
                    Monitor.PulseAll(_gate);
                }
            }

            /// <summary>Blocca finche' c'e' almeno un byte, o finche' il canale non viene chiuso.</summary>
            public int Read(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    while (_readPosition >= _data.Count && !_closed) Monitor.Wait(_gate);
                    if (_readPosition >= _data.Count) return 0;
                    int available = Math.Min(count, _data.Count - _readPosition);
                    for (int i = 0; i < available; i++) buffer[offset + i] = _data[_readPosition + i];
                    _readPosition += available;
                    return available;
                }
            }

            public void Close()
            {
                lock (_gate)
                {
                    _closed = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private sealed class ChannelStream : Stream
        {
            private readonly Channel _inbound;
            private readonly Channel _outbound;

            public ChannelStream(Channel inbound, Channel outbound)
            {
                _inbound = inbound;
                _outbound = outbound;
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
                return _inbound.Read(buffer, offset, count);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _outbound.Write(buffer, offset, count);
            }

            /// <summary>
            /// Chiude entrambe le direzioni: serve quando un lato fallisce e l'altro
            /// resterebbe bloccato in lettura per sempre.
            /// </summary>
            public override void Close()
            {
                _inbound.Close();
                _outbound.Close();
            }
        }
    }

    /// <summary>
    /// Stream che inverte un bit all'offset scelto, per verificare che ogni byte
    /// del messaggio sia davvero autenticato (come tamperReader di handshake_test.go).
    /// </summary>
    public sealed class TamperingStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _target;
        private int _total;

        public TamperingStream(Stream inner, int target)
        {
            _inner = inner;
            _target = target;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { _inner.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            int at = _target - _total;
            if (at >= 0 && at < read) buffer[offset + at] ^= 0x01;
            _total += read;
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        public override void Close()
        {
            _inner.Close();
        }
    }

    /// <summary>
    /// Sorgente casuale deterministica per i test: la sequenza 0, 1, 2, ... a 8 bit.
    /// Non ha alcuna pretesa di sicurezza crittografica ed esiste solo nei test.
    /// </summary>
    public sealed class SequenceRandomSource : IRandomSource
    {
        private byte _next;

        public SequenceRandomSource()
        {
        }

        public SequenceRandomSource(byte start)
        {
            _next = start;
        }

        public void NextBytes(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++) buffer[i] = _next++;
        }
    }
}
