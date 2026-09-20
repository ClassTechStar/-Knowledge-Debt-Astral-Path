using AstralPath.Shared.ViewModels;
using Avalonia.Controls;

namespace AstralPath.Desktop.Views;

/// <summary>
/// 桌面端主窗口。代码后置只负责响应式断点回写（方案 §12.4），业务逻辑全在 <see cref="ShellViewModel"/>。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
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
