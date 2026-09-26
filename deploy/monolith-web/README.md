# UI 产物拓扑（C2 收敛说明）

> 2026-09-26 审计 C2「UI 四份拷贝三套版本」的最终口径。现状：**两类产物，不再有第三套**。

## 一、单体版（唯一事实源，交付态）

| 位置 | 角色 |
|---|---|
| `deploy/monolith-web/index.html` | **源**。8 页自包含应用（file:// 双击 / serve.ps1 均可），业务全在页面内 |
| `src/AstralPath.Monolith/Resources/index.html` | Windows 壳镜像（WebView2 加载） |
| `src/AstralPath.Native/app/src/main/assets/www/index.html` | Android 壳镜像（WebViewAssetLoader 加载，Kotlin 壳为纯加载器） |

三份**必须逐字节一致**，由 `scripts/sync-monolith-html.ps1` 保证：改源 → 跑脚本同步 → md5 门禁 + C3 XSS 回归哨兵；`verify_all.ps1` 每次校验。配套运行时 `ocr-engine/`（WASM tesseract + pdf.js + traineddata）同样三处部署，同步脚本加 `-IncludeOcrEngine` 可一并更新。

## 二、独立在线版（不同应用，勿混同）

| 位置 | 角色 |
|---|---|
| `src/AstralPath.Api/wwwroot/index.html` | 面向 AstralPath.Api 的**在线客户端**（登录/学生/资料演示台），由 Api 宿主 UseWebRoot 提供。文件头部有职责标注注释 |

它有自己的交互与数据流（走 REST + 鉴权），不是单体版的镜像，**不参与** md5 门禁。

## 三、历史遗留（已清除）

| 位置 | 处置 |
|---|---|
| `src/AstralPath.Mobile.Offline/index.html` | **已删除**（2026-09-26）：零代码引用的死产物；离线 Avalonia 端（AstralPath.Mobile）使用 XAML 视图，不加载 HTML |
| `src/AstralPath.Native/.../ocr-engine/ocr-wasm.js.bak-*` | 已删除（提交时剔除） |

## 重构阶段备注

未来 Windows/Android 原生端重构时，单体版 HTML 保留为「行为规格说明书」；原生实现以它为对照基准（见 ADR-0001）。
