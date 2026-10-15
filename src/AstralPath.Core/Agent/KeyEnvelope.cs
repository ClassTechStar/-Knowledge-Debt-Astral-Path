using System.Security.Cryptography;
using System.Text;

namespace AstralPath.Core.Agent;

/// <summary>
/// 密钥信封加密（P0 迁移，源：Astralpath __AP_MIMO__ 信封，L6242-6275）。
/// 明文密钥经 AES-256-GCM 加密后存储（信封），数据密钥（DEK）由宿主持久层保管且不可导出，
/// 信封与 DEK 分离，降低密钥泄露风险。桌面端 DEK 可用 DPAPI/Keychain，Web 端用 IndexedDB 不可导出 CryptoKey。
/// </summary>
public sealed record KeyEnvelope(string CiphertextB64, string IvB64, string? Base, string? Model, long UpdatedAt);

public static class KeyEnvelopeCrypto
{
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
        return new KeyEnvelope(
            Convert.ToBase64String(sealedBytes),
            Convert.ToBase64String(iv),
            baseUri,
            model,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
        catch (Exception)
        {
            // 密钥环不匹配（换机器/清了 DEK）：信封作废，返回 null 触发调用方清理
            return null;
        }
    }
}
