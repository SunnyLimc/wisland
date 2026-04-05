using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private MenuFlyout CreateTrayMenu()
        {
            var menu = new MenuFlyout();

            var showItem = new MenuFlyoutItem { Text = "Show Wisland" };
            showItem.Click += ShowIsland_Click;
            menu.Items.Add(showItem);

            var settingsItem = new MenuFlyoutItem { Text = "Settings" };
            settingsItem.Click += Settings_Click;
            menu.Items.Add(settingsItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var exitItem = new MenuFlyoutItem { Text = "Exit" };
            exitItem.Click += Exit_Click;
            menu.Items.Add(exitItem);

            return menu;
        }

        private void ShowIsland_Click(object sender, RoutedEventArgs e)
        {
            this.Activate();
            this.SetForegroundWindow();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
            => OpenSettingsWindow();

        private void Exit_Click(object sender, RoutedEventArgs e) => RequestAppExit();
    }
}
