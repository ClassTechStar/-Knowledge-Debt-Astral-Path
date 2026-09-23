using AstralPath.Mobile.Services;
using AstralPath.Mobile.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static AstralPath.Mobile.Views.UiTheme;
using AvOrientation = Avalonia.Layout.Orientation;

namespace AstralPath.Mobile.Views;

/// <summary>
/// 与 Desktop / Web 同构：顶栏品牌 + 胶囊导航（起点/藏书阁/识网/知债/今日/智能体/画像/账户）
/// + 相同文案与卡片层级；数据走 SQLite LocalStore。
/// </summary>
public sealed class RootView : UserControl
{
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _nav = new() { Orientation = AvOrientation.Horizontal, Spacing = 4 };
    private readonly List<AvButton> _navBtns = new();
    private readonly string[] _pages =
        { "home", "materials", "graph", "debt", "today", "agent", "profile", "account" };
    private readonly string[] _labels =
        { "起点", "藏书阁", "识网", "知债", "今日", "智能体", "画像", "账户" };

    private readonly LocalStore _store;
    private readonly TodayViewModel _today;
    private readonly PracticeViewModel _practice;
    private readonly DebtListViewModel _debt;
    private readonly ProgressViewModel _progress;
    private readonly SettingsViewModel _settings;
    private string _current = "home";

    public RootView(LocalStore store)
    {
        _store = store;
        _today = new TodayViewModel(store);
        _practice = new PracticeViewModel(store);
        _debt = new DebtListViewModel(store);
        _progress = new ProgressViewModel(store);
        _settings = new SettingsViewModel(store);

        Background = B(CanvasColor);
        var root = new DockPanel();

        // ── 顶栏（与 .rail 一致）──
        var brandMark = new Border
        {
            Width = 40, Height = 30,
            Background = B(SurfaceSoft), CornerRadius = new CornerRadius(10),
            Child = new TextBlock
            {
                Text = "债", FontWeight = FontWeight.ExtraBold, Foreground = B(SurfaceStrong),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var brandText = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                new TextBlock { Text = "知债：星穹学途", FontSize = 16, FontWeight = FontWeight.Bold, Foreground = B(Ink) },
                new TextBlock { Text = "Knowledge Debt: Astral Path · 演示台", FontSize = 11, Foreground = B(Muted) }
            }
        };
        var brand = new StackPanel
        {
            Orientation = AvOrientation.Horizontal, Spacing = 10,
            Children = { brandMark, brandText }
        };

        for (var i = 0; i < _labels.Length; i++)
        {
            var idx = i;
            var b = new AvButton
            {
                Content = _labels[i], Padding = new Thickness(12, 8),
                Background = Brushes.Transparent, Foreground = B(Muted),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(999),
                FontSize = 13
            };
            b.Click += (_, _) => Go(_pages[idx]);
            _navBtns.Add(b);
            _nav.Children.Add(b);
        }

        var navBar = new Border
        {
            Child = _nav, Background = B(SurfaceSoft), CornerRadius = new CornerRadius(999),
            Padding = new Thickness(5), Margin = new Thickness(0, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var avatar = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = B(SurfaceStrong),
            Child = new TextBlock
            {
                Text = "访", Foreground = Brushes.White, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var account = new StackPanel
        {
            Orientation = AvOrientation.Horizontal, Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                avatar,
                new StackPanel
                {
                    Spacing = 0,
                    Children =
                    {
                        new TextBlock { Text = "王小明（有债）", FontSize = 13, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = "未登录 · API 本机", FontSize = 11, Foreground = B(Muted) }
                    }
                }
            }
        };

        var top = new Border
        {
            Background = B(Surface),
            BorderBrush = B(Rule), BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10),
            Child = new DockPanel
            {
                Children = { brand, navBar, account }
            }
        };
        // DockPanel 三列：左品牌 / 中导航 / 右账户
        var topGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        Grid.SetColumn(brand, 0);
        Grid.SetColumn(navBar, 1);
        Grid.SetColumn(account, 2);
        navBar.HorizontalAlignment = HorizontalAlignment.Center;
        topGrid.Children.Add(brand);
        topGrid.Children.Add(navBar);
        topGrid.Children.Add(account);
        top.Child = topGrid;
        DockPanel.SetDock(top, Dock.Top);

        var scroller = new ScrollViewer
        {
            Content = _host,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        root.Children.Add(top);
        root.Children.Add(scroller);
        Content = root;

        Go("home");
    }

    private void Go(string page)
    {
        _current = page;
        for (var i = 0; i < _navBtns.Count; i++)
        {
            var active = _pages[i] == page;
            _navBtns[i].Background = active ? B(Surface) : Brushes.Transparent;
            _navBtns[i].Foreground = active ? B(Ink) : B(Muted);
            _navBtns[i].FontWeight = active ? FontWeight.Bold : FontWeight.Normal;
        }

        _host.Content = page switch
        {
            "home" => BuildHome(),
            "materials" => BuildMaterials(),
            "graph" => BuildGraph(),
            "debt" => BuildDebtPage(),
            "today" => BuildTodayPage(),
            "agent" => BuildAgent(),
            "profile" => BuildProfile(),
            "account" => BuildAccount(),
            _ => BuildHome()
        };
    }

    private static TextBlock MakePrimaryBubble(string text)
        => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    private static StackPanel Page(params Control[] cs)
    {
        var sp = new StackPanel { Margin = new Thickness(18, 16, 18, 28), Spacing = 0 };
        foreach (var c in cs) sp.Children.Add(c);
        return sp;
    }

    // ══════════ 起点（与 Web page-home 同文案）══════════
    private Control BuildHome()
    {
        _today.LoadCommand.Execute(null);
        var mats = 1; // 种子 8 KP 视为 1 套图包
        return Page(
            H1("起点"),
            Sub("上传教材 → 本地解析 → 自动识网 → 今日任务与知识债诊断。UI 与 Desktop / Web 一致。"),
            Row(
                BtnPrimary("导入示例教材", () => { _store.ResetAll(); Go("today"); }),
                BtnPrimary("打开藏书阁", () => Go("materials"))
            ),
            Welcome(
                new TextBlock { Text = "ASTRALPATH WORKSPACE", FontSize = 12, Foreground = B(Muted), FontWeight = FontWeight.SemiBold },
                H1("把讲义变成识网，再把欠债算清楚"),
                Sub("示例图包（会计要素/等式/借贷/分录/试算平衡等 8 个知识点）可生成今日任务；销账走状态机。"),
                StatGrid2x2(
                    Stat("资料数", "1"),
                    Stat("自动图谱", "8 节点 / 9 边"),
                    Stat("今日任务", _today.Tasks.Count.ToString()),
                    Stat("开放债边", _debt.Items.Count > 0 ? _debt.Items.Count.ToString() : "—")
                )
            ),
            Card(
                H2("系统状态"),
                Sub("健康检查与本机数据"),
                StatusLine("本机 SQLite 就绪 · score-v1 / impact-v1 · 无服务端依赖", ok: true)
            )
        );
    }

    // ══════════ 藏书阁（Web page-materials 同构）══════════
    private Control BuildMaterials()
    {
        var list = new StackPanel { Spacing = 0, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var n in _store.GetNodes())
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(4, 12, 4, 12)
            };
            var left = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = $"{n.Title}.pdf", FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"难度 {n.Difficulty} · {n.KpId} · 本机 SQLite", FontSize = 12, Foreground = B(Muted) }
                }
            };
            var badge = new Border
            {
                Child = new TextBlock { Text = "ready", FontSize = 12, Foreground = B(Color.Parse("#1e5d43")) },
                Background = B(Color.Parse("#dcf4e7")), CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(left, 0); Grid.SetColumn(badge, 1);
            row.Children.Add(left); row.Children.Add(badge);
            var wrap = new Border
            {
                Child = row, BorderBrush = B(Rule), BorderThickness = new Thickness(0, 0, 0, 1)
            };
            list.Children.Add(wrap);
        }

        var fileHint = Body("尚未选择文件");
        var fileBtn = Ghost("选择文件（可多选）", () => fileHint.Text = "3 个文件已选择");
        var status = StatusLine("尚未上传");
        var upload = BtnPrimary("上传并解析（支持多选）", () =>
        {
            status.Child = new TextBlock { Text = "上传中…（本机 SQLite 资料库）", TextWrapping = TextWrapping.Wrap, Foreground = B(Color.Parse("#2f6b50")) };
        });

        var leftPanel = Card(
            H2("资料库"),
            FormField("选择学习资料（可多选）", fileBtn),
            FormField("已选文件", fileHint),
            FormField("OCR 模式", MakeSelectAt(1, "quick · 快速", "standard · 目录+抽样", "none · 仅文本层")),
            upload,
            status,
            list
        );
        var rightPanel = Card(
            H2("解析详情"),
            Sub("选择左侧资料查看详情。")
        );
        return Page(
            PageHeader("藏书阁", "支持 PDF/图片/文本。文本层优先，扫描版可 OCR（quick/standard）。",
                BtnPrimary("导入示例资料"), BtnPrimary("解析全部", () => Go("graph")), BtnPrimary("刷新列表")),
            TwoCol(leftPanel, rightPanel, 1.45, 0.7)
        );
    }

    // ══════════ 识网（Web page-graph 同构）══════════
    private Control BuildGraph()
    {
        var nodes = _store.GetNodes();
        var edges = _store.GetEdges();

        // DAG 画布：章节蓝 / 词绿 / 债红，与 .dag-node 一致
        var canvas = new Canvas
        {
            Height = 320,
            Background = B(Surface),
            ClipToBounds = true
        };
        canvas.Children.Add(new Border
        {
            BorderBrush = B(Rule), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusControl), Width = 600, Height = 300
        });
        for (var i = 0; i < Math.Min(nodes.Count, 6); i++)
        {
            var n = nodes[i];
            var isDebt = _store.GetDebts().Any(d => d.FromKp == n.KpId || d.ToKp == n.KpId);
            canvas.Children.Add(DagNode(
                n.Title, n.KpId,
                40 + (i % 3) * 150, 40 + (i / 3) * 130,
                isDebt ? "debt" : (i == 0 ? "chapter" : "term")));
        }

        var bookChips = new WrapPanel { ItemHeight = 48, ItemWidth = double.NaN };
        bookChips.Children.Add(BtnPrimary("会计基础"));
        bookChips.Children.Add(Ghost("示例图包"));

        return Page(
            PageHeader("识网", "切换下方教材卡片，图谱、知识点与该书真题会一起切换（不是空壳下拉框）。",
                Ghost("刷新图谱"), Ghost("预览债边"), BtnPrimary("用本书出今日题", () => Go("today")), Ghost("本书题·换一批")),
            Card(
                H2("教材切换"),
                Sub("点击卡片切换当前教材"),
                bookChips,
                StatusLine($"已切换：会计基础 · {nodes.Count} 节点 / {edges.Count} 边", ok: true)
            ),
            TwoCol(
                Card(
                    new StackPanel
                    {
                        Orientation = AvOrientation.Horizontal, Spacing = 8,
                        Children =
                        {
                            H2("知识图谱 "),
                            new Border
                            {
                                Child = new TextBlock { Text = "会计基础", FontSize = 12, Foreground = B(Muted) },
                                Background = B(SurfaceSoft), CornerRadius = new CornerRadius(999),
                                Padding = new Thickness(8, 2)
                            }
                        }
                    },
                    Sub("思维导图展示完整目录"),
                    DagToolbar(),
                    canvas
                ),
                Card(
                    H2("章节侧栏"),
                    Sub("完整目录 · 点击查看正文与自动出题"),
                    MakeChapterTree(),
                    new Border
                    {
                        Child = Body("选择左侧章节后显示正文"),
                        Background = B(SurfaceSoft), CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 12)
                    },
                    H2("节点检视"),
                    Sub("点击 DAG 节点。")
                ),
                1.5, 0.62)
        );
    }

    private static Control DagToolbar()
    {
        var legend = new StackPanel
        {
            Orientation = AvOrientation.Horizontal, Spacing = 10,
            Children =
            {
                LegendItem("章节", NodeChapter, true),
                LegendItem("知识点/关键词", NodeOk, false),
                new TextBlock { Text = "章节可切换", FontSize = 12, Foreground = B(Primary) }
            }
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(legend, 0);
        row.Children.Add(legend);
        row.Children.Add(new TextBlock
        {
            Text = "章节  全部章节", FontSize = 12, Foreground = B(Muted),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(row.Children[1], 1);
        return row;
    }

    private static Control LegendItem(string text, Color dot, bool square)
    {
        return new StackPanel
        {
            Orientation = AvOrientation.Horizontal, Spacing = 5,
            Children =
            {
                new Border
                {
                    Width = 9, Height = 9,
                    CornerRadius = square ? new CornerRadius(3) : new CornerRadius(5),
                    Background = B(dot), VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock { Text = text, FontSize = 12, Foreground = B(Muted) }
            }
        };
    }

    private static Border DagNode(string title, string sub, double x, double y, string kind)
    {
        Color bgTop, bgBot, border, ink;
        switch (kind)
        {
            case "chapter": bgTop = NodeChapterBgTop; bgBot = NodeChapterBgBot; border = Color.Parse("#4f86c6"); ink = Color.Parse("#1d3d5f"); break;
            case "debt": bgTop = NodeDebtBg; bgBot = NodeDebtBg; border = NodeTarget; ink = Color.Parse("#792e38"); break;
            case "section": bgTop = NodeSectionBg; bgBot = NodeSectionBg; border = Color.Parse("#8aa4c4"); ink = Ink; break;
            default: bgTop = NodeTermBgTop; bgBot = NodeTermBgBot; border = NodeOk; ink = Color.Parse("#1e5d43"); break;
        }
        return new Border
        {
            Width = 108, MinWidth = 108, MaxWidth = 168,
            Background = B(bgTop),
            BorderBrush = B(border), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 6, Color = Color.FromArgb(20, 45, 47, 50) }),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeight.Bold, Foreground = B(ink), TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = sub, FontSize = 10, Foreground = B(Muted), TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        }.Also(b => Canvas.SetLeft(b, x)).Also(b => Canvas.SetTop(b, y));
    }

    private static Control MakeChapterTree()
    {
        var sp = new StackPanel { Spacing = 4, MaxHeight = 280 };
        foreach (var t in new[] { "第1章 会计要素", "第2章 会计等式", "第3章 借贷记账法", "第4章 会计分录", "第5章 试算平衡" })
            sp.Children.Add(Body(t));
        return new Border
        {
            Child = sp, BorderBrush = B(Rule), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 12)
        };
    }

    private static StackPanel FormField(string label, Control field) => new()
    {
        Spacing = 6, Margin = new Thickness(0, 10, 0, 10),
        Children =
        {
            new TextBlock { Text = label, FontSize = 13, Foreground = B(Muted) },
            field
        }
    };

    private static Control MakeSelectAt(int selected, params string[] options)
    {
        var sp = new StackPanel { Spacing = 0 };
        var cur = BtnPrimary(options[Math.Clamp(selected, 0, options.Length - 1)]);
        sp.Children.Add(cur);
        return new Border
        {
            Child = sp, Background = B(Field), BorderBrush = B(Rule), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusControl), MinHeight = 42, Padding = new Thickness(10, 8)
        };
    }

    private static Control TwoCol(Control left, Control right, double w1, double w2)
    {
        // 手机单列堆叠（Web 窄屏同构），宽屏 1.45:0.7 / 1.5:0.62
        var sp = new StackPanel { Spacing = 16 };
        sp.Children.Add(left);
        sp.Children.Add(right);
        return sp;
    }

    private static Control PageHeader(string title, string desc, params Control[] actions)
    {
        var actionRow = new WrapPanel { ItemHeight = 48, ItemWidth = double.NaN };
        foreach (var a in actions) { a.Margin = new Thickness(0, 0, 8, 8); actionRow.Children.Add(a); }
        return new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 28), Spacing = 8,
            Children =
            {
                new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeight.Bold, /* letterspacing skipped */ },
                new TextBlock { Text = desc, FontSize = 13, Foreground = B(Muted), TextWrapping = TextWrapping.Wrap, MaxWidth = 720 },
                actionRow
            }
        };
    }
    // ══════════ 知债 ══════════
    private Control BuildDebtPage()
    {
        _debt.RefreshCommand.Execute(null);
        var items = new StackPanel { Spacing = 8 };
        foreach (var d in _debt.Items)
        {
            items.Children.Add(new Border
            {
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = $"{d.FromTitle} → {d.ToTitle}", FontWeight = FontWeight.Bold, Foreground = B(Ink) },
                        new TextBlock { Text = $"impact={d.Impact:0.000000}", FontSize = 12, Foreground = B(Muted) },
                        new TextBlock
                        {
                            Text = d.Status == "open" ? "待修复" : d.Status == "repairing" ? "修复中" : "已销账",
                            FontSize = 12,
                            Foreground = B(d.Status == "open" ? Danger : NodeOk)
                        }
                    }
                },
                Background = B(Surface), BorderBrush = B(Rule), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(RadiusGroup), Padding = new Thickness(14, 12)
            });
        }
        return Page(
            H1("知债：星穹学途"),
            Sub("BASELINE 诊断 · What-if · 14 天修复计划"),
            Row(
                BtnPrimary("重新诊断", () => { _debt.RefreshCommand.Execute(null); Go("debt"); }),
                Ghost("生成 14 天计划", () => { _debt.BuildPlanCommand.Execute(null); Go("debt"); }),
                Ghost("销账检查", () => Go("progress"))
            ),
            Card(H2("红边（按 impact）"), _debt.Items.Count == 0 ? Sub(_debt.EmptyText) : items),
            Card(H2("修复计划"), Sub(_debt.PlanBrief))
        );
    }

    // ══════════ 今日（与 Web 今日任务一致）══════════
    private Control BuildTodayPage()
    {
        _today.LoadCommand.Execute(null);
        var cards = new StackPanel { Spacing = 10 };
        foreach (var t in _today.Tasks)
        {
            var kp = t.KpId;
            cards.Children.Add(new Border
            {
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = $"巩固「{t.KpTitle}」", FontSize = 16, FontWeight = FontWeight.SemiBold },
                        BtnPrimary("开始", () => { _practice.LoadForKpCommand.Execute(kp); Go("agent"); })
                    }
                },
                Background = B(Surface), BorderBrush = B(Rule), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(RadiusGroup), Padding = new Thickness(16, 14)
            });
        }
        // 练习面板内嵌在今日下方（与 Desktop 详情区语义一致）
        var slider = new Slider { Minimum = 1, Maximum = 5, Value = 3, TickFrequency = 1, IsSnapToTickEnabled = true, MinHeight = 40 };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) _practice.Confidence = (int)slider.Value;
        };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = B(Muted) };
        _practice.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(_practice.Feedback)) feedback.Text = _practice.Feedback; };

        return Page(
            H1("今日任务"),
            Sub(_today.Coach),
            Bar(_today.Progress),
            cards.Children.Count == 0 ? Sub(_today.EmptyText) : cards,
            Card(
                H2("练习"),
                Sub("信心"),
                slider,
                Row(
                    BtnPrimary("提交", () => _practice.SubmitCommand.Execute(null)),
                    Ghost("下一题", () => _practice.NextCommand.Execute(null))
                ),
                feedback
            ),
            Quiet("Demo +1 天", () => { _today.NextDayCommand.Execute(null); Go("today"); })
        );
    }

    // ══════════ 智能体（Web page-agent / agentBubble 同构）══════════
    private Control BuildAgent()
    {
        var chat = new StackPanel { Spacing = 0, Margin = new Thickness(0) };
        var chatBox = new ScrollViewer
        {
            MinHeight = 220, MaxHeight = 420,
            Background = B(Surface), BorderBrush = B(Rule), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(8)
        };
        chatBox.Content = chat;

        void Bubble(bool user, string text)
        {
            var bubble = new Border
            {
                Child = new TextBlock
                {
                    Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    Foreground = B(user ? Colors.White : Ink)
                },
                Background = B(user ? Primary : SurfaceSoft),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 10),
                MaxWidth = 300,
                HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 8)
            };
            var row = new StackPanel
            {
                Orientation = AvOrientation.Horizontal,
                HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Children = { bubble }
            };
            chat.Children.Add(row);
        }

        Bubble(false, "你好，我是知债学习助手。可问「我的知识债」「生成计划」「这周学不完」等。危机词会自动转人工。");

        var input = new TextBox
        {
            Watermark = "例如：我的会计等式总是搞不清，怎么办？",
            MinHeight = 42, MinWidth = 200,
            CornerRadius = new CornerRadius(18),
            Background = B(Field), BorderBrush = B(Rule),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        void Send()
        {
            var t = (input.Text ?? "").Trim();
            if (t.Length == 0) return;
            Bubble(true, t);
            var reply = t.Contains("债") || t.Contains("诊断") ? BuildDiagnoseReply()
                : t.Contains("计划") ? "已按当前债边生成 14 天修复草图（任一天 ≤40 分钟）。见「知债」页。"
                : "我可以帮你：知识债诊断、14 天计划、今日任务、图谱查看。";
            Bubble(false, reply);
            input.Text = "";
        }
        var send = BtnPrimary("发送", Send);

        return Page(
            PageHeader("受约束智能体",
                "意图路由 · 危机转人工 · 敏感词脱敏 · 工具白名单（§44）。演示账户 demo-student-a / role=student",
                Ghost("刷新工具清单"), Ghost("新会话")),
            Card(
                H2("对话"),
                Sub("sessionId: local-sqlite"),
                chatBox,
                new StackPanel
                {
                    Orientation = AvOrientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0),
                    Children = { input, send }
                },
                Row(
                    Quiet("压力求助", () => { Bubble(true, "我这周学不完计划了"); Bubble(false, "已记录减负诉求。可告诉我每天可用分钟数（如 20）。"); }),
                    Quiet("危机词测试", () => { Bubble(true, "我不想活了"); Bubble(false, "你并不孤单。请立即联系身边信任的人或当地心理援助热线。系统不会把这里当作处分依据。"); }),
                    Quiet("诊断债", () => { Bubble(true, "帮我诊断知识债"); Bubble(false, BuildDiagnoseReply()); }),
                    Quiet("生成计划", () => { Bubble(true, "生成14天计划"); Bubble(false, "已按当前债边生成 14 天修复草图（任一天 ≤40 分钟）。"); })
                ),
                StatusLine("ok · debt.diagnose", ok: true)
            ),
            Card(H2("工具清单"), Sub("student · 本机白名单"), Sub("crisis / banned patterns 已加载"))
        );
    }
    private string BuildDiagnoseReply()
    {
        _debt.RefreshCommand.Execute(null);
        if (_debt.Items.Count == 0) return "诊断完成：当前没有开放红边。";
        var sb = new System.Text.StringBuilder("诊断完成：\n");
        var i = 1;
        foreach (var d in _debt.Items)
            sb.AppendLine($"{i++}. {d.FromTitle} → {d.ToTitle}  impact={d.Impact:0.######}");
        return sb.ToString();
    }
    // ══════════ 画像 ══════════
    private Control BuildProfile()
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var m in _store.GetAllMastery())
        {
            list.Children.Add(new Border
            {
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = _store.TitleOf(m.KpId), FontWeight = FontWeight.SemiBold },
                        Bar(Math.Clamp(m.Score, 0, 1)),
                        new TextBlock { Text = $"掌握度 {m.Score:0.000000} · streak {m.Streak}", FontSize = 12, Foreground = B(Muted) }
                    }
                },
                Background = B(Surface), BorderBrush = B(Rule), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(RadiusGroup), Padding = new Thickness(14, 12)
            });
        }
        return Page(
            H1("画像"),
            Sub("掌握度 · 可随时 opt-out"),
            Card(H2("掌握度"), list),
            Card(BtnPrimary("关闭个性化画像（opt-out）"), Sub("标签仅用于学习规划。"))
        );
    }

    // ══════════ 账户 ══════════
    private Control BuildAccount()
    {
        var status = StatusLine("未登录", ok: false);
        return Page(
            H1("账户"),
            Sub("本机演示账户 · 同意/撤销教师可见"),
            Card(
                H2("登录"),
                new TextBox { Text = "demo@astralpath.local", Watermark = "邮箱" },
                new TextBox { PasswordChar = '*', Text = "demo123456", Watermark = "密码" },
                Row(BtnPrimary("登录"), Ghost("退出")),
                status
            ),
            Card(
                H2("教师可见授权"),
                BtnPrimary("同意授权", () => { _settings.GrantConsentCommand.Execute(null); }),
                new AvButton
                {
                    Content = "撤销授权", MinHeight = 40, Margin = new Thickness(0, 8, 0, 0),
                    Foreground = B(Danger), BorderBrush = B(Danger), Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(RadiusControl),
                    Command = _settings.RevokeConsentCommand
                }
            ),
            Card(
                H2("数据"),
                Row(
                    Ghost("导出数据", () => _settings.ExportCommand.Execute(null)),
                    Ghost("重置", () => { _settings.ResetCommand.Execute(null); Go("home"); })
                ),
                Sub(_settings.StatusMessage ?? "")
            )
        );
    }
}
