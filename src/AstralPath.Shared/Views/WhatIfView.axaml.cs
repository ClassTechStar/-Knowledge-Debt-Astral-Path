using Avalonia.Controls;

namespace AstralPath.Shared.Views;

/// <summary>What-if 影响推演页视图（方案 §12.5）。逻辑全部在 <see cref="ViewModels.WhatIfViewModel"/>。</summary>
public partial class WhatIfView : UserControl
{
    public WhatIfView() => InitializeComponent();
}
