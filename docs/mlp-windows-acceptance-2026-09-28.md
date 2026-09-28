# Windows 端 MLP 验收报告 · 2026-09-28

> 范围：`AstralPath-Monolith-Setup-2.2.0.exe` 本机安装 + 12 本 PDF（`Downloads\PDF TEST`）
> 顺序：安装验证 → OCR → 知识图谱 → 智能体 · 标准：MLP（最小喜爱产品）
> 关联：`docs/mlp-acceptance-2026-09-28.md`（管线抽样数据）· `docs/mlp-windows-e2e-2026-09-28.md`（历史 e2e）

---

## 一、结论

**Windows 安装包可顺利安装、应用可启动，12/12 本 PDF 全流程 MLP 通过。**

| 阶段 | 结果 | 关键数据 |
|---|---|---|
| 编译 | ✅ | .NET 10 publish + Inno Setup 7 · 17.5s · `release/AstralPath-Monolith-Setup-2.2.0.exe` 50.5 MB |
| 安装 | ✅ exit=0 | `C:\AstralPath-Mono22\` · 开始菜单 + 桌面快捷方式 · 无需重启 |
| 启动 | ✅ | 窗口标题「知债：星穹学途 · 单体版（无微服务）」 |
| 资源一致性 | ✅ | 安装内 `Resources/index.html` SHA256 与 monolith 源一致 |
| OCR/解析 | ✅ 12/12 | 抽样 24 页/本 · 字符 6.8k–21.2k |
| 知识图谱 | ✅ 12/12 | 30–51 节点 / 47–133 边 |
| 智能体 | ✅ | 22 意图 · 危机词 · 诊断/计划/图谱/危机路由全通 |
| 安装包工具链 | ✅ | `tools/ocr_pipeline.py` + `kg_builder.py` 可 import 并建图 |

---

## 二、编译与安装

| 项 | 值 |
|---|---|
| 安装包 | `release/AstralPath-Monolith-Setup-2.2.0.exe`（50,482,200 字节） |
| 编译链 | `dotnet publish -c Release -r win-x64 --self-contained` → Inno Setup 7 `ISCC` |
| 契约门禁 | verify_constants / verify_agent_intents / verify_formulas **ALL GREEN** 后打包 |
| 安装路径 | `C:\AstralPath-Mono22\`（注册表 InstallLocation 确认） |
| 主程序 | `AstralPath.Monolith.exe`（自包含，目标机无需装 .NET） |
| 随包工具 | `tools/ocr_pipeline.py`、`kg_builder.py`、`deep_chapters.py`、`tessdata/chi_sim+eng` |
| 快捷方式 | 开始菜单「知债：星穹学途 单体版 2.2」· 桌面快捷方式 |
| 静默安装日志 | `C:\Temp\astralpath-install.log` · `Installation process succeeded` |

---

## 三、功能 MLP 测试（12 本 PDF）

与 `tools/mlp_acceptance.py` 同源复跑：

| 阶段 | 通过 | 说明 |
|---|---|---|
| OCR | **12/12** | 文本书文本层直读；扫描书（大模型/自制框架/强化学习）自动 OCR |
| 知识图谱 | **12/12** | 每书 ≥30 节点 / ≥47 边，建图 <0.1s |
| 智能体 | **通过** | 22 意图登记一致；危机词命中 4 类；诊断/计划/图谱/危机路由 OK |

安装目录工具链冒烟：`ocr_pipeline` / `kg_builder` import OK，示例文本建图 5 节点 10 边。

---

## 四、MLP 判定（Windows）

| 维度 | 标准 | 达成 |
|---|---|---|
| 界面直观 | 普通用户无指导可完成 | ✅ 八导航 + 起点引导 + 示例教材 |
| 安装顺畅 | 无异常/无强制重启 | ✅ VERYSILENT exit=0 |
| 响应迅速 | 无卡顿崩溃 | ✅ 解析 <1s/本（抽样）· 建图 <0.1s · 进程稳定 |
| 结果可靠 | 日常可用 | ✅ 12/12 识别建图 · 意图路由正确 |
| 好用 | 流畅可喜爱 | ✅ 本地闭环 · 三端同源 · 危机安全路径 |

**综合：Windows 端 MLP 通过。**

---

## 五、复测入口

```powershell
# 门禁
python scripts/verify_constants.py
python scripts/verify_agent_intents.py
python scripts/verify_formulas.py
# 编译安装包（Windows 段）
dotnet publish src\AstralPath.Monolith\AstralPath.Monolith.csproj -c Release -r win-x64 --self-contained true -o src\AstralPath.Monolith\publish
& 'C:\Program Files\Inno Setup 7\ISCC.exe' src\AstralPath.Monolith\setup-monolith.iss
# 静默安装
release\AstralPath-Monolith-Setup-2.2.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
# 启动
C:\AstralPath-Mono22\AstralPath.Monolith.exe
# 12 本 MLP 抽样
python tools/mlp_acceptance.py
```
