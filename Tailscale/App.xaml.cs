using System;
using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Tailscale
{
    /// <summary>
    /// Comportamento specifico dell'applicazione. Traduzione 1:1 del template VB.
    /// </summary>
    sealed partial class App : Application
    {
        private TransitionCollection _transitions;

        public App()
        {
            this.InitializeComponent();
            // In VB l'aggancio era dichiarativo (Handles Me.Suspending).
            this.Suspending += OnSuspending;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
#if DEBUG
            if (Debugger.IsAttached)
            {
                DebugSettings.EnableFrameRateCounter = true;
            }
#endif
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.CacheSize = 1;

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    // TODO: caricare lo stato di una sessione precedente.
                }

                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
            {
                // Rimuove la transizione "turnstile" all'avvio.
                if (rootFrame.ContentTransitions != null)
                {
                    _transitions = new TransitionCollection();
                    foreach (Transition transition in rootFrame.ContentTransitions)
                    {
                        _transitions.Add(transition);
                    }
                }

                rootFrame.ContentTransitions = null;
                rootFrame.Navigated += RootFrame_FirstNavigated;

                if (!rootFrame.Navigate(typeof(MainPage), e.Arguments))
                {
                    throw new Exception("Failed to create initial page");
                }
            }

            Window.Current.Activate();
        }

        private void RootFrame_FirstNavigated(object sender, NavigationEventArgs e)
        {
            TransitionCollection newTransitions;
            if (_transitions == null)
            {
                newTransitions = new TransitionCollection();
                newTransitions.Add(new NavigationThemeTransition());
            }
            else
            {
                newTransitions = _transitions;
            }

            Frame rootFrame = (Frame)sender;
            rootFrame.ContentTransitions = newTransitions;
            rootFrame.Navigated -= RootFrame_FirstNavigated;
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            SuspendingDeferral deferral = e.SuspendingOperation.GetDeferral();

            // Task 11 del Piano 1 / Task 11 del Piano 2: qui va salvato lo stato
            // e vanno chiusi i socket. Un socket lungo in sospensione viene
            // comunque terminato dal sistema.

            deferral.Complete();
        }
    }
}
