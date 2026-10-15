using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace EzView;

class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;

        // Fluent theme resources, overridden to match the dark/orange design.
        Resources["ContentControlThemeFontFamily"] = Ui.Font;
        Resources["MenuFlyoutPresenterBackground"] = Ui.Panel;
        Resources["MenuFlyoutPresenterBorderBrush"] = Ui.Edge;
        Resources["MenuFlyoutItemForeground"] = Ui.Text;
        Resources["MenuFlyoutItemBackgroundPointerOver"] = Ui.Hover;
        Resources["MenuFlyoutItemBackgroundPressed"] = Ui.Hover;
        Resources["MenuFlyoutItemForegroundPointerOver"] = Ui.Accent;
        Resources["MenuFlyoutItemForegroundPressed"] = Ui.Accent;
        Resources["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = Ui.Muted;
        Resources["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = Ui.Muted;
        Resources["MenuFlyoutSubItemChevron"] = Ui.Muted;
        Resources["MenuFlyoutSubItemChevronPointerOver"] = Ui.Accent;
        Resources["MenuFlyoutSubItemChevronSubMenuOpened"] = Ui.Accent;
        Resources["MenuFlyoutSubItemBackgroundSubMenuOpened"] = Ui.Hover;
        Resources["MenuFlyoutSubItemForegroundSubMenuOpened"] = Ui.Accent;
        Resources["OverlayCornerRadius"] = new CornerRadius(10);
        Resources["ToolTipBackground"] = Ui.Surface;
        foreach (var k in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" })
            Resources[k] = Ui.Accent;

        Styles.Add(new Style(x => x.OfType<MenuItem>())
        {
            Setters = { new Setter(TemplatedControl.FontSizeProperty, 13.0), new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 7, 12, 7)) },
        });
        Styles.Add(new Style(x => x.OfType<MenuItem>().Class("checked"))
        {
            Setters = { new Setter(TemplatedControl.ForegroundProperty, Ui.Accent) },
        });
        // Groups in the menu are split by a thin line instead of headings.
        Styles.Add(new Style(x => x.OfType<Separator>())
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Ui.Edge), new Setter(Layoutable.HeightProperty, 1.0), new Setter(Layoutable.MarginProperty, new Thickness(12, 5, 12, 5)) },
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.MainWindow = new MainWindow(d.Args?.FirstOrDefault());
        base.OnFrameworkInitializationCompleted();
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // English UI, so numbers and dates follow English formatting too ("6.1 MB", not "6,1 MB").
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.CurrentCulture = new("en-US");
        // Unhandled errors end up in %TEMP%\ezview-crash.log, so a crash can be diagnosed afterwards.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "ezview-crash.log"), $"{DateTime.Now:u}{Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}");
        if (args is ["--selftest"]) { SelfTest.Run(); return; }
        if (args is ["--register"]) { Registration.Register(); Registration.OpenDefaultAppsSettings(); return; }
        if (args is ["--unregister"]) { Registration.Unregister(); return; }
        if (args is ["--skel", var skelFile]) { Console.WriteLine(ModelView.DumpSkeleton(skelFile)); return; }
        if (args is ["--specs", var layer]) { Console.WriteLine(Usd.DumpSpecs(layer)); return; }
        if (args is ["--dump", var file]) { Console.WriteLine(AnimScene.Load(file).Summary()); return; }
        if (SingleInstance.HandOff(args is [var open] && !open.StartsWith("--") ? open : null)) return;
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = Ui.FontUri })
            .StartWithClassicDesktopLifetime(args);
    }
}
