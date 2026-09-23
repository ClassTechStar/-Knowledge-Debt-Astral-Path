using AstralPath.Mobile.Services;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using AvApplication = Avalonia.Application;

namespace AstralPath.Mobile;

public class App : AvApplication
{
    public static LocalStore? Store { get; private set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AstralPath");
        Directory.CreateDirectory(dir);
        Store = new LocalStore(Path.Combine(dir, "astralpath.db"));

        var root = new Views.RootView(Store)
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            FontFamily = new FontFamily("sans-serif")
        };
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = root;
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if !ANDROID
            desktop.MainWindow = new MainWindow { Content = root };
#endif
        }
        base.OnFrameworkInitializationCompleted();
    }
}

#if !ANDROID
public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "知债：星穹学途";
        Width = 390;
        Height = 844;
    }
}
#endif
