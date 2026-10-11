using System;

namespace Tailscale.Core
{
    /// <summary>
    /// Una riga di registro a piu' destinazioni: sul telefono quella che si vede
    /// (le ultime N in memoria) e quella che resta (il file).
    ///
    /// Un sink che lancia viene isolato: il registro a schermo deve continuare a
    /// funzionare anche quando il file non e' scrivibile e viceversa. Un registro
    /// che sparisce perche' una delle sue destinazioni e' rotta e' peggio di un
    /// registro mancante, perche' fa credere che non sia successo niente.
    /// </summary>
    public sealed class FanoutLogSink : ILogSink
    {
        private readonly ILogSink[] _sinks;

        public FanoutLogSink(params ILogSink[] sinks)
        {
            _sinks = sinks == null ? new ILogSink[0] : (ILogSink[])sinks.Clone();
        }

        public void Log(string line)
        {
            if (line == null) return;

            for (int i = 0; i < _sinks.Length; i++)
            {
                ILogSink sink = _sinks[i];
                if (sink == null) continue;
                try
                {
                    sink.Log(line);
                }
                catch (Exception)
                {
                    // Volutamente muto: il motivo lo dice il sink stesso (per il
                    // file lo fa TailLogFile, una volta sola). Qui l'unica cosa da
                    // garantire e' che gli altri sink ricevano la riga.
                }
            }
        }
    }
}
