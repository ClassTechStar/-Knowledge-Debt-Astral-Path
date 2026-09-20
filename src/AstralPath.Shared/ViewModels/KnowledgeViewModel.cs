using System.Collections.ObjectModel;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>文档展示行：把 <see cref="KbDocRow"/> 包装为带中文徽章文案的列表行。</summary>
public sealed record KbDocItem(KbDocRow Doc)
{
    public string DocumentId => Doc.DocumentId;

    public string Title => Doc.Title;

    public string Visibility => Doc.Visibility;

    public string Status => Doc.Status;

    public int CurrentVersion => Doc.CurrentVersion;

    public string[] Tags => Doc.Tags;

    public string OwnerUserId => Doc.OwnerUserId;

    /// <summary>可见性徽章文案（不靠颜色单独表意）。</summary>
    public string VisibilityLabel => Visibility switch
    {
        "private" => "私有",
        "course" => "课程可见",
        "public" => "公开",
        _ => Visibility
    };

    public bool IsPrivate => Visibility == "private";

    public bool IsCourse => Visibility == "course";

    public bool IsPublic => Visibility == "public";

    public string StatusLabel => Status == "published" ? "已发布" : "草稿";

    /// <summary>草稿没有发布版本，按方案约定显示为「-」。</summary>
    public string VersionText => Status == "published" ? $"v{CurrentVersion}" : "-";

    public bool HasTags => Tags is { Length: > 0 };

    public string AccessibleName => $"{Title}，{VisibilityLabel}，{StatusLabel}，版本 {VersionText}。";
}

/// <summary>检索命中行：snippet 与匹配度（保留 4 位小数）。</summary>
public sealed record KbHitItem(KbHitRow Hit)
{
    public string DocumentId => Hit.DocumentId;

    public string ChunkId => Hit.ChunkId;

    public string Title => Hit.Title;

    public string Snippet => Hit.Snippet;

    public double Score => Hit.Score;

    public string Visibility => Hit.Visibility;

    public string VisibilityLabel => Visibility switch
    {
        "private" => "私有",
        "course" => "课程可见",
        "public" => "公开",
        _ => Visibility
    };

    public string ScoreText => Score.ToString("0.0000");

    public string AccessibleName => $"{Title}，匹配度 {ScoreText}，{VisibilityLabel}。{Snippet}";
}

/// <summary>可见性下拉项（代码值 + 中文说明）。</summary>
public sealed record VisibilityChoice(string Code, string Label);

/// <summary>
/// 知识库页（方案 §45）。
///
/// 三块能力：文档清单（含可见性 / 状态 / 版本 / 标签）、检索（结果按角色与可见性过滤）、
/// 新建草稿（显式选择可见性）。所有读取都走 <see cref="IAppDataSource"/>，权限判断不在端上自行放宽。
/// </summary>
public sealed partial class KnowledgeViewModel : ViewModelBase
{
    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;

    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    public KnowledgeViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;

        VisibilityOptions = new ObservableCollection<VisibilityChoice>
        {
            new("private", "私有（仅自己可见）"),
            new("course", "课程可见（同课程成员）"),
            new("public", "公开（所有登录用户）")
        };
        _selectedVisibility = VisibilityOptions[0];

        Reload();
    }

    public override string Title => "知识库";

    /// <summary>文档清单（按数据源返回顺序）。</summary>
    public ObservableCollection<KbDocItem> Documents { get; } = new();

    /// <summary>检索命中片段。</summary>
    public ObservableCollection<KbHitItem> Hits { get; } = new();

    /// <summary>新建文档时可选的可见性。</summary>
    public ObservableCollection<VisibilityChoice> VisibilityOptions { get; }

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private string _searchEmptyReason = string.Empty;

    [ObservableProperty]
    private string _newTitle = string.Empty;

    [ObservableProperty]
    private string _newText = string.Empty;

    [ObservableProperty]
    private VisibilityChoice? _selectedVisibility;

    /// <summary>一次性操作反馈（如新建成功后的文档 ID）。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    public bool HasDocuments => Documents.Count > 0;

    public bool IsDocumentsEmpty => Documents.Count == 0;

    public bool HasHits => Hits.Count > 0;

    /// <summary>搜索框为空时的提示。</summary>
    public bool IsQueryEmpty => string.IsNullOrWhiteSpace(SearchQuery);

    public string SearchHint => "输入关键词后回车搜索";

    /// <summary>标题与正文都非空才允许创建（避免产生无法检索的空文档）。</summary>
    public bool CanCreate => !string.IsNullOrWhiteSpace(NewTitle) && !string.IsNullOrWhiteSpace(NewText);

    /// <summary>权限说明：检索结果按角色与可见性过滤，且是 fail-closed。</summary>
    public string PermissionNote =>
        "检索结果按调用者角色与文档可见性过滤（fail-closed：无权限即不返回），可见性变更需显式操作。";

    public string ModeLabel => _data.ModeLabel;

    public override void Load(PageContext context)
    {
        _ctx = context;
        Reload();
    }

    private void Reload() => RunGuarded(() =>
    {
        var docs = _data.ListKbDocuments();
        Documents.Clear();
        foreach (var doc in docs) Documents.Add(new KbDocItem(doc));
        NotifyDerived();
    });

    /// <summary>检索：空关键词不发起请求，只给出输入提示。</summary>
    [RelayCommand]
    private void Search() => RunGuarded(() =>
    {
        if (IsQueryEmpty)
        {
            Hits.Clear();
            HasSearched = false;
            SearchEmptyReason = string.Empty;
            NotifyDerived();
            return;
        }

        var userId = _ctx.TeacherSide ? _ctx.TeacherId : _ctx.StudentId;
        var rows = _data.KbSearch(userId, _ctx.Role, SearchQuery.Trim());

        Hits.Clear();
        foreach (var row in rows) Hits.Add(new KbHitItem(row));

        HasSearched = true;
        SearchEmptyReason = Hits.Count == 0
            ? "没有匹配的片段。可能原因：关键词未命中，或该文档对当前角色不可见（fail-closed）。"
            : string.Empty;
        NotifyDerived();
    });

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        Hits.Clear();
        HasSearched = false;
        SearchEmptyReason = string.Empty;
        NotifyDerived();
    }

    /// <summary>新建文档：始终以草稿落库（版本 -），可见性由用户显式选择。</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create() => RunGuarded(() =>
    {
        var visibility = SelectedVisibility?.Code ?? "private";
        var doc = _data.CreateKbDocument(NewTitle.Trim(), _ctx.StudentId, visibility, NewText);

        StatusMessage = $"已创建草稿：{doc.DocumentId}（{doc.Visibility} · 版本 -）";
        NewTitle = string.Empty;
        NewText = string.Empty;
        Reload();
    });

    [RelayCommand]
    private void Refresh() => Reload();

    partial void OnSearchQueryChanged(string value) => NotifyDerived();

    partial void OnNewTitleChanged(string value) => CreateCommand.NotifyCanExecuteChanged();

    partial void OnNewTextChanged(string value) => CreateCommand.NotifyCanExecuteChanged();

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasDocuments));
        OnPropertyChanged(nameof(IsDocumentsEmpty));
        OnPropertyChanged(nameof(HasHits));
        OnPropertyChanged(nameof(IsQueryEmpty));
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.NotifyCanExecuteChanged();
    }
}
