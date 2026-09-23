namespace AstralPath.Core.Ocr;

/// <summary>
/// 版面阅读顺序：XY-cut 分栏 + 列内 top-to-bottom。
/// 输入词框，输出按阅读顺序排好的 token。
/// </summary>
public static class ReadingOrder
{
    public sealed record Box(string Text, double X, double Y, double W, double H);

    /// <summary>
    /// 两栏检测：在 x 带上投影，找低谷列切开；递归每栏。
    /// 简化版 XY-cut，覆盖教材双栏/图注混排。
    /// </summary>
    public static IReadOnlyList<Box> Sort(IReadOnlyList<Box> boxes, double minColWidth = 40)
    {
        if (boxes.Count <= 1) return boxes;
        return Cut(boxes.ToList(), minColWidth);
    }

    private static List<Box> Cut(List<Box> boxes, double minColWidth)
    {
        if (boxes.Count <= 1) return boxes;
        double minX = boxes.Min(b => b.X);
        double maxX = boxes.Max(b => b.X + b.W);
        double minY = boxes.Min(b => b.Y);
        double maxY = boxes.Max(b => b.Y + b.H);

        var width = maxX - minX;
        if (width < minColWidth * 2)
        {
            // 不再切栏 → 按行 Y，再 X
            return boxes
                .OrderBy(b => Math.Round(b.Y / 6.0)) // 行带
                .ThenBy(b => b.X)
                .ToList();
        }

        // 垂直投影找谷
        var bins = 48;
        var hist = new int[bins];
        foreach (var b in boxes)
        {
            var t = (b.X + b.W / 2 - minX) / width;
            var i = Math.Clamp((int)(t * bins), 0, bins - 1);
            hist[i]++;
        }
        // 找最空的中间 20%–80% 谷
        int best = -1;
        var bestVal = int.MaxValue;
        for (var i = bins / 5; i < bins - bins / 5; i++)
        {
            if (hist[i] < bestVal)
            {
                bestVal = hist[i];
                best = i;
            }
        }
        if (best < 0 || bestVal > 2)
        {
            return boxes.OrderBy(b => Math.Round(b.Y / 6.0)).ThenBy(b => b.X).ToList();
        }

        var splitX = minX + width * ((best + 0.5) / bins);
        var left = boxes.Where(b => b.X + b.W / 2 < splitX).ToList();
        var right = boxes.Where(b => b.X + b.W / 2 >= splitX).ToList();
        if (left.Count == 0 || right.Count == 0)
            return boxes.OrderBy(b => Math.Round(b.Y / 6.0)).ThenBy(b => b.X).ToList();

        var result = new List<Box>(boxes.Count);
        result.AddRange(Cut(left, minColWidth));
        result.AddRange(Cut(right, minColWidth));
        return result;
    }
}
