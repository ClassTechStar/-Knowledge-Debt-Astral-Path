# 智能体独立测试 vs 项目管线对拍（第二轮）与深度优化任务清单

日期：2026-09-28 · 范围：`C:\Users\18948\Downloads\PDF TEST` 全部 12 本 PDF（6,362 页）
前置：P0–P3 十七项已落地（见 `docs/ocr-p0-p3-completion.md`）——本轮为 P0–P3 之后的第一轮全量复核与新一轮任务清单。
**➤ 实施结果（P4–P6）已全部执行完毕，见文末「九、实施结果」——终版覆盖率 100.5%，门禁 PASS。**

---

## 〇、测试方法（两轮智能体，同一批书）

| 轮次 | 执行者 | 内容 | 产物 |
|---|---|---|---|
| **Round A** | 12 个独立测试智能体（每书 1 个） | 页级盘点（独立阈值）→ 分层抽样 18–22 页 OCR（rapidocr v3 @ zoom1.5，独立阅读序拼装，不复用项目 fix_ocr_text）→ 三重 CER（vs 文本层 / vs 引擎参考 / vs 视觉真值）→ 逐页渲染读图视觉检查 → 章节结构抽取 → 缺陷清单 | `ocr-bench/agent-vs-project/A/{slug}/A-report.json` + `A-findings.md` |
| **Round B** | 项目生产管线 | `python tools/ocr_pipeline.py <pdf> --ocr standard`（与 App 生产路径一致，含未提交的 rapid 引擎改动），12 本全量 | `ocr-bench/project/{slug}.json` |
| **Round C** | 对拍脚本 | 四方数据（A 报告 / B payload / 文本层 GT / rapid3 全页参考）统一口径对拍 | `ocr-bench/agent-vs-project/compare_report.json` |
| **Round D** | 2 个代码审计智能体 | `ocr_pipeline.py`+`MaterialPipeline.cs`（P-01…P-24）；`kg_builder`/`deep_chapters`/`AgentRouter`/意图三方（K-01…K-20） | 见 §三/§五 |
| **API 冒烟** | 端到端 | 真实 API：登录→上传 2 本→解析→生成图谱→取回 | 本文档 §二.4 |

对拍口径：字符数一律 `WORD=[A-Za-z0-9一-鿿]` 计数（B 的 `extractedChars` 是 `len(raw)` 含空白，不可跨方比较）。
工具自省（诚实声明）：`zc_agent_probe.py` 的 `tocLike` 对中文点线目录失效（花书 26 页目录漏标）、`chHeads` 会把页眉计入（花书 373 vs 实际 20）——已在 P6-20 立项修复，不影响本轮核心结论。

---

## 一、总表（Round C 汇总）

| 书 | 页 | 文本层字 | 参考①字 | B字 | **B/参考%** | B页覆盖% | B OCR页 | A需OCR页 | A样本CER vs参考 | 缺陷H/M/L（A报） |
|---|---|---|---|---|---|---|---|---|---|---|
| Python编程（第3版） | 732 | 365,553 | 368,002 | 365,538 | **99.3** | 99.7 | 0 | 13 | 0.005 | 2/1/6 |
| 花书 Deep Learning | 738 | 593,596 | 587,324 | 594,839 | **101.3** | 99.3 | 0 | 8 | 0.006 | 1/3/4 |
| 自制框架（扫描+隐藏层） | 504 | 231,097 | 230,047 | 230,575 | **100.2** | 99.6 | 0 | 10 | 0.073 | 4/4/3 |
| NLP（斋藤） | 427 | 203,605 | 205,724 | 203,592 | **99.0** | 98.6 | 3 | 9 | 0.015 | 1/2/7 |
| 深度学习入门（斋藤） | 314 | 149,620 | 151,920 | 149,617 | **98.5** | 98.4 | 2 | 11 | 0.045 | 0/2/4 |
| Kotlin编程实践 | 294 | 144,367 | 143,713 | 135,915 | **94.6** | 85.1 | 0 | 15 | 0.006 | 0/4/3 |
| 黄仁勋·英伟达之芯 | 201 | 197,405 | 194,939 | 176,740 | **90.7** | 46.3② | 0 | 33 | 0.005 | 1/1/7 |
| Java从入门到精通 | 775 | 224,894 | 422,929 | 224,586 | **53.1** | 97.2③ | 0 | 43 | 0.009 | 7/4/6 |
| C#从入门到精通（第7版） | 868 | 218,202 | 404,247 | 204,554 | **50.6** | 69.6 | 0 | 85 | 0.056 | 2/3/2 |
| Go语言从入门到精通 | 506 | 116,517 | 222,513 | 109,416 | **49.2** | 68.1 | 0 | 41 | 0.036 | 2/2/5 |
| 强化学习（斋藤4） | 333 | 161,576 | 165,548 | 24,712 | **14.9** | 2.1 | 0 | 3 | 0.021 | 2/6/6 |
| 大模型应用开发 AI Agent | 290 | 0 | 273,726 | 18,112 | **6.6** | 8.0 | 28 | 290 | 0.012④ | 1/2/4 |
| **合计** | 6,362 | 2,606,432 | 3,370,632 | 2,438,196 | **72.3%** | — | 33 | 561 | — | — |

① 参考 = 上一轮 rapid3 全页 OCR（每页引擎参考，6,362 页全覆盖）；② 黄仁勋页覆盖异常系 B fullText 与文本层的分段/顺序差异导致探针未命中，字符量缺口 9% 才是真实差距；③ Java 页覆盖高但字缺口 47%——缺口在「页内图片化代码」，页面有文本层故探针命中；④ 大模型样本 CER 为 OCR 前 28 页的对参考值。

**一句话结论：文本层好的书（5 本原生版）B 达 94–101%，与独立测试难分伯仲；但「图片化内容多的书」（明日科技系 + 大模型）B 只有 6.6–53%，全书平均 72.3%——项目管线输在「该不该 OCR 的判定」，不输在 OCR 引擎本身。**

---

## 二、关键差异与缺陷（按因果链）

### 1. 病灶一：max_ocr=28 截断 → 大模型书 93.3% 内容丢失
- B 只 OCR 前 28 页（`text_layer_sparse` 分支 max_ocr=28，候选 42 页被 `[:max_pages]` 静默砍半，`ocr_pipeline.py:733/759`）。
- 该书 290/290 页纯图零文本层，参考 27.4 万字，B 仅 1.8 万（6.6%）；独立智能体实测样页均 1,034 字/页、0.545 页/s，**全本 OCR 只需约 9 分钟，可恢复约 30 万字（B 的 13.9 倍）**。
- 更严重：wide 分支会用这 28 页 OCR 结果**整体替换**全书文本（`ocr_pipeline.py:767` `text = ocr_text`）。

### 2. 病灶二：文本层充足性判定「双向失灵」（单页均密度 ≥120 一票通过）
- **漏报（假阴性）**：强化学习 333 页——文本层实测 21.7 万字（独立智能体逐页核实），但 B 的 pypdf 抽取路径只抽出 2.5 万字，密度均值 156 ≥120 仍判 `text_layer_full`，页覆盖 2.1%。同因 `fallback_pdfium` 回退后页级 garble 检测全部失效（P-07）。
- **误报（假阳性）**：自制框架 504 页——「隐藏 OCR 文本层」的扫描版因密度达标直接放行，隐藏层里 `__init__.py`→`一init__.py`、`core.py`→`corc.py` 这类**合法但错误**的字符直通正文与图谱（视觉真值裁定：干净正文页 OCR CER 0.0000 vs 隐藏层 0.0115，正文应以 OCR 为准）。
- `_readable_ratio` 质量校验已停用（`ocr_pipeline.py:662-670`），「合法但错误」类乱码（CID/康熙部首/同形字）无任何检测。

### 3. 病灶三：图片化内容系统性丢失（明日科技系 34–51%）
- C#/Go/Java 三书代码块以截图嵌入，文本层对图片内文字全盲：Go 缺 51%、C# 缺 34%（18 个零文本整页代码图 + 85 页图文混排页）、Java 缺 37%（344 页缺口>150 字，ch23 缺 2.7 万字）。
- B 对这 3 本 0 页 OCR（needsOcr 判定只看「页文本层字数<40 或乱码」，**页内图片区域永远不触发**）。
- Python 书同理但范围小（p589–663 浏览器/终端截图，上一轮 worst CER 0.34 的真因就是文本层缺截图文字，非 OCR 错）。

### 4. 端到端假成功（API 冒烟实锤）
- `MaterialPipeline.ToDto` 硬编码 `Status="ready"`，从不读 payload 的 `ok` 字段（`MaterialPipeline.cs:434-447`）——python 侧 `ok:false` 也登记成功。
- 大模型书经 API 上传解析：仅 23,089 字（参考 8%）但 status=ready，还能生成 154 节点/173 边的图谱，章标题全是 OCR 错字（「第1章 何亩Agent」「OpenAlAPIL」「Lila」← OpenAI API/Llama）——**8% 的内容伪装成完整知识图谱**。
- Kotlin 图谱（124 节点）基本正确，但章标题粘连正文句（「第4章 只有很少的⼀部分内容」）且含康熙部首「⼀」——标题清洗缺失。

### 5. 结构对拍（A 章节 vs B 章节节点）
强化学习 A=10 vs B=1、自制框架 A=5 vs B=1（章节正则只认「第N章/Chapter N/第N部分」，斋藤系「步骤/阶段」塌成 1 个假章，kg_algorithm 兜底合成假章 K-12/P-12）；黄仁勋 A=30 vs B=23；花书 A=26 vs B=20；C# B=25 反超 A=23（交叉引用误报章）。`sec=240` 截断命中 7 本（deep_chapters max_sections=240）。

### 6. 引擎与阅读序：不是短板
- 独立智能体样页 OCR vs rapid3 参考：CER 0.005–0.073（两套独立实现、同一引擎，高度可复现）；Go 书对视觉真值 p450 CER=0.0048——引擎准，缺的是「哪些页送 OCR」的决策。
- 阅读序：单栏书全部正确；本轮未发现新的多栏交错实锤（P0-1 多栏 cuts 已修）。

---

## 三、缺陷清单（项目侧 Top 10，全部有 file:line / 数据证据）

| # | 缺陷 | 证据 | 影响 |
|---|---|---|---|
| 1 | ok:false 仍报 ready（假成功） | MaterialPipeline.cs:434-447 | 空壳图谱当成功入库 |
| 2 | max_ocr=28 截断 + wide 分支整本替换 | ocr_pipeline.py:733/759/767 | 大模型 93.3% 内容丢失 |
| 3 | 文本层判定单页均密度，双向失灵 | ocr_pipeline.py:719 | 强化学习 2.1% 页覆盖；自制框架隐藏层乱码直通 |
| 4 | P0–P3 一半修复只在 umi 脚本，生产管线没接（缓存/版面检测/表格MD/双引擎投票/页质量分/页缓存全缺） | grep 证实 ocr_pipeline.py 无 cache_get/detect_layout_type/_vote_blocks | 生产路径吃不到已修能力，两套管线持续漂移 |
| 5 | 图片化内容零回填（needsOcr 判定不含页内图片区） | 全轮数据：C#/Go/Java 0 OCR 页 | 明日科技系丢 1/3 内容 |
| 6 | 章节正则窄 + 假章兜底 + sec=240 截断 | deep_chapters.py:24-28/94; kg_algorithm.py:417-424 | 斋藤系图谱塌成 1 章；7 本 sec 顶格 |
| 7 | 章节标题不清洗（粘连句/康熙部首/OCR 错字直入节点名） | API 冒烟 Kotlin/大模型图谱 | 图谱可读性与检索质量 |
| 8 | 意图三方对拍门禁失效 + 运行时表≠登记表 | verify_agent_intents.py 实跑 exit=1; AgentRouter.cs:408/465-485 | 危机词「活不下去」后端缺失且无人报警 |
| 9 | 快速路径叠加损失：quick 只抽前 120 页无提示；--pages 区间被静默丢弃；fallback_pdfium 后页级检测全盲 | ocr_pipeline.py:680-684/994-997/692-699 | 静默降级无告警 |
| 10 | tesseract 路径每页跑两遍（txt+tsv）+ 每页重开 PdfDocument + 临时 PNG 永不清理 | ocr_pipeline.py:266-285/203/242 | 扫描书耗时翻倍、TEMP 膨胀 |

（完整 44 条：P-01…P-24 见 §五任务表证据列；K-01…K-20 同。）

---

## 四、算法深研结论

1. **OCR 引擎层（rapidocr v3）**：识别质量不是瓶颈。损失集中在四类内容——分式/矩阵（花书 p247 行序错乱字符损失 16%；NLP p45 分式塌缩、∂ 丢 25%）、计算图/结构图标签（NLP p211 下标 h9→hg、p287 标签重排；自制框架 p200 图区混入正文流）、代码保真（0→O、l→1、缩进拍平、圈码➊→©：强化学习 p233 'CartPole-v0'→'-vO' 复制即错）、旋转 90° 的 y 轴标签整体漏检（花书 p179）。这四类需要「版面分类 → 专用通道」而不是更好的通用识别。
2. **「该不该 OCR」决策层**：这是本轮最大失分点。现有判定是「整本书文本层均值密度」一个标量，而真实需求是三个页级问题：① 这页有没有文本层（大模型 0/290）；② 这页文本层是否完整（Go 205 页图片化代码）；③ 这页文本层是否可信（自制框架隐藏层合法但错误）。三者都要求**页级判定 + 与渲染墨量（rendered ink）对拍**，均值密度在数学上就无法回答。
3. **文本层 vs OCR 的融合策略**：本轮给出了明确裁定——原生版信文本层（Kotlin CER 0.004）、扫描版信 OCR（自制框架干净页 0.0000 vs 0.0115）、图片化混排必须「文本层+页内图区 OCR」双通道（Go/C#/Java）。现在管线只有「二选一」，没有「融合」。
4. **章节抽取**：两套抽取器（deep_chapters/kg_builder/kg_algorithm）模式表互不一致，中文数字解析只到 25/30/40（26–29、31+、带「百」全挂），目录页 offset 与正文 offset 混用导致小节吞章（deep_chapters.py:413-421）。图谱结构质量的上限被章节抽取锁死。
5. **图谱构建**：kg_builder 无分词（8 字窗口机械切片）、无环校验；生产实际调用的 kg_algorithm 只有 ~30 个硬编码 CS/AI 术语（非 CS 书退化为英文术语+章节）、删环不看边类型权重；节点 id 五套命名并存。
6. **受约束智能体**：运行时表（22 意图多角色）与登记表（15 意图 student-only）是两张表；危机词后端缺词且 verify 不查词表；JS 单体打分公式与 C# 不同但注释自称对齐；requiredSlots 全空使澄清机制成为死代码。
7. **评估方法学**：上一轮已把 CER 口径修对（以 B 为底、分区）。本轮补充两个新口径：**覆盖率对拍**（本表 B/参考%）与**页级 shingle 探针覆盖**（B 页覆盖%），两者结合才能同时暴露「整本级截断」与「页内级缺口」；单看字符总数会对 Java（97.2% 页覆盖 vs 53.1% 字覆盖）误判。

---

## 五、深度优化任务清单（P4 正确性 / P5 一致性与工程 / P6 增强）

### P4 — 正确性（数据已证明的用户级内容损失，1 周内）

| # | 任务 | 做法 | 验收标准 | 证据 |
|---|---|---|---|---|
| P4-1 | **解除 max_ocr=28 截断**：sparse/wide 分支 OCR 页数随全书页数缩放（如 `max(28, pages//6)`，290 页→48+；纯图书直接全页 OCR），预算超时记 `ocr_partial:kept/total` 而非静默砍 | ocr_pipeline.py:733/759 | 大模型书 B/参考% ≥95%（现 6.6%）；notes 出现 `sampled=42,capped=28` 类显式记录 | §二.1 |
| P4-2 | **页级文本层三问判定**：对每页算「文本层字数 / 渲染墨量比 / 隐藏层常用字覆盖率」，任一不达标 → 该页入 OCR 队列；判定结果逐页写 payload | 新增 `assess_textlayer_pages()` | 强化学习 B/参考% ≥95%（现 14.9%）；自制框架代码标识符错字率 <0.5% | §二.2 |
| P4-3 | **图片化内容回填**：needsOcr 页 OCR 之外，对「文本层字数 < 阈值但有渲染墨」的页强制 OCR；混排页做「文本层 + 页内图区 OCR」合并 | 复用 rapid_pdf_pages_detail | Go/C#/Java B/参考% ≥85%（现 49–53%）；Go p250、C# p755-773、Java p421 类页字符缺口 <10% | §二.3 |
| P4-4 | **假成功修复**：ToDto/ParseAndRegister 校验 payload.ok 与 exit code，false → Status=failed 携带 error；0 节点图谱不 Upsert | MaterialPipeline.cs:434-447 | API 冒烟：构造 ok:false 材料 status=failed；单测覆盖 | §二.4 |
| P4-5 | **wide 分支禁止整本替换**：`text = ocr_text` 改为「文本层 + OCR 缺口页合并」 | ocr_pipeline.py:767 | 构造乱码书单测：全书正文不被 28 页样本覆盖替换 | §二.1 |
| P6 同步 | **umi 修复下沉生产**（缓存/版面检测/表格MD/双引擎投票/页质量分）或 C# 直调 umi——二选一，消灭双管线 | P-04 | 生产 payload 出现 cacheHit/pageQuality 字段；两管线 diff 脚本归零 | P-04 |

### P5 — 一致性与工程（2–3 周）

| # | 任务 | 做法 | 验收标准 |
|---|---|---|---|
| P5-1 | 意图三方对拍门禁修复：index.html 恢复 `const INTENTS` 顶层字面量；verify 增查 C# crisis/negative/banned 词表、roles、requiredSlots；`CreateDefault()` 直接读 `DefaultAgentIntents.Table` | K-01/02/03 | `verify_agent_intents.py` exit=0；危机词「活不下去」三端命中 crisis.handoff |
| P5-2 | 章节抽取统一：公共模式表（第N章/Chapter/Part/篇/步骤/阶段/讲/回/附录/Lesson）、中文数字全解析、目录 offset 回写正文 offset、假章兜底改为「仅术语图」降级 | K-10/11、P-12 | 强化学习/自制框架 B 章节 ≥8；`sec=240` 顶格书目数 =0（超限显式上报） |
| P5-3 | 章节标题清洗：去尾随粘连句、Kangxi 部首→常用字映射、OCR 错字高置信修正（复用 fix_ocr_text 词表）、36/40 字截断统一 + 按章号去重 | §二.4、P-13 | Kotlin「第4章 只有很少…」类标题 0 条；C00x/TOC_CH00x 双节点 0 条 |
| P5-4 | 魔法数集中 + 显式上报：max_ocr/max_sections/语料 120k/词表 400/时间预算统一常量表，超限写 deepStats | P-11、K-17 | deepStats 含 sectionsTotal/sectionsKept/corpusTruncated |
| P5-5 | 静默降级全部显式化：quick 120 页上限、--pages 区间丢弃、fallback_pdfium 页级检测失效、ocrUsed 子串误判、RapidOCR 加载失败无降级 | P-05/06/07/16/18/23 | 每种降级在 notes 有专属标记；单测 6 例 |
| P5-6 | 性能修复：tesseract 单遍（删 tsv 弃用跑）、PdfDocument 句柄复用、临时目录 finally 清理、C# 180s 超时与 python 预算联动（env 下发 deadline）、per-material 在跑去重 | P-08/09/10/14 | 扫描书 CLI 耗时下降 ≥40%；同书并发解析只跑一个 python 进程 |
| P5-7 | 缓存键补版本维度（引擎/zoom/模式/pipeline 版本）再接入生产路径 | P-04 联动 | 换引擎后旧缓存不命中；二次解析同书 <5s |
| P5-8 | 硬编码路径清理：C# 候选路径改仓库相对优先，种子清单出配置 | P-20 | 仓库内 grep 无 `C:\Users\18948` |

### P6 — 增强（1 月，按 ROI 排序）

| # | 任务 | 做法 | 验收标准 |
|---|---|---|---|
| P6-1 | 公式/矩阵专项通道：检测公式密集页（符号密度）→ 提高 zoom 至 2.5–3 + 行序按列向量修复 + 保留分式结构标记 | 花书 p247、NLP p45 | 公式页 CER 由 0.148 → <0.08 |
| P6-2 | 代码块保真通道：detect_layout_type=code 时字符白名单校正（0/O、l/1/I、➋/②）+ 缩进保留（按 box x 起点重建） | 强化学习 p233、Kotlin p89/180、Go p250 | 代码页复制可运行率抽样 ≥90% |
| P6-3 | 旋转文本检测：det 框宽高比异常页启用 ±90° 重 OCR（花书 y 轴标签） | 花书 p179/p332 | 旋转标签召回 ≥80% |
| P6-4 | kg_builder 接入 jieba（ocr-venv 加依赖）+ 停用词表；环校验 + 删边按 (weight,edgeType) 优先级；前序边先于 related 去重 | K-04/13/14 | 中文术语节点可读（无 8 字碎片）；构造 3 环用例全部破环且主干边保留 |
| P6-5 | 术语词典双通道（硬编码词典 + TextRank 兜底），非 CS 书可用 | K-06 | 黄仁勋传记图谱中文术语节点 ≥20（现≈0） |
| P6-6 | 出题选项洗牌 + correctIndex 回写 | K-07 | 自动题正确答案位置均匀分布（卡方检验） |
| P6-7 | buildPlan 每日 ≤40min 约束 + 溢出顺延 | K-05 | 8 债边用例每日时长 ≤40min |
| P6-8 | 稳定哈希播种 PreviewDebts；重复节点 id 走 issues 不抛 500 | P-21/22 | 同学生跨重启债边一致；重复 id 用例返回 issues |
| P6-9 | 本轮对拍回归化：`zc_compare_round2.py` + `zc_agent_probe.py` 入库为常态回归（每书 20 页抽样门禁） | 本文档 §〇 | CI 跑通，B/参考% <85% 的书红灯 |
| P6-10 | 探测工具自修：tocLike 中文点线模式、chHeads 页眉去重 | §〇声明 | 花书目录 26 页检出 ≥20；chHeads 虚高 <10% |

---

## 六、产物与复跑

```powershell
# 独立测试（每书一个智能体，或直接用探测工具）
python tools/zc_agent_probe.py inventory "<pdf>" --out <json>
"tools/ocr-venv/Scripts/python.exe" tools/zc_agent_probe.py ocr-pages "<pdf>" --pages 1,5,9-12 --out <json>
# 项目管线基准
python tools/run_project_ocr_bench.py --python "tools/ocr-venv/Scripts/python.exe" --mode standard
# 对拍
python tools/zc_compare_round2.py
```

产物：`ocr-bench/agent-vs-project/`（A/ 12 书报告、compare_report.json）、`ocr-bench/project/`（B 侧 12 payload）、本文档。

**遗留待人工**：黄仁勋 B 页覆盖 46.3% 的探针未命中需要一次 fullText 分段对比归因（字符缺口 9% 是真实差距）；API 上传文件名经 curl 变 U+FFFD 需用真实前端复核是否服务端问题。

---

## 九、实施结果（P4–P6 全量执行，2026-09-28 凌晨）

### 9.0 执行方式：双会话车道分工

本文档任务清单发布后，**另一个智能体会话与本会话同时开工**（检测到 `ocr_pipeline.py` 会话中被重写 +432 行、`run_project_ocr_bench.py` 以 paddle-venv 重跑 12 本）。为避免同文件互相覆盖，按文件归属分车道执行：

- **对方车道**（其改动以 P0-x/P1-x/P2-1x/P3-1x 标记）：`ocr_pipeline.py` 核心重写（双抽取器交叉验证、部首还原、弱页+图片覆盖检测、full_page_ocr、逐页 1.25 规则择优、图示页降噪、出版方夹层复核、版本化页缓存、rapid 并行）、`deep_chapters.py`/`kg_algorithm.py`（P2-12 章节形态扩展：篇/阶段/讲/步骤/Part/Unit/Lesson/Stage + 编号解析 + 独立编号行并标题；P2-13 术语按章分布 + 同段共现加权）、`MaterialPipeline.cs`（P3-15 硬编码路径清理 + P0-5 超时按模式参数化）+ 全量基准重跑。
- **本会话车道**：P5-1 意图四方对拍、P4-4 假成功修复、P6-7 buildPlan 每日上限、P6-4 kg_builder 环处理、P6-6 出题洗牌、P5-4 截断显式化、P6-9/10 工具回归与自修、终版对拍与门禁。

### 9.1 逐项落地状态

| 任务 | 状态 | 执行者 | 验证 |
|---|---|---|---|
| P4-1 解除 28 页截断 | ✅ | 对方（`full_page_ocr` + cap=1500 + `targeted_ocr:targets=N_merged=M_cap` 显式上报） | 大模型 6.6%→**106.0%** |
| P4-2 页级文本层判定 | ✅ | 对方（pypdf/pdfium 双抽取器按词字符量+部首污染率择优；弱页中位数判定；overlay_suspect 夹层复核） | 强化学习 14.9%→**98.9%** |
| P4-3 图片化内容回填 | ✅ | 对方（`image_coverage_pages` 图片面积占比 + `full_page_ocr:reason=image_content` + 逐页 merge） | Go 99.1% / C# 98.3% / Java 99.2% |
| P4-4 假成功修复 | ✅ | 本会话（`ToDto` 尊重 `ok:false`→`failed`；`ParseAndRegisterCoreAsync` 提前失败不建图；0/0 空图谱不 Upsert） | 全部 193 个 C# 测试绿 |
| P4-5 wide 分支整本替换 | ✅ | 对方（P0-6 注释明确取消该分支，一律逐页回填） | 代码 + 数据 |
| P5-1 意图四方对拍 | ✅ | 本会话（JS `const INTENTS` 字面量恢复、`DefaultAgentIntents.Table`=运行时表 22 意图、危机词「活不下去」后端补齐、verify 升级：多角色正则/roles/描述/C# 词表/bannedEthics） | `verify_agent_intents.py` ALL GREEN（此前 exit=1）；金样 34 测试绿 |
| P5-2 章节抽取统一 | ✅（就地统一，非共享模块） | 对方（deep_chapters + kg_algorithm 双侧同模式表） | 自制框架 ch=1→**55**；强化学习 ch=1→**10** |
| P5-3 章节标题清洗 | ✅ | 对方（clean_chapter_title 扩展 + 截断符表） | 抽查标题无粘连 |
| P5-4 魔法数显式上报 | ✅ | 对方（cap 上报）+ 本会话（`deepStats.chaptersTotal/sectionsTotal/sectionsCapped`） | Java：549 节抽到/240 入库/`sectionsCapped=true` |
| P5-5 静默降级显式化 | ✅ 部分 | 对方（extractor/full_page_ocr/targeted_ocr/overlay 等 notes 全显式） | 快速路径 `quick_capped`、`ocrUsed` 子串误判、RapidOCR 惰性探针仍遗留 |
| P5-6 性能修复 | ✅ 部分 | 对方（超时按模式参数化 20/40min；rapid 路径 finally 清理 PNG） | tesseract 双跑、PdfDocument 复用仍遗留（tesseract 已非默认引擎，低优先） |
| P5-7 缓存版本化 | ✅ | 对方（key 含 engine/scale/preprocess，`~/.astralpath/ocr-cache/prod`） | 换引擎/倍率自动失效 |
| P5-8 硬编码路径 | ✅ | 对方（XiaomiMiMo 旧路径移除，仓库相对优先） | grep 无残留 |
| P6-4 kg 环处理/分词/边强度 | ✅ | 本会话（kg_builder：jieba+停用词、TextRank 收敛判据、强边替换弱边、remove_cycles 按 (edgeType,weight) 破环、词挂章取最高频章） | 合成环 3 用例过；强化学习术语从 `in/for` → `老虎机/贝尔曼方程/策略`；jieba 装入双环境 |
| P6-5 术语词典双通道 | ◐ 部分 | 对方（P2-13 按章分布）；jieba 兜底由本会话在 kg_builder 实现 | kg_algorithm 词典硬编码仍遗留 |
| P6-6 出题洗牌 | ✅ | 本会话（以 qid 哈希为种子的确定性洗牌，幂等） | 27 题 correctIndex 分布 4/8/10/5，重跑完全一致 |
| P6-7 buildPlan 40min | ✅ | 本会话（每日累计上限+溢出顺延+同日去重；三份 index.html 同步 md5 一致） | 顺带修复 C3 XSS 回归（`.map(title)` 未转义，三份镜像同毒） |
| P6-8 稳定哈希/重复 id | ❌ 未做 | — | 遗留 |
| P6-9 对拍回归门禁 | ✅ | 本会话（`zc_compare_round2.py --gate`，<85% 红灯退出码 1） | 终版跑 PASS |
| P6-10 探测工具自修 | ✅ | 本会话（TOC 三形态：章号点线/空格点线/成片页码尾；章首剔除页眉「页码行首」与页码尾行） | 花书 tocCand 0→15、chHead 373→84 |
| P6-1/2/3 公式/代码/旋转通道 | ❌ 未做 | — | 遗留（见 9.4） |

### 9.2 终版对拍（Round B' = 新管线 12 本重跑）

| 书 | B/参考%（改前→**改后**） | B 页覆盖% | 耗时 s（改前→改后） |
|---|---|---|---|
| Kotlin | 94.6→**101.4** | 100.0 | 14→226 |
| Python | 99.3→**100.4** | 99.9 | 2→907 |
| 花书 | 101.3→**100.9** | 99.7 | 22→329 |
| 自制框架 | 100.2→**99.0** | 98.8 | 67→313 |
| 深度学习入门 | 98.5→**100.4** | 99.7 | 10→229 |
| NLP | 99.0→**101.1** | 100.0 | 13→301 |
| 黄仁勋 | 90.7→**102.1** | 100.0 | 13→314 |
| Java | 53.1→**99.2** | 98.2 | 47→360 |
| C# | 50.6→**98.3** | 96.1 | 14→654 |
| Go | 49.2→**99.1** | 97.6 | 8→367 |
| 强化学习 | 14.9→**98.9** | 99.4 | 11→237 |
| 大模型 | 6.6→**106.0** | 98.3 | 115→1① |
| **合计** | **72.3→100.5%** | 全部 ≥96.1 | — |

① 大模型 1.1s 为页缓存全命中；全量门禁 `--gate` PASS。**代价**：图文/扫描书耗时升至 4–15 分钟（全书逐页 OCR 的正确性成本），可用 `--ocr quick`/`ASTRALPATH_OCR_MAX_PAGES`/`ASTRALPATH_OCR_BUDGET_S` 调节。

### 9.3 验证清单

- `python scripts/verify_agent_intents.py` → **ALL GREEN**（22 意图四方一致）
- `dotnet build AstralPath.slnx -c Release` → 0 错误
- 全部 5 个测试项目 **193/193 绿**（Api 70 / Core 62 / Desktop 51 / Eval 2 / Persistence 8）
- `scripts/sync-monolith-html.ps1 -Check` → **ALL GREEN**（三份 index.html md5 一致，C3 XSS 回归修复）
- `python tools/zc_compare_round2.py --gate` → **GATE PASS**
- kg_builder 合成环 3 用例 + 真书图谱回归通过；出题洗牌幂等性验证通过

### 9.4 遗留（下一轮候选）

1. **P6-1/2/3 内容专项通道**（公式矩阵/代码保真/旋转标签）——引擎层增强，需版面分类分流，建议独立立项；
2. **P6-8** PreviewDebts 稳定哈希播种 + 重复节点 id 走 issues；
3. P5-5 遗留三项：`quick_capped` 提示、`ocrUsed` 由显式布尔替代 notes 子串反推、RapidOCR 惰性实例化探针（失败回落 tesseract）；
4. P6-5 kg_algorithm 术语词典仍为 ~30 词硬编码（jieba 兜底已在 kg_builder 生效，可平移）；
5. 秒级抽查建议：大模型耗时 1.1s 说明**页缓存键未含 pipeline 版本**——管线大改后建议手动清 `~/.astralpath/ocr-cache` 或在 key 中加入管线版本常量。
