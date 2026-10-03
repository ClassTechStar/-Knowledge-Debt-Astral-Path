# -*- coding: utf-8 -*-
"""2026-09-28 三补丁：目录优先章节解析 / 真实题目生成 / 智能体深层联动。"""
from pathlib import Path

p = Path(__file__).resolve().parent.parent / "deploy" / "monolith-web" / "index.html"
t = p.read_text(encoding="utf-8")

# ── 补丁 1：extractChapters 目录页优先 ──
old1 = '''function extractChapters(text,maxCh){
  maxCh=maxCh||40;
  const lines=String(text).split("\\n");const marks=[];'''
new1 = '''function extractChapters(text,maxCh){
  // 2026-09-28 v2：目录页优先（用户反馈：图谱应直接读取 OCR 出的书籍目录）——
  // 书籍自带目录（章节名+点线引导+页码）是最可靠的章节结构，命中即用；
  // 未命中再退回行模式扫描 / 段落兜底。
  maxCh=maxCh||80;
  const lines=String(text).split("\\n");
  const deSp=s=>String(s||"").replace(/[\\s　·•⋅.．…]/g,"");
  const tocEntries=[];
  for(let i=0;i<lines.length;i++){
    const ln=lines[i].trim();if(!ln||ln.length>90)continue;
    let m=/^(第\\s*[0-9一二三四五六七八九十百千]+\\s*[章节回篇讲])([\\s:：·.、-]*)\\s*(\\S.{0,46}?)\\s*[·•⋅.．…◆○○※☆\\s]{2,}\\s*(\\d{1,4})\\s*$/.exec(ln);
    if(m){tocEntries.push({title:(m[1].replace(/\\s+/g,"")+" "+m[3]).replace(/\\s+/g," ").trim(),level:1,page:parseInt(m[4],10),li:i});continue;}
    m=/^([0-9]{1,2}(?:\\.[0-9]{1,2})+)\\s+(\\S.{1,44}?)\\s*[·•⋅.．…]{2,}\\s*(\\d{1,4})\\s*$/.exec(ln);
    if(m){tocEntries.push({title:m[1]+" "+m[2].trim(),level:2,page:parseInt(m[3],10),li:i});continue;}
    m=/^([一二三四五六七八九十]+)、\\s*(\\S.{1,44}?)\\s*[·•⋅.．…]{2,}\\s*(\\d{1,4})\\s*$/.exec(ln);
    if(m){tocEntries.push({title:m[1]+"、"+m[2].trim(),level:2,page:parseInt(m[3],10),li:i});continue;}
  }
  const lvl1=tocEntries.filter(e=>e.level===1);
  if(lvl1.length>=3){
    const searchFrom=(tocEntries[tocEntries.length-1]||{li:0}).li+1;
    const starts=lvl1.map(e=>({e:e,si:(()=>{const key=deSp(e.title);for(let i=searchFrom;i<lines.length;i++){const d=deSp(lines[i]);if(d.length>=key.length&&d.indexOf(key)===0)return i;}return -1;})()}));
    const chs=[];
    for(let k=0;k<starts.length&&chs.length<maxCh;k++){
      const s=starts[k],ns=starts[k+1];
      let body="";
      if(s.si>=0){const end=(ns&&ns.si>=0)?ns.si:lines.length;body=lines.slice(s.si+1,end).join("\\n").trim().slice(0,4000);}
      chs.push({id:"C"+String(chs.length+1).padStart(2,"0"),title:s.e.title,body:body,order:chs.length,level:1,page:s.e.page});
    }
    if(chs.length>=3)return chs; // 目录簇可信：直接采用书籍自带目录
  }
  const marks=[];'''
assert t.count(old1) == 1, "p1 anchor %d" % t.count(old1)
t = t.replace(old1, new1)

# ── 补丁 2：generateQuestions v2（真实题目）──
gq_start = t.index("function generateQuestions(book){")
gq_end = t.index("function contentFingerprint(")
new_gq = '''function chTermsOf(c){
  const toks=tokenize((c&&c.title||"")+"。"+(c&&c.body||""));
  const freq={};toks.forEach(t=>freq[t]=(freq[t]||0)+1);
  return Object.keys(freq).filter(t=>t.length>=2).sort((a,b)=>(freq[b]-freq[a])||a.localeCompare(b)).slice(0,12);
}
function generateQuestions(book){
  // 2026-09-28 v2（用户反馈：出题太傻）：题目全部来自真实教材数据——
  // 章标题/目录/正文/关键词，干扰项取自其他章节的真实内容；正确位置由 id 哈希决定，
  // 不再"正确答案恒为 A"。确定性输出（同书同题）。
  const qs=[];let n=1;const nodes=book.nodes||[];const chs=(book.chapters||[]);
  const hash=s=>{let h=2166136261;const t=String(s||"");for(let i=0;i<t.length;i++){h^=t.charCodeAt(i);h=Math.imul(h,16777619);}return (h>>>0);};
  const byId={};chs.forEach(c=>{byId[c.id]=c;});
  const chTerms={};chs.forEach(c=>{chTerms[c.id]=chTermsOf(c);});
  const snippet=c=>String((c&&c.body)||"").replace(/\\s+/g," ").trim();
  const termCh={};
  for(const c of chs){for(const tm of (chTerms[c.id]||[])){if(!termCh[tm])termCh[tm]=c;}}
  // ① 章节关键词题：本章高频词（真）vs 其他章高频词（真）
  for(let i=0;i<chs.length&&qs.length<70;i++){
    const c=chs[i];const terms=chTerms[c.id]||[];const correct=terms[0];
    if(!correct)continue;
    const dis=[];for(const o of chs){if(o.id===c.id)continue;const ot=(chTerms[o.id]||[])[0];if(ot&&ot!==correct&&dis.indexOf(ot)<0)dis.push(ot);}
    if(dis.length<3)continue;
    const ci=hash("kw"+c.id)%4;const opts=dis.slice(0,3);opts.splice(ci,0,correct);
    qs.push({id:"A"+String(n++).padStart(3,"0"),kp:c.id,stem:"《"+c.title+"》这一章的核心关键词是？",opts:opts,ci:ci,diff:2,src:"auto"});
  }
  // ② 概念归属题（词节点）：「term」主要出现在哪一章？（选项=真实章标题）
  const termNodes=nodes.filter(nd=>nd.kind==="term"&&termCh[nd.title]);
  for(let i=0;i<termNodes.length&&qs.length<120;i++){
    const nd=termNodes[i];const hc=termCh[nd.title];const correct=hc.title;
    const dis=[];for(const o of chs){if(o.id===hc.id)continue;if(o.title!==correct&&dis.indexOf(o.title)<0)dis.push(o.title);if(dis.length>=3)break;}
    if(dis.length<3)continue;
    const ci=hash("tm"+nd.id)%4;const opts=dis.slice(0,3);opts.splice(ci,0,correct);
    qs.push({id:"A"+String(n++).padStart(3,"0"),kp:nd.id,stem:"「"+nd.title+"」这个概念主要出现在哪一章？",opts:opts,ci:ci,diff:Math.min(5,nd.diff||2),src:"auto"});
  }
  // ③ 正文出处题：哪段文字出自本章？（选项=各章真实正文片段）
  for(let i=0;i<chs.length&&qs.length<150;i++){
    const c=chs[i];const body=snippet(c);
    if(body.length<60)continue;
    const correct=body.slice(0,60);
    const dis=[];for(const o of chs){if(o.id===c.id)continue;const ob=snippet(o);if(ob.length>=60){if(dis.indexOf(ob.slice(0,60))<0)dis.push(ob.slice(0,60));}if(dis.length>=3)break;}
    if(dis.length<3)continue;
    const ci=hash("bd"+c.id)%4;const opts=dis.slice(0,3);opts.splice(ci,0,correct);
    qs.push({id:"A"+String(n++).padStart(3,"0"),kp:c.id,stem:"关于「"+c.title+"」，下列哪段文字出自本章正文？",opts:opts,ci:ci,diff:3,src:"auto"});
  }
  // ④ 兜底：仍无题的节点补真实正文题
  for(let i=0;i<nodes.length&&qs.length<160;i++){
    const node=nodes[i];
    if(qs.some(q=>q.kp===node.id))continue;
    const c=byId[node.id];const body=c?snippet(c):"";
    if(body.length<60)continue;
    const correct=body.slice(0,60);
    const dis=[];for(const o of chs){if(c&&o.id===c.id)continue;const ob=snippet(o);if(ob.length>=60){if(dis.indexOf(ob.slice(0,60))<0)dis.push(ob.slice(0,60));}if(dis.length>=3)break;}
    if(dis.length<3)continue;
    const ci=hash("fx"+node.id)%4;const opts=dis.slice(0,3);opts.splice(ci,0,correct);
    qs.push({id:"A"+String(n++).padStart(3,"0"),kp:node.id,stem:"根据教材正文，「"+node.title+"」相关内容是：",opts:opts,ci:ci,diff:node.diff||2,src:"auto"});
  }
  return qs;
}
'''
t = t[:gq_start] + new_gq + t[gq_end:]

# ── 补丁 3：智能体深层联动 ──
old3a = '''    case "graph.view":return graphSummary();
    case "material.parse":return "到「藏书阁」上传 PDF/TXT/MD；乱码或扫描版 PDF 会自动转 OCR 识别，也可粘贴正文后点「从粘贴文本解析」。解析完成会自动写入识网图谱。";
    case "kb.search":{
      const hits=searchKb(raw);
      if(!hits.length)return "在当前《"+activeBook().name+"》中没找到直接匹配。可换个关键词，或到识网翻章节。";
      return "检索命中：\\n"+hits.map(h=>"· "+h.t+"（"+h.kind+"，score="+h.s.toFixed(3)+"）"+(h.body?"\\n  "+h.body.slice(0,60):"")).join("\\n");
    }'''
new3a = '''    case "graph.view":{
      // 2026-09-28 深层联动：直接读当前书的目录与高频概念
      const b=activeBook();const chs=(b.chapters||[]).slice(0,8);
      const terms=(b.nodes||[]).filter(nd=>nd.kind==="term").slice(0,5).map(nd=>nd.title);
      return "《"+b.name+"》识网："+(b.nodes||[]).length+" 节点 / "+(b.edges||[]).length+" 边 / "+(b.chapters||[]).length+" 章。\\n章节目录："+chs.map(c=>c.title).join(" → ")+((b.chapters||[]).length>8?" …":"")+"\\n高频概念："+(terms.join("、")||"—")+"\\n完整图谱见「识网」页，可点章节侧栏看正文与出题。";
    }
    case "material.parse":{
      const all=(S.books||[]);const names=all.slice(0,6).map(b=>b.name);
      return "藏书阁现有 "+all.length+" 本资料"+(names.length?"："+names.join("、")+(all.length>6?" 等":""):"（暂无，去上传第一本吧）")+"。\\n上传新 PDF：到「藏书阁」选择文件（乱码/扫描版自动转 OCR）；也可粘贴正文解析。解析完成自动生成识网图谱与题目。";
    }
    case "kb.search":{
      // 2026-09-28 深层联动：跨全书检索（章标题+正文），带出处
      const nq=normalize(raw);const words=nq.split(/\\s+/).filter(w=>w.length>=2);
      const hits=[];
      for(const b of (S.books||[])){
        for(const c of (b.chapters||[])){
          const tt=normalize(c.title),bd=normalize(c.body||"");
          let s=0;for(const w of words){if(tt.indexOf(w)>=0)s+=3;if(bd.indexOf(w)>=0)s+=1;}
          if(s>0)hits.push({s:s,b:b,c:c});
        }
      }
      hits.sort((a,b)=>b.s-a.s);
      if(!hits.length)return "在资料库 "+(S.books||[]).length+" 本书中没找到「"+raw+"」的直接匹配。可换个关键词，或换更具体的章节/概念名。";
      return "跨书检索命中（按相关度）：\\n"+hits.slice(0,4).map(h=>"· 《"+h.b.name+"》"+h.c.title+"\\n  "+String(h.c.body||"").replace(/\\s+/g," ").slice(0,80)+"…").join("\\n");
    }'''
assert t.count(old3a) == 1, "p3a %d" % t.count(old3a)
t = t.replace(old3a, new3a)

old3b = '''    default:return "我可以帮你：诊断知识债、解释红边、14 天计划、今日任务、识网摘要、章节检索、销账检查、资料解析引导、What-if。数字只来自本地公式。";'''
new3b = '''    default:{
      // 2026-09-28 深层联动：兜底前先尝试用藏书阁的真实教材内容回答
      // （问章节/概念/正文 → 返回书名+章节+正文摘录+关键词）
      const ans=answerFromBooks(raw);
      if(ans)return ans;
      return "我可以帮你：诊断知识债、解释红边、14 天计划、今日任务、识网摘要、章节检索、销账检查、资料解析引导、What-if。也可以直接问教材内容，比如「认识.NET 这一章讲什么」。数字只来自本地公式。";
    }'''
assert t.count(old3b) == 1, "p3b %d" % t.count(old3b)
t = t.replace(old3b, new3b)

anchor_help = "function executeIntent(id,raw){"
helper = '''function answerFromBooks(raw){
  // 深层联动：用藏书阁所有书的章节标题+正文匹配问题，返回最相关章节的摘录
  try{
    const nq=normalize(raw).replace(/[？?。！!，,、]/g," ");
    const words=nq.split(/\\s+/).filter(w=>w.length>=2);
    if(!words.length)return null;
    let best=null;
    for(const b of (S.books||[])){
      for(const c of (b.chapters||[])){
        const tt=normalize(c.title),bd=normalize(c.body||"").slice(0,6000);
        let s=0;
        for(const w of words){if(tt.indexOf(w)>=0)s+=3;if(bd.indexOf(w)>=0)s+=1;}
        if(s>=4&&(!best||s>best.s))best={s:s,b:b,c:c};
      }
    }
    if(!best)return null;
    const terms=(chTermsOf(best.c)||[]).slice(0,3);
    return "《"+best.b.name+"》· "+best.c.title+"\\n正文摘录："+String(best.c.body||"").replace(/\\s+/g," ").slice(0,150)+"…\\n本章关键词："+(terms.join("、")||"—")+"\\n（完整正文与出题见「识网」页章节侧栏）";
  }catch(e){return null;}
}
function executeIntent(id,raw){'''
assert t.count(anchor_help) == 1
t = t.replace(anchor_help, helper)

p.write_text(t, encoding="utf-8", newline="\n")
print("THREE PATCHES APPLIED, bytes:", len(t))
