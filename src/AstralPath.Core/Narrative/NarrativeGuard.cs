using AstralPath.Core.Formula;

namespace AstralPath.Core.Narrative;

public sealed record NarrativeSlots(
    string FromKpName,
    string ToKpName,
    double Impact,
    double ScoreFrom,
    double ScoreTo);

public sealed record NarrativeResult(
    string FromKp,
    string ToKp,
    string Story,
    IReadOnlyList<string> Actions,
    string Tone,
    bool BannedHit,
    bool SlotFailed,
    bool DegradedTemplate,
    string? ModelVer);

/// <summary>禁词门禁 + 槽位校验 + 模板降级（narrative-svc / ModelService）。</summary>
public static class NarrativeGuard
{
    public static IReadOnlyList<string> ScanBanned(string text)
    {
        var hits = new List<string>();
        foreach (var word in FormulaWeights.BannedWords)
        {
            if (text.Contains(word, StringComparison.Ordinal))
                hits.Add(word);
        }
        return hits;
    }

    public static bool ValidateSlots(string draft, NarrativeSlots slots)
    {
        if (!draft.Contains(slots.FromKpName, StringComparison.Ordinal)) return false;
        if (!draft.Contains(slots.ToKpName, StringComparison.Ordinal)) return false;
        if (!ContainsNumber(draft, slots.Impact)) return false;
        if (!ContainsNumber(draft, slots.ScoreFrom)) return false;
        if (!ContainsNumber(draft, slots.ScoreTo)) return false;
        return true;
    }

    public static NarrativeResult Build(NarrativeSlots slots, string? draft = null)
    {
        var bannedHits = draft == null ? Array.Empty<string>() : ScanBanned(draft);
        var slotOk = draft != null && bannedHits.Count == 0 && ValidateSlots(draft, slots);

        if (slotOk && draft != null)
        {
            return new NarrativeResult(
                slots.FromKpName, slots.ToKpName, draft,
                new[] { "8 分钟概念卡", "3 道桥接题" },
                "supportive", false, false, false, "debt-narrative-v1");
        }

        var template = $"你在{slots.ToKpName}相关练习上多次出错，回溯显示{slots.FromKpName}掌握度为 {FormatNum(slots.ScoreFrom)}，" +
                       $"当前知识点掌握度为 {FormatNum(slots.ScoreTo)}，影响分 {FormatNum(slots.Impact)}。建议先巩固{slots.FromKpName}，再回到{slots.ToKpName}。";

        return new NarrativeResult(
            slots.FromKpName, slots.ToKpName, template,
            new[] { "8 分钟概念卡", "3 道桥接题" },
            "supportive",
            bannedHits.Count > 0,
            draft != null && !slotOk && bannedHits.Count == 0,
            true,
            null);
    }

    private static bool ContainsNumber(string text, double value)
    {
        var rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);
        // 禁止整数变体：|v|<1.5 时 Round 变成 "0"/"1"，会被任意数字子串命中
        var variants = new[]
        {
            rounded.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            rounded.ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture),
            rounded.ToString("F6", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')
        };
        foreach (var v in variants)
        {
            if (string.IsNullOrEmpty(v) || v == "0" || v == "1") continue;
            if (text.Contains(v, StringComparison.Ordinal)) return true;
        }
        return text.Contains(rounded.ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string FormatNum(double v)
        => Math.Round(v, 6, MidpointRounding.AwayFromZero).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
