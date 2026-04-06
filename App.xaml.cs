using Microsoft.UI.Xaml;
using System;
using wisland.Helpers;
using wisland.Services;

namespace wisland
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            this.InitializeComponent();
            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Logger.Error(e.Exception, $"Unhandled exception: {e.Message}");
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                Logger.Info("Wisland application starting");

                var settings = new SettingsService();
                settings.Load();
                Loc.Initialize(settings.Language);

                _window = new MainWindow();
                _window.Activate();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Fatal error during OnLaunched");
                throw;
            }
        }
    }
}
