using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using AvOrientation = Avalonia.Layout.Orientation;

namespace AstralPath.Mobile.Views;

/// <summary>与 Web/Desktop 同一套设计令牌（wwwroot/index.html CSS 变量）。</summary>
public static class UiTheme
{
    public static readonly Color CanvasColor = Color.Parse("#f3f4f5");
    public static readonly Color Surface = Color.Parse("#fbfbfc");
    public static readonly Color SurfaceSoft = Color.Parse("#e8eaec");
    public static readonly Color SurfaceStrong = Color.Parse("#858a90");
    public static readonly Color Ink = Color.Parse("#303236");
    public static readonly Color Muted = Color.Parse("#70757a");
    public static readonly Color Rule = Color.Parse("#c7cacf");
    public static readonly Color Field = Color.Parse("#eff0f2");
    public static readonly Color Primary = Color.Parse("#0D72D9");
    public static readonly Color PrimaryHover = Color.Parse("#0a61ba");
    public static readonly Color Danger = Color.Parse("#b64a58");
    public static readonly Color NodeOk = Color.Parse("#55a77b");
    public static readonly Color NodeChapter = Color.Parse("#6d9fc7");
    public static readonly Color NodeTarget = Color.Parse("#d95a67");
    public static readonly Color NodeChapterBgTop = Color.Parse("#e8f2fb");
    public static readonly Color NodeChapterBgBot = Color.Parse("#d9e9f8");
    public static readonly Color NodeTermBgTop = Color.Parse("#e5f7ee");
    public static readonly Color NodeTermBgBot = Color.Parse("#d4f0e2");
    public static readonly Color NodeDebtBg = Color.Parse("#ffe1e5");
    public static readonly Color NodeSectionBg = Color.Parse("#f0f5fa");
    public static readonly Color NodeBorder = Color.Parse("#b7bbc0");

    public const double RadiusShell = 22;
    public const double RadiusGroup = 16;
    public const double RadiusControl = 12;

    public static IBrush B(Color c) => new SolidColorBrush(c);

    public static TextBlock H1(string text) => new()
    {
        Text = text, FontSize = 28, FontWeight = FontWeight.Bold,
        Foreground = B(Ink), Margin = new Thickness(0, 4, 0, 6)
    };

    public static TextBlock H2(string text) => new()
    {
        Text = text, FontSize = 18, FontWeight = FontWeight.Bold,
        Foreground = B(Ink), Margin = new Thickness(0, 16, 0, 8)
    };

    public static TextBlock Sub(string text) => new()
    {
        Text = text, FontSize = 13, Foreground = B(Muted),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
    };

    public static TextBlock Body(string text) => new()
    {
        Text = text, FontSize = 14, Foreground = B(Ink),
        TextWrapping = TextWrapping.Wrap
    };

    public static AvButton BtnPrimary(string label, Action? onClick = null)
    {
        var b = new AvButton
        {
            Content = label, MinHeight = 40, Padding = new Thickness(14, 8),
            Background = B(Primary), Foreground = Brushes.White,
            BorderBrush = B(Primary), CornerRadius = new CornerRadius(RadiusControl),
            FontWeight = FontWeight.SemiBold
        };
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    public static AvButton Quiet(string label, Action? onClick = null)
    {
        var b = new AvButton
        {
            Content = label, MinHeight = 36, Padding = new Thickness(10, 6),
            Background = Brushes.Transparent, Foreground = B(Color.Parse("#7a838e")),
            BorderThickness = new Thickness(0), FontSize = 12
        };
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    public static AvButton Ghost(string label, Action? onClick = null)
    {
        var b = new AvButton
        {
            Content = label, MinHeight = 40, Padding = new Thickness(14, 8),
            Background = B(Surface), Foreground = B(Ink),
            BorderBrush = B(Rule), CornerRadius = new CornerRadius(RadiusControl)
        };
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    public static Border Card(params Control[] children)
    {
        var sp = new StackPanel { Spacing = 8 };
        foreach (var c in children) sp.Children.Add(c);
        return new Border
        {
            Child = sp,
            Background = B(Surface),
            BorderBrush = B(Rule),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusGroup),
            Padding = new Thickness(16, 14),
            Margin = new Thickness(0, 0, 0, 12)
        };
    }

    public static Border Welcome(params Control[] children)
    {
        var sp = new StackPanel { Spacing = 10 };
        foreach (var c in children) sp.Children.Add(c);
        return new Border
        {
            Child = sp,
            Background = B(Surface),
            BorderBrush = B(Rule),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusShell),
            Padding = new Thickness(24, 22),
            Margin = new Thickness(0, 0, 0, 16)
        };
    }

    public static Border StatusLine(string text, bool ok = false) => new()
    {
        Child = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap,
            Foreground = B(ok ? Color.Parse("#2f6b50") : Muted), FontSize = 13
        },
        Background = B(SurfaceSoft),
        BorderBrush = B(ok ? NodeOk : SurfaceStrong),
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(14, 12),
        Margin = new Thickness(0, 10, 0, 0)
    };

    public static Avalonia.Controls.ProgressBar Bar(double value = 0.35) => new()
    {
        Minimum = 0, Maximum = 1, Value = value, Height = 10,
        Background = B(SurfaceSoft), Foreground = B(Primary)
    };

    public static Border Stat(string label, string value) => new()
    {
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, FontSize = 12, Foreground = B(Muted) },
                new TextBlock { Text = value, FontSize = 26, FontWeight = FontWeight.Bold, Foreground = B(Primary) }
            }
        },
        Background = B(Surface), BorderBrush = B(Rule), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(RadiusGroup), Padding = new Thickness(14, 12)
    };

    public static Control StatGrid2x2(params Control[] cells)
    {
        var g = new UniformGrid { Columns = 2 };
        foreach (var c in cells)
        {
            var wrap = new Border { Child = c, Margin = new Thickness(0, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Stretch };
            g.Children.Add(wrap);
        }
        return g;
    }

    public static WrapPanel Row(params Control[] items)
    {
        var w = new WrapPanel { ItemHeight = 48, ItemWidth = double.NaN, Orientation = AvOrientation.Horizontal };
        foreach (var it in items)
        {
            if (it is Control c) c.Margin = new Thickness(0, 0, 8, 8);
            w.Children.Add((Control)it);
        }
        return w;
    }
}

public sealed class AvButton : Avalonia.Controls.Button { }

public static class ControlExt
{
    public static T Also<T>(this T c, System.Action<T> act) { act(c); return c; }
}