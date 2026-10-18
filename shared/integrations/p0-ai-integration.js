/* ═══════════════════════════════════════════════════════════════════
 * P0 迁移：AI 调用层 + AI 智能出题链路（源：Astralpath index.html）
 * 注入位置：.cloudflare/pages/index.html 尾部 </body> 前
 * 对应开发方案 P0-1（模块 A）/ P0-2（模块 C）。
 * 依赖基准已有：esc / sanitizeText / activeBook / activeNodes / activeQs /
 *              title / scoreOf / save / $ / K
 * ═══════════════════════════════════════════════════════════════════ */
(function () {
  "use strict";

  // ── 1. JSON 容错解析（源 parseJsonLoose L6378）────────────────────────
  function jsonLoose(text) {
    var t = String(text || "").trim();
    t = t.replace(/^[\s\S]*?```(?:json)?\s*/i, "").replace(/```[\s\S]*$/, "").trim();
    try { return JSON.parse(t); } catch (e) {}
    var i = t.indexOf("{"), j = t.lastIndexOf("}");
    if (i >= 0 && j > i) { try { return JSON.parse(t.slice(i, j + 1)); } catch (e) {} }
    var i2 = t.indexOf("["), j2 = t.lastIndexOf("]");
    if (i2 >= 0 && j2 > i2) { try { return JSON.parse(t.slice(i2, j2 + 1)); } catch (e) {} }
    return null;
  }

  // ── 2. 故障冷却分类（源 __t13TxErr L7052）────────────────────────────
  function isTransportError(e) {
    var m = String(e && e.message || e);
    return /HTTP \d|Failed to fetch|NetworkError|network error|abort|timeout|超时|load failed|connection|socket/i.test(m);
  }

  // ── 3. 密钥信封 + 内置演示密钥（源 __AP_MIMO__ L6202-6265）───────────
  var MIMO_LS = "astralpath_mimo_v1";
  var MIMO_IDB = "ap_mimo_dek";
  // 内置演示密钥（AES-256-GCM 密文，与前端 window.__AP_EMBED__ 同源；明文不落文件）
  var DEMO = { v: 1, p1: "EBg9FIJi/bCyafZWyXyf8a", p2: "EWFpxaUt6rq6VYcPm7kIE=", iv: "geJIbHcrK3OHQNtO",
    ct: "G+vZ14c3nSwsIM/3jAPxxw75VlbbQ4URIyq9cugKDb7ToHcC+gPjOn4iM7EXyVybZhyTpr6FMn4IwP54S7YtCggxHHwQhKgiGhKqmUbMt01+0a9v4OF4L0ZlDKb/GWo3yAG70tFA3Fk9a7D9hYwpUicQbsiJjlNq3YX9E5OUWFKTanzrnjHnnnOJnhU=" };

  function b64u8(s) { var b = atob(s), n = b.length, u = new Uint8Array(n); for (var i = 0; i < n; i++) u[i] = b.charCodeAt(i); return u; }
  function u8b64(u) { var s = ""; for (var i = 0; i < u.length; i++) s += String.fromCharCode(u[i]); return btoa(s); }

  var _dek = null, _dekP = null;
  function getDek() {
    if (_dek) return Promise.resolve(_dek);
    if (_dekP) return _dekP;
    _dekP = new Promise(function (res, rej) {
      if (!(window.crypto && crypto.subtle)) return rej(new Error("WebCrypto 不可用"));
      var req = indexedDB.open("astralpath", 1);
      req.onupgradeneeded = function () { req.result.createObjectStore("kv"); };
      req.onsuccess = function () {
        var db = req.result;
        var get = db.transaction("kv", "readonly").objectStore("kv").get(MIMO_IDB);
        get.onsuccess = function () {
          var stored = get.result;
          if (stored instanceof CryptoKey) { _dek = stored; db.close(); return res(stored); }
          crypto.subtle.generateKey({ name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]).then(function (k) {
            _dek = k;
            var put = db.transaction("kv", "readwrite").objectStore("kv").put(k, MIMO_IDB);
            put.onsuccess = function () { db.close(); res(k); };
            put.onerror = function () { db.close(); res(k); };
          }, rej);
        };
        get.onerror = function () { db.close(); rej(get.error); };
      };
      req.onerror = function () { rej(req.error); };
    });
    _dekP.then(function () { _dekP = null; }, function () { _dekP = null; });
    return _dekP;
  }

  var _cfg = null, _cfgLoaded = false, _bi = null, _biDone = false, _biP = null;
  function getBuiltin() {
    if (_biDone) return Promise.resolve(_bi);
    if (_biP) return _biP;
    if (!DEMO || !DEMO.ct || !(window.crypto && crypto.subtle)) { _biDone = true; return Promise.resolve(null); }
    _biP = crypto.subtle.importKey("raw", b64u8(DEMO.p1 + DEMO.p2), { name: "AES-GCM" }, false, ["decrypt"])
      .then(function (ck) { return crypto.subtle.decrypt({ name: "AES-GCM", iv: b64u8(DEMO.iv) }, ck, b64u8(DEMO.ct)); })
      .then(function (pt) {
        var o = JSON.parse(new TextDecoder().decode(pt));
        _bi = o && o.key ? { key: String(o.key), base: String(o.base || "https://api.xiaomimimo.com/v1").replace(/\/+$/, ""), model: o.model || "mimo-v2.6-pro" } : null;
      }).catch(function () { _bi = null; })
      .then(function () { _biDone = true; _biP = null; return _bi; });
    return _biP;
  }

  function getConfig() {
    if (_cfgLoaded) return Promise.resolve(_cfg);
    return getDek().catch(function () { return null; }).then(function (dek) {
      if (!dek) return null;
      var env = null;
      try { env = JSON.parse(localStorage.getItem(MIMO_LS) || "null"); } catch (e) {}
      if (!env || !env.ct || !env.iv) return null;
      return crypto.subtle.decrypt({ name: "AES-GCM", iv: b64u8(env.iv) }, dek, b64u8(env.ct)).then(function (pt) {
        _cfg = { key: new TextDecoder().decode(pt), base: (env.base || "https://api.xiaomimimo.com/v1").replace(/\/+$/, ""), model: env.model || "mimo-v2.6-pro" };
        _cfgLoaded = true; return _cfg;
      }).catch(function () { try { localStorage.removeItem(MIMO_LS); } catch (e2) {} return null; });
    }).then(function (cfg) {
      if (cfg) return cfg;
      return getBuiltin().then(function (bi) {
        if (bi) _cfg = { key: bi.key, base: bi.base, model: bi.model, builtin: true };
        else _cfg = null;
        _cfgLoaded = true; return _cfg;
      });
    });
  }

  function ready() { return !!(_cfg && _cfg.key); }
  function ensureCfg() { return getConfig(); }

  // ── 4. 并发闸 + 完整 SSE/非流式补全（源 complete L6318-6376，T15 修复点）──
  var _active = 0, _waiters = [];
  function _acquire() { if (_active < 2) { _active++; return Promise.resolve(); } return new Promise(function (res) { _waiters.push(res); }); }
  function _release() { var w = _waiters.shift(); if (w) w(); else _active--; }

  function complete(messages, opts) {
    if (!_cfg) return Promise.reject(new Error("MiMo 未配置"));
    opts = opts || {};
    var onDelta = opts.onDelta; // T15 修复：SSE 分支引用未定义 onDelta（原意图 opts.onDelta），流式必抛 ReferenceError
    return _acquire().then(function () {
      var ctrl = typeof AbortController !== "undefined" ? new AbortController() : null;
      var timer = ctrl ? setTimeout(function () { try { ctrl.abort(); } catch (e) {} }, opts.timeoutMs || 60000) : null;
      var body = { model: _cfg.model, messages: messages, max_tokens: opts.maxTokens || 1600, stream: true };
      if (opts.temperature != null) body.temperature = opts.temperature;
      return fetch(_cfg.base + "/chat/completions", {
        method: "POST",
        headers: { "Content-Type": "application/json", "Authorization": "Bearer " + _cfg.key },
        body: JSON.stringify(body),
        signal: ctrl ? ctrl.signal : undefined
      }).then(function (resp) {
        if (!resp.ok) return resp.text().catch(function () { return ""; }).then(function (errText) {
          throw new Error("HTTP " + resp.status + (errText ? " " + errText.slice(0, 140) : ""));
        });
        var out = "", model = _cfg.model, reason = "";
        var ct = (resp.headers && resp.headers.get) ? (resp.headers.get("content-type") || "") : "";
        function safeDelta(text, thinking) { if (onDelta) try { onDelta(text, { thinking: !!thinking }); } catch (e) {} }
        if (resp.body && typeof resp.body.getReader === "function" && ct.indexOf("event-stream") >= 0) {
          var reader = resp.body.getReader(), dec = new TextDecoder(), buf = "", sseDone = false;
          function consumeLine(line) {
            line = line.trim();
            if (!line || line.indexOf("data:") !== 0) return;
            var payload = line.slice(5).trim();
            if (payload === "[DONE]") { sseDone = true; return; }
            try {
              var j = JSON.parse(payload);
              if (j.model) model = j.model;
              var d = (j.choices && j.choices[0] && (j.choices[0].delta || j.choices[0].message)) || {};
              var rp = (typeof d.reasoning_content === "string") ? d.reasoning_content : "";
              if (rp) { reason += rp; safeDelta(reason, true); } // T14：思维链流式回调
              var piece = (typeof d.content === "string") ? d.content : "";
              if (piece) { out += piece; safeDelta(out, false); }
            } catch (e) {}
          }
          return (function pump() {
            if (sseDone) return Promise.resolve();
            return reader.read().then(function (rd) {
              if (rd.done) return;
              buf += dec.decode(rd.value, { stream: true });
              if (buf.indexOf("[DONE]") >= 0) sseDone = true; // P2-1 修复②：[DONE] 可能不带换行尾
              var nl;
              while ((nl = buf.indexOf("\n")) >= 0) { consumeLine(buf.slice(0, nl)); buf = buf.slice(nl + 1); if (sseDone) break; }
              return pump(); // P2-1 修复①：[DONE] 后显式终止
            });
          })().then(function () {
            if (!out.trim() && reason.trim().length >= 4) { out = reason.trim(); safeDelta(out, false); } // T13 推理兜底
            if (!out.trim()) throw new Error("模型返回空内容（流式与非流式均无文本）");
            return { text: out.trim(), model: model };
          });
        }
        return resp.json().then(function (data) {
          if (data.model) model = data.model;
          var msg = (data.choices && data.choices[0] && data.choices[0].message) || {};
          out = (typeof msg.content === "string") ? msg.content : "";
          if (!out.trim() && typeof msg.reasoning_content === "string" && msg.reasoning_content.trim().length >= 4)
            out = msg.reasoning_content; // 推理型模型兜底
          if (onDelta && out.trim()) safeDelta(out, false);
          if (!out.trim()) throw new Error("模型返回空内容（流式与非流式均无文本）");
          return { text: out.trim(), model: model };
        });
      }).then(function (r) {
        if (timer) clearTimeout(timer);
        _release();
        return r;
      }, function (e) {
        if (timer) clearTimeout(timer);
        _release();
        throw e;
      });
    });
  }

  window.__AP_MIMO__ = {
    ready: ready, ensureCfg: ensureCfg, complete: complete, parseJsonLoose: jsonLoose,
    isTransportError: isTransportError,
    clear: function () { _cfg = null; _cfgLoaded = false; try { localStorage.removeItem(MIMO_LS); } catch (e) {} }
  };

  // ── 5. AI 出题链路（源 aiGenQuestion L4401-4490 + loadQuiz/paintQuiz）──
  var QUIZ_LEVELS = ["概念理解", "因果分析", "对比辨析", "应用场景", "错误辨析"];
  function pickQuizLevel(book) {
    var n = (book.qs || []).filter(function (q) { return q.layer === "ai"; }).length;
    return QUIZ_LEVELS[n % QUIZ_LEVELS.length];
  }
  function normStem(s) {
    return String(s || "").replace(/[！-～]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0xFEE0); })
      .replace(/　/g, " ").replace(/\s+/g, " ").toLowerCase().trim();
  }
  function isDupQ(q, book) {
    var k = normStem(q.stem).slice(0, 60);
    return (book.qs || []).some(function (x) { return normStem(x.stem).slice(0, 60) === k; });
  }

  // 章节归属（基准 chapters: {id,title,body,order,level}）
  function chapterOfNode(book, node) {
    if (!node) return null;
    var chs = book.chapters || [];
    for (var i = 0; i < chs.length; i++) if (chs[i].id === node.id) return chs[i];
    if (node.chId) { for (var j = 0; j < chs.length; j++) if (chs[j].id === node.chId) return chs[j]; }
    if (node.order != null && chs[node.order]) return chs[node.order];
    for (var k = 0; k < chs.length; k++) if (chs[k].body && String(chs[k].body).indexOf(node.title) >= 0) return chs[k];
    return chs[0] || null;
  }

  function buildQuizContext(book, node) {
    var parts = [], budget = 3600;
    function take(s) { s = String(s || ""); var t = s.slice(0, Math.max(0, budget)); budget -= t.length; return t; }
    if (node) {
      var ch = chapterOfNode(book, node);
      parts.push("【知识点】" + node.title + (node.body ? ("：" + take(node.body)) : ""));
      if (ch && ch.body) parts.push("【所在章节全文】" + ch.title + "：" + take(ch.body));
      else parts.push("【教材摘录】" + take(book.sourceText));
      parts.push("【全书目录】" + take((book.chapters || []).map(function (c) { return c.title; }).join("；")));
    } else {
      var chs = (book.chapters || []).slice(0, 8);
      parts.push("【全书目录】" + take(chs.map(function (c) { return c.title; }).join("；")));
      chs.forEach(function (c) { if (c.body) parts.push("【" + c.title + "】" + take(String(c.body).slice(0, 320))); });
      parts.push("【教材摘录】" + take(book.sourceText));
    }
    return parts.filter(Boolean).join("\n");
  }

  function resolveQuizNode(b, kp) {
    var ns = b.nodes || [];
    if (kp) { for (var i = 0; i < ns.length; i++) if (ns[i].id === kp) return ns[i]; }
    var best = null, bs = Infinity;
    ns.forEach(function (n) { var s = scoreOf(n.id); if (s < bs) { bs = s; best = n; } });
    return best;
  }

  function aiGenQuestion(book, node, opts) {
    opts = opts || {};
    var M = window.__AP_MIMO__;
    if (!M || !M.ready()) return Promise.resolve(null);
    var lvl = opts.level || pickQuizLevel(book);
    var user = "教材《" + book.name + "》\n命题知识点：" + (node ? node.title : "全书综合") + "\n考查层级：" + lvl
      + "\n教材内容如下：\n" + buildQuizContext(book, node)
      + "\n命题提示：可在贴合知识点的范围内适当超越给定材料（补充背景、实例或跨章联系），但不得与教材已陈述的事实冲突。";
    var sys = "你是资深教材命题专家。以给定的教材内容为主要命题依据，同时允许运用你的学科知识适度拓展（补充背景、引入贴合知识点的实例或情境），使命题更有深度与区分度。硬性要求：1) 题目科学严谨、表述准确无歧义，正确答案唯一且无争议；2) 题干须指向明确的考查点或具体情境，禁止空泛的\"下列说法正确的是\"；3) 本题考查层级为「" + lvl + "」；4) 三个干扰项似是而非且确实错误；5) 若正确项或关键考点超出给定教材片段，须在 explain 开头注明\"【拓展】\"；6) 严格输出 JSON，不要任何多余文字：{\"stem\":\"题干\",\"options\":[\"选项1\",\"选项2\",\"选项3\",\"选项4\"],\"answer\":0,\"explain\":\"解析：正确项依据与主要干扰项错因，60字内\"}";
    function genOnce() {
      return M.complete([{ role: "system", content: sys }, { role: "user", content: user }],
        { maxTokens: 1500, temperature: 0.6, timeoutMs: 60000, onDelta: opts.onDelta });
    }
    function parseQuiz(r) {
      var j = M.parseJsonLoose(r && r.text);
      if (!j || !Array.isArray(j.options) || j.options.length !== 4 || !String(j.stem || "").trim()) return null;
      var ci = Number(j.answer);
      if (/^[A-Da-d]$/.test(String(j.answer).trim())) ci = String(j.answer).trim().toUpperCase().charCodeAt(0) - 65;
      if (!(ci >= 0 && ci <= 3)) ci = 0;
      var q = {
        id: "aiq_" + Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
        kp: node ? node.id : "",
        stem: String(j.stem).slice(0, 240),
        opts: j.options.map(function (o) { return String(o).slice(0, 140); }),
        ci: ci, layer: "ai", why: "AI 出题·" + lvl, _generated: true,
        _aiExplain: String(j.explain || "").slice(0, 240)
      };
      return (q.stem && q.opts[0]) ? q : null;
    }
    return (function loop(attempt) {
      return genOnce().then(function (r) {
        var q = parseQuiz(r);
        if (!q) return attempt < 2 ? loop(attempt + 1) : null;
        if (isDupQ(q, book)) {
          if (attempt < 2) return loop(attempt + 1);
          return q; // 重试后仍重复：交付（不入池）
        }
        book.qs = book.qs || [];
        book.qs.push(q);
        if (book.qs.length > 300) book.qs = book.qs.slice(0, 300); // T15：超限从头截断（保新题）
        return q;
      }).catch(function () { return attempt < 2 ? loop(attempt + 1) : null; });
    })(1);
  }

  // ── 6. loadQuiz / paintQuiz 增强（AI 默认出题 + 垫题替换 + T15 状态清理）──
  var _origLoadQuiz = window.loadQuiz;
  window.loadQuiz = function (kp) {
    var b = activeBook();
    var M = window.__AP_MIMO__;
    var useAi = M && M.ready && M.ready() && navigator.onLine !== false && !!(b.nodes && b.nodes.length);
    if (!useAi) { window._q = pickQuestion(kp); paintQuiz(window._q); return; }

    var aiPool = (b.qs || []).filter(function (x) { return x.layer === "ai" && (!kp || x.kp === kp); });
    var _curId = window._q && window._q.id;
    var _alts = aiPool.filter(function (x) { return x.id !== _curId; });
    if (_alts.length) {
      var aq = _alts[_alts.length - 1];
      delete aq._pendingAi;
      window._q = aq;
      paintQuiz(aq);
      return;
    }
    // 池空：先秒出规则垫题，后台生成 AI 题自动替换
    var tk = (window.__quizToken = (window.__quizToken || 0) + 1);
    var node = resolveQuizNode(b, kp);
    var fb = pickQuestion(kp);
    if (fb) {
      fb._pendingAi = tk;
      window._q = fb;
      paintQuiz(fb);
    } else {
      $("quizBox").innerHTML = '<div class="status">🤖 AI 正在基于《' + esc(String(b.name || "").slice(0, 20)) + '》全文内容命题…</div>';
    }
    aiGenQuestion(b, node).then(function (nq) {
      if (fb) delete fb._pendingAi; // T15：垫题状态标记用后即清
      if (nq) save(S);
      if (tk !== window.__quizToken) return;
      if (nq) {
        if (fb && window._q === fb && !fb._answered) {
          window._q = nq;
          paintQuiz(nq);
          var _kk = $("quizKp");
          if (_kk) _kk.insertAdjacentHTML("beforeend", ' <span class="tag" style="background:#2f6fed;margin-left:6px">AI 新题已就绪</span>');
        }
        return;
      }
      if (!fb) { var fb2 = pickQuestion(kp); window._q = fb2 || null; paintQuiz(fb2); }
    });
  };

  var _origPaintQuiz = window.paintQuiz;
  window.paintQuiz = function (q) {
    if (!q) { _origPaintQuiz(q); return; }
    _origPaintQuiz(q);
    // 徽标：AI 命题 / AI 解析
    var kpEl = $("quizKp");
    if (kpEl && q.layer === "ai") {
      kpEl.insertAdjacentHTML("beforeend", ' <span class="tag" style="background:#2f6fed;margin-left:6px">AI 命题</span>');
    }
    if (q._aiExplain) {
      var fbEl = $("fb");
      if (fbEl && !fbEl.querySelector(".ai-explain")) {
        fbEl.insertAdjacentHTML("beforeend", '<div class="hint ai-explain" style="margin-top:6px">AI 解析：' + esc(q._aiExplain) + '</div>');
      }
    }
    // 作答后清理 _pendingAi（T15）
    var btn = $("btnSub");
    if (btn) {
      var orig = btn.onclick;
      btn.onclick = function () {
        try { if (window._q === q) { q._answered = 1; delete q._pendingAi; } } catch (e) {}
        if (orig) orig.apply(this, arguments);
      };
    }
  };

  // ── 7. 手动 AI 出题按钮（源 aiQuiz L6592，简化适配）────────────────────
  function aiQuiz() {
    var M = window.__AP_MIMO__;
    if (!M || !M.ready()) {
      var box = $("quizBox");
      if (box) box.innerHTML = '<div class="status warn">未配置模型密钥：请到「智能体」页配置，或先使用本地题库练习。</div>';
      return;
    }
    var b = activeBook(), ns = activeNodes();
    if (!ns.length) return;
    var kp = (window._q && window._q.kp) || ns.slice().sort(function (a, c) { return scoreOf(a.id) - scoreOf(c.id); })[0].id;
    var node = ns.find(function (n) { return n.id === kp; }) || ns[0];
    var box = $("quizBox");
    if (box) box.innerHTML = '<div class="status">🤖 AI 正在基于《' + esc(String(b.name || "").slice(0, 20)) + '》全文内容命题…</div>';
    aiGenQuestion(b, node, {
      onDelta: function (acc, info) {
        try {
          if (info && info.thinking) box.innerHTML = '<div class="status">🤖 命题思考中：' + esc(String(acc).slice(-72)) + '…</div>';
          else box.innerHTML = '<div class="status">🤖 题目生成中：' + esc(String(acc).slice(0, 110)) + '…</div>';
        } catch (e) {}
      }
    }).then(function (aq) {
      if (!aq) { if (box) box.innerHTML = '<div class="status warn">AI 出题失败：本次未能生成新题，可再试一次或使用本地题库。</div>'; return; }
      save(S); window._q = aq; paintQuiz(aq);
    });
  }
  window.aiQuiz = aiQuiz;

  // ── 8. UI 接线：换一题按钮 + AI 出题按钮 ─────────────────────────────
  document.addEventListener("DOMContentLoaded", function () {
    var nextBtn = document.getElementById("btnNextQ") || document.querySelector("[data-act='nextq']");
    if (nextBtn) {
      nextBtn.addEventListener("click", function () {
        window._q = null;
        window.loadQuiz(window._q && window._q.kp || null);
      });
    }
    // 若存在 AI 出题按钮则接线；否则动态注入一个
    var aiBtn = document.getElementById("btnAiQuiz");
    if (!aiBtn) {
      var quizBox = document.getElementById("quizBox");
      if (quizBox && quizBox.parentNode) {
        aiBtn = document.createElement("button");
        aiBtn.id = "btnAiQuiz";
        aiBtn.className = "btn";
        aiBtn.textContent = "🤖 AI 出题";
        aiBtn.style.marginLeft = "8px";
        var row = quizBox.parentNode.querySelector(".row");
        (row || quizBox.parentNode).appendChild(aiBtn);
      }
    }
    if (aiBtn) aiBtn.addEventListener("click", aiQuiz);
  });

})();
