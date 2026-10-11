using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Tailscale
{
    /// <summary>
    /// Pagina principale. In Task 12 del Piano 1 diventa la UI di stato
    /// (stato control, bottone di login, lista peer, log).
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // TODO (Task 12): avviare EngineService e popolare la UI.
        }
    }
}
