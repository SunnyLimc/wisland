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
            string? preferredSelectionKey = (SessionList.SelectedItem as SessionPickerRowVisualModel)?.SessionKey;

            _rows.Clear();
            SessionPickerRowVisualModel? selectedItem = null;

            for (int i = 0; i < _currentModels.Count; i++)
            {
                SessionPickerRowVisualModel row = CreateVisualModel(_currentModels[i]);
                _rows.Add(row);

                if (!string.IsNullOrWhiteSpace(preferredSelectionKey)
                    && string.Equals(row.SessionKey, preferredSelectionKey, StringComparison.Ordinal))
                {
                    selectedItem = row;
                }
                else if (selectedItem == null && row.IsSelected)
                {
                    selectedItem = row;
                }

                if (!string.IsNullOrWhiteSpace(row.SourceAppId))
                {
                    _ = LoadIconAsync(row, token);
                }
            }

            SessionList.SelectedItem = selectedItem;
            UpdateRowSelectionVisuals(selectedItem);
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
            SessionPickerRowVisualModel row = new(
                model.SessionKey,
                model.SourceAppId,
                model.SourceName,
                model.Title,
                model.Subtitle,
                model.StatusText,
                SessionPickerRowProjector.ResolveMonogram(model.SourceName),
                new SolidColorBrush(_primaryColor),
                new SolidColorBrush(_secondaryColor));
            row.ApplySelectionState(model.IsSelected, _primaryColor, _secondaryColor);
            return row;
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

        private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateRowSelectionVisuals(SessionList.SelectedItem as SessionPickerRowVisualModel);

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

        private void UpdateRowSelectionVisuals(SessionPickerRowVisualModel? selectedRow)
        {
            foreach (SessionPickerRowVisualModel row in _rows)
            {
                row.ApplySelectionState(
                    ReferenceEquals(row, selectedRow),
                    _primaryColor,
                    _secondaryColor);
            }
        }

        private sealed class SessionPickerRowVisualModel : INotifyPropertyChanged
        {
            private ImageSource? _iconSource;
            private bool _isSelected;
            private Brush _rowBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowHoverBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowHoverBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowPressBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowPressBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowFocusBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _badgeBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _badgeBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _statusBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _statusBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            public SessionPickerRowVisualModel(
                string sessionKey,
                string sourceAppId,
                string sourceName,
                string title,
                string subtitle,
                string statusText,
                string monogram,
                Brush primaryForeground,
                Brush secondaryForeground)
            {
                SessionKey = sessionKey;
                SourceAppId = sourceAppId;
                SourceName = sourceName;
                Title = title;
                Subtitle = subtitle;
                StatusText = statusText;
                Monogram = monogram;
                PrimaryForeground = primaryForeground;
                SecondaryForeground = secondaryForeground;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string SessionKey { get; }

            public string SourceAppId { get; }

            public string SourceName { get; }

            public string Title { get; }

            public string Subtitle { get; }

            public string StatusText { get; }

            public bool IsSelected
            {
                get => _isSelected;
                private set
                {
                    if (_isSelected == value)
                    {
                        return;
                    }

                    _isSelected = value;
                    OnPropertyChanged();
                }
            }

            public string Monogram { get; }

            public Brush RowBackground
            {
                get => _rowBackground;
                private set => SetBrush(ref _rowBackground, value);
            }

            public Brush RowBorderBrush
            {
                get => _rowBorderBrush;
                private set => SetBrush(ref _rowBorderBrush, value);
            }

            public Brush RowHoverBackground
            {
                get => _rowHoverBackground;
                private set => SetBrush(ref _rowHoverBackground, value);
            }

            public Brush RowHoverBorderBrush
            {
                get => _rowHoverBorderBrush;
                private set => SetBrush(ref _rowHoverBorderBrush, value);
            }

            public Brush RowPressBackground
            {
                get => _rowPressBackground;
                private set => SetBrush(ref _rowPressBackground, value);
            }

            public Brush RowPressBorderBrush
            {
                get => _rowPressBorderBrush;
                private set => SetBrush(ref _rowPressBorderBrush, value);
            }

            public Brush RowFocusBorderBrush
            {
                get => _rowFocusBorderBrush;
                private set => SetBrush(ref _rowFocusBorderBrush, value);
            }

            public Brush BadgeBackground
            {
                get => _badgeBackground;
                private set => SetBrush(ref _badgeBackground, value);
            }

            public Brush BadgeBorderBrush
            {
                get => _badgeBorderBrush;
                private set => SetBrush(ref _badgeBorderBrush, value);
            }

            public Brush PrimaryForeground { get; }

            public Brush SecondaryForeground { get; }

            public Brush StatusBackground
            {
                get => _statusBackground;
                private set => SetBrush(ref _statusBackground, value);
            }

            public Brush StatusBorderBrush
            {
                get => _statusBorderBrush;
                private set => SetBrush(ref _statusBorderBrush, value);
            }

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

            public void ApplySelectionState(bool isSelected, Color primaryColor, Color secondaryColor)
            {
                InteractionSurfacePalette rowPalette = InteractionSurfacePalette.Create(
                    primaryColor,
                    secondaryColor,
                    isSelected ? (byte)34 : (byte)12,
                    isSelected ? (byte)90 : (byte)32,
                    isSelected ? (byte)46 : (byte)18,
                    isSelected ? (byte)108 : (byte)62,
                    isSelected ? (byte)60 : (byte)28,
                    isSelected ? (byte)120 : (byte)76,
                    140);

                IsSelected = isSelected;
                RowBackground = new SolidColorBrush(rowPalette.BackgroundColor);
                RowBorderBrush = new SolidColorBrush(rowPalette.BorderColor);
                RowHoverBackground = new SolidColorBrush(rowPalette.HoverBackgroundColor);
                RowHoverBorderBrush = new SolidColorBrush(rowPalette.HoverBorderColor);
                RowPressBackground = new SolidColorBrush(rowPalette.PressBackgroundColor);
                RowPressBorderBrush = new SolidColorBrush(rowPalette.PressBorderColor);
                RowFocusBorderBrush = new SolidColorBrush(rowPalette.FocusRingColor);
                BadgeBackground = new SolidColorBrush(WithAlpha(primaryColor, isSelected ? (byte)52 : (byte)28));
                BadgeBorderBrush = new SolidColorBrush(WithAlpha(secondaryColor, 54));
                StatusBackground = new SolidColorBrush(WithAlpha(primaryColor, isSelected ? (byte)34 : (byte)18));
                StatusBorderBrush = new SolidColorBrush(WithAlpha(secondaryColor, 42));
            }

            private static Color WithAlpha(Color color, byte alpha)
                => Color.FromArgb(alpha, color.R, color.G, color.B);

            private void SetBrush(ref Brush field, Brush value, [CallerMemberName] string? propertyName = null)
            {
                if (ReferenceEquals(field, value))
                {
                    return;
                }

                field = value;
                OnPropertyChanged(propertyName);
            }

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
