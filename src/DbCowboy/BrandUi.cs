using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DbCowboy;

/// <summary>
/// Brand surfaces for controls that live outside <see cref="MainWindow"/> (the result grid, query tabs, the SQL editor).
/// Colours come from brand/tokens.json. Windows high contrast always wins over them.
/// </summary>
static class BrandUi
{
    public static readonly Color Ink = Rgb(0x19272E), Muted = Rgb(0x58656A), Silver = Rgb(0xB3C3CB), Line = Rgb(0x7B8A90),
        Raised = Rgb(0x1A2A33), Action = Rgb(0xA64F22), Amber = Rgb(0xF5B761), Cream = Rgb(0xF8F4EC);

    static Color Rgb(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    public static Color Fade(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    public static bool HighContrast => new AccessibilitySettings().HighContrast;

    /// <summary>Runs <paramref name="apply"/> now and whenever the element's theme changes, passing whether it is dark.</summary>
    public static T Themed<T>(T element, Action<T, bool> apply) where T : FrameworkElement
    {
        void Update() => apply(element, element.ActualTheme == ElementTheme.Dark);
        element.Loaded += (_, _) => Update();
        element.ActualThemeChanged += (_, _) => Update();
        Update();
        return element;
    }

    public static SolidColorBrush LineBrush(bool dark) => new(Fade(dark ? Silver : Line, dark ? (byte)40 : (byte)60));
    public static SolidColorBrush MutedBrush(bool dark) => new(dark ? Silver : Muted);
    public static SolidColorBrush AccentBrush(bool dark) => new(dark ? Amber : Action);

    /// <summary>A brand surface: white on paper in light, raised on night in dark.</summary>
    public static Border Card(FrameworkElement? child, double padding = 20, double radius = 12)
    {
        var card = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1) };
        return Themed(card, (c, dark) =>
        {
            if (HighContrast)
            {
                c.ClearValue(Border.BackgroundProperty);
                c.BorderBrush = new SolidColorBrush(new UISettings().UIElementColor(UIElementType.WindowText));
                return;
            }
            c.Background = new SolidColorBrush(dark ? Raised : Microsoft.UI.Colors.White);
            c.BorderBrush = LineBrush(dark);
        });
    }

    /// <summary>Secondary text in the brand's muted ink.</summary>
    public static TextBlock MutedText(string text, double size = 12)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        return Themed(label, (l, dark) =>
        {
            if (HighContrast) l.ClearValue(TextBlock.ForegroundProperty);
            else l.Foreground = MutedBrush(dark);
        });
    }

    /// <summary>A header band for tables: a faint tint of the brand line colour.</summary>
    public static SolidColorBrush HeaderBrush(bool dark) => new(Fade(dark ? Silver : Line, dark ? (byte)22 : (byte)28));
}
