# 知债：星穹学途 · Android 构建说明

## 同构策略

桌面/Windows、Web、Android **共用同一份** `www/index.html`：

| 端 | 壳 | 界面 | 后端 |
|----|----|------|------|
| Web | 浏览器 | wwwroot/index.html | AstralPath.Api |
| Windows | WebView2 | 同一 index.html | 本机 Api |
| Android | 系统 WebView | 同一 index.html（assets） | `http://127.0.0.1:5190` |

功能、布局、交互、数据处理逻辑一致。

## 构建 APK（Windows）

```powershell
cd src\AstralPath.Android
powershell -File build-apk.ps1
```

产物：`dist/AstralPath-1.3.0.apk`

## 安装到手机（ADB）

```powershell
# 1) 将电脑 5190 映射到手机 127.0.0.1:5190
adb reverse tcp:5190 tcp:5190

# 2) 安装
adb install -r dist\AstralPath-1.3.0.apk

# 3) 启动
adb shell am start -n com.astralpath.app/.MainActivity
```

## 系统要求

- Android 8.0（API 26）及以上
- 主流手机分辨率（宽屏 viewport，支持缩放）
- 需电脑上已启动 `AstralPath.Api`（或可访问的兼容后端）

## 兼容性与性能

- minSdk 26 / targetSdk 34
- 单 Activity + 系统 WebView，启动快、内存占用低
- 状态栏/导航栏与 Web 端浅灰主题一致
