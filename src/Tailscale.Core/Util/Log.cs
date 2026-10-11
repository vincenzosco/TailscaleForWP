using System;
using System.Collections.Generic;

namespace Tailscale.Core
{
    /// <summary>
    /// Sink di log del motore. Le chiavi private non vengono mai passate qui:
    /// la responsabilità di non loggare segreti è di chi chiama, e i test dei
    /// task successivi lo verificano.
    /// </summary>
    public interface ILogSink
    {
        void Log(string line);
    }

    public sealed class NullLogSink : ILogSink
    {
        public static readonly NullLogSink Instance = new NullLogSink();
        public void Log(string line) { }
    }

    public sealed class DelegateLogSink : ILogSink
    {
        private readonly Action<string> _write;

        public DelegateLogSink(Action<string> write)
        {
            if (write == null) throw new ArgumentNullException("write");
            _write = write;
        }

        public void Log(string line)
        {
            if (line == null) return;
            _write(line);
        }
    }

    /// <summary>
    /// Tiene solo le ultime N righe: è quello che la UI del telefono mostra,
    /// senza far crescere la memoria di un'app che resta aperta per giorni.
    /// </summary>
    public sealed class RingLogSink : ILogSink
    {
        private readonly object _gate = new object();
        private readonly Queue<string> _lines;
        private readonly int _capacity;

        public RingLogSink(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity");
            _capacity = capacity;
            _lines = new Queue<string>(capacity);
        }

        public void Log(string line)
        {
            if (line == null) return;
            lock (_gate)
            {
                if (_lines.Count == _capacity) _lines.Dequeue();
                _lines.Enqueue(line);
            }
        }

        public string[] Snapshot()
        {
            lock (_gate) return _lines.ToArray();
        }
    }
}
