/* 知债：星穹学途 · 统一 WASM OCR 引擎（Web 浏览器 / 安卓 WebView 共用）
 * ============================================================================
 * 与 Windows 宿主桥（python tools\ocr_pipeline.py + tesseract CLI）行为对齐：
 *   - 语言 chi_sim+eng（与 python 管线 TESS_LANG 默认一致，traineddata 复用
 *     仓库 tools\tessdata 的同一份文件，识别质量同源）
 *   - OEM=1（LSTM）、PSM=3（全自动页面分割），与 TESS_OEM/TESS_PSM 默认一致
 *   - 页采样策略对齐 extract_full_text：目录区前 24 页 + 全书均匀采样，
 *     standard 上限 28 页 / quick 上限 10 页，渲染 scale=2.0
 *   - 输出文本格式对齐 tesseract_pdf_pages：每页 "[page N]\n<文本>"，"\n" 连接
 *   - 按页定向（v1.1.0）：入参 pages（1 基页码数组或逗号串，来自前端 pdf.js 页级乱码判定）非空时
 *     只 OCR 这些页，其余页由前端 backfillOcrPages() 保留文本层原文；上限 OPTS.maxPagesDirect，
 *     与 python tools\ocr_pipeline.py 的 GARBLE_OCR_MAX_PAGES 对齐；pages 为空时退回原有
 *     「目录区 + 全书均匀采样」策略（扫描件全本无文本层的场景）
 *   - 返回约定与宿主桥一致：{ok:true,text} / {ok:false,error}
 *
 * 依赖（与本文件同目录，全部离线自包含）：
 *   tesseract.min.js / worker.min.js               (tesseract.js 5.1.1, Apache-2.0)
 *   tesseract-core-simd-lstm.wasm.js               (tesseract.js-core 5.1.1)
 *   tesseract-core-lstm.wasm.js                    (无 SIMD 设备回退)
 *   chi_sim.traineddata / eng.traineddata          (与 Windows 端同一份)
 *   pdf.min.js / pdf.worker.min.js                 (pdf.js 3.11.174 legacy, Apache-2.0)
 *
 * 运行环境要求：页面经 http/https 加载（本地静态服务、安卓 appassets 虚拟域）。
 * file:// 直开时浏览器禁止 Worker/fetch 本地资源，本引擎在初始化时探测失败并
 * 返回不可用，由上层降级到「粘贴正文」兜底提示。
 */
(function (global) {
  "use strict";

  var BASE = (function () {
    var el = document.currentScript;
    var src = el && el.src ? el.src : "ocr-engine/ocr-wasm.js";
    return new URL(".", new URL(src, global.location.href)).href;
  })();

  // 与 python 管线一致的可调参数
  var OPTS = {
    lang: "chi_sim+eng",
    oem: 1,          // OEM.LSTM_ONLY
    psm: "3",        // PSM.AUTO
    scale: 2.0,
    tocPages: 24,
    sampleMax: 18,
    maxPagesStandard: 28,
    maxPagesQuick: 10,
    quickIndexes: 12,
    maxPagesDirect: 12,
    budgetStandardMs: 8 * 60 * 1000,
    budgetQuickMs: 3 * 60 * 1000
  };

  var state = {
    libsTried: false,
    libsOk: false,
    worker: null,
    workerPromise: null,
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

  function loadLibs() {
    if (state.libsTried) return state.libsOk ? Promise.resolve() : Promise.reject(new Error("引擎库不可用"));
    state.libsTried = true;
    return loadScript(BASE + "pdf.min.js").then(function () {
      if (!global.pdfjsLib) throw new Error("pdf.js 未初始化");
      global.pdfjsLib.GlobalWorkerOptions.workerSrc = new URL("pdf.worker.min.js", BASE).href;
      return loadScript(BASE + "tesseract.min.js");
    }).then(function () {
      if (!global.Tesseract) throw new Error("tesseract.js 未初始化");
      state.libsOk = true;
    }).catch(function (e) {
      state.libsOk = false;
      throw e;
    });
  }

  function getWorker() {
    if (state.worker) return Promise.resolve(state.worker);
    if (state.workerPromise) return state.workerPromise;
    state.workerPromise = loadLibs().then(function () {
      return global.Tesseract.createWorker(OPTS.lang, OPTS.oem, {
        workerPath: BASE + "worker.min.js",
        corePath: BASE,                 // 目录 → worker 内按 SIMD 能力选择 *-lstm.wasm.js
        langPath: BASE,                 // 同目录 traineddata（未压缩）
        gzip: false,
        cacheMethod: "none",            // 避免 IndexedDB 里残留旧模型
        logger: function (m) {
          if (m && m.status === "recognizing text" && typeof m.progress === "number") {
            prog("OCR 识别中 " + Math.round(m.progress * 100) + "%…");
          } else if (m && m.status) {
            prog("OCR 引擎：" + m.status + "…");
          }
        }
      });
    }).then(function (w) {
      return w.setParameters({ tessedit_pageseg_mode: OPTS.psm }).then(function () {
        state.worker = w;
        return w;
      });
    }).catch(function (e) {
      state.workerPromise = null;
      throw new Error("WASM OCR 引擎初始化失败：" + (e && e.message ? e.message : e));
    });
    return state.workerPromise;
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

  function renderPageToCanvas(pdf, pageNo /*1-based*/) {
    return pdf.getPage(pageNo).then(function (page) {
      var viewport = page.getViewport({ scale: OPTS.scale });
      var canvas = document.createElement("canvas");
      canvas.width = Math.max(1, Math.floor(viewport.width));
      canvas.height = Math.max(1, Math.floor(viewport.height));
      var ctx = canvas.getContext("2d", { alpha: false });
      ctx.fillStyle = "#ffffff";
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      return page.render({ canvasContext: ctx, viewport: viewport, background: "#ffffff" }).promise.then(function () {
        if (page.cleanup) { try { page.cleanup(); } catch (e) { } }
        return canvas;
      });
    });
  }

  /**
   * 统一 OCR 入口。
   * @param {File|Blob} file PDF 文件（与宿主桥 ocrViaHost 的入参同一份文件对象）
   * @param {string} mode quick | standard | deep（none 不应进入本函数）
   * @returns {Promise<{ok:boolean,text?:string,error?:string}>}
   */
  function recognize(file, mode, requestedPages) {
    if (mode === "none") return Promise.resolve({ ok: false, error: "OCR 已关闭（none 模式）" });
    var t0 = Date.now();
    var budget = mode === "quick" ? OPTS.budgetQuickMs : OPTS.budgetStandardMs;
    var workerRef = null;

    return getWorker().then(function (w) {
      workerRef = w;
      prog("正在读取 PDF…");
      return file.arrayBuffer();
    }).then(function (buf) {
      var task = global.pdfjsLib.getDocument({
        data: new Uint8Array(buf),
        isEvalSupported: false,
        cMapUrl: BASE + "cmaps/",
        cMapPacked: true
      });
      return task.promise;
    }).then(function (pdf) {
      var total = pdf.numPages;
      if (!total) throw new Error("PDF 无有效页面");
      var plan = planPages(total, mode, requestedPages);
      var pages = plan.pages;
      prog("共 " + total + " 页，" + (plan.plan === "direct"
        ? "按前端可疑页定向 OCR " + pages.length + " 页（其余页保留 pdf.js 文本层原文）"
        : "采样 " + pages.length + " 页进行 OCR（目录区优先 + 全书均匀采样）") + "…");
      var chunks = [];
      var used = 0;
      var idx = 0;
      function next() {
        if (idx >= pages.length) return Promise.resolve();
        if (Date.now() - t0 > budget) { chunks.push("[ocr-budget] stopped"); return Promise.resolve(); }
        var p = pages[idx++];
        prog("OCR 第 " + (p + 1) + "/" + total + " 页（" + idx + "/" + pages.length + "）…");
        return renderPageToCanvas(pdf, p + 1).then(function (canvas) {
          return workerRef.recognize(canvas);
        }).then(function (res) {
          var text = res && res.data && typeof res.data.text === "string" ? res.data.text : "";
          if (text && text.trim()) { chunks.push("[page " + (p + 1) + "]\n" + text); used++; }
          return next();
        }).catch(function (e) {
          // 单页失败不中断（对齐 python：render 失败 continue）
          prog("第 " + (p + 1) + " 页识别失败，跳过：" + (e && e.message ? e.message : e));
          return next();
        });
      }
      return next().then(function () {
        try { pdf.destroy(); } catch (e) { /* 忽略 */ }
        var text = chunks.join("\n");
        if (!text.trim()) throw new Error("OCR 未识别出文本（" + used + "/" + pages.length + " 页有效）");
        prog("OCR 完成：" + used + " 页有效，" + text.length + " 字符，耗时 " + Math.round((Date.now() - t0) / 1000) + " 秒");
        return { ok: true, text: text, meta: { engine: "tesseract-wasm", pages: used, total: total, pageList: pages.map(function (p) { return p + 1; }), lang: OPTS.lang, oem: OPTS.oem, psm: OPTS.psm, mode: mode } };
      });
    }).catch(function (e) {
      var msg = e && e.message ? e.message : String(e);
      return { ok: false, error: "WASM OCR：" + msg };
    });
  }

  /* 环境可用性快速探测（不初始化引擎）：http/https 且引擎目录可寻址 */
  function available() {
    var p = global.location.protocol;
    return p === "http:" || p === "https:";
  }

  global.__AP_WASM_OCR__ = {
    version: "1.1.0",
    base: BASE,
    options: OPTS,
    available: available,
    setProgress: setProgress,
    recognize: recognize
  };
})(window);
