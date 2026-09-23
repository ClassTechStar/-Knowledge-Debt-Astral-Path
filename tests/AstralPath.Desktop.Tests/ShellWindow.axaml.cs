using AstralPath.Shared.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace AstralPath.Desktop.Tests;

/// <summary>测试用三栏宿主窗口：只负责把宽度回写给 Shell 的响应式断点。</summary>
public partial class ShellWindow : Window
{
    public ShellWindow()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            if (DataContext is ShellViewModel shell) shell.ShellWidth = e.NewSize.Width;
        };
    }
}
