using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading.Tasks;
using wisland.Models;
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

            var testItem = new MenuFlyoutItem { Text = "Test Notification" };
            testItem.Click += TestNotification_Click;
            menu.Items.Add(testItem);

            var testProgressItem = new MenuFlyoutItem { Text = "Test Task Progress" };
            testProgressItem.Click += TestProgress_Click;
            menu.Items.Add(testProgressItem);

            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(CreateBackdropSubMenu());
            menu.Items.Add(new MenuFlyoutSeparator());

            var exitItem = new MenuFlyoutItem { Text = "Exit" };
            exitItem.Click += Exit_Click;
            menu.Items.Add(exitItem);

            return menu;
        }

        private MenuFlyoutSubItem CreateBackdropSubMenu()
        {
            var backdropSub = new MenuFlyoutSubItem { Text = "Backdrop Style" };
            backdropSub.Items.Add(CreateBackdropMenuItem("Mica", BackdropType.Mica, Mica_Click));
            backdropSub.Items.Add(CreateBackdropMenuItem("Acrylic", BackdropType.Acrylic, Acrylic_Click));
            backdropSub.Items.Add(CreateBackdropMenuItem("None", BackdropType.None, None_Click));
            return backdropSub;
        }

        private MenuFlyoutItem CreateBackdropMenuItem(string text, BackdropType type, RoutedEventHandler handler)
        {
            var item = new MenuFlyoutItem { Text = text };
            if (_currentBackdropType == type)
            {
                item.Icon = new SymbolIcon(Symbol.Accept);
            }

            item.Click += handler;
            return item;
        }

        private void ShowIsland_Click(object sender, RoutedEventArgs e)
        {
            this.Activate();
            this.SetForegroundWindow();
        }

        private void TestNotification_Click(object sender, RoutedEventArgs e)
            => ShowNotification("Wisland", "Flawless Physics!", header: "Test Notification");

        private async void TestProgress_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i <= 100; i += 5)
            {
                SetTaskProgress(i / 100.0);
                await Task.Delay(200);
            }

            ClearTaskProgress();
        }

        private void Mica_Click(object sender, RoutedEventArgs e) => SetBackdrop(BackdropType.Mica);
        private void Acrylic_Click(object sender, RoutedEventArgs e) => SetBackdrop(BackdropType.Acrylic);
        private void None_Click(object sender, RoutedEventArgs e) => SetBackdrop(BackdropType.None);
        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();
    }
}
