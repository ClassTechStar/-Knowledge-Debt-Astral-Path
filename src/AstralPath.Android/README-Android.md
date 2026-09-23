# 知债：星穹学途 · Android 构建说明

> 完整构建与签名说明见 [`../../docs/Android-构建与签名说明.md`](../../docs/Android-构建与签名说明.md)
> 产物验证与跨端一致性测试见 [`../../docs/Android-测试报告.md`](../../docs/Android-测试报告.md)

## 同构策略

桌面/Windows、Web、Android **共用同一份** `www/index.html`：

| 端 | 壳 | 界面 | 后端 |
|----|----|------|------|
| Web | 浏览器 | wwwroot/index.html | AstralPath.Api |
| Windows | WebView2 | 同一 index.html | 本机 Api |
| Android | 系统 WebView | 同一 index.html（assets） | `http://127.0.0.1:5190` |

功能、布局、交互、数据处理逻辑一致。构建期以 md5 校验 `assets/www/index.html` 与
`src/AstralPath.Api/wwwroot/index.html` 逐字节相同。

加载顺序：优先 `www/index.html`；仅当该文件缺失时回退 `www/mobile.html`。

## 构建 APK（Windows，无需 Gradle）

```powershell
cd src\AstralPath.Android
powershell -ExecutionPolicy Bypass -File build-apk.ps1
```

产物：`dist/AstralPath-1.3.0-release.apk`（**已签名发布版**）

脚本自动完成：资源编译 → 链接（注入 SDK/版本信息）→ 打包 assets → javac → d8 →
zip → zipalign → **以发布密钥签名** → `apksigner verify` + `aapt2 dump badging`。

首次运行会在 `keystore/` 生成发布密钥并在 `keystore.properties` 记录口令（两者均已
`.gitignore`）。**请务必备份**：丢失后无法对同一应用发布更新。

## 安装到设备（ADB）

```powershell
# 1) 将电脑 5190 映射到手机 127.0.0.1:5190
adb reverse tcp:5190 tcp:5190

# 2) 安装
adb install -r dist\AstralPath-1.3.0-release.apk

# 3) 启动
adb shell am start -n com.astralpath.app/.MainActivity
```

> **小米 / Redmi 用户**：`adb install` 可能报 `INSTALL_FAILED_USER_RESTRICTED`，
> 这是 MIUI 安全策略。需在手机上开启 开发者选项 → 「USB 调试（安全设置）」，
> 或在手机屏幕上确认安装弹窗；也可把 APK 传入手机后用文件管理器点击安装。
> 详见构建说明 §5.2。

> **已装过旧版**：若设备上存在 debug 签名的旧构建，安装发布版会报
> `INSTALL_FAILED_UPDATE_INCOMPATIBLE`（签名不一致）。先卸载旧版再安装。

## 连接后端（手机端必读）

**手机上的 `127.0.0.1` 指向手机自己**，因此必须让 App 知道电脑的地址。三种方式任选：

### 方式 A：App 内配置局域网地址（推荐，无需数据线）

1. 电脑与手机连**同一个 Wi-Fi**（注意手机关闭 VPN，VPN 会拦截局域网访问）；
2. 电脑上以局域网模式启动后端：

   ```text
   dotnet run --project src/AstralPath.Api -c Release --urls http://0.0.0.0:5190
   ```

   启动横幅会直接打印可用地址，例如：
   `[AstralPath] 手机端请在「服务器地址」填入：http://10.102.22.199:5190`
3. 首次放行 Windows 防火墙（管理员执行一次）：

   ```text
   netsh advfirewall firewall add rule name="AstralPath 5190" dir=in action=allow protocol=TCP localport=5190
   ```
4. 打开 App → 首页「系统状态」→ 在**服务器地址**填入上面打印的地址 → 点「保存并测试」。
   地址会持久化，之后自动连接。也可点「自动探测」；「恢复默认」清除配置。

### 方式 B：USB + adb reverse（无需改网络，最稳）

```text
adb reverse tcp:5190 tcp:5190     # 把电脑 5190 映射到手机的 127.0.0.1:5190
```

App 保持默认地址 `http://127.0.0.1:5190` 即可。**每次重新插拔 USB 后需重执行**。

### 方式 C：手机开热点，电脑连热点

手机开启热点 → 电脑连上该热点 → 电脑按方式 A 以局域网模式启动 → App 填电脑在热点网段的 IP。

### 排障

「系统状态」卡片会列出**所有探测过的候选地址及结果**（✅/❌），可直接定位是哪一环不通。
用手机浏览器打开填入的地址，若同样打不开，则是网络/防火墙问题而非 App 问题。

## 系统要求

- Android 8.0（API 26）及以上；targetSdk 34
- 需电脑上已启动 `AstralPath.Api`（或可访问的兼容后端）

## 兼容性与性能

- minSdk 26 / targetSdk 34，单 Activity + 系统 WebView
- 自适应矢量图标（任意密度清晰，无位图资源）
- 状态栏/导航栏与 Web 端浅灰主题一致；`allowBackup=false`（学生数据不参与云备份）
- 已接入系统文件选择器：Web 端「藏书阁 → 上传教材」在 Android 上可用（支持多选）
- 实测冷启动 `TotalTime ≈ 3.1s`（API 35 模拟器），无致命异常

## 工程结构

```
src/AstralPath.Android/
├── build-apk.ps1                     # 发布版 APK 构建脚本（8 步管线）
├── keystore.properties.example       # 签名凭据字段示例（真实凭据不入库）
├── app/
│   ├── build.gradle.kts              # Gradle 配置（含 keystore.properties 驱动的发布签名）
│   └── src/main/
│       ├── AndroidManifest.xml
│       ├── java/com/astralpath/app/MainActivity.java
│       ├── assets/www/{index,mobile}.html
│       └── res/{layout,values,drawable,mipmap-anydpi-v26,xml}
└── dist/                             # 构建产物（*release.apk 为交付物）
```
