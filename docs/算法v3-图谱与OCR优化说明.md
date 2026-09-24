# 算法 v3 优化说明 · 知识图谱与 OCR

> **日期**：2026-09-25　**范围**：`src/AstralPath.Core/Graph/*`、`src/AstralPath.Core/Ocr/*`
> **原则**：先定位具体缺陷（给出 v2 的问题代码），再改，最后用测试锁住。不重写风格，只修真问题。
> **验证**：`powershell -File scripts\verify_all.ps1` → `ALL GREEN`（222 项：28+8+47+53+2+**84**）
> **同步**：`src/AstralPath.Mobile.Offline/AstralPath.Core/` 下的副本已同步

---

## 一、总览：改了什么

| # | 模块 | v2 的毛病 | v3 的做法 | 验证测试 |
|---:|---|---|---|---|
| G1 | TextRank | 稠密 `double[V,V]` 矩阵，大书必然 OOM | 稀疏邻接 + 词表上限 4000 + 输出归一化 | G31 / G32 |
| G2 | 术语锚定 | `FirstOrDefault` 恒定取同一章 → 图谱退化成星形 | 按术语首次出现位置落在所属章节区间 | G34 |
| G3 | 依赖句式 | 每个概念现场 `new Regex` | 静态编译一次，句式扩到 3 类 | KG11 / G34 |
| G4 | PMI | 裸 PMI，低频词对虚高 | 词频门槛 + 加一平滑 + NPMI（∈[-1,1]） | G33 |
| G5 | 概念去重 | 无 → 「网络 / 神经网络」各建一节点 | 子词去重（高分候选包含则丢弃） | KG11 |
| G6 | DAG | 无收尾；`TopologicalOrder` 遇环直接抛异常 | DFS 回边检测 + 删最弱边 | G35 |
| G7 | 布局 | 单趟下扫、`ToLookup` 写在层循环里、无择优 | 下扫/上扫交替 + 交叉数择优 + O(E log E) 计交叉 | G36 |
| G8 | 学习路径 | PageRank 未归一化（≈0.004，等于没加）；layer 重复计权 | 下游阻塞质量为主 + PageRank 归一化 | G37 |
| O1 | 混淆修复 | `0→O/1→l/5→S/8→B` 无差别替换，腐蚀标识符 | 标识符/版本号/代码识别后跳过 | O31 / O32 |
| O2 | 页眉页脚 | 完全没有 | 跨页重复行检测（≥30% 页出现即删） | O33 / O34 |
| O3 | 英文断字 | 无 | `inter-\nnational` → `international` | O35 |
| O4 | 断行合并 | 无保护，列表项/标题被并进上一段 | 列表/编号/疑似标题三道保护 | O36 / O37 |
| O5 | TSV 行聚类 | 拿行首词 Y 当基准，基线漂移就串行 | 按 Y 排序后的间隙聚类 | O38 |
| O6 | 多配置投票 | 整行精确匹配 → 三路 PSM 微差全投不中；`Contains` 还是 O(n²) | 归一化键模糊投票 + HashSet | O39 / O40 |
| O7 | 阅读顺序 | 行带写死 6px；只用中心点分栏；阈值拍脑袋 | 行带按字高自适应 + 覆盖度投影 + 跨栏宽框单独处理 | OC06 |

---

## 二、知识图谱：逐个说清

### G1 · TextRank 稠密矩阵（最严重）

**v2（GraphInference.cs L22）**
```csharp
var w = new double[vocab.Count, vocab.Count];
```

中文教材按 `[一-鿿]{2,8}` 切词，一本 78 万字（花书实测量级）的词表约 3 万：

```
3 万² = 9×10⁸ 个 double × 8 B = 7.2 GB
```

单是这一个矩阵就 7.2 GB，**根本跑不起来**。即使词表小到 1500，也要 18 MB 且是 O(V²) 的无效开销（绝大多数格子是 0）。

**v3**：邻接表 `List<(int,double)>[]`，只存真实共现；词表上限 `maxVocab=4000`（按词频截断）；输出归一化（便于跨书比较重要性）。
**语义不变**：同样的 `1/(j-i)` 权重、阻尼 0.85、迭代 30 次 —— 测试 G32 验证了「甲乙对称、甲≥丙」与 v2 一致。

### G2 · 术语→章节锚定（最蠢的一个）

**v2（GraphInference.cs L156-161）**
```csharp
foreach (var n in kg.Nodes.Where(n => n.Kind == "term").ToList())
{
    var ch = chapters.FirstOrDefault(c => text.IndexOf(c.Title, StringComparison.Ordinal) >= 0);
    if (ch is not null) kg.AddEdge(ch.Id, n.Id, "prerequisite", 0.5, 0.5);
}
```

`FirstOrDefault` 里的谓词**与术语无关**——`text.IndexOf(c.Title) >= 0` 只取决于章节标题在不在正文里。
结果：**所有术语都被挂到同一个章节**，图谱退化成一颗星，层级信息全丢。

**v3**：按「术语首次出现位置」落在哪个章节区间就挂哪一章（取起点不晚于它的最后一个章节）。
测试 G34 断言锚定章节数 ≥ 2（v2 恒为 1）。

### G3 · 依赖句式正则

**v2**：在 `foreach (var c in list)` 里 `new Regex($"...")` —— 每个概念编译一次正则，24 个概念就是 24 次编译。
**v3**：3 条静态 `Compiled` 正则：
- ① `基于/先学/掌握 X 后/之后/再/然后/才能 Y`
- ② `X 是 Y 的基础/前提/先修`
- ③ `Y 依赖/需要/要求 X`

概念匹配也从「第一个包含匹配」改成「最长包含匹配」，避免短词误配。

### G4 · PMI 低频偏置

裸 PMI 的已知问题：共现 2 次的两个罕见词，PMI 可能比共现 200 次的常见词还高。
**v3** 三层处理：
1. `minTermFreq`（默认 2）：低频词不进候选
2. `PmiSmoothed`：加一平滑
3. `NormalizedPmi`（NPMI ∈ [-1,1]）：作为边权，`w = 0.2 + 1.8 × clamp(npmi,0,1)`

> 保留了 `Pmi(co,a,b,total)` 原签名与口径 —— 金样 KG10 依赖它，不能动。

### G6 · DAG 收尾

`GraphTopology.TopologicalOrder` 遇环是 **throw**，不是返回空：
```csharp
if (order.Count != nodes.Count) throw new InvalidOperationException("graph contains cycle: ...");
```
v2 的 `BuildFromText` 不做任何去环，一旦构图产生环，下游 `Layers`/`CriticalPath`/`Layout` 全部崩。

**v3** `EnsureDag`：DFS 找回边 → 删置信度最低那条 → 重复直到无回边。
> 注意：不能用 `TopologicalOrder` 探测环（会抛异常），必须用回边检测。这一点我自己第一版就踩了。

### G7 · 分层布局

**v2 三处**：`ToLookup` 建在层循环里（O(层×边)）、只做单趟下扫、没有择优。
**v3**：邻接表只建一次；下扫/上扫交替最多 8 轮；每轮用**归并排序数逆序对**（O(E log E)）算真实交叉数，保留交叉最少的一套顺序；交叉为 0 提前收敛。

测试 G36：`A→F, B→E, C→D`（明显反序）结果 **0 交叉**。

### G8 · 学习路径排序

**v2**：`prio = 2.5×block + 1.5×crit + 0.5×layer + rank`
- `rank`（PageRank）全图求和为 1，250 节点时单节点 ≈ 0.004 —— **加进去等于没加**
- 排序又是 `OrderBy(Layer).ThenByDescending(Priority)`，layer 既当主键又打分，**重复计权**
- 瓶颈用「模拟删除该点看多少点不可达」，O(V·(V+E))，对链式图区分度差

**v3**：主看下流阻塞质量（`DownstreamMass` 传递闭包规模，归一化）；PageRank 按最大值归一化到 [0,1]；去掉优先级里的 layer 项（排序主键已保证）。

---

## 三、OCR：逐个说清

### O1 · 混淆修复会腐蚀标识符（最蠢的一个）

**v2（OcrTextEngine.cs L50-62）**
```csharp
return w.Replace('0', 'O').Replace('1', 'l').Replace('5', 'S').Replace('8', 'B');
```
只要一个词「同时含字母和数字」就无差别替换（唯一例外是 `^[A-Z]\d+$`）。实际后果：

| 原文 | v2 输出 | 说明 |
|---|---|---|
| `Win10` | `WinlO` | 版本号被毁 |
| `ISO9001` | `ISO9OOl` | 标准号被毁 |
| `H2O` | `H2O`→`H2O` | 化学式语义变了 |
| `my_var1` | `my_varl` | 变量名被毁 |

**v3**：先判 `IsIdentifierLike`（含 `_`、`^[A-Za-z]{1,4}\d{1,3}$`、连续数字 `d{2,}`、camelCase、大写字母+数字），命中就**跳过替换**；同时要求词长 ≥4 才动。
测试 O31 用 `[Theory]` 覆盖 6 个真实标识符；O32 确认普通词的真实混淆仍会修。

### O2 · 页眉页脚

教材 OCR 最大噪声源是跨页重复的页眉页脚，v2 **完全没有处理**。
**v3** `RemoveRepeatedBoilerplate`：按分页符 `\f` 切页（无分页符时按 40 行/页估算），统计每行出现页数 ≥30% 且 ≥3 页 → 判为页眉页脚删除。纯数字页码跳过（避免误删公式/题号）。

> 实现坑：分页符不是换行符。若把整篇按 `\n` 切开再过滤，跨页粘连的行（"正文。\f页眉"）会漏掉页眉 —— 必须**逐页过滤再拼接**。这一点第一版也踩了。

### O5 · TSV 行聚类

**v2（L169）**
```csharp
var row = rows.FirstOrDefault(r => Math.Abs(r[0].Y - w.Y) <= 8);
```
拿**本行第一个词**的 Y 当基准，且线性扫描 O(n²)。扫描件常有基线漂移（每行 Y 缓慢增大），漂移一累积，整页会被串成一行。

**v3**：按 Y 排序后做**间隙聚类**（相邻词 Y 差 > 阈值就换行），天然抗漂移。测试 O38 构造每行 +1px 的漂移，断言仍是 2 行。

### O6 · 多配置投票

**v2** 用整行**精确**匹配统计票数。三路 PSM 出来的同一行常有空白/全角差异 → 全都投不中，投票形同虚设。另外 `order.Contains(kv.Key)` 在循环里是 O(n²)，`lists[0][0] is null` 是无意义判断。

**v3**：`VoteKey` 归一化（去空白 + 全角转半角 + 小写）后分组投票，组内选出现最多的原文形态；`HashSet` 去重；单路直通。

### O7 · 阅读顺序

- 行带：v2 写死 `Math.Round(Y/6.0)`；v3 用**中位框高 × 0.6**（clamp 3–40）
- 分栏：v2 只用框**中心点**做 48 桶直方图，跨栏宽框（图注/整幅图）会按中心被切进某一栏；v3 用**覆盖度投影**找最宽连续空档，并先把 `W > 55% 版宽` 的框摘出来单独按 Y 插回
- 阈值：v2 的 `bestVal > 2` 是拍脑袋；v3 用「空档宽度 ≥3% 版宽 + 空档内零覆盖」双条件

---

## 四、复杂度对比

| 操作 | v2 | v3 |
|---|---|---|
| TextRank 空间 | O(V²) — 3 万词表需 **7.2 GB** | O(V + E)，词表上限 4000 |
| TextRank 时间 | O(V² · iter) | O(E · iter) |
| 依赖句式 | O(概念数 × 正则编译) | 3 条编译一次 |
| 布局邻接 | O(层数 × 边数)（每层重建） | O(边数)（建一次） |
| 交叉数统计 | 无 | O(E log E)（归并数逆序对） |
| TSV 行聚类 | O(n²) | O(n log n) |
| 投票去重 | O(n²) | O(n) |
| 阅读顺序分栏 | 固定 48 桶 + 中心点 | 覆盖度投影（桶数随版宽自适应） |

---

## 五、测试

新增 `src/AstralPath.Mobile.Offline/AstralPath.Mobile.Tests/GraphOcrV3Tests.cs`（22 项，编号 G31–G37 / O31–O40），
每条都对应上面表格里的一个具体缺陷，**不是"跑通就行"的冒烟测试**：

- `G34` 断言锚定章节数 ≥ 2 —— v2 恒为 1，这条能直接钉死那个 bug
- `G35` 断言去环后能完整拓扑排序
- `O31` 用 `[Theory]` 覆盖 6 个会被 v2 腐蚀的真实标识符
- `O38` 构造基线漂移，断言不被串成一行
- `O39` 构造只有空白/全角差异的三路结果，断言仍能投中

原有 62 项（KG01–KG12 / OC01–OC08 等）**全部保持通过** —— 改动没有破坏既有契约。

---

## 六、没动的部分（有意保留）

- **`Pmi(co,a,b,total)` 签名与口径**：金样 KG10 依赖，未改
- **`Score()` 的权重与评级阈值**：金样 OC07 依赖，未改
- **`eval/golden/*.json`**：v1 口径契约，未动
- **`tools/*.py`（Python 工具链）**：核查后确认 `kg_algorithm.py` 的术语锚定**本来就是正确的就近锚定**，「所有术语挂同一章」这个 bug 只存在于 C# 的 `GraphInference`，因此 Python 侧无需同步修改

---

## 七、怎么验

```powershell
# 图谱 + OCR 算法回归（84 项）
dotnet test src/AstralPath.Mobile.Offline/AstralPath.Mobile.Tests -c Release

# 全量门禁（222 项）
powershell -File scripts\verify_all.ps1
```
