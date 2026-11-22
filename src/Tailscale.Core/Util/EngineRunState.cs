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
    ///
    /// E la richiesta di avvio che arriva mentre il ciclo precedente sta uscendo non
    /// va perduta: `RunWhenFree` aspetta che il posto si liberi, invece di rispondere
    /// di no a un utente che ha appena chiesto di ripartire.
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

        /// <summary>
        /// Esegue il ciclo del motore aspettando, se serve, che il posto si liberi.
        ///
        /// Un ciclo che sta **uscendo** -- arresto chiesto adesso, oppure budget
        /// esaurito -- tiene il posto ancora per poco. E' la finestra in cui il
        /// telefono ha fatto perdere una richiesta di avvio: il ritorno in primo piano
        /// arrivava mentre il ciclo precedente era in uscita, l'avvio veniva rifiutato
        /// e la richiesta spariva; in stato "Accesso richiesto" il pulsante apre solo
        /// il browser, quindi da li' il motore non ripartiva piu'. Qui la richiesta
        /// aspetta invece di perdersi.
        ///
        /// Restituisce false, come <see cref="Run"/>, se dopo i tentativi previsti il
        /// motore e' ancora in moto: in quel caso il rifiuto resta la risposta giusta,
        /// perche' due client sullo stesso stato si contenderebbero la stessa chiave.
        /// </summary>
        public bool RunWhenFree(Action body, Action<int> wait, int attempts, int waitMilliseconds)
        {
            if (body == null) throw new ArgumentNullException("body");
            // Senza un modo di aspettare non c'e' niente da riprovare: la risposta
            // immediata resta quella di Run, e non si inventa un'attesa qui dentro
            // (il telefono non ha Thread.Sleep: chi ospita decide come aspettare).
            if (wait == null || attempts < 1) return Run(body);

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (Run(body)) return true;
                if (attempt + 1 < attempts) wait(waitMilliseconds);
            }

            return false;
        }
    }
}
