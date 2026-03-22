using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace wisland.Views
{
    public sealed partial class CompactView : UserControl
    {
        public CompactView()
        {
            this.InitializeComponent();
        }

        public string Text
        {
            get => CompactText.Text;
            set => CompactText.Text = value;
        }

        public void SetTextColor(Color color)
        {
            CompactText.Foreground = new SolidColorBrush(color);
        }
    }
}
