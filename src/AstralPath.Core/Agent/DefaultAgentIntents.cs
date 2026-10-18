namespace AstralPath.Core.Agent;

/// <summary>
/// 默认意图登记表（P2：单一事实源对拍；P5-1 起同时是运行时表）。
/// 与 tools/agent-intents.json 及 deploy/monolith-web/index.html 的 INTENTS 字面量
/// 逐字一致；scripts/verify_agent_intents.py 负责对拍，改动任何一方必须同步。
/// 角色为逗号分隔多值（与 JSON 的 roles 数组一一对应）。
/// </summary>
public static class DefaultAgentIntents
{
    public static readonly AgentIntent[] Table =
    {
        new(0, "crisis.handoff", "student,teacher,admin,demo", Array.Empty<string>(), Array.Empty<string>(), "危机转介"),
        new(1, "debt.diagnose", "student,demo", new[] { "知识债", "债边", "诊断", "红边", "薄弱", "搞不清", "漏洞", "哪里不会", "哪里薄弱", "帮我分析" }, Array.Empty<string>(), "知识债诊断"),
        new(2, "debt.explain", "student,teacher,demo", new[] { "解释", "为什么", "怎么来的", "公式", "讲解", "怎么回事", "什么意思", "怎么理解", "什么原理" }, Array.Empty<string>(), "债边解释"),
        new(3, "plan.create", "student,demo", new[] { "计划", "14天", "安排", "复习计划", "做个计划", "怎么安排", "复习规划" }, Array.Empty<string>(), "生成修复计划"),
        new(4, "plan.rebalance", "student,demo", new[] { "减负", "重排", "调整计划", "太重", "换个计划", "改一下计划" }, Array.Empty<string>(), "计划减负"),
        new(5, "today.tasks", "student,demo", new[] { "今日", "今天做", "今日任务", "今天学什么", "现在学什么", "该学什么", "今天干什么" }, Array.Empty<string>(), "今日任务"),
        new(6, "practice.start", "student,demo", new[] { "练习", "做题", "小测", "测验", "来几道", "出几道", "出个题", "刷几道", "做几道", "练一下" }, Array.Empty<string>(), "开始练习"),
        new(7, "progress.check", "student,demo", new[] { "进度", "销账", "进展", "streak", "学得怎么样", "看看进度", "掌握怎么样" }, Array.Empty<string>(), "销账进度"),
        new(8, "graph.view", "student,teacher,demo", new[] { "图谱", "识网", "知识图谱", "思维导图", "关系图", "知识网", "看看图" }, Array.Empty<string>(), "查看图谱"),
        new(9, "material.parse", "student,teacher,demo", new[] { "解析资料", "上传", "OCR", "教材", "藏书", "解析文件", "导入教材", "上传课本" }, Array.Empty<string>(), "解析资料"),
        new(22, "material.list", "student,teacher,demo", new[] { "列出资料", "教材列表", "藏书", "有哪些书", "书单", "几本书", "已解析教材", "有什么教材" }, Array.Empty<string>(), "列出教材"),
        new(23, "material.switch", "student,teacher,demo", new[] { "切换教材", "换书", "切书", "切换到", "换一本", "切到", "切过去", "换个教材", "换本书" }, Array.Empty<string>(), "切换教材"),
        new(10, "kb.search", "student,teacher,demo", new[] { "知识库", "检索", "什么是", "查资料", "文档", "搜索", "搜一下", "查一下", "找找", "章节", "帮我找", "找找看", "查一查", "讲一下", "知识点", "介绍", "概要", "概述", "概括" }, Array.Empty<string>(), "知识库检索"),
        new(11, "kb.ingest", "teacher,admin", new[] { "入库", "上传文档", "发布知识" }, Array.Empty<string>(), "知识入库"),
        new(12, "profile.view", "student,demo", new[] { "画像", "我的标签", "学习画像", "我的情况" }, Array.Empty<string>(), "查看画像"),
        new(13, "profile.optout", "student,demo", new[] { "退出画像", "关闭画像", "opt-out" }, Array.Empty<string>(), "画像退出"),
        new(14, "teacher.hotspots", "teacher,admin,demo", new[] { "热点", "班级", "共性问题", "大家的情况" }, Array.Empty<string>(), "班级热点"),
        new(15, "consent.grant", "student,demo", new[] { "授权", "同意可见", "consent", "允许查看" }, Array.Empty<string>(), "授权教师可见"),
        new(16, "consent.revoke", "student,demo", new[] { "撤销", "取消授权", "关闭可见" }, Array.Empty<string>(), "撤销授权"),
        new(17, "whatif.simulate", "student,teacher,demo", new[] { "what-if", "如果", "模拟", "假设", "假如", "要是", "试想" }, Array.Empty<string>(), "What-if 模拟"),
        new(18, "narrative.read", "student,demo", new[] { "故事", "叙事", "讲解", "讲个故事", "举个例子", "给我讲讲", "讲一讲" }, Array.Empty<string>(), "叙事讲解"),
        new(19, "sale.check", "student,demo", new[] { "销账检查", "能否销账", "销账条件", "能销账吗", "销账了没" }, Array.Empty<string>(), "销账检查"),
        new(24, "chapter.read", "student,teacher,demo", new[] { "目录", "读一下", "讲了什么", "讲了啥", "章节", "这一章", "内容是什么", "翻到", "第几章", "前几章", "本章", "这章", "有什么内容", "不明白", "不懂", "不理解", "不清楚" }, Array.Empty<string>(), "章节内容问答"),
        new(25, "graph.path", "student,teacher,demo", new[] { "什么关系", "怎么联系", "有没有关", "路径", "谁先", "先后", "前置", "怎么到", "怎么学", "学习顺序", "什么联系", "有关吗", "先后顺序" }, Array.Empty<string>(), "先修路径"),
        new(20, "meta.help", "student,teacher,admin,demo", new[] { "帮助", "怎么用", "说明", "你能干什么", "你会什么", "功能有哪些" }, Array.Empty<string>(), "帮助"),
        new(21, "meta.feedback", "student,teacher,demo", new[] { "反馈", "建议", "不好用" }, Array.Empty<string>(), "反馈")
    };
}
