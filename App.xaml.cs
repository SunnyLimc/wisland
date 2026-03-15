using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace island
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
            File.WriteAllText("app_crash_log.txt", $"Unhandled Exception: {e.Exception}\n{e.Message}");
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                _window = new MainWindow();
                _window.Activate();
            }
            catch (Exception ex)
            {
                File.WriteAllText("onlaunched_crash_log.txt", ex.ToString());
                throw;
            }
        }
    }
}
