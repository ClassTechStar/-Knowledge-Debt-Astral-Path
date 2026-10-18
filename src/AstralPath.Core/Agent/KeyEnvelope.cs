using System.Security.Cryptography;
using System.Text;

namespace AstralPath.Core.Agent;

/// <summary>
/// 密钥信封（P0 迁移，源：Astralpath __AP_MIMO__ 信封，L6242-6275）。
/// 明文密钥经 AES-256-GCM 加密后存储（信封），数据密钥（DEK）由宿主持久层保管且不可导出，
/// 信封与 DEK 分离，降低密钥泄露风险。桌面端 DEK 可用 DPAPI/Keychain，Web 端用 IndexedDB 不可导出 CryptoKey。
/// </summary>
/// <param name="CiphertextB64">密文（ciphertext ‖ tag，与 Web Crypto 格式一致）。</param>
/// <param name="IvB64">12 字节随机 IV。</param>
/// <param name="Base">网关 base URL（同样在信封内，不落明文）。</param>
/// <param name="Model">模型名（同样在信封内）。</param>
/// <param name="UpdatedAt">写入时间（Unix ms）。</param>
/// <param name="Version">
/// 信封格式版本（P5）：Seal 时写入 <see cref="KeyEnvelopeCrypto.CurrentVersion"/>。
/// 版本落后说明是旧格式，仍可解密；仅用于排障与后续迁移判断，不参与解密。
/// </param>
/// <param name="KeyId">
/// 信封指纹（P5）：密文的 SHA-256 前 8 字节 hex。用于**吊销清单**精确匹配——
/// 服务端/运维下发一个 keyId 黑名单即可让某个泄露信封作废，无需接触用户本机 DEK。
/// </param>
public sealed record KeyEnvelope(
    string CiphertextB64,
    string IvB64,
    string? Base,
    string? Model,
    long UpdatedAt,
    int Version = 1,
    string? KeyId = null);

/// <summary>信封失效原因（P5：让「静默回退」变成「可感知」）。</summary>
public enum EnvelopeIssue
{
    /// <summary>无问题（未配置自定义密钥，或解密成功）。</summary>
    None = 0,
    /// <summary>本机拿不到 DEK（换浏览器 / 清理过 IndexedDB / 换机器）。</summary>
    DekMissing = 1,
    /// <summary>信封命中吊销清单（运维判定泄露）。</summary>
    Revoked = 2,
    /// <summary>密文损坏或 DEK 不匹配，GCM 校验失败。</summary>
    Unreadable = 3,
}

public static class KeyEnvelopeCrypto
{
    /// <summary>当前信封格式版本（P5 起为 2：新增 keyId 指纹）。</summary>
    public const int CurrentVersion = 2;

    public static KeyEnvelope Seal(string plaintextKey, byte[] dek, string? baseUri, string? model)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plaintextKey ?? "");
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(dek, 16);
        gcm.Encrypt(iv, pt, ct, tag);
        // 密文 = ciphertext || tag（16 字节尾部），与 Web Crypto 密文格式一致
        var sealedBytes = new byte[ct.Length + tag.Length];
        Buffer.BlockCopy(ct, 0, sealedBytes, 0, ct.Length);
        Buffer.BlockCopy(tag, 0, sealedBytes, ct.Length, tag.Length);
        var ctB64 = Convert.ToBase64String(sealedBytes);
        return new KeyEnvelope(
            ctB64,
            Convert.ToBase64String(iv),
            baseUri,
            model,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CurrentVersion,
            Fingerprint(ctB64));
    }

    /// <summary>信封指纹：密文 SHA-256 前 8 字节的 hex（16 字符）。不含任何密钥材料。</summary>
    public static string Fingerprint(string ciphertextB64)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ciphertextB64 ?? ""));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// 轮换（P5）：用新 DEK 重新加密同一明文，生成**新信封**（版本升到当前值、指纹重算）。
    /// 旧信封随即失效——调用方应在换机器/怀疑泄露时用它，而不是原地改 IV。
    /// </summary>
    public static KeyEnvelope? Rotate(KeyEnvelope env, byte[] oldDek, byte[] newDek)
    {
        var key = Open(env, oldDek);
        if (key is null) return null; // 旧信封已不可解，无法轮换（调用方应让用户重配）
        return Seal(key, newDek, env.Base, env.Model);
    }

    public static string? Open(KeyEnvelope env, byte[] dek)
    {
        try
        {
            var sealedBytes = Convert.FromBase64String(env.CiphertextB64);
            if (sealedBytes.Length < 16) return null;
            var iv = Convert.FromBase64String(env.IvB64);
            var ctLen = sealedBytes.Length - 16;
            var ct = new byte[ctLen];
            var tag = new byte[16];
            Buffer.BlockCopy(sealedBytes, 0, ct, 0, ctLen);
            Buffer.BlockCopy(sealedBytes, ctLen, tag, 0, 16);
            var pt = new byte[ctLen];
            using var gcm = new AesGcm(dek, 16);
            gcm.Decrypt(iv, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch (Exception ex)
        {
            // 密钥环不匹配（换机器/清了 DEK）：信封作废，返回 null 触发调用方清理
            Diagnostics.Diag.Recover(ex, "envelope.open");
            return null;
        }
    }
}

/// <summary>信封吊销清单（P5）：只认 <see cref="KeyEnvelope.KeyId"/>，不需要接触用户 DEK。</summary>
public interface IKeyRevocationList
{
    bool IsRevoked(KeyEnvelope env);
}

/// <summary>内存吊销清单（测试与单机场景用；服务端可换成持久化实现）。</summary>
public sealed class InMemoryKeyRevocationList : IKeyRevocationList
{
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryKeyRevocationList(IEnumerable<string>? revokedKeyIds = null)
    {
        if (revokedKeyIds is null) return;
        foreach (var id in revokedKeyIds)
        {
            if (!string.IsNullOrWhiteSpace(id)) _ids.Add(id.Trim());
        }
    }

    public int Count => _ids.Count;

    public void Revoke(string keyId)
    {
        if (!string.IsNullOrWhiteSpace(keyId)) _ids.Add(keyId.Trim());
    }

    public bool IsRevoked(KeyEnvelope env)
    {
        if (env.KeyId is null) return false; // 旧信封无指纹，吊销清单管不到（可先轮换再吊销）
        return _ids.Contains(env.KeyId);
    }
}
