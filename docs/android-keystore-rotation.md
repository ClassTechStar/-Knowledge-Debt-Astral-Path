# P3-16 Android 发布密钥轮换（2026-09-24）

## 状态：已完成

| 项 | 旧 | 新 |
|----|----|----|
| Keystore | `src/AstralPath.Native/astralpath-release.keystore`（曾入库） | `~/.android/astralpath-signing/astralpath-release-2026.keystore`（库外） |
| 口令 | 明文 `astralpath2026`（已废止） | 32 位随机，仅存 `signing.env`（gitignore） |
| 算法 | — | RSA 2048 / SHA384withRSA / 有效期 30 年 |
| Alias | `astralpath` | `astralpath`（保持） |
| 指纹 | — | SHA-256 `2F4FE71222CB3E88DF52ED4B5EC8BCA88B961B70493ACD0D84F25C4717EB7C95` |

## 使用方法

```powershell
# 构建前加载环境变量
. "$env:USERPROFILE\.android\astralpath-signing\signing.env"
# 或手动
$env:ASTRALPATH_KEYSTORE="$env:USERPROFILE\.android\astralpath-signing\astralpath-release-2026.keystore"
$env:ASTRALPATH_STORE_PASSWORD="…见 signing.env…"
$env:ASTRALPATH_KEY_PASSWORD="…同上…"
```

Gradle 默认路径已指向新 keystore；未设 `ASTRALPATH_KEYSTORE` 时用 `user.home` 路径。

## 安全约束
- `signing.env` 与 `*.keystore` 均在 `.gitignore`
- 旧 keystore 已 `git rm --cached`，不再跟踪
- **新签名与旧包签名不同**：已上架/已装的 2.2.0 需卸载后安装，或保留旧钥做升级签名（若需兼容请用旧钥签升级包）

## 再次轮换
```powershell
keytool -genkeypair -v -keystore "$env:USERPROFILE\.android\astralpath-signing\astralpath-release-2026.keystore" `
  -alias astralpath -keyalg RSA -keysize 2048 -validity 10950 `
  -storepass <new> -keypass <new> -dname "CN=AstralPath, OU=Mobile, O=ClassTechStar, C=CN"
```
