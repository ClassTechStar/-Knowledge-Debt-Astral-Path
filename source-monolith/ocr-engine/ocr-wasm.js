/* 知债：星穹学途 · 统一 WASM OCR 引擎（Web 浏览器 / 安卓 WebView 共用）
 * ============================================================================
 * 与 Windows 宿主桥（python tools\ocr_pipeline.py + tesseract CLI）行为对齐：
 *   - 语言 chi_sim+eng（与 python 管线 TESS_LANG 默认一致，traineddata 复用
 *     同一份文件，识别质量同源）
 *   - OEM=3（Legacy+LSTM 混合）、PSM=3（全自动页面分割）；低置信页 3.0x 重渲染重识别
 *   - 页采样策略对齐 extract_full_text：目录区前 24 页 + 全书均匀采样，
 *     standard 上限 28 页 / quick 上限 10 页，渲染 scale=2.0
 *   - 输出文本格式对齐 tesseract_pdf_pages：每页 "[page N]\n<文本>"，"\n" 连接
 *   - 按页定向（v1.1.0）：入参 pages（1 基页码数组或逗号串，来自前端 pdf.js 页级乱码判定）非空时
 *     只 OCR 这些页，其余页由前端 backfillOcrPages() 保留文本层原文；上限 OPTS.maxPagesDirect，
 *     与 python tools\ocr_pipeline.py 的 GARBLE_OCR_MAX_PAGES 对齐；pages 为空时退回原有
 *     「目录区 + 全书均匀采样」策略（扫描件全本无文本层的场景）
 *   - 返回约定与宿主桥一致：{ok:true,text} / {ok:false,error}
 *
 * v1.3.0 性能与精度优化（T6-OCR，保持纯前端）：
 *   - 流水线：渲染 → 预处理 → PNG 编码 → 识别 四阶段重叠。渲染/预处理/编码在主线程，
 *     识别在 worker 线程，页 N 识别期间即准备页 N+1 的图像（canvas 池上限 = worker 数 + 1）。
 *     tesseract.js v5 对 canvas 走异步 toBlob()，直接复用 canvas 存在竞态——本版改为
 *     自持编码：先 toBlob 得到 PNG 字节再提交 worker（worker 收到的是独立副本），天然安全。
 *   - 双 worker 池：桌面端（hardwareConcurrency≥4 且 deviceMemory≥4 且非移动 UA）并发 2 个
 *     识别 worker，识别阶段近似 2x；移动端保守单 worker（内存受限）。
 *   - 空白页快路径：预处理单遍统计墨迹覆盖率，<0.4% 直接跳过识别（封面/隔页/空白页常见）。
 *   - 智能重试：原实现「有文本且 conf<65 即 3.0x 重渲染重识别」；现在要求文本 ≥80 字符、
 *     conf ∈ [30,65)、剩余预算 >20% 才重试——短文本页（章节扉页）重试收益低，避免浪费一整次识别。
 *   - 图像预处理：灰度直方图 2%/98% 分位裁剪拉伸（仅当对比度不足时触发），改善淡墨扫描件
 *     的二值化输入；超长边 >2600px 的页面按比例降采样渲染（内存与 Leptonica 开销同时下降）。
 *   - DPI 声明：canvas 无 DPI 元数据，tesseract 默认按 70dpi 处理导致版面分析失准；
 *     现按渲染倍率逐页声明真实 DPI（72×scale），并保留词间空格（preserve_interword_spaces）。
 *   - 内存：canvas 池复用 + 识别完成后显式清零；单页处理峰值 ≤ 2 张位图。
 *   - 兼容：recognize(file, mode, pages, {legacy:true}) 走 v1.2.0 原行为（单 worker、串行、
 *     无预处理），作为基准对照与降级开关；默认关。
 *
 * 依赖（与本文件同目录，全部离线自包含）：
 *   tesseract.min.js / worker.min.js               (tesseract.js 5.1.1, Apache-2.0)
 *   tesseract-core-simd-lstm.wasm.js               (tesseract.js-core 5.1.1)
 *   tesseract-core-lstm.wasm.js                    (无 SIMD 设备回退)
 *   chi_sim.traineddata / eng.traineddata          (与 Windows 端同一份)
 *   pdf.min.js / pdf.worker.min.js                 (pdf.js 3.11.174 legacy, Apache-2.0)
 *
 * v1.4.0 内联资源桥（单文件构建专用，纯前端）：
 *   构建脚本（tools/build-single.py）可把上述 tesseract 全栈（tesseract.min.js 正文、
 *   worker.min.js Blob、双 wasm core Blob、gzip traineddata base64）注入页面，并暴露
 *   window.__AP_OCR_INLINE__ = { init(): Promise<{workerUrl, coreMap, langs} | null> }。
 *   本引擎检测到桥存在时优先走内联资源（Worker 由 Blob URL 创建、语言数据经
 *   createWorker 的 langs 对象数组直传、wasm core 经 worker 内 importScripts 补丁
 *   按文件名映射），页面部署在只托管单个 HTML 的环境（如 DuMate artifacts）也
 *   能完成 OCR。桥缺失（web 端布局）或 init 失败时，行为与 v1.3.0 完全一致：
 *   回退到外部 ocr-engine/ 目录。
 *
 * 运行环境要求：页面经 http/https 加载（本地静态服务、安卓 appassets 虚拟域）。
 * file:// 直开时浏览器禁止 Worker/fetch 本地资源，本引擎在初始化时探测失败并
 * 返回不可用，由上层降级到「粘贴正文」兜底提示。
 *
 * v1.5.0 图像识别（T10，试卷·作业·错题拍照/截图/图片导入）：
 *   新增 recognizeImage(file, opts) / recognizeImages(files, opts)。入参
 *   File/Blob/Uint8Array（PNG/JPEG/GIF/BMP/WebP），与 PDF 路径同参数：
 *   OEM=3、PSM=3、preserve_interword_spaces=1、长边上限 2600px、逐图按渲染
 *   倍率声明 DPI；复用同一预处理（极性归一/对比度拉伸/空白跳过）与同一 worker
 *   池；低置信（conf∈[30,65) 且文本≥80 字符）按 3.0x 重采样重识别。空白图返回
 *   {ok:true,text:""}（调用方按空文本跳过），识别失败返回 {ok:false,error}。
 */
(function (global) {
  "use strict";

  var VERSION = "1.5.0";

  var BASE = (function () {
    var el = document.currentScript;
    var src = el && el.src ? el.src : "ocr-engine/ocr-wasm.js";
    return new URL(".", new URL(src, global.location.href)).href;
  })();

  // 与 python 管线一致的可调参数（+ v1.3.0 新增项）
  var OPTS = {
    lang: "chi_sim+eng",
    oem: 3,          // OEM.DEFAULT（tesseract.js 中与 LSTM_ONLY 同为 LSTM 内核；主增益来自 scaleRetry 重渲染）
    psm: "3",        // PSM.AUTO
    scale: 2.0,
    scaleRetry: 3.0, // 低置信页重渲染倍率（实验验证 3.0x 对 conf<65 页有效提升）
    confThreshold: 65, // 置信度阈值：低于此值的页触发高清重识别（历史验证 conf<65 有效）
    retryFloorConf: 30,   // v1.3.0：conf 低于此值视为非文本页（封面/图片页），不重试
    retryMinChars: 80,    // v1.3.0：首轮文本少于该字符数的页不做重试（短文本页收益低）
    retryBudgetFrac: 0.8, // v1.3.0：已消耗预算超过 80% 后不再重试（保页数优先）
    maxLongEdge: 2600,    // v1.3.0：渲染位图长边上限（超出按比例降采样）
    blankCoverage: 0.004, // v1.3.0：墨迹覆盖率低于 0.4% 判为空白页，跳过识别
    inkDepth: 40,      // v1.3.0：墨迹判定 = 亮度低于「背景众数 − 40」的像素
    contrastMin: 120,  // v1.3.0：1%/99% 分位动态范围低于该值才触发对比度拉伸
    userDefinedDpi: "150",// v1.3.0：兜底 DPI 声明（正常路径按渲染倍率逐页计算）
    preserveSpaces: "1",  // v1.3.0：保留词间空格（CJK 排版保真）
    tocPages: 24,
    sampleMax: 18,
    maxPagesStandard: 28,
    maxPagesQuick: 10,
    quickIndexes: 12,
    maxPagesDirect: 12,
    budgetStandardMs: 8 * 60 * 1000,
    budgetQuickMs: 3 * 60 * 1000,
    pipeline: true,   // v1.3.0：流水线总开关（false 退回串行旧行为）
    workersMax: 2,    // v1.3.0：识别 worker 池上限（实际受设备能力门控）
    workersMin: 1
  };

  var state = {
    libsTried: false,
    libsOk: false,
    inline: null,     // v1.4.0：内联资源桥句柄 {workerUrl, coreMap, langs}（单文件构建注入）
    workers: [],
    workerPromises: [],
    progress: null
  };

  function setProgress(cb) { state.progress = typeof cb === "function" ? cb : null; }
  function prog(msg) { try { if (state.progress) state.progress(msg); } catch (e) { /* 忽略 */ } }

  function loadScript(src) {
    return new Promise(function (resolve, reject) {
      var s = document.createElement("script");
      s.src = src;
      s.async = false;
      s.onload = function () { resolve(); };
      s.onerror = function () { reject(new Error("无法加载引擎脚本：" + src)); };
      (document.head || document.documentElement).appendChild(s);
    });
  }

  var libsPromise = null;
  function loadLibs() {
    // v1.3.0：缓存 in-flight Promise。双 worker 并行创建时会并发调用本函数，
    // 原布尔标志记忆会让第二个调用在库尚未加载完时被误判为「不可用」。
    if (libsPromise) return libsPromise;
    state.libsTried = true;
    // v1.4.0：内联资源桥先行（解码 base64 → 建 Blob URL）。桥不存在（web 端布局）
    // 或解码失败时返回 null，后续与 v1.3.0 行为完全一致（外部目录加载）。
    var bridge = global.__AP_OCR_INLINE__;
    var inlineReady = (bridge && typeof bridge.init === "function")
      ? bridge.init().catch(function () { return null; })
      : Promise.resolve(null);
    // 单文件构建：pdf.js 已内联（pdf.min.js + pdf.worker Blob），优先复用内联资源；
    // 否则回退到外部 ocr-engine/ 目录加载（web 端布局）。
    libsPromise = inlineReady.then(function (inline) {
      state.inline = inline || null;
      var pdfReady = global.pdfjsLib ? Promise.resolve() : loadScript(BASE + "pdf.min.js");
      return pdfReady.then(function () {
        if (!global.pdfjsLib) throw new Error("pdf.js 未初始化");
        global.pdfjsLib.GlobalWorkerOptions.workerSrc = (window.__AP_WORKER_BLOB__) ? window.__AP_WORKER_BLOB__ : new URL("pdf.worker.min.js", BASE).href;
        // tesseract.min.js：单文件构建已内联为普通 <script>（global.Tesseract 就绪），
        // web 端布局从引擎目录动态加载。
        if (global.Tesseract) return Promise.resolve();
        return loadScript(BASE + "tesseract.min.js");
      });
    }).then(function () {
      if (!global.Tesseract) throw new Error("tesseract.js 未初始化");
      state.libsOk = true;
    }).catch(function (e) {
      state.libsOk = false;
      libsPromise = null; // 失败允许重试
      throw e;
    });
    return libsPromise;
  }

  /* v1.3.0：识别 worker 数量门控。移动端 WebView 内存受限，保守单 worker；
     桌面端 4 核 + 4GB 以上开双 worker（识别阶段近似 2x）。 */
  function workerCount() {
    if (!OPTS.pipeline) return 1;
    var nav = global.navigator || {};
    var hc = nav.hardwareConcurrency || 2;
    var dm = (typeof nav.deviceMemory === "number") ? nav.deviceMemory : 4;
    var mobile = /Mobi|Android|iPhone/i.test(nav.userAgent || "");
    if (mobile) return 1;
    if (hc >= 4 && dm >= 4) return Math.max(1, Math.min(OPTS.workersMax, 2));
    return 1;
  }

  function createOneWorker() {
    return loadLibs().then(function () {
      // v1.4.0：内联桥模式（单文件构建）——worker/core 均 Blob URL、语言数据直传。
      // langs 用对象数组 [{code, data(gzip 字节)}]：worker 端 loadLanguage 按 code 写入
      // Emscripten FS，gzip 由魔数自动识别解压（不落 IndexedDB，cacheMethod:none）。
      var inline = state.inline;
      var langs = inline ? inline.langs : OPTS.lang;
      var opts = {
        gzip: false,                // 外部模式目录内为未压缩 traineddata；内联模式该参数不参与
        cacheMethod: "none",        // 避免 IndexedDB 里残留旧模型
        logger: function (m) {
          if (m && m.status === "recognizing text" && typeof m.progress === "number") {
            prog("OCR 识别中 " + Math.round(m.progress * 100) + "%…");
          } else if (m && m.status) {
            prog("OCR 引擎：" + m.status + "…");
          }
        }
      };
      if (inline) {
        opts.workerPath = inline.workerUrl;   // Blob URL（已带 corePath 映射补丁头）
        opts.workerBlobURL = false;           // 直接 new Worker(blob:)，跳过二次包装
        opts.corePath = "inline-ocr-engine";  // 占位前缀：worker 内 importScripts 补丁按文件名映射 coreMap
        opts.langPath = "";                   // langs 已内联，禁止网络回退
      } else {
        opts.workerPath = BASE + "worker.min.js";
        opts.corePath = BASE;                 // 目录 → worker 内按 SIMD 能力选择 *-lstm.wasm.js
        opts.langPath = BASE;                 // 同目录 traineddata（未压缩）
      }
      return global.Tesseract.createWorker(langs, OPTS.oem, opts);
    }).then(function (w) {
      return w.setParameters({ tessedit_pageseg_mode: OPTS.psm }).then(function () {
        return w;
      });
    });
  }

  /* v1.3.0：worker 池创建（并行预热；单个失败不拖垮整个池，≥1 个可用即可）。 */
  function ensureWorkers(n) {
    var want = Math.max(OPTS.workersMin, Math.min(n || 1, OPTS.workersMax));
    if (state.workers.length >= want) return Promise.resolve(state.workers.slice(0, want));
    var creates = [];
    for (var i = 0; i < want; i++) {
      if (!state.workerPromises[i]) {
        state.workerPromises[i] = createOneWorker().then(function (w) {
          state.workers.push(w);
          return w;
        });
      }
      creates.push(state.workerPromises[i]);
    }
    return Promise.all(creates).then(function () {
      return state.workers.slice(0, want);
    }).catch(function (e) {
      if (state.workers.length) return Promise.resolve(state.workers.slice(0, 1));
      state.workerPromises = [];
      throw new Error("WASM OCR 引擎初始化失败：" + (e && e.message ? e.message : e));
    });
  }

  /* v1.3.0：预热入口——选定 PDF 文件后立即调用，把 worker 初始化（约 3-10s：
     traineddata 加载 + WASM 编译）与文件选择/文本层探测重叠。 */
  function warmup() {
    if (!available()) return Promise.resolve(false);
    return ensureWorkers(workerCount()).then(function () { return true; }).catch(function () { return false; });
  }

  function getWorker() {
    return ensureWorkers(1).then(function (ws) { return ws[0]; });
  }

  /* 定向页解析：把前端给的 1 基页码（数组 / 逗号串）转成 0 基、去重、升序、限量 */
  function parseRequestedPages(requested, total) {
    if (!requested) return [];
    var raw = [];
    if (typeof requested === "string") raw = requested.split(/[,\s;]+/);
    else if (typeof requested.length === "number") raw = Array.prototype.slice.call(requested);
    var seen = {}, out = [];
    for (var i = 0; i < raw.length; i++) {
      var n = Math.round(Number(raw[i]));
      if (!isFinite(n) || n < 1) continue;
      var idx = n - 1;
      if (idx >= total || seen[idx]) continue;
      seen[idx] = 1; out.push(idx);
    }
    out.sort(function (a, b) { return a - b; });
    return out.slice(0, OPTS.maxPagesDirect);
  }

  /* 页策略：requested 非空 → 只 OCR 指定页（其余页保留文本层原文，由前端按页回填）；
     否则退回 python extract_full_text 的采样分支（目录区前 tocPages 页 + 全书均匀采样） */
  function planPages(total, mode, requested) {
    var direct = parseRequestedPages(requested, total);
    if (direct.length) return { pages: direct, plan: "direct" };
    var toc = [];
    for (var i = 0; i < Math.min(total, OPTS.tocPages); i++) toc.push(i);
    var sample = [];
    if (total > OPTS.tocPages) {
      var step = Math.max(8, Math.floor(total / 20));
      for (var j = OPTS.tocPages; j < total && sample.length < OPTS.sampleMax; j += step) sample.push(j);
    }
    var seen = {};
    var all = [];
    toc.concat(sample).forEach(function (p) {
      if (p >= 0 && p < total && !seen[p]) { seen[p] = 1; all.push(p); }
    });
    all.sort(function (a, b) { return a - b; });
    if (mode === "quick") all = all.slice(0, OPTS.quickIndexes);
    var cap = mode === "quick" ? OPTS.maxPagesQuick : OPTS.maxPagesStandard;
    return { pages: all.slice(0, cap), plan: "sample" };
  }

  /* v1.3.0：渲染时可指定复用目标 canvas（池化）；长边超限按比例降采样；
     把实际生效的 DPI 挂在 canvas 上（72 × 有效倍率），供逐页 user_defined_dpi 使用。 */
  function renderPageToCanvas(pdf, pageNo /*1-based*/, scale, target) {
    var s = scale || OPTS.scale;
    return pdf.getPage(pageNo).then(function (page) {
      var v1 = page.getViewport({ scale: 1 });
      var eff = Math.min(s, OPTS.maxLongEdge / Math.max(v1.width, v1.height));
      if (!(eff > 0.5)) eff = 0.5;
      var viewport = page.getViewport({ scale: eff });
      var canvas = target || document.createElement("canvas");
      canvas.width = Math.max(1, Math.floor(viewport.width));
      canvas.height = Math.max(1, Math.floor(viewport.height));
      var ctx2 = canvas.getContext("2d", { alpha: false });
      ctx2.fillStyle = "#ffffff";
      ctx2.fillRect(0, 0, canvas.width, canvas.height);
      return page.render({ canvasContext: ctx2, viewport: viewport, background: "#ffffff" }).promise.then(function () {
        if (page.cleanup) { try { page.cleanup(); } catch (e) { } }
        try { canvas.__apDpi = Math.round(72 * eff); } catch (e) { }
        return canvas;
      });
    });
  }

  /* v1.3.0：图像预处理（单遍直方图 + 极性归一 + 按需拉伸）。
     返回 {blank, coverage, stretched, inverted}：
       - blank：墨迹（与背景众数亮度差 > inkDepth 的像素）覆盖率 < blankCoverage；
       - inverted：深色底页面（背景众数 < 128，夜模式 PDF/深底版权页）反色归一为白底黑字；
       - stretched：全局动态范围不足（1%/99% 分位差 < contrastMin，典型为淡墨扫描件）
         时按 [p1,p99] → [0,255] 拉伸灰度。
     设计要点：背景取直方图众数而非分位数——文字占比可以远超 2%，分位数会落进
     文字分布内部，把正常页判成「需要拉伸」甚至把空白页整页拉黑（实测踩坑）；
     墨迹/拉伸判定必须与背景极性绑定，深底页按 (bg+inkDepth) 取亮像素统计。 */
  function preprocessCanvas(canvas) {
    var w = canvas.width, h = canvas.height, n = w * h;
    if (!n) return { blank: true, coverage: 0, stretched: false, inverted: false };
    var ctx2 = canvas.getContext("2d", { alpha: false });
    var img;
    try { img = ctx2.getImageData(0, 0, w, h); } catch (e) { return { blank: false, coverage: -1, stretched: false, inverted: false }; }
    var d = img.data;
    var hist = new Uint32Array(256);
    var p = 0;
    for (var i = 0; i < n; i++, p += 4) {
      hist[(d[p] * 77 + d[p + 1] * 151 + d[p + 2] * 28) >> 8]++;
    }
    // 背景众数（直方图峰值）
    var bg = 255, peak = 0;
    for (var v0 = 0; v0 < 256; v0++) { if (hist[v0] > peak) { peak = hist[v0]; bg = v0; } }
    // 相对背景的墨迹覆盖率（自动适配深底/白底极性）
    var invert = bg < 128;
    var ink = 0, v;
    if (!invert) { for (v = 0; v <= bg - OPTS.inkDepth && v < 256; v++) ink += hist[v]; }
    else { for (v = bg + OPTS.inkDepth; v < 256; v++) ink += hist[v]; }
    var coverage = ink / n;
    if (coverage < OPTS.blankCoverage) return { blank: true, coverage: coverage, stretched: false, inverted: invert };
    // 全局动态范围：1%/99% 分位（极性无关）
    var p1Cut = Math.floor(n * 0.01), p99Cut = Math.floor(n * 0.99);
    var acc = 0, p1 = 0, p99 = 255;
    for (var v2 = 0; v2 < 256; v2++) { acc += hist[v2]; if (acc >= p1Cut) { p1 = v2; break; } }
    acc = 0;
    for (var v3 = 255; v3 >= 0; v3--) { acc += hist[v3]; if (acc >= n - p99Cut) { p99 = v3; break; } }
    var needStretch = p99 - p1 < OPTS.contrastMin;
    if (!invert && !needStretch) return { blank: false, coverage: coverage, stretched: false, inverted: false };
    // 写回：反色（深底页）与/或拉伸（动态范围不足）
    var span = Math.max(1, p99 - p1);
    p = 0;
    for (var j = 0; j < n; j++, p += 4) {
      var g0 = (d[p] * 77 + d[p + 1] * 151 + d[p + 2] * 28) >> 8;
      var g1 = invert ? 255 - g0 : g0;
      if (needStretch) g1 = (g1 - p1) * 255 / span;
      g1 = g1 < 0 ? 0 : (g1 > 255 ? 255 : g1);
      var gg = g1 | 0;
      d[p] = d[p + 1] = d[p + 2] = gg;
    }
    try { ctx2.putImageData(img, 0, 0); } catch (e) { /* 忽略 */ }
    return { blank: false, coverage: coverage, stretched: needStretch, inverted: invert };
  }

  /* v1.3.0：canvas → PNG 字节（自持编码）。tesseract.js v5 对 canvas 的 toBlob 是异步的，
     直接复用 canvas 有竞态；先编码成字节再提交 worker，worker 收到独立副本后 canvas 即可复用。 */
  function canvasToBytes(canvas) {
    return new Promise(function (resolve, reject) {
      if (!canvas.toBlob) return reject(new Error("canvas.toBlob 不可用"));
      canvas.toBlob(function (b) {
        if (!b) return reject(new Error("canvas 编码失败"));
        var fr = new FileReader();
        fr.onload = function () { resolve(new Uint8Array(fr.result)); };
        fr.onerror = function () { reject(new Error("blob 读取失败")); };
        fr.readAsArrayBuffer(b);
      }, "image/png");
    }).catch(function () {
      // 兜底：dataURL 同步解码（极少走到）
      var bin = atob(canvas.toDataURL("image/png").split(",")[1]);
      var u = new Uint8Array(bin.length);
      for (var i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
      return u;
    });
  }

  function sleep(ms) { return new Promise(function (res) { setTimeout(res, ms); }); }

  /* v1.3.0：单页识别 + 智能重试。
     params 为逐页 Tesseract 参数（user_defined_dpi 按渲染倍率计算）。
     重试条件：文本 ≥ retryMinChars 且 conf ∈ [retryFloorConf, confThreshold) 且预算未过 retryBudgetFrac。 */
  function recognizePage(ctx, w, pageNo, bytes, dpi) {
    var params = null;
    if (ctx.params) {
      params = {};
      for (var k in ctx.params) params[k] = ctx.params[k];
      if (dpi) params.user_defined_dpi = String(dpi);
    }
    var tO = Date.now();
    return w.recognize(bytes, params).then(function (res) {
      ctx.stats.msOcr += Date.now() - tO;
      var d = res && res.data ? res.data : {};
      var text0 = typeof d.text === "string" ? d.text : "";
      var conf0 = typeof d.confidence === "number" ? d.confidence : -1;
      var verdict = { text: text0, confidence: conf0, retried: false };
      var wantRetry = text0 && text0.trim().length >= OPTS.retryMinChars
        && conf0 >= OPTS.retryFloorConf && conf0 < OPTS.confThreshold
        && (Date.now() - ctx.start) < ctx.budgetMs * OPTS.retryBudgetFrac;
      if (!wantRetry) return verdict;
      return renderPageToCanvas(ctx.pdf, pageNo, OPTS.scaleRetry).then(function (c2) {
        var pre2 = null;
        try { pre2 = preprocessCanvas(c2); } catch (e) { pre2 = null; }
        if (pre2 && pre2.blank) return verdict;
        return canvasToBytes(c2).then(function (b2) {
          var p2 = null;
          if (params) { p2 = {}; for (var k2 in params) p2[k2] = params[k2]; if (c2.__apDpi) p2.user_defined_dpi = String(c2.__apDpi); }
          var tO2 = Date.now();
          return w.recognize(b2, p2).then(function (res2) {
            ctx.stats.msOcr += Date.now() - tO2;
            var d2 = res2 && res2.data ? res2.data : {};
            var text1 = typeof d2.text === "string" ? d2.text : "";
            var conf1 = typeof d2.confidence === "number" ? d2.confidence : -1;
            if (text1.trim().length > text0.trim().length) {
              verdict.text = text1;
              if (conf1 >= 0) verdict.confidence = conf1;
            } else if (conf1 >= 0 && text1.trim().length === text0.trim().length) {
              verdict.confidence = conf1;
            }
            verdict.retried = true;
            return verdict;
          });
        });
      }).catch(function () {
        // 重捕获失败则保留首轮结果
        return verdict;
      });
    });
  }

  /* v1.2.0 原串行行为（legacy 基准 / 降级开关）：渲染 → 识别 → 低置信 3.0x 重试，逐页串行。 */
  function legacyRecognizeWithRetry(w, pdf, pageNo, firstCanvas) {
    return w.recognize(firstCanvas).then(function (res) {
      var d = res && res.data ? res.data : {};
      var text0 = typeof d.text === "string" ? d.text : "";
      var conf0 = typeof d.confidence === "number" ? d.confidence : -1;
      var verdict = { text: text0, confidence: conf0, retried: false };
      if (text0 && conf0 >= 0 && conf0 < OPTS.confThreshold) {
        return renderPageToCanvas(pdf, pageNo, OPTS.scaleRetry).then(function (canvas2) {
          return w.recognize(canvas2);
        }).then(function (res2) {
          var text1 = res2 && res2.data && typeof res2.data.text === "string" ? res2.data.text : "";
          var conf1 = res2 && res2.data && typeof res2.data.confidence === "number" ? res2.data.confidence : -1;
          if (text1.trim().length > text0.trim().length) {
            verdict.text = text1;
            if (conf1 >= 0) verdict.confidence = conf1;
          } else if (conf1 >= 0 && text1.trim().length === text0.trim().length) {
            verdict.confidence = conf1;
          }
          verdict.retried = true;
          return verdict;
        }).catch(function () {
          return verdict;
        });
      }
      return verdict;
    });
  }

  function runLegacy(ctx, pages, w) {
    var results = new Array(pages.length);
    var idx = 0;
    function next() {
      if (idx >= pages.length) return Promise.resolve();
      if (Date.now() - ctx.start > ctx.budgetMs) {
        for (var r = idx; r < pages.length; r++) results[r] = { text: "", confidence: -1, budget: true };
        return Promise.resolve();
      }
      var i = idx++;
      var p = pages[i];
      prog("OCR 第 " + (p + 1) + " 页（" + (i + 1) + "/" + pages.length + "）…");
      return renderPageToCanvas(ctx.pdf, p + 1).then(function (canvas) {
        return legacyRecognizeWithRetry(w, ctx.pdf, p + 1, canvas);
      }).then(function (v) {
        if (v.retried) prog("第 " + (p + 1) + " 页置信度低，已 3.0x 重渲染重识别（conf " + v.confidence + "）");
        results[i] = v;
        return next();
      }).catch(function (e) {
        prog("第 " + (p + 1) + " 页识别失败，跳过：" + (e && e.message ? e.message : e));
        results[i] = { text: "", confidence: -1 };
        return next();
      });
    }
    return next().then(function () { return results; });
  }

  /* v1.3.0 流水线：
     主线程依次「渲染 → 预处理 →（空白则跳过）→ PNG 编码」，编码完成即把字节提交给
     空闲 worker（轮询分配、每 worker 队列串行）；canvas 池上限 = worker 数 + 1，
     编码完成后槽位即被下一页复用。渲染/编码与识别完全重叠。
     完成度：每页一条 completion promise（空白页立即 resolve；正常页在识别落盘后
     resolve）——不能用 Promise.all(chains) 快照，否则快照后追加的任务会被漏等，
     recognize() 提前组装出缺页结果。 */
  function runPipeline(ctx, pdf, pages, ws) {
    var W = ws.length;
    var C = Math.min(W + 1, pages.length);
    var pool = new Array(C).fill(null);
    var chains = ws.map(function () { return Promise.resolve(null); });
    var nextWi = 0;
    var results = new Array(pages.length);
    var completions = [];
    // 槽位忙闲：toBlob 完成顺序不保证（大页 PNG 编码慢、小页快），全局计数门控
    // 会出现「槽位尚在编码却被复用渲染清空」的竞态（实测出白页/半页）。
    // 只有槽位自己的编码完成了才允许下一次渲染复用该槽位。
    var slotBusy = new Array(C).fill(false);

    function poolCanvas(slot) {
      if (!pool[slot]) pool[slot] = document.createElement("canvas");
      return pool[slot];
    }

    var producer = (async function () {
      for (var i = 0; i < pages.length; i++) {
        if (Date.now() - ctx.start > ctx.budgetMs) {
          for (var r = i; r < pages.length; r++) results[r] = { text: "", confidence: -1, budget: true };
          break;
        }
        let slot = i % C;
        while (slotBusy[slot]) await sleep(16);
        slotBusy[slot] = true;
        let canvas = poolCanvas(slot);
        let pageNo = pages[i] + 1;
        prog("OCR 第 " + pageNo + "/" + ctx.total + " 页（" + (i + 1) + "/" + pages.length + "）…");
        var tR = Date.now();
        await renderPageToCanvas(pdf, pageNo, OPTS.scale, canvas);
        ctx.stats.msRender += Date.now() - tR;
        let pre = null;
        try { pre = preprocessCanvas(canvas); } catch (e) { pre = { blank: false, coverage: -1, stretched: false }; }
        if (pre && pre.blank) {
          results[i] = { text: "", confidence: -1, blank: true };
          ctx.stats.blankSkipped++;
          completions.push(Promise.resolve(null));
          slotBusy[slot] = false;
          prog("第 " + pageNo + " 页近乎空白，快速跳过（墨迹覆盖率 " + Math.max(0, pre.coverage * 100).toFixed(2) + "%）");
          continue;
        }
        if (pre && pre.stretched) ctx.stats.stretched++;
        if (pre && pre.inverted) ctx.stats.inverted++;
        let idx = i; // 必须块级作用域：var 会让所有异步闭包共享最后一次循环的 i
        completions.push(canvasToBytes(canvas).then(function (bytes) {
          slotBusy[slot] = false; // 编码完成，槽位可复用（字节已是独立副本）
          var wi = nextWi % W; nextWi++;
          var seg = chains[wi].then(function () {
            return recognizePage(ctx, ws[wi], pages[idx] + 1, bytes, canvas.__apDpi).then(function (v) {
              results[idx] = v;
              if (v && v.retried) prog("第 " + (pages[idx] + 1) + " 页置信度低，已 3.0x 重渲染重识别（conf " + v.confidence + "）");
              return null;
            }).catch(function (e) {
              results[idx] = { text: "", confidence: -1, error: String(e && e.message || e) };
              return null;
            });
          });
          chains[wi] = seg;
          return seg; // completion 在该页识别落盘后才 resolve
        }, function (err) {
          slotBusy[slot] = false;
          results[idx] = { text: "", confidence: -1, error: String(err && err.message || err) };
          return null;
        }));
      }
    })();

    return producer.then(function () {
      return Promise.all(completions);
    }).then(function () {
      // 释放池内位图
      for (var s = 0; s < C; s++) {
        if (pool[s]) { try { pool[s].width = 0; pool[s].height = 0; } catch (e) { } pool[s] = null; }
      }
      return results;
    });
  }

  /**
   * 统一 OCR 入口。
   * @param {File|Blob} file PDF 文件（与宿主桥 ocrViaHost 的入参同一份文件对象）
   * @param {string} mode quick | standard | deep（none 不应进入本函数）
   * @param {Array<number>|string} [requestedPages] 定向页（1 基）
   * @param {{legacy?:boolean}} [opts] v1.3.0：legacy=true 走 v1.2.0 串行基线
   * @returns {Promise<{ok:boolean,text?:string,error?:string,meta?:object}>}
   */
  async function recognize(file, mode, requestedPages, opts) {
    if (mode === "none") return { ok: false, error: "OCR 已关闭（none 模式）" };
    var legacy = !!(opts && opts.legacy);
    var t0 = Date.now();
    var budget = mode === "quick" ? OPTS.budgetQuickMs : OPTS.budgetStandardMs;
    var stats = { msRender: 0, msOcr: 0, retriedCount: 0, retriedImpr: 0, blankSkipped: 0, stretched: 0, inverted: 0, workers: 1, pipeline: !legacy };
    try {
      var ws = legacy ? [await getWorker()] : await ensureWorkers(workerCount());
      stats.workers = ws.length;
      prog("正在读取 PDF…");
      var buf = await file.arrayBuffer();
      var pdf = await global.pdfjsLib.getDocument({
        data: new Uint8Array(buf),
        isEvalSupported: false,
        cMapUrl: BASE + "cmaps/",
        cMapPacked: true
      }).promise;
      var total = pdf.numPages;
      if (!total) throw new Error("PDF 无有效页面");
      var plan = planPages(total, mode, requestedPages);
      var pages = plan.pages;
      prog("共 " + total + " 页，" + (plan.plan === "direct"
        ? "按前端可疑页定向 OCR " + pages.length + " 页（其余页保留 pdf.js 文本层原文）"
        : "采样 " + pages.length + " 页进行 OCR（目录区优先 + 全书均匀采样）") + "…");
      var ctx = { pdf: pdf, total: total, start: t0, budgetMs: budget, stats: stats, params: legacy ? null : { preserve_interword_spaces: OPTS.preserveSpaces } };
      var results = legacy ? await runLegacy(ctx, pages, ws[0]) : await runPipeline(ctx, pdf, pages, ws);
      var chunks = [];
      var used = 0;
      for (var i = 0; i < pages.length; i++) {
        var v = results[i] || { text: "", confidence: -1 };
        var text = typeof v.text === "string" ? v.text : "";
        if (v.retried) {
          stats.retriedCount++;
          if (text.trim()) stats.retriedImpr++;
        }
        if (text && text.trim()) { chunks.push("[page " + (pages[i] + 1) + "]\n" + text); used++; }
      }
      try { pdf.destroy(); } catch (e) { /* 忽略 */ }
      var outText = chunks.join("\n");
      if (!outText.trim()) throw new Error("OCR 未识别出文本（" + used + "/" + pages.length + " 页有效）");
      prog("OCR 完成：" + used + " 页有效，" + outText.length + " 字符，重试 " + stats.retriedCount + " 页（改善 " + stats.retriedImpr + " 页），跳过空白 " + stats.blankSkipped + " 页，耗时 " + Math.round((Date.now() - t0) / 1000) + " 秒");
      return {
        ok: true, text: outText,
        meta: {
          engine: "tesseract-wasm", version: VERSION, pages: used, total: total,
          pageList: pages.map(function (p) { return p + 1; }),
          lang: OPTS.lang, oem: OPTS.oem, psm: OPTS.psm, mode: mode,
          confThreshold: OPTS.confThreshold, scaleRetry: OPTS.scaleRetry,
          retried: stats.retriedCount, retriedImpr: stats.retriedImpr,
          blankSkipped: stats.blankSkipped, stretched: stats.stretched, inverted: stats.inverted,
          workers: stats.workers, pipeline: stats.pipeline,
          msRender: stats.msRender, msOcr: stats.msOcr, msTotal: Date.now() - t0,
          preprocessed: !legacy,
          pageStatus: pages.map(function (p, i) {
            var v = results[i];
            if (!v) return "missing";
            if (v.blank) return "blank";
            if (v.budget) return "budget";
            if (v.error) return "error:" + String(v.error).slice(0, 40);
            return "ok(" + String(v.text || "").trim().length + "c,conf=" + v.confidence + ")";
          })
        }
      };
    } catch (e) {
      var msg = e && e.message ? e.message : String(e);
      return { ok: false, error: "WASM OCR：" + msg };
    }
  }

  /* ── v1.5.0：图像 OCR（拍照 / 截图 / 图片导入） ───────────────────────────
     与 PDF 路径同参数（OEM=3、PSM=3、保留词间空格、逐页/逐图 DPI 声明）：
     createImageBitmap 解码 → 等比缩放（长边上限同 maxLongEdge）→ 预处理
     （极性归一/对比度拉伸/空白跳过）→ 自持 PNG 编码 → worker 识别 → 低置信
     3.0x 重识别。入参 File/Blob/Uint8Array（PNG/JPEG/GIF/BMP/WebP）。 */
  function toBlobInput(file){
    if(typeof Blob!=="undefined"&&file instanceof Blob)return file;
    if(file instanceof Uint8Array)return new Blob([file]);
    if(file&&file.buffer)return new Blob([file.buffer]);
    return new Blob([file]);
  }
  function loadBitmap(blob){
    if(global.createImageBitmap){
      return createImageBitmap(blob).catch(function(){
        return new Promise(function(resolve,reject){
          var url=URL.createObjectURL(blob);
          var img=new Image();
          img.onload=function(){resolve(img);};
          img.onerror=function(){URL.revokeObjectURL(url);reject(new Error("图像解码失败"));};
          img.src=url;
        });
      });
    }
    return new Promise(function(resolve,reject){
      var url=URL.createObjectURL(blob);
      var img=new Image();
      img.onload=function(){resolve(img);};
      img.onerror=function(){URL.revokeObjectURL(url);reject(new Error("图像解码失败"));};
      img.src=url;
    });
  }
  function drawScaled(bmp,scale,target){
    var w0=bmp.width||bmp.videoWidth||0,h0=bmp.height||bmp.videoHeight||0;
    if(!w0||!h0)throw new Error("图像尺寸无效");
    var eff=Math.min(scale||OPTS.scale,OPTS.maxLongEdge/Math.max(w0,h0));
    if(!(eff>0.5))eff=0.5;
    var canvas=target||document.createElement("canvas");
    canvas.width=Math.max(1,Math.floor(w0*eff));
    canvas.height=Math.max(1,Math.floor(h0*eff));
    var ctx2=canvas.getContext("2d",{alpha:false});
    ctx2.fillStyle="#ffffff";
    ctx2.fillRect(0,0,canvas.width,canvas.height);
    ctx2.drawImage(bmp,0,0,canvas.width,canvas.height);
    try{canvas.__apDpi=Math.round(72*eff);}catch(e){}
    return canvas;
  }
  async function recognizeImage(file,opts){
    var t0=Date.now();
    var bmp=null;
    try{
      var ws=await ensureWorkers(workerCount());
      var blob=toBlobInput(file);
      bmp=await loadBitmap(blob);
      var canvas=drawScaled(bmp,OPTS.scale);
      var pre=null;
      try{pre=preprocessCanvas(canvas);}catch(e){}
      if(pre&&pre.blank){
        return {ok:true,text:"",meta:{engine:"tesseract-wasm",version:VERSION,pages:0,image:true,blank:true,workers:ws.length,msTotal:Date.now()-t0}};
      }
      var params={preserve_interword_spaces:OPTS.preserveSpaces,user_defined_dpi:String(canvas.__apDpi||OPTS.userDefinedDpi)};
      var bytes=await canvasToBytes(canvas);
      var res=await ws[0].recognize(bytes,params);
      var d=res&&res.data?res.data:{};
      var text0=typeof d.text==="string"?d.text:"";
      var conf0=typeof d.confidence==="number"?d.confidence:-1;
      var retried=false;
      var wantRetry=text0&&text0.trim().length>=OPTS.retryMinChars
        &&conf0>=OPTS.retryFloorConf&&conf0<OPTS.confThreshold;
      if(wantRetry){
        try{
          var c2=drawScaled(bmp,OPTS.scaleRetry);
          var pre2=preprocessCanvas(c2);
          if(!(pre2&&pre2.blank)){
            var b2=await canvasToBytes(c2);
            var p2={preserve_interword_spaces:OPTS.preserveSpaces,user_defined_dpi:String(c2.__apDpi||OPTS.userDefinedDpi)};
            var res2=await ws[0].recognize(b2,p2);
            var d2=res2&&res2.data?res2.data:{};
            var text1=typeof d2.text==="string"?d2.text:"";
            var conf1=typeof d2.confidence==="number"?d2.confidence:-1;
            if(text1.trim().length>text0.trim().length){text0=text1;if(conf1>=0)conf0=conf1;retried=true;}
            else if(text1.trim().length===text0.trim().length&&conf1>=0){conf0=conf1;retried=true;}
          }
        }catch(e2){/* 重捕获失败保留首轮结果 */}
      }
      if(bmp.close){try{bmp.close();}catch(e3){}}
      bmp=null;
      if(!text0.trim())return {ok:false,error:"图像中未识别出文本"};
      prog("图像识别完成："+text0.trim().length+" 字符，置信度 "+conf0+"，耗时 "+Math.round((Date.now()-t0)/1000)+" 秒");
      return {ok:true,text:text0,meta:{engine:"tesseract-wasm",version:VERSION,pages:1,image:true,
        confidence:conf0,retried:retried,workers:ws.length,msTotal:Date.now()-t0}};
    }catch(e){
      if(bmp&&bmp.close){try{bmp.close();}catch(e4){}}
      var msg=e&&e.message?e.message:String(e);
      return {ok:false,error:"WASM OCR（图像）："+msg};
    }
  }
  async function recognizeImages(files,opts){
    var out=[],errs=[];
    var list=Array.prototype.slice.call(files||[]);
    for(var i=0;i<list.length;i++){
      prog("识别第 "+(i+1)+"/"+list.length+" 张图像…");
      var r=await recognizeImage(list[i],opts);
      if(r&&r.ok)out.push({text:r.text,meta:r.meta});
      else errs.push({index:i,error:(r&&r.error)||"未知错误"});
    }
    return {ok:out.length>0,texts:out,errors:errs};
  }

  /* 环境可用性快速探测（不初始化引擎）：http/https 且引擎目录可寻址 */
  function available() {
    var p = global.location.protocol;
    return p === "http:" || p === "https:";
  }

  global.__AP_WASM_OCR__ = {
    version: VERSION,
    base: BASE,
    options: OPTS,
    available: available,
    setProgress: setProgress,
    recognize: recognize,
    recognizeImage: recognizeImage,
    recognizeImages: recognizeImages,
    warmup: warmup,
    preprocessCanvas: preprocessCanvas, // 导出供自测/调参（生产流程内部使用）
    canvasToBytes: canvasToBytes
  };
})(window);
