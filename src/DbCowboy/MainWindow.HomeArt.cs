using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace DbCowboy;

/// <summary>
/// The Home banner: a frontier horizon (sun, two ranges of mesas, a few night stars) drawn in the brand palette.
/// The horizon drifts more slowly than the page as it scrolls, so the banner gains depth without any motion of its own.
/// </summary>
public sealed partial class MainWindow
{
    static Border HomeBanner(ScrollViewer page, FrameworkElement content)
    {
        var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        var stops = new[] { new GradientStop { Offset = 0 }, new GradientStop { Offset = .62 }, new GradientStop { Offset = 1 } };
        foreach (var stop in stops) sky.GradientStops.Add(stop);

        var horizon = new Grid { IsHitTestVisible = false };
        // A low sun (or moon) resting on the far range, its glow fading out from the centre.
        var sun = new Ellipse { Width = 132, Height = 132, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 120, 30) };
        var glow = new RadialGradientBrush();
        var core = new GradientStop { Offset = 0 }; var rim = new GradientStop { Offset = .62 }; var edge = new GradientStop { Offset = 1 };
        glow.GradientStops.Add(core); glow.GradientStops.Add(rim); glow.GradientStops.Add(edge);
        var stars = new Canvas();
        var far = Range(new double[] { 0, 30, 9, 24, 14, 24, 20, 12, 27, 12, 31, 22, 44, 22, 49, 15, 58, 15, 62, 26, 74, 26, 79, 8, 86, 8, 90, 20, 100, 20, 100, 40, 0, 40 }, 92);
        var near = Range(new double[] { 0, 26, 6, 26, 10, 14, 19, 14, 23, 28, 38, 28, 41, 21, 47, 21, 50, 30, 68, 30, 72, 18, 81, 18, 85, 32, 100, 32, 100, 40, 0, 40 }, 60);
        horizon.Children.Add(stars); horizon.Children.Add(far); horizon.Children.Add(near);
        // A few fixed stars; they only show at night.
        foreach (var (x, y, r) in new[] { (.08, .18, 2.0), (.21, .32, 1.5), (.37, .12, 2.0), (.52, .27, 1.4), (.66, .1, 1.8), (.79, .3, 1.4), (.91, .15, 2.2), (.3, .45, 1.2), (.6, .42, 1.3) })
        {
            var star = new Ellipse { Width = r * 2, Height = r * 2, Tag = (x, y) };
            stars.Children.Add(star);
        }
        stars.SizeChanged += (_, e) => { foreach (Ellipse s in stars.Children) { var (x, y) = ((double, double))s.Tag; Canvas.SetLeft(s, x * e.NewSize.Width); Canvas.SetTop(s, y * e.NewSize.Height); } };

        var layers = new Grid();
        layers.Children.Add(sun);   // the sun stays put while the ranges drift: the far-away thing moves least
        layers.Children.Add(Fluent.Parallax(page, horizon, 56));
        layers.Children.Add(content);
        var banner = new Border { Child = layers, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Background = sky };
        Themed(banner, (b, dark) =>
        {
            horizon.Visibility = HighContrast ? Visibility.Collapsed : Visibility.Visible;
            if (HighContrast) { b.Background = null; b.BorderBrush = SystemText(); return; }
            b.Background = sky;
            b.BorderBrush = BrandUi.LineBrush(dark);
            stops[0].Color = dark ? Color.FromArgb(255, 14, 24, 31) : Color.FromArgb(255, 255, 239, 214);
            stops[1].Color = dark ? Color.FromArgb(255, 26, 42, 51) : Color.FromArgb(255, 250, 233, 208);
            stops[2].Color = dark ? Color.FromArgb(255, 36, 52, 60) : Color.FromArgb(255, 244, 222, 196);
            core.Color = Brand.Fade(dark ? Brand.Cream : Brand.Amber, dark ? (byte)150 : (byte)220);
            rim.Color = Brand.Fade(Brand.Amber, dark ? (byte)60 : (byte)120);
            edge.Color = Brand.Fade(Brand.Amber, 0);
            sun.Fill = glow;
            far.Fill = new SolidColorBrush(Brand.Fade(dark ? Brand.Silver : Brand.Copper, dark ? (byte)26 : (byte)44));
            near.Fill = new SolidColorBrush(Brand.Fade(dark ? Brand.Silver : Brand.Action, dark ? (byte)40 : (byte)62));
            foreach (Ellipse star in stars.Children) { star.Fill = new SolidColorBrush(Brand.Fade(Brand.Cream, 180)); star.Visibility = dark ? Visibility.Visible : Visibility.Collapsed; }
        });
        return banner;
    }

    /// <summary>One silhouette of mesas from points on a 100 x 40 grid, stretched across the bottom of the banner.</summary>
    static Polygon Range(double[] xy, double height)
    {
        var shape = new Polygon { Stretch = Stretch.Fill, Height = height, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < xy.Length; i += 2) shape.Points.Add(new Point(xy[i], xy[i + 1]));
        return shape;
    }
}
