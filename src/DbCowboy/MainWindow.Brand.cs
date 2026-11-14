using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DbCowboy;

public sealed partial class MainWindow
{
    static readonly UISettings brandSystemColors = new();
    static readonly string[] IdentityNames = Profile.Identities;
    static readonly string[] IdentityFiles = ["", "01-rowdy.png", "02-dash.png", "03-dot.png"];
    readonly Image connectionIdentity = new() { Width = 32, Height = 32, Visibility = Visibility.Collapsed };
    static BitmapImage Asset(string name) => new(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", name)));
    static void BrandAccent(Button button)
    {
        void Update()
        {
            string[] keys = ["AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed"];
            if (new AccessibilitySettings().HighContrast)
            {
                foreach (var key in keys) button.Resources.Remove(key);
                button.ClearValue(Control.BackgroundProperty); button.ClearValue(Control.ForegroundProperty); return;
            }
            bool dark = button.ActualTheme == ElementTheme.Dark;
            var fill = new SolidColorBrush(dark ? Color.FromArgb(255, 245, 183, 97) : Color.FromArgb(255, 181, 93, 50));
            var foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 16, 27, 33) : Color.FromArgb(255, 255, 255, 255));
            foreach (var key in keys) button.Resources[key] = key.Contains("Foreground") ? foreground : fill;
            button.Background = fill; button.Foreground = foreground;
        }
        // Contrast changes do not necessarily change ActualTheme in an unpackaged app.
        // Only retain the system event subscription while this button is mounted.
        void ColorsChanged(UISettings _, object args) => button.DispatcherQueue.TryEnqueue(Update);
        button.Loaded += (_, _) => { Update(); brandSystemColors.ColorValuesChanged -= ColorsChanged; brandSystemColors.ColorValuesChanged += ColorsChanged; };
        button.Unloaded += (_, _) => brandSystemColors.ColorValuesChanged -= ColorsChanged;
        button.ActualThemeChanged += (_, _) => Update();
    }
    FrameworkElement BrandHeader()
    {
        // Sized for the 48 px title bar it now lives in.
        var logo = new Image { Source = Asset("brand-light.png"), Width = 146, Height = 32, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(logo, "DB Cowboy — Rowdy the database cowboy");
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin=new Thickness(2,0,12,0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(logo);   // the connection identity art lives in the sidebar connection block
        var accessibility = new AccessibilitySettings();
        void Update()
        {
            ApplyAccentTheme();
            var systemBackground = brandSystemColors.GetColorValue(UIColorType.Background);
            bool dark = accessibility.HighContrast ? (systemBackground.R + systemBackground.G + systemBackground.B) < 384 : root.ActualTheme == ElementTheme.Dark;
            logo.Source = Asset(dark ? "brand-dark.png" : "brand-light.png");
            // Mica shows through the title bar and sidebar; high contrast keeps solid system surfaces.
            ApplyWindowMaterial(dark);
        }
        root.ActualThemeChanged += (_, _) => Update();
        root.Loaded += (_, _) => Update();
        brandSystemColors.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(Update);
        // This WinRT event is unavailable on some unpackaged desktop hosts.
        // XAML theme changes still refresh the high-contrast state.
        try { accessibility.HighContrastChanged += (_, _) => DispatcherQueue.TryEnqueue(Update); }
        catch (System.Runtime.InteropServices.COMException) { }
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "DbCowboy.ico")); } catch { /* App still starts if shell icon assignment is unavailable. */ }
        return brand;
    }
    void UpdateConnectionIdentity(Profile? profile)
    {
        int index = profile == null ? 0 : Array.IndexOf(IdentityNames, profile.Identity);
        connectionIdentity.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (index > 0) { connectionIdentity.Source = Asset(IdentityFiles[index]); ToolTipService.SetToolTip(connectionIdentity, profile!.Name + " · " + profile.Identity); AutomationProperties.SetName(connectionIdentity, profile.Name + " connection identity"); }
    }
}
