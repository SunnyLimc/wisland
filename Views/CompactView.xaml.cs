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
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _textTransition.Initialize();
            _textTransition.UpdateViewportBounds();
        }

        private void TextViewport_SizeChanged(object sender, SizeChangedEventArgs e)
            => _textTransition.UpdateViewportBounds();

        private void ApplyTextToSlot(int slotIndex, string text)
        {
            _slotText[slotIndex] = text;
            GetMarquee(slotIndex).Text = text;
        }

        private void ApplyTextColorToSlot(int slotIndex)
            => GetMarquee(slotIndex).MarqueeForeground = new SolidColorBrush(_textColor);

        private MarqueeText GetMarquee(int slotIndex)
            => slotIndex == 0 ? CompactTextPrimary : CompactTextSecondary;
    }
}
