# -*- coding: utf-8 -*-
from pathlib import Path

p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\deploy\monolith-web\index.html")
c = p.read_text(encoding="utf-8")

old = """    applyGlass();
    ensureMastery(activeBook());go("home");whatif();ensureChat();"""
new = """    applyGlass();
    ensureMastery(activeBook());go("home");whatif();ensureChat();
    var _bc=$("btnClearOcrCache"); if(_bc) _bc.onclick=function(){clearOcrCache();};
    var _bu=$("btnUploadCache"); if(_bu) _bu.onclick=function(){clearUploadCacheHint();};
    refreshNetBadge();"""
if old in c:
    c = c.replace(old, new, 1)
    print("bound in init")
else:
    print("init anchor miss")
    print(repr(c[c.find("applyGlass"):c.find("applyGlass")+120]))

old = '+(m.qs||[]).length+" 题</span></div>"'
new = '+(m.qs||[]).length+" 题</span>"+(m.quality?"<span class=\\"badge\\">质量"+(m.quality==="good"?"优":m.quality==="fair"?"中":"弱")+"</span>":"")+"</div>"'
if old in c:
    c = c.replace(old, new, 1)
    print("detail quality")
else:
    print("detail miss")
    i = c.find("(m.qs||[]).length")
    print(repr(c[i:i+100]) if i >= 0 else "no")

p.write_text(c, encoding="utf-8")
print("len", len(c))
