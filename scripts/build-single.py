#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""知债：星穹学途 · 自包含单文件构建
================================================================================
以本目录权威 index.html（单体版 2.5 基座）为唯一事实源，内联全部 OCR 依赖：

  A. pdf.js 三件套（v1.x 起，file:// 也可用）
    - ocr-wasm.js                        → 原样内联（IIFE，引擎入口）
    - pdf.min.js                         → 原样内联（pdf.js 文本层主路径）
    - pdf.worker.min.js                  → base64 内联为 Blob URL

  B. WASM tesseract 全栈（v1.4.0 起，纯前端 OCR 离线可用）
    - tesseract.min.js                   → 原样内联（主线程 API）
    - worker.min.js                      → base64 内联为 Blob URL，注入前打两处补丁：
        1) initialize 的 langs join：对象项取 code（tesseract.js 5.1.1 上游此处误取
           data，字节流 join 会产生垃圾语言串导致 Init 失败）
        2) importScripts(corePath + "/tesseract-core-*.wasm.js")：文件名映射到
           __AP_CORE_MAP__（Blob URL），绕过「Blob URL 不能拼接路径」限制
    - tesseract-core-simd-lstm.wasm.js   → base64 → Blob URL（SIMD 设备）
    - tesseract-core-lstm.wasm.js        → base64 → Blob URL（无 SIMD 回退）
    - chi_sim.traineddata / eng.traineddata → gzip（level 9，mtime=0 确定性）→ base64，
      经 createWorker 的 langs 对象数组 [{code, data}] 直传；worker 端 loadLanguage
      按 gzip 魔数自动解压并写入 Emscripten FS（cacheMethod:none，不落 IndexedDB）

  运行期由注入的 window.__AP_OCR_INLINE__ 资源桥惰性解码（OCR 引擎首次初始化才
  执行），ocr-wasm.js v1.4.0 检测到桥后全部走 Blob/内存资源，零网络请求——
  部署在只托管单个 HTML 的环境（如 DuMate artifacts）也能完成扫描版 PDF OCR。

  产物：astralpath-monolith.html（除 onnxruntime-web CDN（带 onerror 降级哨兵，
  缺失仅损失 ONNX 推理引擎）外零外部资源引用）
================================================================================
"""
import base64
import gzip
import hashlib
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # 仓库根（scripts/ 的上一级）
SRC_HTML = os.path.join(ROOT, "deploy", "monolith-web", "index.html")
OCR_DIR = os.path.join(ROOT, "deploy", "monolith-web", "ocr-engine")
OCR_WASM = os.path.join(OCR_DIR, "ocr-wasm.js")
PDF_JS = os.path.join(OCR_DIR, "pdf.min.js")
PDF_WORKER = os.path.join(OCR_DIR, "pdf.worker.min.js")
TESSERACT_JS = os.path.join(OCR_DIR, "tesseract.min.js")
TESSERACT_WORKER = os.path.join(OCR_DIR, "worker.min.js")
CORE_SIMD_LSTM = os.path.join(OCR_DIR, "tesseract-core-simd-lstm.wasm.js")
CORE_LSTM = os.path.join(OCR_DIR, "tesseract-core-lstm.wasm.js")
CHI_SIM = os.path.join(OCR_DIR, "chi_sim.traineddata")
ENG = os.path.join(OCR_DIR, "eng.traineddata")
OUT = os.path.join(ROOT, "deploy", "monolith-web", "astralpath-monolith.html")

# 门禁：基座 index.html 漂移检测。2026-10-12 T11（UX 反馈落地）、2026-10-13 T12（内置演示密钥 AES-256-GCM 密文内嵌）、T13（并发闸/冷却分类/推理兜底/出题预算修复）、T14（AI 随时可用且迅速：思维链流式、垫题秒出+自动替换、命题进度流式、重复重试、冷却 30→8s、建议生成中占位）改动后重打基线。
# 同日 T10.1 修复 matchExamKps 同题重复命中（重复知识点只计一次）后再次重打基线。
# 2026-10-15 T15 修复（AI 构图/出题失效三连）：complete() SSE 分支 onDelta 未定义致全链路失败、mergeGraphAI 整池覆盖丢 AI 题、垫题 _pendingAi 残留持久化。
# （主仓库 deploy/monolith-web 门禁 97135d02... 对应更早版本，此副本已含同版
# OCR/功能改动，重基线前先人工比对确认。）
EXPECTED_MD5 = "c1d7afcfa4170f49835f181de214c35e"

# 唯一放行的外部 CDN 引用（页面内有 onerror 哨兵，缺失时自动降级）
ALLOWED_EXTERNAL_PREFIX = "https://cdn.jsdelivr.net/npm/onnxruntime-web"


def read(path):
    # 二进制读取后解码：保留原文件行尾（CRLF），避免 universal newline 改写导致 md5 漂移
    with open(path, "rb") as f:
        return f.read().decode("utf-8")


def read_bin(path):
    with open(path, "rb") as f:
        return f.read()


def b64(data):
    return base64.b64encode(data).decode("ascii")


def must_contain(text, needle, what):
    n = text.count(needle)
    if n != 1:
        print(f"[内联失败] worker.min.js 补丁目标 {what} 命中 {n} 次（期望 1 次）")
        sys.exit(1)


def main():
    html = read(SRC_HTML)

    # 0) 基座一致性门禁
    md5 = hashlib.md5(html.encode("utf-8")).hexdigest()
    if md5 != EXPECTED_MD5:
        print(f"[门禁失败] index.html md5={md5} != {EXPECTED_MD5}，拒绝构建")
        sys.exit(1)

    ocr_wasm = read(OCR_WASM)
    pdf_js = read(PDF_JS)
    pdf_worker = read(PDF_WORKER)
    tesseract_js = read(TESSERACT_JS)
    worker_js = read(TESSERACT_WORKER)

    # 内联安全：文本内联的 JS 均不得包含 </script> 字面量（base64 常量天然安全）
    for name, content in (("ocr-wasm.js", ocr_wasm), ("pdf.min.js", pdf_js),
                          ("pdf.worker.min.js", pdf_worker), ("tesseract.min.js", tesseract_js),
                          ("worker.min.js", worker_js)):
        if "</script" in content.lower():
            print(f"[内联失败] {name} 含 </script 字面量，无法直接内联")
            sys.exit(1)

    # ── B-1：worker.min.js 补丁 ──────────────────────────────────────────────
    # a) 去掉 sourcemap 注释（Blob 上下文无意义）
    worker_js = re.sub(r"//# sourceMappingURL=worker\.min\.js\.map\s*$", "", worker_js)
    # b) initialize langs join：t.data → t.code（上游 5.1.1 缺陷，见文件头说明）
    jb_from = 'return"string"==typeof t?t:t.data})).join("+"'
    jb_to = 'return"string"==typeof t?t:t.code})).join("+"'
    must_contain(worker_js, jb_from, "initialize-join(t.data)")
    worker_js = worker_js.replace(jb_from, jb_to)
    # c) importScripts：按文件名映射 __AP_CORE_MAP__（Blob URL 不能拼接路径）
    ji_from = "r.g.importScripts(h)"
    ji_to = ('r.g.importScripts((r.g.__AP_CORE_MAP__&&r.g.__AP_CORE_MAP__'
             '[String(h).split("/").pop().replace(/\\.wasm\\.js$/,"")])||h)')
    must_contain(worker_js, ji_from, "importScripts(core)")
    worker_js = worker_js.replace(ji_from, ji_to)

    # ── B-2：资源 base64 化 ─────────────────────────────────────────────────
    worker_b64 = b64(worker_js.encode("utf-8"))
    core_simd_b64 = b64(read_bin(CORE_SIMD_LSTM))
    core_lstm_b64 = b64(read_bin(CORE_LSTM))
    chi_b64 = b64(gzip.compress(read_bin(CHI_SIM), 9, mtime=0))
    eng_b64 = b64(gzip.compress(read_bin(ENG), 9, mtime=0))

    # ── B-3：内联资源桥（惰性解码，OCR 引擎首次初始化才执行）────────────────
    bridge_block = (
        "<script>\n"
        "/* 单文件构建注入：WASM OCR 内联资源桥（ocr-wasm.js v1.4.0，纯前端离线）。\n"
        " * 常量：worker.min.js（带 initialize-join/corePath 两处补丁）·双 wasm core·\n"
        " * gzip traineddata，全部 base64。init() 惰性解码并建 Blob URL；失败返回 null，\n"
        " * ocr-wasm.js 自动回退外部 ocr-engine/ 目录（行为与 v1.3.0 一致）。 */\n"
        "(function(){\n"
        '"use strict";\n'
        'function b64u8(s){var b=atob(s),n=b.length,u=new Uint8Array(n);for(var i=0;i<n;i++)u[i]=b.charCodeAt(i);return u;}\n'
        'function jsUrl(u){return URL.createObjectURL(new Blob([u],{type:"application/javascript"}));}\n'
        'var W="' + worker_b64 + '";\n'
        'var C1="' + core_simd_b64 + '";\n'
        'var C2="' + core_lstm_b64 + '";\n'
        'var L1="' + chi_b64 + '";\n'
        'var L2="' + eng_b64 + '";\n'
        'var P=null;\n'
        'window.__AP_OCR_INLINE__={init:function(){\n'
        '  if(P)return P;\n'
        '  P=new Promise(function(resolve){\n'
        '    try{\n'
        '      if(!window.URL||!window.Blob||!window.atob)throw new Error("Blob/URL/atob 不可用");\n'
        '      var coreMap={"tesseract-core-simd-lstm":jsUrl(b64u8(C1)),"tesseract-core-lstm":jsUrl(b64u8(C2))};\n'
        '      var wText=new TextDecoder("utf-8").decode(b64u8(W));\n'
        '      var workerUrl=jsUrl("__AP_CORE_MAP__="+JSON.stringify(coreMap)+";\\n"+wText);\n'
        '      resolve({workerUrl:workerUrl,coreMap:coreMap,langs:[{code:"chi_sim",data:b64u8(L1)},{code:"eng",data:b64u8(L2)}]});\n'
        '    }catch(e){P=null;resolve(null);}\n'
        '  });\n'
        '  return P;\n'
        '}};\n'
        "})();\n"
        "</script>\n"
    )
    tesseract_block = "<script>\n" + tesseract_js + "\n</script>\n"

    # ── A-1：替换外部 script 引用为内联块（按原顺序）——用 lambda 避免 re 模板转义
    # ocr-wasm.js 位置替换为：资源桥 + tesseract.min.js + ocr-wasm.js 三连内联块
    # （保证解析顺序：桥与全局 Tesseract 先于引擎入口就绪）
    r1 = re.sub(
        r'<script src="ocr-engine/ocr-wasm\.js" defer></script>',
        lambda m: bridge_block + tesseract_block + "<script>\n" + ocr_wasm + "\n</script>",
        html,
        count=1,
    )
    if r1 == html:
        print("[失败] 未找到 ocr-wasm.js 引用行")
        sys.exit(1)

    r2 = re.sub(
        r'<script src="ocr-engine/pdf\.min\.js" defer></script>',
        lambda m: "<script>\n" + pdf_js + "\n</script>",
        r1,
        count=1,
    )
    if r2 == r1:
        print("[失败] 未找到 pdf.min.js 引用行")
        sys.exit(1)

    # ── A-2：注入 pdf worker Blob：base64 → Uint8Array → Blob → objectURL
    pw_b64 = b64(pdf_worker.encode("utf-8"))
    worker_script = (
        "<script>\n"
        "/* 单文件构建注入：pdf.js worker 以内联 Blob 提供（file:// 可用） */\n"
        "(function(){\n"
        "  var B64='" + pw_b64 + "';\n"
        "  if(!window.__AP_WORKER_BLOB__&&window.URL&&window.atob&&window.Blob){\n"
        "    try{\n"
        "      var bin=atob(B64),u=new Uint8Array(bin.length);\n"
        "      for(var i=0;i<bin.length;i++){u[i]=bin.charCodeAt(i);}\n"
        "      window.__AP_WORKER_BLOB__=URL.createObjectURL(new Blob([u],{type:'text/javascript'}));\n"
        "    }catch(e){window.__AP_WORKER_BLOB__=null;}\n"
        "  }\n"
        "})();\n"
        "</script>\n"
    )
    r3 = re.sub(
        r'</head>',
        worker_script + "</head>",
        r2,
        count=1,
    )

    # ── A-3：pdfJsTextLayer 内 workerSrc：优先使用内联 Blob
    # （兼容两种基座形态：原始行 / 已含 __AP_WORKER_BLOB__ 回退的行）
    raw_src = 'window.pdfjsLib.GlobalWorkerOptions.workerSrc=new URL("ocr-engine/pdf.worker.min.js",document.baseURI).href;'
    patched_src = 'window.pdfjsLib.GlobalWorkerOptions.workerSrc = window.__AP_WORKER_BLOB__ || new URL("ocr-engine/pdf.worker.min.js", document.baseURI).href;'
    if raw_src in r3:
        r4 = r3.replace(raw_src, patched_src)
    elif patched_src in r3:
        r4 = r3  # 基座已是回退形态，无需处理
    else:
        print("[警告] 未找到 workerSrc 行（pdf.js 将尝试相对加载）")
        r4 = r3

    # 4) 收尾清理：确保无残留外部 src/link（onnxruntime-web CDN 除外：带 onerror 降级哨兵）
    externals = []
    for tag in re.finditer(r'<(?:script|link)\b[^>]*?(?:src|href)="([^"]+)"', r4):
        url = tag.group(1)
        if url.startswith("data:") or url.startswith("#"):
            continue
        if url.startswith(ALLOWED_EXTERNAL_PREFIX):
            continue
        externals.append(url)
    if externals:
        print("[失败] 单文件仍含外部资源引用：", externals)
        sys.exit(1)

    with open(OUT, "w", encoding="utf-8", newline="") as f:
        f.write(r4)

    size = os.path.getsize(OUT)
    print(f"[OK] {OUT}")
    print(f"[OK] 大小 {size:,} 字节 ({size/1024/1024:.1f} MB)")
    print(f"[OK] 外部资源引用: 仅 onnxruntime-web CDN（onerror 降级）")
    print(f"[OK] OCR 内联: worker({len(worker_b64):,}B) core×2({len(core_simd_b64)+len(core_lstm_b64):,}B) "
          f"traineddata.gz({len(chi_b64)+len(eng_b64):,}B) 均 base64 就绪")
    print(f"[OK] 基座 index.html md5 {md5} 保持逐字节一致")


if __name__ == "__main__":
    main()
