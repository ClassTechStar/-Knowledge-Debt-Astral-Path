using AstralPath.Infrastructure;
using Xunit;

namespace AstralPath.Desktop.Tests;

/// <summary>
/// OcrHost 路径解析冒烟（P2 审计 M6 下沉后的回归锁）：
/// ① 开发态必须能从测试输出目录向上回溯找到仓库 tools\ocr_pipeline.py；
/// ② 解析结果**不得**包含任何机器特定路径（C4 回归哨兵）；
/// ③ Python 解析永远有回退值，不抛异常。
/// </summary>
public class OcrHostTests
{
    [Fact]
    public void ResolveScript_Finds_Repo_Pipeline_Via_WalkUp()
    {
        var script = OcrHost.ResolveScript();
        Assert.NotNull(script);
        Assert.True(File.Exists(script), $"应存在：{script}");
        Assert.EndsWith("ocr_pipeline.py", script!, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveScript_Never_Uses_Hardcoded_Machine_Fallbacks()
    {
        // 注意：开发态回溯出的仓库路径本身就位于用户目录下（C:\Users\<user>\Documents\...），
        // 含用户名是**合法的运行时事实**；这里锁的是「代码里不再有硬编码的机器特定回退」——
        // 即曾写死的 MiMo 工程目录绝不能再出现在解析结果中（C4 回归哨兵）。
        var script = OcrHost.ResolveScript() ?? "";
        Assert.DoesNotContain("XiaomiMiMo", script);
        Assert.DoesNotContain("Knowledge Debt Astral Path", script);
    }

    [Fact]
    public void ResolvePython_Always_Returns_Fallback()
    {
        var python = OcrHost.ResolvePython();
        Assert.False(string.IsNullOrWhiteSpace(python));
    }

    [Fact]
    public void ResolveTessdata_Returns_Null_Or_Existing_Directory()
    {
        var dir = OcrHost.ResolveTessdata();
        Assert.True(dir is null || Directory.Exists(dir));
    }
}
