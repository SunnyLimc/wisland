using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using wisland.Helpers;
using wisland.Models;
using wisland.Services;

namespace wisland.Views
{
    public sealed partial class SessionPickerOverlayView : UserControl
    {
        private static readonly MediaSourceIconResolver IconResolver = new();
        private readonly ObservableCollection<SessionPickerRowVisualModel> _rows = new();
        private IReadOnlyList<SessionPickerRowModel> _currentModels = Array.Empty<SessionPickerRowModel>();
        private Color _primaryColor = Microsoft.UI.Colors.White;
        private Color _secondaryColor = Microsoft.UI.Colors.LightGray;
        private Color _surfaceColor = Color.FromArgb(236, 24, 28, 36);
        private long _iconLoadToken;

        public SessionPickerOverlayView()
        {
            this.InitializeComponent();
            SessionList.ItemsSource = _rows;
            UpdateListMaxHeight();
            ApplyPanelColors();
        }

        public event Action<string>? SessionSelected;

        public event EventHandler? DismissRequested;

        public event Action<SessionPickerOverlayLayoutMetrics>? LayoutMetricsChanged;

        public void SetColors(Color primary, Color secondary, Color surface)
        {
            _primaryColor = primary;
            _secondaryColor = secondary;
            _surfaceColor = surface;
            ApplyPanelColors();
            RebuildRows();
        }

        public void SetRows(IReadOnlyList<SessionPickerRowModel> models)
        {
            _currentModels = models;
            UpdateListMaxHeight();
            RebuildRows();
            QueueLayoutMetricsUpdate();
        }

        public Size MeasureDesiredSize()
        {
            double width = IslandConfig.SessionPickerOverlayWidth;
            double height = GetVisibleListHeight(_currentModels.Count)
                + (IslandConfig.SessionPickerOverlayPanelPadding * 2.0);

            return new Size(width, Math.Max(1.0, height));
        }

        public void FocusList()
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (SessionList.SelectedItem != null)
                {
                    SessionList.ScrollIntoView(SessionList.SelectedItem, ScrollIntoViewAlignment.Leading);
                }

                SessionList.Focus(FocusState.Programmatic);
            });
        }

        private void RebuildRows()
        {
            _iconLoadToken++;
            long token = _iconLoadToken;

            _rows.Clear();
            SessionPickerRowVisualModel? selectedItem = null;

            for (int i = 0; i < _currentModels.Count; i++)
            {
                SessionPickerRowVisualModel row = CreateVisualModel(_currentModels[i]);
                _rows.Add(row);

                if (row.IsSelected)
                {
                    selectedItem = row;
                }

                if (!string.IsNullOrWhiteSpace(row.SourceAppId))
                {
                    _ = LoadIconAsync(row, token);
                }
            }

            SessionList.SelectedItem = selectedItem;
        }

        private void QueueLayoutMetricsUpdate()
            => DispatcherQueue?.TryEnqueue(PublishLayoutMetrics);

        private void PublishLayoutMetrics()
        {
            if (TryBuildLayoutMetrics(out SessionPickerOverlayLayoutMetrics metrics))
            {
                LayoutMetricsChanged?.Invoke(metrics);
            }
        }

        private bool TryBuildLayoutMetrics(out SessionPickerOverlayLayoutMetrics metrics)
        {
            metrics = default;
            if (_currentModels.Count == 0)
            {
                return false;
            }

            UpdateLayout();

            int visibleCount = Math.Min(_currentModels.Count, IslandConfig.SessionPickerOverlayMaxVisibleItems);
            bool hasBounds = false;
            Rect unionBounds = default;

            for (int index = 0; index < visibleCount; index++)
            {
                if (SessionList.ContainerFromIndex(index) is not FrameworkElement container
                    || container.ActualWidth <= 0
                    || container.ActualHeight <= 0)
                {
                    return false;
                }

                Rect containerBounds = container
                    .TransformToVisual(RootGrid)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

                unionBounds = hasBounds
                    ? Union(unionBounds, containerBounds)
                    : containerBounds;
                hasBounds = true;
            }

            if (!hasBounds)
            {
                return false;
            }

            double inset = IslandConfig.SessionPickerOverlayPanelPadding;
            metrics = new SessionPickerOverlayLayoutMetrics(
                Width: Math.Max(IslandConfig.SessionPickerOverlayWidth, unionBounds.Width + (inset * 2.0)),
                Height: Math.Max(1.0, unionBounds.Height + (inset * 2.0)));
            return true;
        }

        private static Rect Union(Rect left, Rect right)
        {
            double x = Math.Min(left.X, right.X);
            double y = Math.Min(left.Y, right.Y);
            double maxX = Math.Max(left.X + left.Width, right.X + right.Width);
            double maxY = Math.Max(left.Y + left.Height, right.Y + right.Height);
            return new Rect(x, y, maxX - x, maxY - y);
        }

        private SessionPickerRowVisualModel CreateVisualModel(SessionPickerRowModel model)
        {
            Color borderColor = model.IsSelected
                ? Color.FromArgb(90, _secondaryColor.R, _secondaryColor.G, _secondaryColor.B)
                : Color.FromArgb(32, _secondaryColor.R, _secondaryColor.G, _secondaryColor.B);
            Color backgroundColor = model.IsSelected
                ? Color.FromArgb(34, _primaryColor.R, _primaryColor.G, _primaryColor.B)
                : Color.FromArgb(12, _primaryColor.R, _primaryColor.G, _primaryColor.B);
            Color badgeBackgroundColor = Color.FromArgb(
                model.IsSelected ? (byte)52 : (byte)28,
                _primaryColor.R,
                _primaryColor.G,
                _primaryColor.B);
            Color badgeBorderColor = Color.FromArgb(54, _secondaryColor.R, _secondaryColor.G, _secondaryColor.B);
            Color statusBackgroundColor = Color.FromArgb(
                model.IsSelected ? (byte)34 : (byte)18,
                _primaryColor.R,
                _primaryColor.G,
                _primaryColor.B);
            Color statusBorderColor = Color.FromArgb(42, _secondaryColor.R, _secondaryColor.G, _secondaryColor.B);

            return new SessionPickerRowVisualModel(
                model.SessionKey,
                model.SourceAppId,
                model.SourceName,
                model.Title,
                model.Subtitle,
                model.StatusText,
                model.IsSelected,
                SessionPickerRowProjector.ResolveMonogram(model.SourceName),
                new SolidColorBrush(backgroundColor),
                new SolidColorBrush(borderColor),
                new SolidColorBrush(badgeBackgroundColor),
                new SolidColorBrush(badgeBorderColor),
                new SolidColorBrush(_primaryColor),
                new SolidColorBrush(_secondaryColor),
                new SolidColorBrush(statusBackgroundColor),
                new SolidColorBrush(statusBorderColor));
        }

        private async Task LoadIconAsync(SessionPickerRowVisualModel row, long token)
        {
            try
            {
                ImageSource? icon = await IconResolver.ResolveAsync(row.SourceAppId);
                if (token != _iconLoadToken || icon == null)
                {
                    return;
                }

                row.IconSource = icon;
            }
            catch
            {
                // Keep the monogram fallback on icon resolution failure.
            }
        }

        private void ApplyPanelColors()
        {
            byte alpha = (byte)Math.Clamp(Math.Max((int)_surfaceColor.A, 228), 0, 255);
            Color backgroundColor = Color.FromArgb(alpha, _surfaceColor.R, _surfaceColor.G, _surfaceColor.B);
            Color borderColor = Color.FromArgb(72, _secondaryColor.R, _secondaryColor.G, _secondaryColor.B);
            RootGrid.Background = new SolidColorBrush(backgroundColor);
            HostSurface.Background = new SolidColorBrush(backgroundColor);
            PanelBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            PanelBorder.BorderBrush = new SolidColorBrush(borderColor);
        }

        private void UpdateListMaxHeight()
            => SessionList.MaxHeight = GetVisibleListHeight(IslandConfig.SessionPickerOverlayMaxVisibleItems)
                + (IslandConfig.SessionPickerOverlayPanelPadding * 2.0);

        private static double GetVisibleListHeight(int itemCount)
        {
            int visibleCount = Math.Clamp(itemCount, 0, IslandConfig.SessionPickerOverlayMaxVisibleItems);
            if (visibleCount == 0)
            {
                return 0;
            }

            return visibleCount * IslandConfig.SessionPickerOverlayRowHeight
                + (visibleCount * IslandConfig.SessionPickerOverlayItemSpacing);
        }

        private void SessionList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SessionPickerRowVisualModel row)
            {
                SessionSelected?.Invoke(row.SessionKey);
            }
        }

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
            => HandleListKeyDown(e);

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
            => QueueLayoutMetricsUpdate();

        private void SessionList_KeyDown(object sender, KeyRoutedEventArgs e)
            => HandleListKeyDown(e);

        private void HandleListKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                DismissRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (e.Key == VirtualKey.Enter
                && SessionList.SelectedItem is SessionPickerRowVisualModel row)
            {
                e.Handled = true;
                SessionSelected?.Invoke(row.SessionKey);
            }
        }

        private sealed class SessionPickerRowVisualModel : INotifyPropertyChanged
        {
            private ImageSource? _iconSource;

            public SessionPickerRowVisualModel(
                string sessionKey,
                string sourceAppId,
                string sourceName,
                string title,
                string subtitle,
                string statusText,
                bool isSelected,
                string monogram,
                Brush rowBackground,
                Brush rowBorderBrush,
                Brush badgeBackground,
                Brush badgeBorderBrush,
                Brush primaryForeground,
                Brush secondaryForeground,
                Brush statusBackground,
                Brush statusBorderBrush)
            {
                SessionKey = sessionKey;
                SourceAppId = sourceAppId;
                SourceName = sourceName;
                Title = title;
                Subtitle = subtitle;
                StatusText = statusText;
                IsSelected = isSelected;
                Monogram = monogram;
                RowBackground = rowBackground;
                RowBorderBrush = rowBorderBrush;
                BadgeBackground = badgeBackground;
                BadgeBorderBrush = badgeBorderBrush;
                PrimaryForeground = primaryForeground;
                SecondaryForeground = secondaryForeground;
                StatusBackground = statusBackground;
                StatusBorderBrush = statusBorderBrush;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string SessionKey { get; }

            public string SourceAppId { get; }

            public string SourceName { get; }

            public string Title { get; }

            public string Subtitle { get; }

            public string StatusText { get; }

            public bool IsSelected { get; }

            public string Monogram { get; }

            public Brush RowBackground { get; }

            public Brush RowBorderBrush { get; }

            public Brush BadgeBackground { get; }

            public Brush BadgeBorderBrush { get; }

            public Brush PrimaryForeground { get; }

            public Brush SecondaryForeground { get; }

            public Brush StatusBackground { get; }

            public Brush StatusBorderBrush { get; }

            public Brush StatusForeground => SecondaryForeground;

            public string SecondaryLine
                => string.IsNullOrWhiteSpace(Subtitle)
                    ? Title
                    : $"{Title} · {Subtitle}";

            public Visibility StatusVisibility
                => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;

            public Visibility IconVisibility
                => _iconSource == null ? Visibility.Collapsed : Visibility.Visible;

            public Visibility MonogramVisibility
                => _iconSource == null ? Visibility.Visible : Visibility.Collapsed;

            public ImageSource? IconSource
            {
                get => _iconSource;
                set
                {
                    if (ReferenceEquals(_iconSource, value))
                    {
                        return;
                    }

                    _iconSource = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IconVisibility));
                    OnPropertyChanged(nameof(MonogramVisibility));
                }
            }

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
