using AstralPath.Core.Agent;

namespace AstralPath.Core.Quiz;

/// <summary>
/// AI 出题编排（P0 迁移，源：Astralpath aiGenQuestion 的 loop，L4475-4486；出题提示词 T8.3 版，L4453-4454）。
/// 流程：组装上下文 → 调模型 → parseQuiz → 与池重复则重试一次（共 2 次尝试）→ 入池。
/// 重试后仍重复：交付题目但不入池（源 L4481 注释行为）。
/// </summary>
public sealed class AiQuestionOrchestrator
{
    private readonly MimoClient _client;

    public AiQuestionOrchestrator(MimoClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>出题系统提示词（五层级 + 拓展标注 + 禁空泛 + 严格 JSON）。</summary>
    public static string BuildSystemPrompt(string level)
    {
        return "你是资深教材命题专家。以给定的教材内容为主要命题依据，同时允许运用你的学科知识适度拓展（补充背景、引入贴合知识点的实例或情境），使命题更有深度与区分度。硬性要求："
            + "1) 题目科学严谨、表述准确无歧义，正确答案唯一且无争议；"
            + "2) 题干须指向明确的考查点或具体情境，禁止空泛的\"下列说法正确的是\"；"
            + "3) 本题考查层级为「" + level + "」；"
            + "4) 三个干扰项似是而非且确实错误；"
            + "5) 若正确项或关键考点超出给定教材片段，须在 explain 开头注明\"【拓展】\"；"
            + "6) 严格输出 JSON，不要任何多余文字：{\"stem\":\"题干\",\"options\":[\"选项1\",\"选项2\",\"选项3\",\"选项4\"],\"answer\":0,\"explain\":\"解析：正确项依据与主要干扰项错因，60字内\"}";
    }

    /// <summary>出题用户消息（教材名 + 知识点 + 层级 + 上下文 + 拓展提示）。</summary>
    public static string BuildUserPrompt(QuizBook book, QuizNode? node, string level)
    {
        var topic = node != null ? node.Title : "全书综合";
        return "教材《" + book.Name + "》\n命题知识点：" + topic + "\n考查层级：" + level
            + "\n教材内容如下：\n" + AiQuestionGen.BuildQuizContext(book, node)
            + "\n命题提示：可在贴合知识点的范围内适当超越给定材料（补充背景、实例或跨章联系），但不得与教材已陈述的事实冲突。";
    }

    /// <summary>
    /// 生成一题并管理题池。解析失败/与池重复 → 自动重试一次（共 2 次）。
    /// 返回 null 表示两次均失败；返回的题目若 DuplicateInPool=true 表示重试后仍重复（交付但未入池）。
    /// </summary>
    public async Task<AiQuestionGenResult?> GenerateAsync(
        MimoConfig cfg,
        QuizBook book,
        QuizNode? node,
        List<AiQuestion> pool,
        string? level = null,
        Action<string, bool>? onDelta = null,
        CancellationToken ct = default)
    {
        level ??= AiQuestionGen.PickQuizLevel(pool.Count(q => q.Layer == "ai"));
        var messages = new[]
        {
            new MimoMessage("system", BuildSystemPrompt(level)),
            new MimoMessage("user", BuildUserPrompt(book, node, level))
        };
        var options = new MimoOptions(MaxTokens: 1500, Temperature: 0.6, TimeoutMs: 60000, OnDelta: onDelta);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            string? text;
            try
            {
                var result = await _client.CompleteAsync(cfg, messages, options, ct).ConfigureAwait(false);
                text = result.Text;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 外部取消：如实上抛
            }
            catch (Exception)
            {
                // 传输/解析故障：重试一次（T14），第二次仍失败 → null（调用方回退规则法）
                if (attempt < 2) continue;
                return null;
            }

            var q = AiQuestionGen.ParseQuiz(text, node?.Id, level);
            if (q == null)
            {
                if (attempt < 2) continue;
                return null;
            }
            if (AiQuestionGen.IsDuplicate(q.Stem, pool))
            {
                if (attempt < 2) continue;
                return new AiQuestionGenResult(q, Level: level, DuplicateInPool: true); // 重试后仍重复：交付但不入池
            }
            AiQuestionGen.TryAddToPool(pool, q);
            return new AiQuestionGenResult(q, Level: level, DuplicateInPool: false);
        }
        return null;
    }
}

/// <summary>出题结果：题目 + 命题层级 + 是否仅交付未入池。</summary>
public sealed record AiQuestionGenResult(AiQuestion Question, string Level, bool DuplicateInPool);
