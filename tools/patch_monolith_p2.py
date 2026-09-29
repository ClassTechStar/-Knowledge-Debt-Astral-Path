# -*- coding: utf-8 -*-
"""P2/P3 monolith HTML patches: quality badge, status, cache clear, onboarding."""
from pathlib import Path

p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\deploy\monolith-web\index.html")
c = p.read_text(encoding="utf-8")
n0 = len(c)

# quality badge on material card
old = "(m.parseMode||\"-\")+(m.ocr?(' · OCR'+(m.ocrPages?('（回填 '+m.ocrPages+' 页）'):'')):'')+'</div>"
new = "(m.parseMode||\"-\")+(m.ocr?(' · OCR'+(m.ocrPages?('（回填 '+m.ocrPages+' 页）'):'')):'')+(m.quality?(' · <span class=\"badge\">质量'+(m.quality===\"good\"?\"优\":(m.quality===\"fair\"?\"中\":\"弱\"))+'</span>'):'')+'</div>"
if old in c:
    c = c.replace(old, new, 1)
    print("quality badge")
else:
    print("quality badge miss")

# home book quality note
old = '(b.qs||[]).length+" 题 · "+esc(b.status||"ready")+"</div>")'
new = '(b.qs||[]).length+" 题 · "+esc(b.status||"ready")+(b.quality?(" · 质量"+(b.quality==="good"?"优":b.quality==="fair"?"中":"弱")):"")+"</div>")'
if old in c:
    c = c.replace(old, new, 1)
    print("home quality")
else:
    print("home quality miss")

# P3-4/P3-5: cache clear + status strip near system status
old = '''function setMatStatus(kind,msg){'''
new = '''function setMatStatus(kind,msg){'''
# insert helpers before setMatStatus
helper = '''
/* ── P2-4 运行状态 · P3-4/3-5 缓存治理 ── */
function apOnline(){return (typeof navigator!=="undefined"&&navigator.onLine!==false);}
function refreshNetBadge(){
  const el=$("netBadge");if(!el)return;
  el.textContent=apOnline()?"本机模式 · 在线":"本机模式 · 离线（功能仍可用）";
  el.className="badge"+(apOnline()?"":"");
}
async function clearOcrCache(){
  try{
    if(window.chrome&&window.chrome.webview&&window.chrome.webview.postMessage){
      window.chrome.webview.postMessage({type:"astralpath.cache.clear",id:"c"+Date.now()});
      setMatStatus("ok","已请求宿主清理 OCR 页缓存");
      return;
    }
    if(typeof caches!=="undefined"&&caches.keys){
      const keys=await caches.keys();
      await Promise.all(keys.filter(k=>/ocr|rapid/i.test(k)).map(k=>caches.delete(k)));
    }
    if(window.__AP_WASM_OCR__&&window.__AP_WASM_OCR__.clearCache){await window.__AP_WASM_OCR__.clearCache();}
    setMatStatus("ok","已清理浏览器侧 OCR 缓存");
  }catch(e){setMatStatus("err","清理缓存失败："+(e.message||e));}
}
function clearUploadCacheHint(){
  setMatStatus("","原 PDF 缓存在 %LOCALAPPDATA%\\\\AstralPath\\\\materials-uploads；可在系统设置或资源管理器中清理（P3-5）");
}
addEventListener("online",function(){refreshNetBadge();});
addEventListener("offline",function(){refreshNetBadge();});
'''
if "function clearOcrCache" not in c:
    c = c.replace("function setMatStatus(kind,msg){", helper + "function setMatStatus(kind,msg){", 1)
    print("cache/status helpers")

# inject net badge + cache buttons in system status panel if present
# find system status HTML
if 'id="netBadge"' not in c:
    # try insert near 系统状态
    old = '<div class="panel"><h2>系统状态</h2>'
    new = '<div class="panel"><h2>系统状态</h2><div class="row" style="margin:6px 0"><span class="badge" id="netBadge">本机模式</span><button class="btn" id="btnClearOcrCache">清理OCR缓存</button><button class="btn" id="btnUploadCache">上传缓存说明</button></div>'
    if old in c:
        c = c.replace(old, new, 1)
        print("status panel")
    else:
        print("status panel miss")

# bind buttons in init
if "btnClearOcrCache" in c and "$(\"btnClearOcrCache\").onclick" not in c:
    # append at end of script before last closing
    bind = '''
if($("btnClearOcrCache"))$("btnClearOcrCache").onclick=function(){clearOcrCache();};
if($("btnUploadCache"))$("btnUploadCache").onclick=function(){clearUploadCacheHint();};
refreshNetBadge();
'''
    # insert before final line of script - after last typical init
    marker = 'refreshNetBadge();'
    if marker not in c.split("function setMatStatus")[-1]:
        # append near end of inline script
        idx = c.rfind("</script>")
        # find inline script end - the last </script> of inline
        # safer: after renderAll or save init
        anchor = "renderAll();"
        if anchor in c:
            # add after last renderAll();
            pos = c.rfind(anchor)
            c = c[:pos+len(anchor)] + "\n" + bind + c[pos+len(anchor):]
            print("bind buttons")
        else:
            print("renderAll miss")

# P2-1 onboarding: ensure sample import hint is friendly (already has 示例)
# add quality note in detail
old = '"<span class=\\"badge\\">"+(m.qs||[]).length+" 题</span>"'
new = '"<span class=\\"badge\\">"+(m.qs||[]).length+" 题</span>"+(m.quality?("<span class=\\"badge\\">质量"+(m.quality==="good"?"优":m.quality==="fair"?"中":"弱")+"</span>"):"")'
if old in c:
    c = c.replace(old, new, 1)
    print("detail quality")
else:
    print("detail quality miss")

p.write_text(c, encoding="utf-8")
print("len", n0, "->", len(c))
