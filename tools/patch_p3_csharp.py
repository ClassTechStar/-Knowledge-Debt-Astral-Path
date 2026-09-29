# -*- coding: utf-8 -*-
from pathlib import Path

# OcrHost
p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\src\AstralPath.Infrastructure\OcrHost.cs")
c = p.read_text(encoding="utf-8")
if "ClearOcrCache" not in c:
    method = """    /// <summary>P3-4：清理本地 OCR 页缓存（~/.astralpath/ocr-cache）。</summary>
    public static int ClearOcrCache()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".astralpath", "ocr-cache");
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(f); n++; } catch { /* ignore */ }
        }
        return n;
    }

"""
    c = c.replace("    private static string Truncate(", method + "    private static string Truncate(", 1)
    p.write_text(c, encoding="utf-8")
    print("OcrHost.ClearOcrCache added")
else:
    print("ClearOcrCache exists")

# Program.cs
p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\src\AstralPath.Monolith\Program.cs")
c = p.read_text(encoding="utf-8")
old = '        if (req is not { Type: "astralpath.ocr" } || string.IsNullOrEmpty(req.Id) || string.IsNullOrEmpty(req.Data)) return;'
new = '''        if (req is { Type: "astralpath.cache.clear" } && !string.IsNullOrEmpty(req.Id))
        {
            var cid = req.Id!;
            _ = Task.Run(() =>
            {
                try { OcrHost.ClearOcrCache(); } catch { /* ignore */ }
                Reply(cid, "ok", "");
            });
            return;
        }
        if (req is not { Type: "astralpath.ocr" } || string.IsNullOrEmpty(req.Id) || string.IsNullOrEmpty(req.Data)) return;'''
if old in c:
    c = c.replace(old, new, 1)
    p.write_text(c, encoding="utf-8")
    print("Program cache.clear handler")
else:
    print("Program handler miss")

# HTML detail quality
p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\deploy\monolith-web\index.html")
c = p.read_text(encoding="utf-8")
old = '+(m.qs||[]).length+" 题</span><span class=\\"badge\\">"+(m.chapters||[]).length+" 章</span></div>"+(m.err'
new = '+(m.qs||[]).length+" 题</span><span class=\\"badge\\">"+(m.chapters||[]).length+" 章</span>"+(m.quality?"<span class=\\"badge\\">质量"+(m.quality==="good"?"优":m.quality==="fair"?"中":"弱")+"</span>":"")+"</div>"+(m.err'
if old in c:
    c = c.replace(old, new, 1)
    print("detail quality")
else:
    print("detail still miss")
    i = c.find('题</span><span')
    print(repr(c[i:i+80]) if i >= 0 else "no")
p.write_text(c, encoding="utf-8")
