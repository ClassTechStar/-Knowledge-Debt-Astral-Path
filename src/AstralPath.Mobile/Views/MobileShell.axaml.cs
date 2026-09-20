using AstralPath.Shared.ViewModels;
using Avalonia.Controls;

namespace AstralPath.Mobile.Views;

/// <summary>移动端外壳视图。代码后置只做响应式回写，业务逻辑在 <see cref="ShellViewModel"/>。</summary>
public partial class MobileShell : UserControl
{
    public MobileShell()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.ShellWidth = e.NewSize.Width;
        }
    }
}
