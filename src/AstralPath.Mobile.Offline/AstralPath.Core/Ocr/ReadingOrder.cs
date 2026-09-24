namespace AstralPath.Core.Ocr;

/// <summary>
/// 版面阅读顺序 v3：XY-cut 分栏 + 列内 top-to-bottom。
///
/// 相对 v2 的修复：
/// ① 行带 v2 写死 6px（Math.Round(Y/6)），大字号/小字号页面直接串行；
///    v3 用「中位框高 × 0.6」自适应
/// ② 分栏 v2 只用框中心点做 48 桶直方图，宽框（跨栏图注）会按中心被切进某一栏；
///    v3 用「覆盖度投影」并按「最宽连续空档」切分，且先摘出跨栏宽框单独成段
/// ③ v2 的 bestVal > 2 是拍脑袋阈值；v3 用「空档宽度 + 空档内无覆盖」双条件
/// </summary>
public static class ReadingOrder
{
    public sealed record Box(string Text, double X, double Y, double W, double H);

    public static IReadOnlyList<Box> Sort(IReadOnlyList<Box> boxes, double minColWidth = 40)
    {
        if (boxes.Count <= 1) return boxes;
        var band = EstimateRowBand(boxes);
        return Cut(boxes.ToList(), minColWidth, band);
    }

    /// <summary>按中位框高估计行带：太小会把同一行拆开，太大会把两行并一行。</summary>
    public static double EstimateRowBand(IReadOnlyList<Box> boxes)
    {
        var hs = boxes.Where(b => b.H > 0).Select(b => b.H).OrderBy(h => h).ToList();
        if (hs.Count == 0) return 6;
        var median = hs[hs.Count / 2];
        return Math.Clamp(median * 0.6, 3, 40);
    }

    private static List<Box> OrderWithinColumn(List<Box> boxes, double band)
        => boxes
            .OrderBy(b => Math.Round(b.Y / band))
            .ThenBy(b => b.X)
            .ToList();

    private static List<Box> Cut(List<Box> boxes, double minColWidth, double band)
    {
        if (boxes.Count <= 1) return boxes;

        var minX = boxes.Min(b => b.X);
        var maxX = boxes.Max(b => b.X + b.W);
        var width = maxX - minX;

        if (width < minColWidth * 2)
            return OrderWithinColumn(boxes, band);

        // ① 先摘出跨栏宽框（图注/整幅图），它们不属于任何一栏
        var fullWidth = boxes.Where(b => b.W > width * 0.55).ToList();
        var cols = fullWidth.Count > 0 ? boxes.Except(fullWidth).ToList() : boxes;

        if (cols.Count == 0) return OrderWithinColumn(boxes, band);

        var cMinX = cols.Min(b => b.X);
        var cMaxX = cols.Max(b => b.X + b.W);
        var cWidth = cMaxX - cMinX;
        if (cWidth < minColWidth * 2 || cols.Count <= 1)
        {
            // 无法再分栏：宽框按 Y 插回，其余按列内顺序
            return Merge(fullWidth, OrderWithinColumn(cols, band), band);
        }

        // ② 覆盖度投影：每个桶记录有多少框覆盖（不是中心点计数）
        var bins = (int)Math.Clamp(cWidth / 10.0, 16, 96);
        var cover = new int[bins];
        foreach (var b in cols)
        {
            var a = (int)Math.Clamp(Math.Floor((b.X - cMinX) / cWidth * bins), 0, bins - 1);
            var z = (int)Math.Clamp(Math.Ceiling((b.X + b.W - cMinX) / cWidth * bins) - 1, 0, bins - 1);
            for (var i = a; i <= z; i++) cover[i]++;
        }

        // ③ 找中间区域（10%–90%）里最宽的「零覆盖连续段」
        var lo = Math.Max(1, bins / 10);
        var hi = bins - lo;
        var bestStart = -1; var bestLen = 0; var curStart = -1; var curLen = 0;
        for (var i = lo; i < hi; i++)
        {
            if (cover[i] == 0)
            {
                if (curStart < 0) { curStart = i; curLen = 0; }
                curLen++;
                if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; }
            }
            else { curStart = -1; curLen = 0; }
        }

        // 空档太窄（不足 3% 版宽）就当作没有分栏
        if (bestStart < 0 || bestLen < Math.Max(1, bins * 0.03))
            return Merge(fullWidth, OrderWithinColumn(cols, band), band);

        var splitX = cMinX + cWidth * ((bestStart + bestLen / 2.0) / bins);
        var left = cols.Where(b => b.X + b.W / 2 < splitX).ToList();
        var right = cols.Where(b => b.X + b.W / 2 >= splitX).ToList();
        if (left.Count == 0 || right.Count == 0)
            return Merge(fullWidth, OrderWithinColumn(cols, band), band);

        var result = new List<Box>(boxes.Count);
        result.AddRange(Cut(left, minColWidth, band));
        result.AddRange(Cut(right, minColWidth, band));
        // 跨栏框按 Y 插回整体序列
        return Merge(fullWidth, result, band);
    }

    /// <summary>把跨栏宽框按 Y 位置插回已排好的列序列。</summary>
    private static List<Box> Merge(List<Box> fullWidth, List<Box> ordered, double band)
    {
        if (fullWidth.Count == 0) return ordered;
        var all = new List<Box>(ordered.Count + fullWidth.Count);
        all.AddRange(ordered);
        foreach (var b in fullWidth.OrderBy(b => b.Y))
        {
            var at = all.Count;
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].Y > b.Y + band) { at = i; break; }
            }
            all.Insert(at, b);
        }
        return all;
    }
}
