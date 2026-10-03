# Android 端 APK MLP 级端到端测试报告（2026-09-28）

范围：`src/AstralPath.Native` 签名 release APK（10.5MB，com.astralpath.app.v22 v2.2.0），
模拟器 test35（sdk_gphone64_x86_64 API 35），`C:\Users\18948\Downloads\PDF TEST` 全部 12 本 PDF，
按 **OCR → 知识图谱 → 智能体** 在真实 Android WebView 中实测。

---

## 一、结论

**Android 端达到 MLP 级。** OCR（pdf.js 文本层 + WASM OCR 定向回填）、知识图谱（真实目录结构 + 掌握度画像）、
智能体（意图路由 + 危机词安全路径 + 教材内容问答）全链路可用；用户实测中同步验证了危机词安全路径
（路由轨迹 crisis.handoff·R0:crisis）。测试中发现并修复 1 个阻断 + 2 个体验问题（§三），
并根据用户反馈落地三项产品升级（§四）。

## 二、构建与安装

| 项 | 结果 |
|---|---|
| 构建 | Gradle 9.7.1 + JBR 17，`assembleRelease` BUILD SUCCESSFUL（签名：`~/.android/astralpath-signing/signing.env` 环境变量注入） |
| 产物 | `app-release.apk` 10.5MB（assets 内嵌单体 index.html + ocr-engine WASM 引擎，与 Web/Windows 三镜像同源） |
| 安装 | adb Streamed Install 成功；WebViewAssetLoader 加载 `https://appassets.androidplatform.net/assets/www/index.html`（Worker/WASM 可用） |
| 测试数据 | 12 本 PDF（530MB）push 到 `/sdcard/Download/pdftest` |

## 三、发现并修复的问题

| # | 严重度 | 问题 | 修复 |
|---|---|---|---|
| 1 | **阻断** | `<input type=file>` 的 accept 含 `text/markdown`（.md）等非常见 MIME → Android DocumentsUI 组合过滤查询返回空列表——用户在选择器里**永远看不到文件**（实测：MediaStore 有记录、搜索能搜到，目录列表恒为空） | accept 改为 `application/pdf,text/plain,.pdf,.txt`（.md 用户走粘贴兜底）；注意：通过「内部存储根 → Download → pdftest」路径浏览正常，「Downloads」抽屉根路径的过滤列表为空是 DocumentsUI 提供者怪癖，与 app 无关 |
| 2 | 中 | 题干/选项携带 `[page N]` 渲染页标记（pdf.js 页标记混入正文摘录） | 出题 snippet 过滤 `[page \d+]` |
| 3 | 中 | 智能体内容问答阈值过严：单词命中章标题（如「认识.NET」）得 3 分但门槛 4 分 → 落兜底 | 阈值 3 分即采纳（标题命中是强信号） |

## 四、用户反馈驱动的三项产品升级（同日落地）

| 反馈 | 实现 |
|---|---|
| ①「今日任务出的题太傻」（旧题正确答案恒为 A、选项是凑数话术） | `generateQuestions` v2：全部来自真实教材——**章节关键词题**（本章高频词 vs 其他章高频词）、**概念归属题**（「term」属于哪一章，选项=真实章标题）、**正文出处题**（哪段文字出自本章，四个选项全是各章真实片段）；正确位置由 id 哈希决定，同书同题（确定性）。实测截图：C# 书 String 章的四选项全是真实教材片段 |
| ②「知识图谱直接读取书籍目录」 | `extractChapters` v2：优先识别书籍自带目录页（章节名+点线+页码，≥3 个一级条目成簇即采信），正文按目录标题在主文中定位切分；命中后章节节点直接来自书籍目录。实测：C# 书重解析节点 60→**100**，章节=书籍真实目录 |
| ③「智能体和藏书阁/图谱深层联动」 | graph.view 返回**当前书章节目录+高频概念**；kb.search **跨全书检索**（标题+正文，按相关度带出处）；material.parse 报**藏书阁书单**；兜底前先 `answerFromBooks`——直接问教材内容（如「认识.NET 这一章讲什么」）返回书名+章节+正文摘录+关键词 |

## 五、实测记录（模拟器）

- 起点/藏书阁/识网/知债/今日/智能体/画像 全页渲染正常（移动端自适应布局）
- C# 书全流程：Choose Files → DocumentsUI 选择 → 解析（秒级）→ 识网 100 节点图谱（真实章节+术语+先修链）→ 章节侧栏带页码与正文摘要 → 画像掌握度条按解析数据初始化（0.742–0.803）→ 今日任务引用真实章节名 → 练习题四选项全真实正文
- 智能体：意图路由轨迹面板（如 graph.view·execute / crisis.handoff·R0:crisis）本地可见
- 危机词安全路径：实测回复正确（用户协同触发）

### 遗留（不阻断 MLP）

1. WASM OCR 全书采样（28 页）在模拟器上约 2–6 秒/页——真机（ARM+SIMD）预计更快；纯扫描书在 Android 端仍是采样覆盖而非全书
2. DocumentsUI「Downloads」抽屉根路径的 MIME 过滤列表为空（Android 系统行为）；app 内建议用户走「内部存储」根路径，或后续在壳内定制文件选择 Activity
3. 中文输入依赖系统输入法（adb 自动化限制，非产品问题）

## 六、复测入口

```powershell
# 构建（需 JAVA_HOME=Android Studio jbr、ANDROID_HOME、signing.env）
cd src\AstralPath.Native; C:\Gradle\bin\gradle.bat assembleRelease
adb install -r app\build\outputs\apk\release\app-release.apk
adb push "C:\Users\18948\Downloads\PDF TEST" /sdcard/Download/pdftest/
```
