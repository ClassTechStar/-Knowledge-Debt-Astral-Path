using System.Globalization;
using AstralPath.Contracts;
using AstralPath.Shared.Services;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace AstralPath.Shared.Controls;

/// <summary>
/// 知识债图谱自绘控件（方案 §12.2 / §41.1）。
///
/// 为什么自绘而不是用现成图控件：
/// <list type="bullet">
///   <item>配色、线型（颜色 + 虚线双重编码）、深色画布由方案硬性指定，自绘最可控；</item>
///   <item>400 节点 60fps 的预算下，<c>Render</c> 直绘比维护 400 个视觉树元素更省；</item>
///   <item>LOD（细节分级）可以在同一处按节点数切换，避免布局抖动。</item>
/// </list>
///
/// 无障碍：控件本身可聚焦，方向键在节点间移动、Home/End 跳首尾，
/// <c>AutomationProperties.Name</c> 随选中节点更新，读屏可播报当前节点语义。
/// </summary>
public sealed class GraphCanvas : Control
{
    public static readonly StyledProperty<GraphViewDto?> SourceProperty =
        AvaloniaProperty.Register<GraphCanvas, GraphViewDto?>(nameof(Source));

    public static readonly StyledProperty<string?> SelectedNodeIdProperty =
        AvaloniaProperty.Register<GraphCanvas, string?>(
            nameof(SelectedNodeId), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ColorBlindSafeProperty =
        AvaloniaProperty.Register<GraphCanvas, bool>(nameof(ColorBlindSafe));

    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<GraphCanvas, bool>(nameof(ReduceMotion));

    /// <summary>超过该节点数即进入 LOD 精简模式（隐藏非选中节点标签）。</summary>
    public const int LabelLodThreshold = 120;

    private GraphScene _scene = GraphScene.Empty;
    private object? _sceneSourceKey;
    private Size _sceneSize;

    static GraphCanvas()
    {
        AffectsRender<GraphCanvas>(SourceProperty, SelectedNodeIdProperty, ColorBlindSafeProperty);
        FocusableProperty.OverrideDefaultValue<GraphCanvas>(true);
    }

    public GraphViewDto? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? SelectedNodeId
    {
        get => GetValue(SelectedNodeIdProperty);
        set => SetValue(SelectedNodeIdProperty, value);
    }

    /// <summary>色盲安全模式：债边改用三色替代 + 加粗虚线差异（方案 附录 X）。</summary>
    public bool ColorBlindSafe
    {
        get => GetValue(ColorBlindSafeProperty);
        set => SetValue(ColorBlindSafeProperty, value);
    }

    /// <summary>减少动画偏好（本控件无动画，保留属性以统一设置项语义）。</summary>
    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    /// <summary>当前场景（供测试与诊断面板读取）。</summary>
    public GraphScene Scene => _scene;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedNodeIdProperty) UpdateAccessibleName();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 1 || size.Height <= 1) return;

        // 深色画布（§41.1 指定 #0F0F1A）
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0F0F1A")), new Rect(size));

        var source = Source;
        if (source is null || source.Nodes.Count == 0)
        {
            DrawPlaceholder(context, size);
            return;
        }

        // 布局缓存：仅当数据引用或尺寸变化时重算（避免每帧 O(V+E)）
        if (!ReferenceEquals(_sceneSourceKey, source) || _sceneSize != size)
        {
            _scene = GraphLayout.Build(source, size.Width, size.Height);
            _sceneSourceKey = source;
            _sceneSize = size;
        }

        var lod = _scene.Nodes.Count > LabelLodThreshold;
        var typeface = new Typeface(FontFamily.Default);

        DrawEdges(context, lod);
        DrawNodes(context, lod, typeface);
    }

    private void DrawEdges(DrawingContext context, bool lod)
    {
        foreach (var e in _scene.Edges)
        {
            var brush = new SolidColorBrush(EdgeColor(e.Status));
            var thickness = e.Status == "open" ? 2.4 : 1.6;
            var dash = DashFor(e.Status);

            var pen = dash is null
                ? new Pen(brush, thickness)
                : new Pen(brush, thickness, new DashStyle(dash, 0));

            context.DrawLine(pen, new Point(e.X1, e.Y1), new Point(e.X2, e.Y2));

            // LOD 精简：边过多时不画箭头，减少绘制调用
            if (!lod && e.Impact > 0)
            {
                DrawArrowHead(context, brush, e.X1, e.Y1, e.X2, e.Y2);
            }
        }
    }

    private void DrawNodes(DrawingContext context, bool lod, Typeface typeface)
    {
        var selected = SelectedNodeId;

        foreach (var n in _scene.Nodes)
        {
            var center = new Point(n.X, n.Y);
            var fill = new SolidColorBrush(BandColor(n.Band));

            // 选中环（2px 焦点环语义，方案 附录 X）
            if (n.Id == selected)
            {
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#3B82F6")), 3),
                    center, n.Radius + 5, n.Radius + 5);
            }

            context.DrawEllipse(fill, new Pen(new SolidColorBrush(Color.Parse("#E2E8F0")), 1),
                center, n.Radius, n.Radius);

            var showLabel = !lod || n.Id == selected;
            if (!showLabel) continue;

            var text = new FormattedText(
                n.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11,
                new SolidColorBrush(Color.Parse("#E2E8F0")));

            context.DrawText(text, new Point(n.X - text.Width / 2, n.Y + n.Radius + 4));
        }
    }

    private static void DrawPlaceholder(DrawingContext context, Size size)
    {
        var text = new FormattedText(
            "暂无图谱数据", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 14,
            new SolidColorBrush(Color.Parse("#64748B")));

        context.DrawText(text, new Point((size.Width - text.Width) / 2, (size.Height - text.Height) / 2));
    }

    private static void DrawArrowHead(DrawingContext context, IBrush brush, double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;

        var ux = dx / len;
        var uy = dy / len;
        var tipX = x2 - ux * (GraphLayout.NodeRadius + 2);
        var tipY = y2 - uy * (GraphLayout.NodeRadius + 2);

        const double size2 = 7;
        var leftX = tipX - ux * size2 - uy * size2 * 0.6;
        var leftY = tipY - uy * size2 + ux * size2 * 0.6;
        var rightX = tipX - ux * size2 + uy * size2 * 0.6;
        var rightY = tipY - uy * size2 - ux * size2 * 0.6;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(tipX, tipY), true);
            ctx.LineTo(new Point(leftX, leftY));
            ctx.LineTo(new Point(rightX, rightY));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    // ── 命中测试与键盘导航 ─────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var p = e.GetPosition(this);
        var hit = HitTest(p);
        if (hit is not null)
        {
            SelectedNodeId = hit.Id;
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_scene.Nodes.Count == 0) return;

        var current = _scene.Nodes.FirstOrDefault(n => n.Id == SelectedNodeId);

        switch (e.Key)
        {
            case Key.Home:
                SelectedNodeId = _scene.Nodes[0].Id;
                break;
            case Key.End:
                SelectedNodeId = _scene.Nodes[^1].Id;
                break;
            case Key.Enter or Key.Space:
                if (current is null) SelectedNodeId = _scene.Nodes[0].Id;
                break;
            case Key.Left or Key.Up or Key.Right or Key.Down:
                SelectedNodeId = NeighborInDirection(current, e.Key).Id;
                break;
            default:
                return;
        }

        e.Handled = true;
        UpdateAccessibleName();
    }

    private GraphNodePos NeighborInDirection(GraphNodePos? current, Key key)
    {
        if (current is null) return _scene.Nodes[0];

        // 方向键语义：沿主方向取最近的节点（与「层」的方向一致）
        var dx = key is Key.Left ? -1 : key is Key.Right ? 1 : 0;
        var dy = key is Key.Up ? -1 : key is Key.Down ? 1 : 0;

        GraphNodePos? best = null;
        var bestScore = double.MaxValue;

        foreach (var n in _scene.Nodes)
        {
            if (ReferenceEquals(n, current) || n.Id == current.Id) continue;

            var vx = n.X - current.X;
            var vy = n.Y - current.Y;
            var projection = vx * dx + vy * dy;
            if (projection <= 0) continue;

            var perp = Math.Abs(vx * dy - vy * dx);
            var score = projection + perp * 2;
            if (score < bestScore)
            {
                bestScore = score;
                best = n;
            }
        }

        return best ?? current;
    }

    /// <summary>返回命中的节点（含半径容差，触达友好）。</summary>
    public GraphNodePos? HitTest(Point p)
    {
        GraphNodePos? best = null;
        var bestDistance = double.MaxValue;

        foreach (var n in _scene.Nodes)
        {
            var dx = p.X - n.X;
            var dy = p.Y - n.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var tolerance = n.Radius + 8;
            if (distance <= tolerance && distance < bestDistance)
            {
                bestDistance = distance;
                best = n;
            }
        }

        return best;
    }

    private void UpdateAccessibleName()
    {
        var node = _scene.Nodes.FirstOrDefault(n => n.Id == SelectedNodeId);
        AutomationProperties.SetName(this, node is null
            ? "知识债图谱，未选中节点。使用方向键在节点间移动。"
            : $"知识债图谱，当前选中 {node.Name}，掌握度 {node.Score:0.##}，{Shared.DemoMeta.BandLabel(node.Band)}。");
    }

    // ── 配色（严格取自方案；色盲安全模式下改用三色） ─────────────

    private Color BandColor(string band) => ColorBlindSafe
        ? band switch
        {
            "green" => Color.Parse("#1B9E77"),
            "yellow" => Color.Parse("#D95F02"),
            "red" => Color.Parse("#7570B3"),
            _ => Color.Parse("#BDBDBD")
        }
        : band switch
        {
            "green" => Color.Parse("#43A047"),
            "yellow" => Color.Parse("#FB8C00"),
            "red" => Color.Parse("#E53935"),
            _ => Color.Parse("#BDBDBD")
        };

    private Color EdgeColor(string status) => ColorBlindSafe
        ? status switch
        {
            "open" => Color.Parse("#D95F02"),
            "repairing" => Color.Parse("#1B9E77"),
            "cleared" => Color.Parse("#7570B3"),
            "dropped" => Color.Parse("#9E9E9E"),
            _ => Color.Parse("#BDBDBD")
        }
        : status switch
        {
            "open" => Color.Parse("#E53935"),
            "repairing" => Color.Parse("#FB8C00"),
            "cleared" => Color.Parse("#43A047"),
            "dropped" => Color.Parse("#9E9E9E"),
            _ => Color.Parse("#BDBDBD")
        };

    /// <summary>线型是颜色之外的第二重编码：open 长虚线 / repairing 短虚线 / cleared 实线 / dropped 点线。</summary>
    private static double[]? DashFor(string status) => status switch
    {
        "open" => new double[] { 6, 3 },
        "repairing" => new double[] { 2, 2 },
        "cleared" => null,
        "dropped" => new double[] { 1, 4 },
        _ => null
    };
}
