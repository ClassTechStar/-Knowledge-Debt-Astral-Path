using AstralPath.Shared.ViewModels;
using Avalonia.Controls;

namespace AstralPath.Shared.Views;

/// <summary>
/// 图谱页视图。唯一的代码逻辑是响应式：窄屏时收起右栏，把宽度让给画布（方案 §12.4）。
/// </summary>
public partial class GraphView : UserControl
{
    public GraphView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is GraphViewModel vm)
        {
            vm.ShowSidePanel = e.NewSize.Width >= 900;
        }
    }
}
