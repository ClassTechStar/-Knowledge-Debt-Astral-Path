# 知债：星穹学途 · Android APK 安装说明

**Knowledge Debt: Astral Path** · v1.3.0 · Android 8.0+（API 26+）

## 一、与 Web / Windows 的关系

Android 端通过系统 **WebView** 加载与 Web **同一份** `index.html`，因此：

| 维度 | 是否一致 |
|------|----------|
| 功能（藏书阁/识网/知债/今日/智能体/画像/账户） | 一致 |
| 界面布局、色彩、字体、交互 | 一致 |
| 数据交互逻辑（REST API） | 一致 |

## 二、安装包

| 文件 | 说明 |
|------|------|
| `src/AstralPath.Android/dist/AstralPath-1.3.0.apk` | 可安装 APK（debug 签名，可侧载） |

- 包名：`com.astralpath.app`
- 版本：1.3.0（versionCode 13）
- minSdk 26 / targetSdk 34
- 权限：INTERNET、ACCESS_NETWORK_STATE

## 三、安装步骤（ADB，推荐开发调试）

```powershell
$adb = "C:\Users\18948\AppData\Local\Android\Sdk\platform-tools\adb.exe"
$apk = "src\AstralPath.Android\dist\AstralPath-1.3.0.apk"

# 1. 手机开启 USB 调试并连接
& $adb devices

# 2. 将电脑 5190 端口映射到手机（App 默认访问 127.0.0.1:5190）
& $adb reverse tcp:5190 tcp:5190

# 3. 在电脑上启动后端 AstralPath.Api（监听 http://127.0.0.1:5190）

# 4. 安装并启动
& $adb install -r $apk
& $adb shell am start -n com.astralpath.app/.MainActivity
```

## 四、手机上直接安装（侧载）

1. 将 `AstralPath-1.3.0.apk` 拷到手机
2. 设置 → 允许「安装未知应用」
3. 点击 APK 安装
4. **后端**：需能访问 `http://127.0.0.1:5190`  
   - 同一 Wi‑Fi 下可把 App 内 API 基址改为电脑局域网 IP（后续版本可配置）  
   - 开发调试推荐 USB + `adb reverse`

## 五、功能自测清单

| 项 | 预期 |
|----|------|
| 启动 | 状态栏「已就绪 · 与 Web 端同构」 |
| 首页 | 品牌「知债：星穹学途」+ 导航 8 项 |
| UI 版本角标 | `UI-full-v13`（与 Web 相同） |
| 系统状态 | `status=ready · graph=ok · students=2` |
| 知债 | 红边数与叙事与 Web 一致 |
| 藏书阁/识网/今日/智能体/画像 | 可打开并与 Web 同构 |

演示账户：`demo@astralpath.local` / `demo123456`

## 六、兼容性与性能

- Android 8.0+，已在 Android 16（API 36，分辨率 1440×3200）真机验证
- 单 Activity + 系统 WebView：启动快、内存占用低
- 支持主流屏幕与缩放（wide viewport）
- 离线时界面仍可显示（内置 UI）；业务数据需连接后端

## 七、重新编译

```powershell
cd src\AstralPath.Android
powershell -File build-apk.ps1
```

依赖：Android SDK（build-tools 36、platform android-34）、JDK 17+。

## 八、合规

教学辅助系统，不构成处分依据。consent fail-closed；敏感词脱敏；危机内容转人工。
