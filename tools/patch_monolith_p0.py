# -*- coding: utf-8 -*-
"""Patch monolith index.html for P0-P3 optimizations."""
from pathlib import Path

p = Path(r"C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\deploy\monolith-web\index.html")
c = p.read_text(encoding="utf-8")
orig_len = len(c)

radical_js = r'''
/* ── P0-3 康熙部首还原（与 tools/ocr_pipeline.fix_radical_chars 同源）── */
const _RADICAL_EXTRA={"⺀":"丷","⺁":"厂","⺄":"乛","⺆":"冂","⺇":"冂","⺈":"刀","⺉":"刀","⺊":"卜","⺌":"小","⺍":"幺","⺎":"兀","⺏":"尣","⺐":"屮","⺑":"巳","⺒":"巳","⺓":"幺","⺔":"彐","⺕":"彐","⺖":"忄","⺗":"心","⺘":"扌","⺙":"攵","⺛":"毋","⺜":"曰","⺝":"月","⺞":"歹","⺟":"母","⺠":"氏","⺡":"氵","⺢":"水","⺣":"灬","⺤":"爫","⺥":"爫","⺦":"丬","⺧":"牛","⺨":"犭","⺩":"王","⺪":"玉","⺫":"目","⺬":"礻","⺭":"示","⺮":"竹","⺯":"糸","⺰":"纟","⺱":"罒","⺲":"罒","⺳":"罒","⺴":"覀","⺵":"襾","⺶":"羊","⺷":"羊","⺸":"羽","⺹":"耂","⺺":"𦘒","⺻":"聿","⺼":"月","⺽":"臼","⺾":"艹","⺿":"艹","⻀":"艹","⻁":"虎","⻂":"衤","⻃":"覀","⻄":"西","⻅":"见","⻆":"角","⻇":"讠","⻈":"讠","⻉":"贝","⻊":"足","⻋":"车","⻌":"辶","⻍":"辶","⻎":"辶","⻏":"阝","⻐":"钅","⻑":"長","⻒":"镸","⻓":"長","⻔":"门","⻕":"开","⻖":"阝","⻗":"雨","⻘":"青","⻙":"韦","⻚":"页","⻛":"风","⻜":"飞","⻝":"食","⻞":"食","⻟":"食","⻠":"饣","⻡":"饣","⻢":"马","⻣":"骨","⻤":"鬼","⻥":"鱼","⻦":"鸟","⻧":"卤","⻨":"麦","⻩":"黄","⻪":"黾","⻫":"齐","⻬":"齐","⻭":"齒","⻮":"齿","⻯":"龍","⻰":"龙","⻱":"龟","⻲":"龟","⻳":"龟"};
function fixRadicalChars(s){
  s=String(s==null?"":s);
  let out="";
  for(let i=0;i<s.length;i++){
    const ch=s[i],cp=s.charCodeAt(i);
    if(cp>=0x2E80&&cp<=0x2FDF){
      if(_RADICAL_EXTRA[ch]){out+=_RADICAL_EXTRA[ch];continue;}
      try{const n=ch.normalize("NFKC");out+=(n&&n!==ch)?n:ch;}catch(e){out+=ch;}
      continue;
    }
    out+=ch;
  }
  return out;
}
'''

anchor = "// ── OCR 引擎阶梯 ①宿主桥"
if "function fixRadicalChars" not in c and anchor in c:
    c = c.replace(anchor, radical_js + "\n" + anchor, 1)
    print("added fixRadicalChars")
else:
    print("skip radical")

old = 'const raw=sanitizeText(String(o.text||"")).replace(/\\r\\n/g,"\\n").trim();'
new = 'const raw=fixRadicalChars(sanitizeText(String(o.text||""))).replace(/\\r\\n/g,"\\n").trim();'
if old in c:
    c = c.replace(old, new, 1)
    print("radical on OCR raw")
else:
    print("ocr raw pattern miss")

old = '''  if(d.type==="astralpath.ocr.result"&&d.id&&Object.prototype.hasOwnProperty.call(_ocrPending,d.id)){'''
new = '''  if(d.type==="astralpath.ocr.progress"&&d.id&&Object.prototype.hasOwnProperty.call(_ocrPending,d.id)){
    const slot=_ocrPending[d.id];
    if(slot&&slot.onProgress){try{slot.onProgress(String(d.message||d.msg||""));}catch(e){}}
    return;
  }
  if(d.type==="astralpath.ocr.result"&&d.id&&Object.prototype.hasOwnProperty.call(_ocrPending,d.id)){'''
if old in c:
    c = c.replace(old, new, 1)
    print("progress listener")
else:
    print("progress listener miss")

c = c.replace("async function ocrViaHost(file,mode,pages){", "async function ocrViaHost(file,mode,pages,onProgress){", 1)
c = c.replace(
    "_ocrPending[id]={resolve:resolve,reject:reject,timer:timer};",
    "_ocrPending[id]={resolve:resolve,reject:reject,timer:timer,onProgress:onProgress};",
    1,
)
print("ocrViaHost progress")

old = "const OCR_BRIDGE_TIMEOUT_MS=10*60*1000;"
new = "const OCR_BRIDGE_TIMEOUT_MS=10*60*1000;\nfunction ocrBridgeTimeoutMs(mode){return mode===\"full\"?40*60*1000:(mode===\"standard\"?20*60*1000:3*60*1000);}"
if old in c:
    c = c.replace(old, new, 1)
    print("timeout map")

old = '''    const timer=setTimeout(function(){delete _ocrPending[id];reject(new Error("OCR 超时（10 分钟）；可改用 quick 模式或拆分文件"));},OCR_BRIDGE_TIMEOUT_MS);'''
new = '''    const _to=ocrBridgeTimeoutMs(mode||"standard");
    const timer=setTimeout(function(){delete _ocrPending[id];reject(new Error("OCR 超时（"+Math.round(_to/60000)+" 分钟）；可改用 quick 模式或拆分文件"));},_to);'''
if old in c:
    c = c.replace(old, new, 1)
    print("timeout apply")

# host OCR call: full mode + progress callback
old = '''        try{o=await ocrViaHost(f,mode,r.pages);if(o)ocrEngine="宿主桥（python+tesseract"+(r.pages&&r.pages.length?"·定向 "+r.pages.length+" 页":"")+"）";}'''
new = '''        const _hostMode=(mode==="full")? "full":((mode==="standard"||!mode)? "standard":mode);
        try{o=await ocrViaHost(f,_hostMode,r.pages,function(m){setMatStatus("","「"+f.name+"」OCR "+String(m||"").replace(/^PROGRESS\\s*/,""));});if(o)ocrEngine="宿主桥（python+tesseract·"+_hostMode+(r.pages&&r.pages.length?"·定向 "+r.pages.length+" 页":"")+"）";}'''
if old in c:
    c = c.replace(old, new, 1)
    print("host ocr full+progress")
else:
    print("host ocr call miss")

# makeBookFromFile: fingerprint + quality
old = '''function makeBookFromFile(name,text,mode,kind){
  const chapters=extractChapters(text);
  const course=String(name).replace(/\\.(pdf|txt|md|markdown)$/i,"").slice(0,24);
  const g=buildGraphFromText(text,chapters,{course:course,maxConcepts:mode==="quick"?12:(mode==="none"?8:20)});
  const book={id:"m"+Date.now().toString(36)+Math.random().toString(36).slice(2,6),name:name,chars:text.length,kind:kind||"user",parsedAt:Date.now(),parseMode:mode,status:"ready",chapters:chapters,nodes:g.nodes,edges:g.edges,qs:[],sourceText:String(text).slice(0,4000),masterySeed:null};
  book.qs=generateQuestions(book);
  return book;
}'''
new = '''function contentFingerprint(name,text){
  const t=String(text||"").replace(/\\s+/g,"").slice(0,4000);
  let h=2166136261;
  for(let i=0;i<t.length;i++){h^=t.charCodeAt(i);h=Math.imul(h,16777619);}
  return (String(name||"").toLowerCase().replace(/\\s+/g,"_").slice(0,24))+"|"+(h>>>0).toString(16)+"|"+t.length;
}
function makeBookFromFile(name,text,mode,kind){
  const chapters=extractChapters(text);
  const course=String(name).replace(/\\.(pdf|txt|md|markdown)$/i,"").slice(0,24);
  const g=buildGraphFromText(text,chapters,{course:course,maxConcepts:mode==="quick"?12:(mode==="none"?8:20)});
  const fp=contentFingerprint(name,text);
  const book={id:"m"+Date.now().toString(36)+Math.random().toString(36).slice(2,6),name:name,chars:text.length,kind:kind||"user",parsedAt:Date.now(),parseMode:mode,status:"ready",chapters:chapters,nodes:g.nodes,edges:g.edges,qs:[],sourceText:String(text).slice(0,4000),masterySeed:null,fingerprint:fp};
  const dens=chapters.length?Math.round(text.length/Math.max(1,chapters.length)):text.length;
  book.quality=(countReadable(text)>=200&&chapters.length>=2&&g.nodes.length>=10)?"good":(countReadable(text)>=40?"fair":"low");
  book.qualityNote="可读"+countReadable(text)+"字·"+chapters.length+"章·"+g.nodes.length+"节点";
  book.qs=generateQuestions(book);
  return book;
}'''
if old in c:
    c = c.replace(old, new, 1)
    print("dedup+quality")
else:
    print("makeBook miss")

# parseFiles: skip duplicate fingerprints
old = '''      const book=makeBookFromFile(f.name,text,mode,"user");
      if(r.ocr){book.ocr=true;book.ocrPages=r.ocrPages||0;book.ocrMode=r.ocrMode||"whole";book.textLayerChars=r.textLayerChars||0;}
      results.push(book);'''
new = '''      const book=makeBookFromFile(f.name,text,mode,"user");
      if(r.ocr){book.ocr=true;book.ocrPages=r.ocrPages||0;book.ocrMode=r.ocrMode||"whole";book.textLayerChars=r.textLayerChars||0;}
      // P1-1 内容指纹去重
      const st=(window.state&&window.state.materials)||[];
      const dup=st.some(function(m){return m&&m.fingerprint&&m.fingerprint===book.fingerprint;});
      if(dup){errors.push({name:f.name,reason:"内容与已有教材重复，已跳过（P1-1 去重）"});continue;}
      results.push(book);'''
if old in c:
    c = c.replace(old, new, 1)
    print("parse dedup")
else:
    print("parse dedup miss")

# apply fixRadicalChars on text layer path in readFileAsText return
# find `return {text:...` patterns - safer: wrap after pdfJsTextLayer assignment
if "function fixRadicalChars" in c and "fixRadicalChars(pj" not in c:
    # try common pattern after pdfJsTextLayer
    for pat in [
        "const pj=await pdfJsTextLayer(file);",
    ]:
        if pat in c:
            c = c.replace(pat, pat + "\n      if(pj&&pj.text)pj.text=fixRadicalChars(pj.text);", 1)
            print("radical on text layer")
            break

p.write_text(c, encoding="utf-8")
print("len", orig_len, "->", len(c))
