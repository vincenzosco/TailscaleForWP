using System;
using System.Collections.Generic;
using System.Text;
using Tailscale.Core;
using Tailscale.Core.Control;
using Tailscale.Core.Tailcfg;
using Tailscale.Services;
using Windows.ApplicationModel.Resources;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Tailscale
{
    /// <summary>
    /// La pagina principale: stato, una sola azione, i dispositivi, il registro.
    ///
    /// Una sola azione perche' e' quello che serve davvero: se il nodo non e'
    /// autorizzato si accede, altrimenti si avvia o si ferma. Due pulsanti sempre
    /// visibili di cui uno non ha senso in quel momento sono un modo per far
    /// sbagliare l'utente.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly EngineService _engine = EngineService.Current;

        /// <summary>
        /// L'ultimo URL di login per cui il browser e' stato aperto da solo. Si tiene
        /// l'URL e non un semplice "gia' avvisato": un motore riavviato (ritorno in
        /// primo piano, o tocco dopo un arresto) riporta lo stesso "Accesso
        /// richiesto", e l'utente deve rivedere la pagina di accesso senza dover
        /// sapere che il motore dietro e' nuovo.
        /// </summary>
        private string _openedLoginUrl;

        public MainPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Le notifiche arrivano dal thread del motore: la UI si tocca solo sul
            // proprio thread.
            _engine.Changed += OnEngineChanged;
            _engine.Start();
            Refresh();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _engine.Changed -= OnEngineChanged;
            base.OnNavigatedFrom(e);
        }

        private void OnEngineChanged()
        {
            CoreDispatcher dispatcher = Dispatcher;
            if (dispatcher == null) return;

            // Fire-and-forget voluto: se la pagina non c'e' piu' l'aggiornamento si
            // perde, ed e' esattamente quello che deve succedere. Il risultato si
            // assegna a una variabile buttata via solo per non far avvisare il
            // compilatore (CS4014): l'attesa qui non avrebbe senso.
            var notAwaited = dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                                                 delegate() { Refresh(); });
        }

        /// <summary>
        /// Cosa fa il pulsante lo decide una funzione sola, la stessa che sceglie la
        /// sua etichetta (vedi <see cref="LabelKey"/> e <c>PrimaryButtonPolicy</c> nel
        /// core). Prima erano due condizioni scritte due volte, in due punti di questa
        /// pagina: l'etichetta diceva "Avvia" in ogni stato tranne il login, mentre in
        /// <c>Connecting</c> e <c>Authorized</c> l'azione era un arresto. Il motore
        /// pero' parte da solo in <c>OnNavigatedTo</c>, quindi lo stato che l'utente
        /// trova e' proprio quello: il tocco con cui credeva di collegarsi lo
        /// scollegava, e nel registro non restava nessun errore -- solo
        /// `log:arresto del motore` e lo stato fermo (visto sul telefono il
        /// 2026-10-12).
        /// </summary>
        private void OnPrimaryButtonClick(object sender, RoutedEventArgs e)
        {
            switch (Decide())
            {
                case PrimaryAction.Login:
                    bool opened = _engine.OpenLogin();
                    NoBrowserHint.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
                    break;

                case PrimaryAction.Restart:
                    // Un motore fermo non ha nessun URL da riaprire: il tocco lo
                    // rimette in moto. E' il caso visto sul telefono il 2026-10-11:
                    // il ritorno in primo piano arrivava mentre il ciclo precedente
                    // stava uscendo, l'avvio veniva rifiutato, e qui il pulsante
                    // apriva solo il browser: l'app restava su "Accesso richiesto"
                    // con il motore spento. Il browser si riapre da solo appena il
                    // ciclo nuovo ha un URL (quello salvato e' legato all'identita'
                    // vecchia, che il server non riconosce piu').
                    _engine.Start();
                    NoBrowserHint.Visibility = Visibility.Collapsed;
                    break;

                case PrimaryAction.Stop:
                    _engine.Stop();
                    break;

                default:
                    _engine.Start();
                    break;
            }

            Refresh();
        }

        /// <summary>
        /// Il collegamento "VPN via interop" si attiva e si disattiva da qui: un
        /// tocco solo, e tutto il giro -- scrittura nel registro, rilettura che
        /// conferma, stato aggiornato -- lo fa il servizio. Il motore lo attiva da
        /// solo quando si collega, se l'interruttore e' acceso: e' il "tutto in
        /// una volta" chiesto.
        /// </summary>
        private void OnVpnButtonClick(object sender, RoutedEventArgs e)
        {
            _engine.Vpn.Toggle();
            Refresh();
        }

        /// <summary>La decisione sul pulsante: la regola sta nel core, con il suo test.</summary>
        private PrimaryAction Decide()
        {
            return PrimaryButtonPolicy.Decide(_engine.Status.State, _engine.IsStarted);
        }

        private void Refresh()
        {
            ResourceLoader resources = ResourceLoader.GetForCurrentView();
            ControlStatus status = _engine.Status;

            DeviceText.Text = EngineService.Current.DeviceName;

            string statusKey = StatusResourceKey(status.State);
            StatusText.Text = resources.GetString(statusKey);
            if (status.State == ControlState.Error && !string.IsNullOrEmpty(status.ErrorMessage))
                StatusText.Text = resources.GetString(statusKey) + ": " + status.ErrorMessage;

            bool needLogin = status.State == ControlState.NeedLogin;
            // L'etichetta viene dall'azione decisa: cosi' non puo' dire "Avvia"
            // mentre il tocco ferma il motore.
            PrimaryButton.Content = resources.GetString(LabelKey(Decide()));
            LoginHint.Visibility = needLogin ? Visibility.Visible : Visibility.Collapsed;

            // Il collegamento via interop: lo stato viene riletto dal registro
            // (e' il servizio che lo aggiorna), il pulsante dice cosa fa il tocco.
            VpnText.Text = resources.GetString(VpnStatusKey(_engine.Vpn.State));
            VpnButton.Content = resources.GetString(
                _engine.Vpn.State == VpnLinkState.On
                    ? "VpnDisconnectButton/Content"
                    : "VpnConnectButton/Content");

            // Il browser si apre da solo una volta per URL: quando il login e' ancora
            // in sospeso l'URL resta lo stesso, e riaprirlo a ogni ridisegno sarebbe
            // un assalto; quando invece il motore e' ripartito e l'URL e' tornato
            // disponibile, la pagina di accesso si riapre da sola.
            if (!needLogin) _openedLoginUrl = null;
            else if (!string.IsNullOrEmpty(status.LoginUrl) && status.LoginUrl != _openedLoginUrl)
            {
                _openedLoginUrl = status.LoginUrl;
                _engine.OpenLogin();
            }

            FillPeers(resources);
            FillLog();
        }

        private void FillPeers(ResourceLoader resources)
        {
            NetworkMap netmap = _engine.Netmap;
            List<PeerRow> rows = new List<PeerRow>();

            for (int i = 0; i < netmap.Peers.Count; i++)
            {
                NodeInfo peer = netmap.Peers[i];
                string address = peer.Addresses != null && peer.Addresses.Count > 0 ? peer.Addresses[0] : "";
                string state = peer.Online ? resources.GetString("PeerOnline") : resources.GetString("PeerOffline");

                StringBuilder detail = new StringBuilder();
                detail.Append(peer.OS == null ? "" : peer.OS);
                if (address.Length > 0)
                {
                    if (detail.Length > 0) detail.Append(" - ");
                    detail.Append(address);
                }
                if (detail.Length > 0) detail.Append(" - ");
                detail.Append(state);

                rows.Add(new PeerRow(peer.Hostname, detail.ToString()));
            }

            PeerList.ItemsSource = rows;

            // Nelle risorse la chiave e' "PeersHeader.Text", ma ResourceLoader la
            // espone come "PeersHeader/Text": il punto separa il controllo dalla
            // proprieta' e dal codice diventa una barra.
            string header = resources.GetString("PeersHeader/Text");
            if (netmap.Peers.Count > 0)
                header = header + " (" + string.Format(resources.GetString("PeerCountFormat"), netmap.Peers.Count) + ")";
            PeersHeader.Text = header;
        }

        private void FillLog()
        {
            string[] lines = _engine.Log;
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) text.Append("\n");
                text.Append(lines[i]);
            }
            LogText.Text = text.ToString();
        }

        /// <summary>
        /// L'etichetta del pulsante, dall'azione che il tocco compie: "Ferma" quando
        /// ferma il motore, "Accedi" quando apre la pagina di accesso, "Avvia" quando
        /// avvia. Tutte e tre esistono in entrambe le lingue.
        ///
        /// Per il riavvio resta "Accedi": il motore che si rimette in moto e' un
        /// passaggio interno, quello che l'utente vede un istante dopo e' il browser
        /// che si riapre con il nuovo URL.
        /// </summary>
        private static string LabelKey(PrimaryAction action)
        {
            switch (action)
            {
                case PrimaryAction.Stop: return "StopButton/Content";
                case PrimaryAction.Login:
                case PrimaryAction.Restart: return "LoginButton/Content";
                default: return "StartButton/Content";
            }
        }

        private static string StatusResourceKey(ControlState state)
        {
            switch (state)
            {
                case ControlState.Connecting: return "Status_Connecting";
                case ControlState.NeedLogin: return "Status_NeedLogin";
                case ControlState.Authorized: return "Status_Authorized";
                case ControlState.Error: return "Status_Error";
                default: return "Status_Stopped";
            }
        }

        /// <summary>
        /// La riga di stato del collegamento via interop, dallo stato che il
        /// servizio ha riletto nel registro. Tutte le voci esistono in entrambe le
        /// lingue: il controllo sulle risorse le elenca fra le chiavi usate dal
        /// codice.
        /// </summary>
        private static string VpnStatusKey(VpnLinkState state)
        {
            switch (state)
            {
                case VpnLinkState.On: return "Vpn_Status_On";
                case VpnLinkState.Off: return "Vpn_Status_Off";
                case VpnLinkState.Locked: return "Vpn_Status_Locked";
                case VpnLinkState.Unknown: return "Vpn_Status_Unknown";
                case VpnLinkState.Failed: return "Vpn_Status_Failed";
                default: return "Vpn_Status_NotConfigured";
            }
        }
    }

    /// <summary>
    /// Una riga della lista: due testi, cosi' il template XAML resta banale e non
    /// serve un converter per comporre l'etichetta.
    /// </summary>
    public sealed class PeerRow
    {
        public PeerRow(string title, string detail)
        {
            Title = title == null ? "" : title;
            Detail = detail == null ? "" : detail;
        }

        public string Title { get; private set; }
        public string Detail { get; private set; }
    }
}
