using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using wisland.Controls;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Views
{
    public sealed partial class ExpandedMediaView : UserControl
    {
        private static readonly MediaSourceIconResolver IconResolver = new();

        private readonly DirectionalContentTransitionCoordinator _metadataTransition;
        private readonly DirectionalContentTransitionCoordinator _headerTransition;
        private readonly MetadataSnapshot[] _metadataSnapshots = new MetadataSnapshot[2];
        private readonly HeaderSnapshot[] _headerSnapshots = new HeaderSnapshot[2];
        private readonly Border[,] _headerAvatarBorders;
        private readonly Image[,] _headerAvatarImages;
        private readonly TextBlock[,] _headerAvatarFallbacks;
        private readonly Rectangle[] _headerOverflowFades;
        private readonly TextBlock[] _headerLabels;
        private readonly FontIcon[] _headerExpandGlyphs;
        private readonly long[,] _avatarLoadTokens = new long[2, 3];

        private Color _mainColor = Microsoft.UI.Colors.White;
        private Color _subColor = Microsoft.UI.Colors.LightGray;
        private Color _iconColor = Microsoft.UI.Colors.White;
        private string? _selectedSessionKey;
        private IReadOnlyList<MediaSessionSnapshot> _pickerSessions = Array.Empty<MediaSessionSnapshot>();

        public event EventHandler? BackClick;
        public event EventHandler? PlayPauseClick;
        public event EventHandler? ForwardClick;
        public event Action<string>? SessionSelected;

        public ExpandedMediaView()
        {
            this.InitializeComponent();

            _metadataTransition = new DirectionalContentTransitionCoordinator(
                MetadataViewport,
                MetadataSlotPrimary,
                MetadataSlotSecondary,
                IslandConfig.ExpandedMediaTransitionProfile);

            _headerTransition = new DirectionalContentTransitionCoordinator(
                HeaderContentViewport,
                HeaderSlotPrimary,
                HeaderSlotSecondary,
                IslandConfig.HeaderChipTransitionProfile);

            _headerAvatarBorders = new[,]
            {
                { HeaderAvatarPrimary0, HeaderAvatarPrimary1, HeaderAvatarPrimary2 },
                { HeaderAvatarSecondary0, HeaderAvatarSecondary1, HeaderAvatarSecondary2 }
            };

            _headerAvatarImages = new[,]
            {
                { HeaderAvatarPrimary0Image, HeaderAvatarPrimary1Image, HeaderAvatarPrimary2Image },
                { HeaderAvatarSecondary0Image, HeaderAvatarSecondary1Image, HeaderAvatarSecondary2Image }
            };

            _headerAvatarFallbacks = new[,]
            {
                { HeaderAvatarPrimary0Fallback, HeaderAvatarPrimary1Fallback, HeaderAvatarPrimary2Fallback },
                { HeaderAvatarSecondary0Fallback, HeaderAvatarSecondary1Fallback, HeaderAvatarSecondary2Fallback }
            };

            _headerOverflowFades = new[]
            {
                HeaderAvatarOverflowFadePrimary,
                HeaderAvatarOverflowFadeSecondary
            };

            _headerLabels = new[]
            {
                HeaderLabelPrimary,
                HeaderLabelSecondary
            };

            _headerExpandGlyphs = new[]
            {
                HeaderExpandGlyphPrimary,
                HeaderExpandGlyphSecondary
            };

            _metadataSnapshots[0] = ReadMetadataSnapshotFromSlot(0);
            _metadataSnapshots[1] = ReadMetadataSnapshotFromSlot(1);
            _headerSnapshots[0] = ReadHeaderSnapshotFromSlot(0);
            _headerSnapshots[1] = ReadHeaderSnapshotFromSlot(1);

            SessionPickerList.MaxHeight = IslandConfig.SessionPickerMaxVisibleItems * IslandConfig.SessionPickerEstimatedRowHeight;
            Loaded += OnLoaded;
        }

        public bool IsSessionPickerOpen { get; private set; }

        public bool UpdateMedia(
            MediaSessionSnapshot? session,
            int displayIndex,
            int sessionCount,
            IReadOnlyList<MediaSessionSnapshot> availableSessions,
            ContentTransitionDirection direction = ContentTransitionDirection.None)
        {
            UpdatePlayPauseSymbol(session.HasValue && session.Value.IsPlaying);
            UpdateSessionPickerItems(availableSessions, session?.SessionKey);

            HeaderSnapshot nextHeaderSnapshot = CreateMediaHeaderSnapshot(session, displayIndex, availableSessions);
            MetadataSnapshot nextMetadataSnapshot = session.HasValue
                ? new MetadataSnapshot(
                    session.Value.Title,
                    session.Value.Artist)
                : new MetadataSnapshot(
                    Title: "No Media",
                    Artist: "Waiting for music...");

            bool headerChanged = ApplyHeaderSnapshot(nextHeaderSnapshot, direction);
            bool metadataChanged = ApplyMetadataSnapshot(nextMetadataSnapshot, direction);
            return headerChanged || metadataChanged;
        }

        public void ShowNotification(string title, string message, string header)
        {
            HeaderSnapshot nextHeaderSnapshot = new(
                Label: header,
                ShowExpandHint: false,
                ShowOverflowFade: false,
                First: HeaderAvatarSnapshot.Hidden,
                Second: HeaderAvatarSnapshot.Hidden,
                Third: HeaderAvatarSnapshot.Hidden);
            MetadataSnapshot nextMetadataSnapshot = new(title, message);

            ApplyHeaderSnapshot(nextHeaderSnapshot, ContentTransitionDirection.None);
            ApplyMetadataSnapshot(nextMetadataSnapshot, ContentTransitionDirection.None);
        }

        public void SetColors(Color main, Color sub, Color icon)
        {
            _mainColor = main;
            _subColor = sub;
            _iconColor = icon;

            ApplyMetadataColors();
            ApplyTransportIconColors();
            ApplyHeaderColors();
            RefreshSessionPickerItemColors();
        }

        private bool ApplyHeaderSnapshot(HeaderSnapshot snapshot, ContentTransitionDirection direction)
        {
            if (_headerSnapshots[_headerTransition.ActiveSlotIndex].Equals(snapshot))
            {
                return false;
            }

            if (direction == ContentTransitionDirection.None)
            {
                _headerTransition.ApplyImmediately(slotIndex => ApplyHeaderSnapshotToSlot(slotIndex, snapshot));
                return true;
            }

            _headerTransition.Transition(direction, slotIndex => ApplyHeaderSnapshotToSlot(slotIndex, snapshot));
            return true;
        }

        private bool ApplyMetadataSnapshot(MetadataSnapshot snapshot, ContentTransitionDirection direction)
        {
            if (_metadataSnapshots[_metadataTransition.ActiveSlotIndex].Equals(snapshot))
            {
                return false;
            }

            if (direction == ContentTransitionDirection.None)
            {
                _metadataTransition.ApplyImmediately(slotIndex => ApplyMetadataSnapshotToSlot(slotIndex, snapshot));
                return true;
            }

            _metadataTransition.Transition(direction, slotIndex => ApplyMetadataSnapshotToSlot(slotIndex, snapshot));
            return true;
        }

        private void OnBack_Click(object sender, RoutedEventArgs e) => BackClick?.Invoke(this, EventArgs.Empty);
        private void OnPlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseClick?.Invoke(this, EventArgs.Empty);
        private void OnForward_Click(object sender, RoutedEventArgs e) => ForwardClick?.Invoke(this, EventArgs.Empty);

        private void OnSessionHeader_Click(object sender, RoutedEventArgs e)
        {
            if (SessionPickerList.Items.Count > 1)
            {
                SessionPickerFlyout.ShowAt(SessionHeaderButton);
            }
        }

        private void SessionPickerFlyout_Opening(object sender, object e)
            => IsSessionPickerOpen = true;

        private void SessionPickerFlyout_Closed(object sender, object e)
            => IsSessionPickerOpen = false;

        private void SessionPickerList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ListViewItem item && item.Tag is string sessionKey)
            {
                SessionSelected?.Invoke(sessionKey);
                SessionPickerFlyout.Hide();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _metadataTransition.Initialize();
            _metadataTransition.UpdateViewportBounds();
            _headerTransition.Initialize();
            _headerTransition.UpdateViewportBounds();
            ApplyMetadataColors();
            ApplyTransportIconColors();
            ApplyHeaderColors();
            RefreshSessionPickerItemColors();
        }

        private void MetadataViewport_SizeChanged(object sender, SizeChangedEventArgs e)
            => _metadataTransition.UpdateViewportBounds();

        private void HeaderContentViewport_SizeChanged(object sender, SizeChangedEventArgs e)
            => _headerTransition.UpdateViewportBounds();

        private void UpdateSessionPickerItems(IReadOnlyList<MediaSessionSnapshot> sessions, string? selectedSessionKey)
        {
            _pickerSessions = sessions;
            _selectedSessionKey = selectedSessionKey;
            SessionPickerList.Items.Clear();
            ListViewItem? selectedItem = null;

            for (int i = 0; i < sessions.Count; i++)
            {
                MediaSessionSnapshot session = sessions[i];
                bool isSelected = string.Equals(session.SessionKey, selectedSessionKey, StringComparison.Ordinal);
                ListViewItem item = BuildSessionPickerItem(session, isSelected);
                SessionPickerList.Items.Add(item);

                if (isSelected)
                {
                    selectedItem = item;
                }
            }

            SessionPickerList.SelectedItem = selectedItem;
        }

        private ListViewItem BuildSessionPickerItem(MediaSessionSnapshot session, bool isSelected)
        {
            Border monogramBadge = new()
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(26, _mainColor.R, _mainColor.G, _mainColor.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(56, _subColor.R, _subColor.G, _subColor.B)),
                Child = new TextBlock
                {
                    Text = GetSourceMonogram(session.SourceName),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(_mainColor)
                }
            };

            StackPanel textStack = new()
            {
                Spacing = 2
            };

            textStack.Children.Add(new TextBlock
            {
                Text = session.SourceName,
                FontSize = 11,
                Foreground = new SolidColorBrush(_subColor),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            textStack.Children.Add(new TextBlock
            {
                Text = session.Title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_mainColor),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            textStack.Children.Add(new TextBlock
            {
                Text = session.Artist,
                FontSize = 11,
                Foreground = new SolidColorBrush(_subColor),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = string.IsNullOrWhiteSpace(session.Artist) ? Visibility.Collapsed : Visibility.Visible
            });

            TextBlock statusText = new()
            {
                Text = GetPlaybackStatusLabel(session),
                FontSize = 11,
                Foreground = new SolidColorBrush(_subColor),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12, 0, 0, 0)
            };

            Grid content = new()
            {
                Padding = new Thickness(12, 10, 12, 10)
            };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(monogramBadge);
            Grid.SetColumn(monogramBadge, 0);

            Border textHost = new()
            {
                Child = textStack,
                Margin = new Thickness(10, 0, 0, 0)
            };
            content.Children.Add(textHost);
            Grid.SetColumn(textHost, 1);

            content.Children.Add(statusText);
            Grid.SetColumn(statusText, 2);

            return new ListViewItem
            {
                Tag = session.SessionKey,
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsSelected = isSelected
            };
        }

        private void RefreshSessionPickerItemColors()
            => UpdateSessionPickerItems(_pickerSessions, _selectedSessionKey);

        private HeaderSnapshot CreateMediaHeaderSnapshot(
            MediaSessionSnapshot? session,
            int displayIndex,
            IReadOnlyList<MediaSessionSnapshot> availableSessions)
        {
            if (!session.HasValue)
            {
                return new HeaderSnapshot(
                    Label: "Wisland",
                    ShowExpandHint: false,
                    ShowOverflowFade: false,
                    First: HeaderAvatarSnapshot.Hidden,
                    Second: HeaderAvatarSnapshot.Hidden,
                    Third: HeaderAvatarSnapshot.Hidden);
            }

            HeaderAvatarSnapshot[] avatars = GetVisibleHeaderAvatars(availableSessions, displayIndex);
            return new HeaderSnapshot(
                Label: GetHeaderLabel(session.Value),
                ShowExpandHint: availableSessions.Count > 1,
                ShowOverflowFade: availableSessions.Count > 3,
                First: avatars[0],
                Second: avatars[1],
                Third: avatars[2]);
        }

        private HeaderAvatarSnapshot[] GetVisibleHeaderAvatars(
            IReadOnlyList<MediaSessionSnapshot> sessions,
            int displayIndex)
        {
            HeaderAvatarSnapshot[] avatars =
            {
                HeaderAvatarSnapshot.Hidden,
                HeaderAvatarSnapshot.Hidden,
                HeaderAvatarSnapshot.Hidden
            };

            if (sessions.Count == 0)
            {
                return avatars;
            }

            int startIndex = displayIndex >= 0 && displayIndex < sessions.Count ? displayIndex : 0;
            int visibleCount = Math.Min(3, sessions.Count);
            for (int i = 0; i < visibleCount; i++)
            {
                MediaSessionSnapshot session = sessions[(startIndex + i) % sessions.Count];
                avatars[i] = new HeaderAvatarSnapshot(
                    session.SessionKey,
                    session.SourceAppId,
                    session.SourceName,
                    true);
            }

            return avatars;
        }

        private void ApplyHeaderSnapshotToSlot(int slotIndex, HeaderSnapshot snapshot)
        {
            _headerSnapshots[slotIndex] = snapshot;
            _headerLabels[slotIndex].Text = snapshot.Label;
            _headerExpandGlyphs[slotIndex].Visibility = snapshot.ShowExpandHint
                ? Visibility.Visible
                : Visibility.Collapsed;
            _headerOverflowFades[slotIndex].Visibility = snapshot.ShowOverflowFade ? Visibility.Visible : Visibility.Collapsed;
            ApplyAvatarToSlot(slotIndex, 0, snapshot.First);
            ApplyAvatarToSlot(slotIndex, 1, snapshot.Second);
            ApplyAvatarToSlot(slotIndex, 2, snapshot.Third);
        }

        private void ApplyMetadataSnapshotToSlot(int slotIndex, MetadataSnapshot snapshot)
        {
            _metadataSnapshots[slotIndex] = snapshot;
            TextBlock title = GetTitleText(slotIndex);
            TextBlock artist = GetArtistText(slotIndex);

            title.Text = snapshot.Title;
            artist.Text = snapshot.Artist;
            artist.Visibility = string.IsNullOrWhiteSpace(snapshot.Artist) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyAvatarToSlot(int slotIndex, int avatarIndex, HeaderAvatarSnapshot avatar)
        {
            Border border = _headerAvatarBorders[slotIndex, avatarIndex];
            Image image = _headerAvatarImages[slotIndex, avatarIndex];
            TextBlock fallback = _headerAvatarFallbacks[slotIndex, avatarIndex];

            _avatarLoadTokens[slotIndex, avatarIndex]++;
            image.Source = null;
            image.Visibility = Visibility.Collapsed;

            if (!avatar.IsVisible)
            {
                border.Visibility = Visibility.Collapsed;
                fallback.Visibility = Visibility.Collapsed;
                fallback.Text = string.Empty;
                return;
            }

            border.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Visible;
            fallback.Text = GetSourceMonogram(avatar.SourceName);

            if (!string.IsNullOrWhiteSpace(avatar.SourceAppId))
            {
                long token = _avatarLoadTokens[slotIndex, avatarIndex];
                _ = LoadAvatarIconAsync(slotIndex, avatarIndex, avatar.SourceAppId, token);
            }
        }

        private async System.Threading.Tasks.Task LoadAvatarIconAsync(
            int slotIndex,
            int avatarIndex,
            string sourceAppId,
            long token)
        {
            ImageSource? icon = await IconResolver.ResolveAsync(sourceAppId);
            if (_avatarLoadTokens[slotIndex, avatarIndex] != token || icon == null)
            {
                return;
            }

            Image image = _headerAvatarImages[slotIndex, avatarIndex];
            TextBlock fallback = _headerAvatarFallbacks[slotIndex, avatarIndex];
            image.Source = icon;
            image.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Collapsed;
        }

        private void ApplyMetadataColors()
        {
            SolidColorBrush mainBrush = new(_mainColor);
            SolidColorBrush subBrush = new(_subColor);
            MusicTitleTextPrimary.Foreground = mainBrush;
            MusicTitleTextSecondary.Foreground = mainBrush;
            ArtistNameTextPrimary.Foreground = subBrush;
            ArtistNameTextSecondary.Foreground = subBrush;
        }

        private void ApplyTransportIconColors()
        {
            SolidColorBrush iconBrush = new(_iconColor);
            IconBack.Foreground = iconBrush;
            IconPlayPause.Foreground = iconBrush;
            IconForward.Foreground = iconBrush;
        }

        private void ApplyHeaderColors()
        {
            Color borderColor = Color.FromArgb(54, _subColor.R, _subColor.G, _subColor.B);
            Color backgroundColor = Color.FromArgb(20, _mainColor.R, _mainColor.G, _mainColor.B);
            Color avatarFillColor = Color.FromArgb(34, _mainColor.R, _mainColor.G, _mainColor.B);
            Color avatarBorderColor = Color.FromArgb(68, _subColor.R, _subColor.G, _subColor.B);

            HeaderChipBorder.BorderBrush = new SolidColorBrush(borderColor);
            HeaderChipBorder.Background = new SolidColorBrush(backgroundColor);
            SessionPickerBorder.BorderBrush = new SolidColorBrush(borderColor);
            SessionPickerBorder.Background = new SolidColorBrush(backgroundColor);
            HeaderLabelPrimary.Foreground = new SolidColorBrush(_subColor);
            HeaderLabelSecondary.Foreground = new SolidColorBrush(_subColor);
            SolidColorBrush expandGlyphBrush = new(new Color { A = 204, R = _subColor.R, G = _subColor.G, B = _subColor.B });
            HeaderExpandGlyphPrimary.Foreground = expandGlyphBrush;
            HeaderExpandGlyphSecondary.Foreground = expandGlyphBrush;

            for (int slotIndex = 0; slotIndex < 2; slotIndex++)
            {
                for (int avatarIndex = 0; avatarIndex < 3; avatarIndex++)
                {
                    _headerAvatarBorders[slotIndex, avatarIndex].Background = new SolidColorBrush(avatarFillColor);
                    _headerAvatarBorders[slotIndex, avatarIndex].BorderBrush = new SolidColorBrush(avatarBorderColor);
                    _headerAvatarBorders[slotIndex, avatarIndex].BorderThickness = new Thickness(1);
                    _headerAvatarFallbacks[slotIndex, avatarIndex].Foreground = new SolidColorBrush(_mainColor);
                }

                _headerOverflowFades[slotIndex].Fill = CreateOverflowFadeBrush(backgroundColor);
            }
        }

        private void UpdatePlayPauseSymbol(bool isPlaying)
        {
            Symbol playPauseSymbol = isPlaying ? Symbol.Pause : Symbol.Play;
            if (IconPlayPause.Symbol != playPauseSymbol)
            {
                IconPlayPause.Symbol = playPauseSymbol;
            }
        }

        private MetadataSnapshot ReadMetadataSnapshotFromSlot(int slotIndex)
            => new(
                GetTitleText(slotIndex).Text,
                GetArtistText(slotIndex).Text);

        private HeaderSnapshot ReadHeaderSnapshotFromSlot(int slotIndex)
            => new(
                _headerLabels[slotIndex].Text,
                ShowExpandHint: _headerExpandGlyphs[slotIndex].Visibility == Visibility.Visible,
                ShowOverflowFade: false,
                First: HeaderAvatarSnapshot.Hidden,
                Second: HeaderAvatarSnapshot.Hidden,
                Third: HeaderAvatarSnapshot.Hidden);

        private TextBlock GetTitleText(int slotIndex)
            => slotIndex == 0 ? MusicTitleTextPrimary : MusicTitleTextSecondary;

        private TextBlock GetArtistText(int slotIndex)
            => slotIndex == 0 ? ArtistNameTextPrimary : ArtistNameTextSecondary;

        private static string GetHeaderLabel(MediaSessionSnapshot session)
            => session.PlaybackStatus switch
            {
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Now Playing",
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Paused",
                _ => "Media"
            };

        private static string GetSourceMonogram(string sourceName)
        {
            foreach (char c in sourceName)
            {
                if (char.IsLetterOrDigit(c))
                {
                    return char.ToUpperInvariant(c).ToString();
                }
            }

            return "M";
        }

        private static string GetPlaybackStatusLabel(MediaSessionSnapshot session)
            => session.PlaybackStatus switch
            {
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Playing",
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Paused",
                _ => "Idle"
            };

        private static Brush CreateOverflowFadeBrush(Color backgroundColor)
        {
            LinearGradientBrush brush = new()
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(0, backgroundColor.R, backgroundColor.G, backgroundColor.B),
                Offset = 0
            });
            brush.GradientStops.Add(new GradientStop
            {
                Color = backgroundColor,
                Offset = 1
            });
            return brush;
        }

        private readonly record struct MetadataSnapshot(string Title, string Artist);

        private readonly record struct HeaderSnapshot(
            string Label,
            bool ShowExpandHint,
            bool ShowOverflowFade,
            HeaderAvatarSnapshot First,
            HeaderAvatarSnapshot Second,
            HeaderAvatarSnapshot Third);

        private readonly record struct HeaderAvatarSnapshot(
            string SessionKey,
            string SourceAppId,
            string SourceName,
            bool IsVisible)
        {
            public static HeaderAvatarSnapshot Hidden { get; } = new(string.Empty, string.Empty, string.Empty, false);
        }
    }
}
