using System;

namespace Tailscale.Core
{
    /// <summary>
    /// Il ciclo del motore visto da fuori: uno solo per volta, e il motore si libera
    /// quando il ciclo esce.
    ///
    /// Esiste per un guasto preciso, visto sul telefono: il ciclo del client esce da
    /// solo (budget del canale esaurito: la netmap non e' mai arrivata, o la
    /// registrazione non e' passata), ma il flag "avviato" che l'app teneva per conto
    /// suo non veniva mai spento. In quello stato il pulsante principale non faceva
    /// piu' nulla -- chiamava un avvio che usciva subito -- e l'app restava ferma
    /// finche' non la si ammazzava. La regola sta qui, e non nell'app, perche' e' una
    /// decisione: si prova senza telefono, e chi chiama non puo' dimenticarsene.
    ///
    /// Il `finally` e' dentro il core per la stessa ragione: un ciclo che esce per
    /// un'eccezione non prevista deve lasciare il motore riavviabile, non bloccato.
    /// </summary>
    public sealed class EngineRunState
    {
        private readonly object _gate = new object();
        private bool _running;

        /// <summary>Vero mentre un ciclo del motore e' in corso. Si legge da un altro thread.</summary>
        public bool IsRunning
        {
            get { lock (_gate) return _running; }
        }

        /// <summary>
        /// Esegue il ciclo del motore, se nessun altro ciclo sta girando.
        /// Restituisce false quando un motore e' gia' in moto: chi chiama non deve
        /// avviarne un secondo, perche' due client sullo stesso stato si
        /// contenderebbero la stessa chiave e il tailnet vedrebbe due dispositivi.
        /// </summary>
        public bool Run(Action body)
        {
            if (body == null) throw new ArgumentNullException("body");

            lock (_gate)
            {
                if (_running) return false;
                _running = true;
            }

            try
            {
                body();
            }
            finally
            {
                lock (_gate) _running = false;
            }

            return true;
        }
    }
}
