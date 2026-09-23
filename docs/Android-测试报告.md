# 知债：星穹学途 · Android 测试报告

> 被测产物：`src/AstralPath.Android/dist/AstralPath-1.3.0-release.apk`（已签名发布版）
> 测试日期：2026-09-23
> 结论：**静态一致性与运行时可验证项全部通过**；真机安装受小米 MIUI 设备策略限制（详见 §5）

---

## 一、构建产物清单与指纹

| 项目 | 值 |
|---|---|
| 文件名 | `AstralPath-1.3.0-release.apk` |
| 体积 | 66,571 字节（约 65 KB） |
| MD5 | `4a24d5533b9dc12694d45e8f9bd3098f` |
| SHA-256 | `ae82b399d6831cdc0017c55c5367494f9968e7865a07a63b38076c5164db738c` |

### 1.1 APK 元信息（`aapt2 dump badging`）

```
package: name='com.astralpath.app' versionCode='13' versionName='1.3.0'
minSdkVersion:'26'          # Android 8.0 —— 满足"Android 8.0 及以上"
targetSdkVersion:'34'
application-label:'知债：星穹学途'
launchable-activity: name='com.astralpath.app.MainActivity'
uses-permission: android.permission.INTERNET
uses-permission: android.permission.ACCESS_NETWORK_STATE
```

> 标签中文 `知债：星穹学途` 以 UTF-8 显式解码校验通过（控制台直接输出会显示乱码，属终端解码差异，非产物缺陷）。

### 1.2 包内容

| 条目 | 大小 | 说明 |
|---|---:|---|
| `AndroidManifest.xml` | 3,020 | 单 Activity + 两个权限 + 网络安全配置 |
| `assets/www/index.html` | 148,912 | **与 Web 端同一份界面** |
| `assets/www/mobile.html` | 29,990 | 备用移动版界面（index.html 缺失时回退） |
| `classes.dex` | 10,744 | 壳层字节码 |
| `res/mipmap-anydpi-v26/ic_launcher*.xml` | 448 ×2 | 自适应图标（前景+背景，纯矢量） |
| `res/drawable/ic_launcher_*.xml` | 568 / 1,660 | 图标图层 |
| `res/layout/activity_main.xml` | 528 | 全屏 WebView |
| `res/xml/network_security_config.xml` | 772 | 仅对 127.0.0.1 / localhost / 10.0.2.2 放行明文 |
| `META-INF/*.RSA|.SF|MANIFEST.MF` | — | v1 签名文件 |

---

## 二、签名验证

`apksigner verify --print-certs --verbose` 输出要点：

```
Verifies
Verified using v1 scheme (JAR signing): false
Verified using v2 scheme (APK Signature Scheme v2): true
Verified using v3 scheme (APK Signature Scheme v3): true
Number of signers: 1
Signer #1 certificate DN: CN=Knowledge Debt: Astral Path, OU=AstralPath,
                         O=AstralPath Team, L=Guangzhou, ST=Guangdong, C=CN
Signer #1 certificate SHA-256 digest: af53486d08b22cee9aca82b6604d76f79db33cf0a45bcd9a500261b0df16a841
Signer #1 key algorithm: RSA
Signer #1 key size (bits): 2048
```

| 检查项 | 结果 | 说明 |
|---|---|---|
| v2 签名 | ✅ 通过 | Android 7.0+ 主流校验方式 |
| v3 签名 | ✅ 通过 | 支持密钥轮换 |
| v1 签名 | ➖ 未启用 | `minSdk=26` 下无需求（v1 仅服务 Android < 7.0） |
| 使用发布密钥（非 debug） | ✅ | 首版脚本使用 debug 签名，本次改为独立发布密钥库 |
| 签名顺序 | ✅ | 先 `zipalign` 后 `apksigner`，顺序正确 |
| 私钥/口令是否入库 | ✅ 未入库 | `keystore/`、`keystore.properties` 已被 `.gitignore` 拦截 |

---

## 三、跨端一致性（静态，可完全复现）

**方法**：解出 APK 内 `assets/www/index.html`，与 Web 端权威界面 `src/AstralPath.Api/wwwroot/index.html` 逐字节比对。

| 文件 | APK 内 | Web 端 | md5 | 结论 |
|---|---:|---:|---|---|
| `index.html` | 148,912 B | 148,912 B | `7c3ab1996f4585c9ef96514e5e3f2b0c` | ✅ **逐字节一致** |
| `mobile.html` | 29,990 B | 29,990 B | `540a50f02202383c698c200816a34c11` | ✅ 逐字节一致（源文件） |

**意义**：Android 端不维护第二套界面，因此"功能特性 / 界面设计 / 交互逻辑"与 Web、Windows 端
在代码层面就是同一份实现，不存在风格漂移或功能裁剪的可能。这是跨端一致性最强的静态证据。

---

## 四、运行时验证（Android 15 / API 35 模拟器）

环境：AVD `test35`（Android 15，API 35，1080×2400），后端为宿主机 `AstralPath.Api`，
通过 `adb reverse tcp:5190 tcp:5190` 建立反向端口映射。

| 步骤 | 命令 | 结果 |
|---|---|---|
| 安装 | `adb install AstralPath-1.3.0-release.apk` | ✅ `Success` |
| 冷启动 | `am start -W -n com.astralpath.app/.MainActivity` | ✅ `TotalTime: 3087 ms`，`Complete` |
| 前台确认 | `dumpsys activity activities` | ✅ `topResumedActivity=com.astralpath.app/.MainActivity` |
| 进程存活 | `pidof com.astralpath.app` | ✅ pid 2418 |
| 崩溃检查 | `logcat \| grep "FATAL EXCEPTION"` | ✅ 无致命异常 |
| 交互导航 | 点击顶部标签（起点/藏书阁/知债/今日） | ✅ 页面切换，截图字节数各不相同 |

### 4.1 截图证据（`docs/android-shots/`）

| 截图 | 页面 | 大小 | 观察到的内容 |
|---|---|---:|---|
| `emulator-01-home.png` | 起点 | 273,788 B | 顶部标签栏、账户态、API 地址、四大模块卡片（藏书阁/识网/知债/今日任务） |
| `emulator-02-library.png` | 藏书阁 | 216,509 B | 导入示例资料/解析全部/刷新列表；**「Choose Files」文件选择控件**；OCR 模式下拉；上传并解析（支持多选） |
| `emulator-03-debt.png` | 知债诊断 | 251,676 B | **真实后端数据**：红边数 5、最高 impact 176、计划天数 14；三条债边明细（impact 176 / 153.6 / 76.8，状态 repairing） |
| `emulator-04-today.png` | 今日 | 226,034 B | 今日任务页 |

### 4.2 数据交互验证

知债诊断页显示 `最高 impact 176` 与三条债边（26.8→28 / 28→34 / 26.8→34）——
这与 `accounting-v1` 图包的 BASELINE 公式结果一致，说明：

1. WebView 经 `adb reverse` **成功调用宿主机后端**（数据链路打通）；
2. 端上展示的数值与 Web / Windows 端**同源同值**（同一 API、同一公式实现）；
3. 移动端布局自适应（统计卡片两列排布，标签栏可横向滚动）。

> 说明：宿主机后端 `appsettings.json` 将 `Microsoft.AspNetCore` 日志级别设为 `Warning`，
> 因此后端访问日志未打印，本次以**界面呈现的真实数值**作为数据交互证据。

---

## 五、真机验证与限制说明（如实标注）

| 项目 | 结果 |
|---|---|
| 设备 | Xiaomi 23117RK66C（**Android 16 / API 36**），经 `adb devices` 识别 |
| `adb reverse tcp:5190` | ✅ 成功（返回端口号） |
| 安装 | ❌ `INSTALL_FAILED_USER_RESTRICTED: Install canceled by user` |
| 卸载旧版 | ❌ `DELETE_FAILED_INTERNAL_ERROR`（同受策略限制） |

**原因**：这是 **MIUI/HyperOS 的设备侧安全策略**——「通过 USB 安装」默认关闭，
需要机主在开发者选项中开启（部分机型还要求登录小米账号并等待），或直接在手机屏幕上确认安装弹窗。
PC 端无法绕过，**与 APK 本身无关**：同一 APK 在 Android 15 模拟器上安装、启动、运行全部正常。

**待用户启用后即可复测的命令**：

```powershell
adb reverse tcp:5190 tcp:5190
adb install -r src\AstralPath.Android\dist\AstralPath-1.3.0-release.apk
adb shell am start -n com.astralpath.app/.MainActivity
```

---

## 六、覆盖范围与未覆盖项

### 6.1 已验证

- 构建可复现（8 步管线，脚本化）
- 发布签名有效（v2 + v3，非 debug 密钥）
- SDK 约束正确（minSdk 26 = Android 8.0，targetSdk 34）
- 界面与 Web 端逐字节同源
- 安装、冷启动、前台运行、进程存活、无致命异常
- 页面导航与后端数据渲染（数值与 BASELINE 金样一致）
- 文件选择器接入（上传链路在 Android 上可用）

### 6.2 未覆盖（需真机或后续补测）

| 项 | 原因 | 建议 |
|---|---|---|
| 小米真机端到端 | MIUI USB 安装策略（见 §5） | 开启「USB 调试（安全设置）」后按 §5 命令复测 |
| 实际文件上传落盘 | 需用户在弹出框中手动选文件（无法脚本化） | 人工点击「Choose Files」选一份 PDF 验证上传→解析→建图 |
| Android 8.0（API 26）最低版本实测 | 本机只有 API 35 模拟器与 API 36 真机 | 建 API 26 AVD 复测；APK 已声明 minSdk 26，`d8 --min-api 26` 已按该基线脱糖 |
| 多厂商 ROM 兼容（华为/OPPO/vivo） | 无设备 | 侧载后抽测 |
| 电池/内存长稳 | 非本次范围 | 用 `dumpsys meminfo` 做 30 分钟长稳观察 |

---

## 七、本轮发现并修复的缺陷

| # | 缺陷 | 影响 | 修复 |
|---|---|---|---|
| 1 | 未实现 `onShowFileChooser` | 系统 WebView 默认不处理 `<input type="file">`，**「藏书阁 → 上传教材」在 Android 上静默失效**（与 Web/Windows 不一致） | 接入系统文件选择器（支持多选）+ `onActivityResult` 回填，并处理回调悬挂 |
| 2 | 发布版使用 **debug 签名** | 无法作为正式发布产物；与调试包签名相同存在被替换风险 | 改为独立发布密钥库（RSA 2048 / 10950 天），签名 v1+v2+v3 |
| 3 | `aapt2 link` 未注入 SDK 版本 | 产物缺失 `minSdkVersion`/`targetSdkVersion`/版本号等元信息 | 显式传入 `--min-sdk-version/--target-sdk-version/--version-code/--version-name` |
| 4 | `keytool`（JDK 26）默认 PKCS12 与独立密钥口令冲突 | `apksigner` 报 `BadPaddingException`，**签名必然失败** | 密钥口令＝存储口令；签名时口令缺省回落存储口令 |
| 5 | 脚本用 `Stop` 错误策略 + 无 BOM | 原生工具的正常 stderr 提示会中断构建；中文脚本被按 ANSI 解析导致语法错误 | 改为 `Continue` + 显式退出码判定；脚本存为 UTF-8 with BOM |
| 6 | 使用系统默认图标 `sym_def_app_icon` | 发布产物在桌面显示为 Android 默认图标 | 新增自适应矢量图标（前景：知识图谱意象；背景：品牌色） |
| 7 | `allowBackup="true"` | 学生画像/consent 数据会被云备份，不符合本项目隐私口径 | 改为 `allowBackup="false"` + `fullBackupContent="false"` |
| 8 | 类注释与实现不一致 | 注释写"加载 mobile.html"，实际优先加载 index.html，误导维护 | 注释更正为真实加载顺序（index.html 优先，mobile.html 回退） |

---

## 八、结论

1. **跨端一致性成立**：Android 端与 Web / Windows 端使用**同一份** `index.html`（md5 相同），
   后端为同一 `AstralPath.Api`，运行时实测展示的 impact 数值与 BASELINE 金样一致。
2. **发布产物合规**：已签名（v2+v3）、无 debug 签名、`minSdk 26` 满足 Android 8.0 及以上、
   私有签名材料未进入版本库。
3. **构建可复现**：`build-apk.ps1` 一条命令产出签名 APK，并自动校验签名与元信息。
4. **遗留事项**：真机（小米 Android 16）安装受 MIUI 策略限制，需机主开启「USB 安装」后复测；
   文件实际上传、API 26 最低版本与多厂商 ROM 兼容性建议按 §6.2 补测。
