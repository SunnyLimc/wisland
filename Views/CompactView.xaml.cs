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
    public sealed partial class CompactView : UserControl
    {
        private readonly DirectionalContentTransitionCoordinator _textTransition;
        private readonly string[] _slotText = new string[2];
        private Color _textColor = Microsoft.UI.Colors.White;
        private string _sessionCountText = string.Empty;
        private bool _showSessionCount;

        public CompactView()
        {
            this.InitializeComponent();
            _textTransition = new DirectionalContentTransitionCoordinator(
                TextViewport,
                CompactTextSlotPrimary,
                CompactTextSlotSecondary,
                IslandConfig.CompactContentTransitionProfile);

            _slotText[0] = CompactTextPrimary.Text;
            _slotText[1] = CompactTextSecondary.Text;

            Loaded += OnLoaded;
        }

        public string Text
        {
            get => _slotText[_textTransition.ActiveSlotIndex];
            set => Update(value);
        }

        public bool Update(string text, ContentTransitionDirection direction = ContentTransitionDirection.None)
        {
            if (string.Equals(_slotText[_textTransition.ActiveSlotIndex], text, StringComparison.Ordinal))
            {
                return false;
            }

            if (direction == ContentTransitionDirection.None)
            {
                _textTransition.ApplyImmediately(slotIndex => ApplyTextToSlot(slotIndex, text));
                return true;
            }

            _textTransition.Transition(direction, slotIndex => ApplyTextToSlot(slotIndex, text));
            return true;
        }

        public void SetTextColor(Color color)
        {
            _textColor = color;
            ApplyTextColorToSlot(0);
            ApplyTextColorToSlot(1);
            ApplySessionCountAppearance();
        }

        public void SetSessionCountHint(string text, bool visible)
        {
            bool shouldShow = visible && !string.IsNullOrWhiteSpace(text);
            if (_showSessionCount == shouldShow
                && string.Equals(_sessionCountText, text, StringComparison.Ordinal))
            {
                return;
            }

            _showSessionCount = shouldShow;
            _sessionCountText = shouldShow ? text : string.Empty;
            CompactSessionCountText.Text = _sessionCountText;
            SessionCountBadge.Visibility = _showSessionCount ? Visibility.Visible : Visibility.Collapsed;
            UpdateTextMaxWidth();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _textTransition.Initialize();
            _textTransition.UpdateViewportBounds();
            ApplySessionCountAppearance();
            UpdateTextMaxWidth();
        }

        private void TextViewport_SizeChanged(object sender, SizeChangedEventArgs e)
            => _textTransition.UpdateViewportBounds();

        private void ApplyTextToSlot(int slotIndex, string text)
        {
            _slotText[slotIndex] = text;
            GetTextBlock(slotIndex).Text = text;
        }

        private void ApplyTextColorToSlot(int slotIndex)
            => GetTextBlock(slotIndex).Foreground = new SolidColorBrush(_textColor);

        private void ApplySessionCountAppearance()
        {
            Color borderColor = Color.FromArgb(68, _textColor.R, _textColor.G, _textColor.B);
            Color backgroundColor = Color.FromArgb(26, _textColor.R, _textColor.G, _textColor.B);
            Color textColor = Color.FromArgb(224, _textColor.R, _textColor.G, _textColor.B);

            SessionCountBadge.BorderBrush = new SolidColorBrush(borderColor);
            SessionCountBadge.Background = new SolidColorBrush(backgroundColor);
            CompactSessionCountText.Foreground = new SolidColorBrush(textColor);
        }

        private void UpdateTextMaxWidth()
        {
            double maxWidth = _showSessionCount ? 126 : 160;
            CompactTextPrimary.MaxWidth = maxWidth;
            CompactTextSecondary.MaxWidth = maxWidth;
        }

        private TextBlock GetTextBlock(int slotIndex)
            => slotIndex == 0 ? CompactTextPrimary : CompactTextSecondary;
    }
}
