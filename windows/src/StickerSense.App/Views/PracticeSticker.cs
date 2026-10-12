using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace StickerSense.App.Views;

/// <summary>
/// A cheerful sticker drawn in WPF for practising the shortcut. It takes keyboard focus and
/// reports itself to screen readers as an image named "Practice sticker", like a sticker in a chat.
/// </summary>
internal sealed class PracticeSticker : ContentControl
{
    public PracticeSticker()
    {
        Focusable = true;
        IsTabStop = true;
        Width = 150;
        Height = 150;
        AutomationProperties.SetName(this, "Practice sticker");
        Content = Drawing();
    }

    private static UIElement Drawing()
    {
        var grid = new Grid();
        grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)), Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x9B, 0x00)), StrokeThickness = 4 });
        var face = new Canvas { Width = 150, Height = 150 };
        void Eye(double left) => face.Children.Add(Positioned(new Ellipse { Width = 14, Height = 22, Fill = Brushes.SaddleBrown }, left, 38));
        Eye(48);
        Eye(88);
        face.Children.Add(new Path
        {
            Data = Geometry.Parse("M 42,68 Q 75,100 108,68"),
            Stroke = Brushes.SaddleBrown,
            StrokeThickness = 6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        grid.Children.Add(face);
        grid.Children.Add(new TextBlock
        {
            Text = "you got this!",
            FontSize = 17,
            FontWeight = FontWeights.Heavy,
            Foreground = Brushes.Purple,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 22),
        });
        return grid;
    }

    private static UIElement Positioned(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PracticeStickerPeer(this);

    private sealed class PracticeStickerPeer(PracticeSticker owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(PracticeSticker);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override List<AutomationPeer>? GetChildrenCore() => null; // one image, no parts
    }
}
