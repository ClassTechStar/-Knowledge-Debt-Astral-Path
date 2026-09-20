using AstralPath.Contracts;

namespace AstralPath.Infrastructure;

/// <summary>进程内资料/图谱/今日任务注册表（Materials 与 Auth 共享）。</summary>
public static class MaterialRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, MaterialDoc> Materials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AutoKnowledgeGraph> Graphs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<TodayTaskDto>> MaterialTasks = new(StringComparer.OrdinalIgnoreCase);
    private static string? _latestMaterialName;
    private static List<TodayTaskDto> _latestTasks = new();

    public static void UpsertMaterial(MaterialDoc doc)
    {
        lock (Gate) Materials[doc.Id] = doc;
    }

    public static void UpsertGraph(AutoKnowledgeGraph graph, List<TodayTaskDto>? tasks = null)
    {
        lock (Gate)
        {
            Graphs[graph.GraphId] = graph;
            if (tasks != null)
            {
                MaterialTasks[graph.GraphId] = tasks;
                _latestTasks = tasks;
                _latestMaterialName = graph.MaterialName;
            }
        }
    }

    public static List<MaterialDoc> ListMaterials()
    {
        lock (Gate) return Materials.Values.OrderByDescending(m => m.UpdatedAt).ToList();
    }

    public static MaterialDoc? GetMaterial(string id)
    {
        lock (Gate) return Materials.GetValueOrDefault(id);
    }

    public static List<AutoKnowledgeGraph> ListGraphs()
    {
        lock (Gate) return Graphs.Values.OrderByDescending(g => g.GeneratedAt).ToList();
    }

    public static AutoKnowledgeGraph? GetGraph(string graphId)
    {
        lock (Gate) return Graphs.GetValueOrDefault(graphId);
    }

    public static List<TodayTaskDto> GetTasksForGraph(string graphId)
    {
        lock (Gate) return MaterialTasks.GetValueOrDefault(graphId) ?? new List<TodayTaskDto>();
    }

    public static (string MaterialName, List<TodayTaskDto> Tasks) GetLatestTasks()
    {
        lock (Gate) return (_latestMaterialName ?? "未指定教材", _latestTasks.ToList());
    }

    public static void MergeLatestTasks(string materialName, List<TodayTaskDto> tasks)
    {
        lock (Gate)
        {
            if (tasks.Count == 0) return;
            _latestMaterialName = materialName;
            _latestTasks = tasks;
        }
    }
}
