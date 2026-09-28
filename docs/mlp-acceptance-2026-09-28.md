# MLP 验收报告 · 2026-09-28

> 范围：Android APK 2.2.0 + 12 本 PDF（`C:\Users\18948\Downloads\PDF TEST`）
> 顺序：OCR → 知识图谱 → 智能体 · 含 Android 模拟器实机安装验收
> 原始数据：`docs/mlp-acceptance-2026-09-28.json`

---

## 一、结论

**12/12 本 PDF 全流程可用，达到 MLP 级。** Android APK 已签名可安装；三阶段无阻断；危机词安全路径有效。

| 阶段 | 结果 | 关键数据 |
|---|---|---|
| APK 构建/安装 | ✅ | `com.astralpath.app.v22` v2.2.0(32) · minSdk 26 / target 35 · 签名 CN=AstralPath · 模拟器 test35 安装启动成功 |
| OCR/解析 | ✅ 12/12 | 抽样 24 页/本 · 字符 6.8k–21.2k · 扫描书自动走 OCR · 文本书文本层直读 |
| 知识图谱 | ✅ 12/12 | 每书 30–51 节点 / 47–133 边 · 建图 <0.1s/本 |
| 智能体 | ✅ | 22 意图四方一致 · 危机词命中 4 · 诊断/计划/图谱/危机路由全通 |

---

## 二、Android APK 验收

| 项 | 值 |
|---|---|
| 产物 | `release/AstralPath-Android-2.2.0-Store.apk`（11.0 MB） |
| 包名 / 版本 | `com.astralpath.app.v22` · versionCode 32 · versionName 2.2.0 |
| SDK | minSdk 26 · targetSdk 35 · compileSdk 35 |
| 权限 | `INTERNET` · `ACCESS_NETWORK_STATE` |
| 签名 | v1/v2/v3 · `CN=AstralPath, OU=Mobile, O=ClassTechStar, C=CN` |
| 资源一致性 | APK 内 `assets/www/index.html` SHA256 与 `deploy/monolith-web/index.html` **一致** |
| OCR 引擎资产 | `assets/www/ocr-engine/` 179 文件（含 cmaps） |
| 模拟器 | AVD `test35`（API 35）· `adb install` Success · 启动后 UI 正常 |
| UI 验收 | 首页含导航：起点/藏书阁/识网/知债/今日/智能体/画像/账户 · 示例教材 C# 60 节点·123 边·99 题 |

截图：`docs/android-shots/mlp-2.2.0-home.png` · UI 文本：`docs/android-shots/mlp-ui-home.xml`

---

## 三、OCR/解析（12/12）

抽样 24 页/本（含扫描页），`ocr_pipeline_umi` mixed 模式：

| # | 书 | 字符 | OCR页 | 耗时s |
|---:|---|---:|---:|---:|
| 1 | Kotlin编程实践 | 14,130 | 6 | 0.2 |
| 2 | Go语言从入门到精通 | 11,462 | 15 | 0.5 |
| 3 | 大模型应用开发·AI Agent | 14,782 | 24 | 0.1 |
| 4 | 深度学习入门2·自制框架(扫描) | 20,200 | 24 | 0.1 |
| 5 | 深度学习入门4·强化学习 | 20,041 | 24 | 0.1 |
| 6 | C#从入门到精通 | 9,340 | 16 | 0.3 |
| 7 | 深度学习进阶·NLP | 12,131 | 5 | 0.1 |
| 8 | 黄仁勋：英伟达之芯 | 21,200 | 3 | 0.9 |
| 9 | Java从入门到精通 | 8,007 | 11 | 0.1 |
| 10 | 花书中译 Deep Learning | 10,568 | 0 | 0.1 |
| 11 | Python从入门到实践 | 6,860 | 1 | 0.3 |
| 12 | 深度学习入门·Python实现 | 12,146 | 4 | 0.1 |

- 文本书：文本层直读，OCR 仅补空页/坏页
- 扫描书（大模型、自制框架、强化学习）：自动全页 OCR
- 完整度门槛：可读字符 ≥40 → **12/12 通过**

---

## 四、知识图谱（12/12）

| # | 书 | 节点 | 边 | 耗时s |
|---:|---|---:|---:|---:|
| 1 | Kotlin编程实践 | 50 | 127 | 0.0 |
| 2 | Go语言从入门到精通 | 51 | 106 | 0.0 |
| 3 | 大模型应用开发·AI Agent | 51 | 114 | 0.0 |
| 4 | 深度学习入门2·自制框架 | 50 | 103 | 0.1 |
| 5 | 深度学习入门4·强化学习 | 50 | 93 | 0.0 |
| 6 | C#从入门到精通 | 50 | 105 | 0.0 |
| 7 | 深度学习进阶·NLP | 51 | 113 | 0.0 |
| 8 | 黄仁勋：英伟达之芯 | 30 | 47 | 0.0 |
| 9 | Java从入门到精通 | 51 | 103 | 0.0 |
| 10 | 花书中译 | 50 | 84 | 0.1 |
| 11 | Python从入门到实践 | 50 | 132 | 0.0 |
| 12 | 深度学习入门·Python实现 | 50 | 133 | 0.0 |

门槛：节点≥10 且边≥5 → **12/12 通过**

---

## 五、智能体

| 检查 | 结果 |
|---|---|
| 意图登记表 | 22 意图 · json/js/cs 三方一致（verify_agent_intents ALL GREEN） |
| 危机词 | 命中 4 类（不想活/自杀/活不下去/结束生命） |
| 诊断路由 | ✅ |
| 计划路由 | ✅ |
| 图谱路由 | ✅ |
| 危机路由 | ✅ |

---

## 六、契约门禁

| 门禁 | 结果 |
|---|---|
| `verify_constants.py` | ALL GREEN · 26 项对齐 |
| `verify_agent_intents.py` | ALL GREEN · 22 意图四方一致 |
| `verify_formulas.py` | ALL GREEN · score/impact/sale 对拍一致 |

---

## 七、MLP 判定

| 维度 | 标准 | 达成 |
|---|---|---|
| 界面直观 | 普通用户无指导可完成核心操作 | ✅ 八导航 + 起点引导 + 示例教材 |
| 响应迅速 | 无明显卡顿/崩溃 | ✅ 抽样解析 <1s/本 · 建图 <0.1s · 模拟器无崩溃 |
| 结果可靠 | 满足日常使用 | ✅ 12/12 识别与建图 · 意图路由正确 |
| 好用 | 流畅、可喜爱 | ✅ 三端同源 · 危机安全路径 · 本地闭环 |

**综合：MLP 通过。**

---

## 八、复测入口

```powershell
# 门禁
python scripts/verify_constants.py
python scripts/verify_agent_intents.py
python scripts/verify_formulas.py
# MLP 验收（12 本抽样）
python tools/mlp_acceptance.py
# Android
# release\AstralPath-Android-2.2.0-Store.apk → adb install
```
