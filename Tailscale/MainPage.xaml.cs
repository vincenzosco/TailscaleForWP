using System;
using System.Collections.Generic;
using System.Text;
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
        private bool _loggedInHintShown;

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

        private void OnPrimaryButtonClick(object sender, RoutedEventArgs e)
        {
            ControlStatus status = _engine.Status;

            if (status.State == ControlState.NeedLogin)
            {
                bool opened = _engine.OpenLogin();
                NoBrowserHint.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
                Refresh();
                return;
            }

            if (status.State == ControlState.Authorized || status.State == ControlState.Connecting)
                _engine.Stop();
            else
                _engine.Start();

            Refresh();
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
            PrimaryButton.Content = resources.GetString(needLogin ? "LoginButton/Content" : "StartButton/Content");
            LoginHint.Visibility = needLogin ? Visibility.Visible : Visibility.Collapsed;

            if (needLogin && !_loggedInHintShown)
            {
                _loggedInHintShown = true;
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
