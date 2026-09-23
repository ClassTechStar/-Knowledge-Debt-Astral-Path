# 知债：星穹学途 · Android 构建与签名说明

> 适用工程：`src/AstralPath.Android`
> 产物：`src/AstralPath.Android/dist/AstralPath-1.3.0-release.apk`（**已签名的发布版**）
> 目标平台：Android 8.0（API 26）及以上；`targetSdk 34`

---

## 一、同构策略（为什么三端功能一致）

桌面/Windows、Web、Android **共用同一份界面代码**，Android 端不做二次实现：

| 端 | 壳 | 界面来源 | 后端 |
|---|---|---|---|
| Web | 浏览器 | `src/AstralPath.Api/wwwroot/index.html` | `AstralPath.Api` |
| Windows | WebView2 | 同一份 `index.html` | 本机 `AstralPath.Api` |
| Android | 系统 WebView | `app/src/main/assets/www/index.html`（**与 Web 端逐字节同一份**） | `http://127.0.0.1:5190`（经 `adb reverse` 或 `10.0.2.2` 指向宿主机） |

一致性由构建期校验保证：`build-apk.ps1` 打包的 `assets/www/index.html` 与
`src/AstralPath.Api/wwwroot/index.html` md5 相同（详见测试报告）。

---

## 二、环境要求

| 组件 | 版本要求 | 本机实测 |
|---|---|---|
| JDK | 17+（用于 `javac`/`keytool`） | JDK 26.0.2.1 |
| Android SDK | platform `android-34` + build-tools `36.0.0` | 均已安装 |
| 平台工具 | `adb`（仅安装/调试需要） | 已安装 |

构建脚本会自动探测：环境变量 `JAVA_HOME` → `%ProgramFiles%\Java\jdk-*` → Android Studio 自带 `jbr`；
SDK 路径取 `ANDROID_SDK_ROOT`，未设置时回落 `%LOCALAPPDATA%\Android\Sdk`。

---

## 三、构建发布版 APK

```powershell
cd src\AstralPath.Android
powershell -ExecutionPolicy Bypass -File build-apk.ps1
```

脚本共 8 步，全部可直接复现：

| 步骤 | 动作 | 说明 |
|---|---|---|
| 1 | `aapt2 compile` | 资源编译为 `.flat` |
| 2 | `aapt2 link` | 生成基础 APK，并注入 `minSdk=26` / `targetSdk=34` / `versionCode=13` / `versionName=1.3.0` |
| 3 | 拷贝 assets | 打包 `assets/www`（与 Web 端同一份 `index.html`） |
| 4 | `javac --release 17` | 编译 `MainActivity` 与 `R.java` |
| 5 | `d8 --release --min-api 26` | class → `classes.dex` |
| 6 | 打包 | `classes.dex` + assets 写入 APK |
| 7 | `zipalign -f 4` | **必须在签名之前**，否则签名失效 |
| 8 | `apksigner sign` | 以发布密钥签名（启用 v1/v2/v3），随后自动 `verify` + `dump badging` |

构建成功后脚本会打印 APK 路径、体积与 SHA-256。

### 为什么不用 Gradle

工程内保留了完整的 Gradle 配置（`build.gradle.kts`、`settings.gradle.kts`、`gradle/wrapper/gradle-wrapper.properties`），
但本机仅有 JDK 26、未安装 Gradle 发行版，且 Gradle 8.2.1 不支持该 JDK 版本；
`gradle-wrapper.jar` 与 `gradlew` 脚本也不在仓库中（仅保留了 `gradle-wrapper.properties`）。
因此提供**等价的命令行管线**，无需 Gradle 即可产出与 Gradle 构建语义一致的发布版 APK。

若后续安装 Gradle 8.7+ 与 JDK 17/21，可直接：

```bash
gradle :app:assembleRelease
```

已同步修改 `app/build.gradle.kts`：`release` 构建类型读取 `keystore.properties` 使用真实发布签名
（`storeFile` 缺失时才回落 debug 签名，便于本地侧载调试）。

---

## 四、签名与密钥管理

### 4.1 首次构建

脚本检测到 `keystore.properties` 不存在时，会：

1. 用 `keytool` 生成 2048 位 RSA 发布密钥（有效期 10950 天），落于 `keystore/astralpath-release.jks`；
2. 生成 `keystore.properties` 记录 `storeFile / storePassword / keyAlias / keyPassword`。

> ⚠ **PKCS12 口令约束**：JDK 9+ 的 `keytool` 默认生成 **PKCS12** 密钥库，它**不支持与密钥库口令不同的密钥口令**
> （`keytool` 会忽略 `-keypass` 并告警）。因此脚本令密钥口令＝存储口令；如需独立口令请改用 `-storetype JKS`。
> 这是构建期实际踩到的坑：首版脚本为两者生成了不同随机口令，导致 `apksigner` 报
> `UnrecoverableKeyException: Given final block not properly padded`。

### 4.2 必须备份

**keystore 与口令一旦丢失，将无法对同一应用发布更新**（只能更换包名重新发布）。
请将 `keystore/astralpath-release.jks` 与 `keystore.properties` 一并备份到安全位置。

### 4.3 版本控制边界

以下内容已加入 `.gitignore`，**不会进入版本库**：

```
src/AstralPath.Android/keystore/            # 私钥库
src/AstralPath.Android/keystore.properties  # 口令
src/AstralPath.Android/build-manual/        # 构建中间产物
src/AstralPath.Android/.gradle/
src/AstralPath.Android/local.properties
src/AstralPath.Android/dist/*.idsig         # 签名中间文件
src/AstralPath.Android/dist/*.png|*.xml     # 截图与 UI dump
```

`keystore.properties.example` 保留了字段说明，供新环境配置参考（不含真实口令）。

---

## 五、安装到设备

### 5.1 模拟器 / 已开启「USB 安装」的设备

```powershell
# 1) 把宿主机的 5190 端口映射到设备的 127.0.0.1:5190（应用内写死该地址）
adb reverse tcp:5190 tcp:5190

# 2) 安装
adb install -r src\AstralPath.Android\dist\AstralPath-1.3.0-release.apk

# 3) 启动
adb shell am start -n com.astralpath.app/.MainActivity
```

宿主机需先启动后端：

```powershell
$env:ASTRALPATH_GRAPH_PACK = "<repo>\graph-packs\accounting-v1"
dotnet run --project src/AstralPath.Api -c Release --urls http://127.0.0.1:5190
```

### 5.2 ⚠ 小米 / Redmi（MIUI、HyperOS）安装受限说明

在小米设备上通过 `adb install` 可能失败并报：

```
INSTALL_FAILED_USER_RESTRICTED: Install canceled by user
```

这是 **MIUI 的设备侧安全策略**（并非 APK 缺陷），需在手机上操作：

1. 设置 → 我的设备 → 全部参数 → 连续点击「MIUI 版本」开启开发者模式；
2. 设置 → 更多设置 → 开发者选项 → 打开 **「USB 调试」** 与 **「USB 调试（安全设置）」**，
   部分机型需登录小米账号并等待 7 天；
3. 重新执行 `adb install`，**并在手机屏幕上确认安装提示**（该弹窗不可跳过）。

替代方案（无需开发者选项）：把 APK 传到手机，用文件管理器点击安装，并在弹出的
「未知来源应用」提示中允许安装。

### 5.3 与旧版本共存问题

若设备上已装过 **debug 签名**的旧构建，安装发布版会报：

```
INSTALL_FAILED_UPDATE_INCOMPATIBLE: Existing package signatures do not match
```

因为 Android 要求同一包名的 APK 使用**同一签名**。处理方式：先卸载旧版再安装发布版
（`adb uninstall com.astralpath.app`，或在手机上长按图标卸载）。

---

## 六、手机连接后端（服务器地址）

App 内「系统状态」卡片提供**服务器地址**设置（Web / Windows / Android 三端共用同一套逻辑）：

| 控件 | 说明 |
|---|---|
| 服务器地址 | 如 `http://10.102.22.199:5190`；未写协议自动补 `http://`，未写端口自动补 `:5190` |
| 保存并测试 | 立即探测并持久化（localStorage）；失败时列出全部候选地址的探测结果 |
| 自动探测 | 按候选顺序（用户配置 → 宿主注入 → 页面来源 → `10.0.2.2` → `127.0.0.1` → `localhost`）逐个探测 |
| 恢复默认 | 清除配置，回到自动探测 |

后端以局域网模式启动时会打印可直接填写的地址：

```text
dotnet run --project src/AstralPath.Api -c Release --urls http://0.0.0.0:5190
[AstralPath] 手机端请在「服务器地址」填入：http://10.102.22.199:5190
```

> **为什么必须可配置**：真机上 `127.0.0.1` 指的是手机自身；原实现把地址写死为
> `http://127.0.0.1:5190`，脱离 USB（未做 `adb reverse`）时所有请求 `Failed to fetch`，
> 上传、诊断、账户等功能全部不可用。现已改为可配置且持久化，并保留 USB 与其余候选作为回退。

## 七、故障排查

| 现象 | 原因 | 处理 |
|---|---|---|
| `javac.exe : 使用或覆盖了已过时的 API` 导致构建中断 | 脚本把原生工具的 stderr 提示当作错误 | 已改为 `$ErrorActionPreference="Continue"` + 显式 `$LASTEXITCODE` 判定，并加 `-Xlint:-deprecation` |
| `apksigner sign` 报 `Wrong password? / BadPaddingException` | PKCS12 密钥库不支持独立密钥口令 | 令 `keyPassword` = `storePassword`（见 4.1） |
| 构建脚本报中文乱码语法错误（如「意外的标记"鎷疯礉"」） | 5.1 版脚本宿主在无 BOM 时按 ANSI 解析 UTF-8 脚本 | 脚本已保存为 **UTF-8 with BOM**；修改时请保持 BOM |
| 应用内上传按钮无反应 | 系统 WebView 默认不处理 `<input type="file">` | 已在 `MainActivity` 实现 `onShowFileChooser` + `onActivityResult` 接入系统文件选择器 |
| 界面能开但提示「API 不可用：Failed to fetch」 | 手机上 `127.0.0.1` 指向手机自身；既未做 `adb reverse` 也未配置局域网地址 | 在「系统状态 → 服务器地址」填入电脑局域网 IP（见 §6），或执行 `adb reverse tcp:5190 tcp:5190` |
| 填了局域网地址仍不通 | 电脑仅监听 127.0.0.1 / 防火墙未放行 / 手机开了 VPN / 不在同一网段 | 以 `--urls http://0.0.0.0:5190` 启动；放行 TCP 5190；关闭手机 VPN；确认同 Wi-Fi |
| 上传大批文件失败 | 单请求体积超过服务端上限（512 MB） | 分批上传，或调大 `MaxRequestBodySize` |

---

## 七、相关文件

| 文件 | 说明 |
|---|---|
| `build-apk.ps1` | 发布版 APK 构建脚本（8 步管线 + 签名 + 校验） |
| `app/build.gradle.kts` | Gradle 构建配置（含 `keystore.properties` 驱动的发布签名） |
| `keystore.properties.example` | 签名凭据字段示例 |
| `README-Android.md` | Android 端速览（同构策略与安装） |
| `../../docs/Android-测试报告.md` | 构建产物验证与跨端一致性测试报告 |
| `../../docs/android-shots/` | 运行时验证截图（起点 / 藏书阁 / 知债 / 今日） |
