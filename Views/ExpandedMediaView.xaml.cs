using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using wisland.Controls;
using wisland.Models;

namespace wisland.Views
{
    public sealed partial class ExpandedMediaView : UserControl
    {
        private readonly DirectionalContentTransitionCoordinator _metadataTransition;
        private readonly MetadataSnapshot[] _slotSnapshots = new MetadataSnapshot[2];
        private Color _mainColor = Microsoft.UI.Colors.White;
        private Color _subColor = Microsoft.UI.Colors.LightGray;

        public event EventHandler? BackClick;
        public event EventHandler? PlayPauseClick;
        public event EventHandler? ForwardClick;

        public ExpandedMediaView()
        {
            this.InitializeComponent();
            _metadataTransition = new DirectionalContentTransitionCoordinator(
                MetadataViewport,
                MetadataSlotPrimary,
                MetadataSlotSecondary,
                IslandConfig.ExpandedMediaTransitionProfile);

            _slotSnapshots[0] = ReadSnapshotFromSlot(0);
            _slotSnapshots[1] = ReadSnapshotFromSlot(1);

            Loaded += OnLoaded;
        }

        public bool Update(string title, string artist, string header, bool isPlaying, ContentTransitionDirection direction = ContentTransitionDirection.None)
        {
            Symbol playPauseSymbol = isPlaying ? Symbol.Pause : Symbol.Play;
            if (IconPlayPause.Symbol != playPauseSymbol)
            {
                IconPlayPause.Symbol = playPauseSymbol;
            }

            MetadataSnapshot nextSnapshot = new(title, artist, header);
            if (_slotSnapshots[_metadataTransition.ActiveSlotIndex].Equals(nextSnapshot))
            {
                return false;
            }

            if (direction == ContentTransitionDirection.None)
            {
                _metadataTransition.ApplyImmediately(slotIndex => ApplySnapshotToSlot(slotIndex, nextSnapshot));
                return true;
            }

            _metadataTransition.Transition(direction, slotIndex => ApplySnapshotToSlot(slotIndex, nextSnapshot));
            return true;
        }

        public void SetColors(Color main, Color sub, Color icon)
        {
            _mainColor = main;
            _subColor = sub;

            ApplyColorsToSlot(0);
            ApplyColorsToSlot(1);

            var iconBrush = new SolidColorBrush(icon);
            IconBack.Foreground = iconBrush;
            IconPlayPause.Foreground = iconBrush;
            IconForward.Foreground = iconBrush;
        }

        private void OnBack_Click(object sender, RoutedEventArgs e) => BackClick?.Invoke(this, EventArgs.Empty);
        private void OnPlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseClick?.Invoke(this, EventArgs.Empty);
        private void OnForward_Click(object sender, RoutedEventArgs e) => ForwardClick?.Invoke(this, EventArgs.Empty);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _metadataTransition.Initialize();
            _metadataTransition.UpdateViewportBounds();
        }

        private void MetadataViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _metadataTransition.UpdateViewportBounds();
        }

        private void ApplySnapshotToSlot(int slotIndex, MetadataSnapshot snapshot)
        {
            _slotSnapshots[slotIndex] = snapshot;

            TextBlock header = GetHeaderText(slotIndex);
            TextBlock title = GetTitleText(slotIndex);
            TextBlock artist = GetArtistText(slotIndex);

            header.Text = snapshot.Header;
            title.Text = snapshot.Title;
            artist.Text = snapshot.Artist;
            artist.Visibility = string.IsNullOrWhiteSpace(snapshot.Artist) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyColorsToSlot(int slotIndex)
        {
            GetTitleText(slotIndex).Foreground = new SolidColorBrush(_mainColor);
            SolidColorBrush subBrush = new(_subColor);
            GetArtistText(slotIndex).Foreground = subBrush;
            GetHeaderText(slotIndex).Foreground = subBrush;
        }

        private MetadataSnapshot ReadSnapshotFromSlot(int slotIndex)
        {
            return new MetadataSnapshot(
                GetTitleText(slotIndex).Text,
                GetArtistText(slotIndex).Text,
                GetHeaderText(slotIndex).Text);
        }

        private TextBlock GetHeaderText(int slotIndex)
            => slotIndex == 0 ? HeaderStatusTextPrimary : HeaderStatusTextSecondary;

        private TextBlock GetTitleText(int slotIndex)
            => slotIndex == 0 ? MusicTitleTextPrimary : MusicTitleTextSecondary;

        private TextBlock GetArtistText(int slotIndex)
            => slotIndex == 0 ? ArtistNameTextPrimary : ArtistNameTextSecondary;

        private readonly record struct MetadataSnapshot(string Title, string Artist, string Header);
    }
}
