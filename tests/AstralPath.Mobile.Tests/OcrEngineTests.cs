using AstralPath.Core.Ocr;
using Xunit;

namespace AstralPath.Mobile.Tests;

/// <summary>OCR v2：混淆修复 / 断行合并 / 噪声 / TSV 重建 / 投票 / 阅读顺序 / 质量分。</summary>
public class OcrEngineTests
{
    [Fact]
    public void OC01_FixConfusions_FullwidthAndGlue()
    {
        var s = OcrTextEngine.FixConfusions("会计等式：资产（Assets）＝负债");
        Assert.Contains("(Assets)", s);
        Assert.Contains("(Assets)", s);
        Assert.DoesNotContain("＝", s); // 全角等号已归一
    }

    [Fact]
    public void OC02_ReconstructCjkLines_MergesSoftWrap()
    {
        var raw = "会计等式是复式记账的\n基础，必须先掌握。";
        var s = OcrTextEngine.ReconstructCjkLines(raw);
        Assert.Contains("会计等式是复式记账的基础", s.Replace(" ", ""));
    }

    [Fact]
    public void OC03_FilterNoise_DropsJunkKeepsNumber()
    {
        var s = OcrTextEngine.FilterNoise("page\n@@@\n37\n会计要素\n");
        Assert.DoesNotContain("page", s);
        Assert.Contains("37", s);
        Assert.Contains("会计要素", s);
    }

    [Fact]
    public void OC04_TsvWords_ByXY()
    {
        var words = new[]
        {
            new OcrTextEngine.WordTok("负债", 92, 20, 100),
            new OcrTextEngine.WordTok("资产", 95, 2, 100),
            new OcrTextEngine.WordTok("garbage", 5, 2, 100), // 低置信丢弃
            new OcrTextEngine.WordTok("权益", 88, 2, 140),
        };
        var text = OcrTextEngine.ReconstructFromTsvWords(words, minConf: 40);
        Assert.Contains("资产 负债", text); // 同行按 X
        Assert.DoesNotContain("garbage", text);
    }

    [Fact]
    public void OC05_VotePasses_MajorityWins()
    {
        var a = "会计要素\n会计等式\n幻觉行X";
        var b = "会计要素\n会计等式\n另一幻觉";
        var c = "会计要素\n借贷记账法\n会计等式";
        var voted = OcrTextEngine.VotePasses(new[] { a, b, c });
        Assert.Contains("会计要素", voted);
        Assert.Contains("会计等式", voted);
        Assert.DoesNotContain("幻觉行X", voted);
    }

    [Fact]
    public void OC06_ReadingOrder_TwoColumns()
    {
        // 左栏两行，右栏一行
        var boxes = new[]
        {
            new ReadingOrder.Box("L1", 10, 10, 40, 12),
            new ReadingOrder.Box("L2", 10, 40, 40, 12),
            new ReadingOrder.Box("R1", 200, 10, 40, 12),
        };
        var order = ReadingOrder.Sort(boxes).Select(b => b.Text).ToList();
        Assert.Equal(new[] { "L1", "L2", "R1" }, order); // 先读完左栏再到右栏
    }

    [Fact]
    public void OC07_Quality_Grade()
    {
        var good = OcrTextEngine.Score("会计等式\n借贷记账法\n会计分录");
        Assert.Equal("A", good.Grade);
        var bad = OcrTextEngine.Score("page\n@@\n##\n$$");
        Assert.True(bad.Grade is "C" or "D");
    }

    [Fact]
    public void OC08_Refine_EndToEnd()
    {
        var raw = "page\n会计等式是复式记账的\n基础。Data0l\n@@@";
        var s = OcrTextEngine.Refine(raw);
        Assert.DoesNotContain("page", s);
        Assert.DoesNotContain("@@@", s);
        Assert.Contains("会计等式", s);
    }
}
