using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using System;

namespace island.Views
{
    public sealed partial class ExpandedMediaView : UserControl
    {
        public event EventHandler? BackClick;
        public event EventHandler? PlayPauseClick;
        public event EventHandler? ForwardClick;

        public ExpandedMediaView()
        {
            this.InitializeComponent();
        }

        public void Update(string title, string artist, string header, bool isPlaying)
        {
            MusicTitleText.Text = title;
            ArtistNameText.Text = artist;
            HeaderStatusText.Text = header;
            ArtistNameText.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;
            IconPlayPause.Symbol = isPlaying ? Symbol.Pause : Symbol.Play;
        }

        public void SetColors(Color main, Color sub, Color icon)
        {
            MusicTitleText.Foreground = new SolidColorBrush(main);
            ArtistNameText.Foreground = new SolidColorBrush(sub);
            HeaderStatusText.Foreground = new SolidColorBrush(sub);
            IconBack.Foreground = new SolidColorBrush(icon);
            IconPlayPause.Foreground = new SolidColorBrush(icon);
            IconForward.Foreground = new SolidColorBrush(icon);
        }

        private void OnBack_Click(object sender, RoutedEventArgs e) => BackClick?.Invoke(this, EventArgs.Empty);
        private void OnPlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseClick?.Invoke(this, EventArgs.Empty);
        private void OnForward_Click(object sender, RoutedEventArgs e) => ForwardClick?.Invoke(this, EventArgs.Empty);
    }
}
