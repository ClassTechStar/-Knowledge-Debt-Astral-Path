using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AstralPath.Core.Agent;

/// <summary>
/// 内置演示密钥（P0 迁移，源：Astralpath __AP_EMBED__ + getBuiltin，L6201/L6223-6241；T12 设计）。
/// 密钥以 AES-256-GCM 密文形态随代码分发、运行时解密，明文不落源码；
/// 用户另行配置自定义密钥时自定义优先。解密失败（环境不支持/密文损坏）返回 null，整体降级为未配置。
/// </summary>
public static class DemoKeyProvider
{
    // AES-256-GCM 密文信封（与前端 window.__AP_EMBED__ 同源；明文密钥不出现于任何文件）
    private const string P1 = "EBg9FIJi/bCyafZWyXyf8a";
    private const string P2 = "EWFpxaUt6rq6VYcPm7kIE=";
    private const string IV = "geJIbHcrK3OHQNtO";
    private const string CT = "G+vZ14c3nSwsIM/3jAPxxw75VlbbQ4URIyq9cugKDb7ToHcC+gPjOn4iM7EXyVybZhyTpr6FMn4IwP54S7YtCggxHHwQhKgiGhKqmUbMt01+0a9v4OF4L0ZlDKb/GWo3yAG70tFA3Fk9a7D9hYwpUicQbsiJjlNq3YX9E5OUWFKTanzrnjHnnnOJnhU=";

    private static readonly object Gate = new();
    private static MimoConfig? _cached;
    private static bool _resolved;

    /// <summary>解析内置演示密钥（幂等缓存）；失败返回 null。</summary>
    public static MimoConfig? Resolve()
    {
        lock (Gate)
        {
            if (_resolved) return _cached;
            _cached = Decrypt();
            _resolved = true;
            return _cached;
        }
    }

    private static MimoConfig? Decrypt()
    {
        try
        {
            var key = Convert.FromBase64String(P1 + P2); // 32 字节 AES-256 密钥材料
            var iv = Convert.FromBase64String(IV);
            var sealedBytes = Convert.FromBase64String(CT);
            if (sealedBytes.Length < 16) return null;
            var ctLen = sealedBytes.Length - 16;
            var ct = new byte[ctLen];
            var tag = new byte[16];
            Buffer.BlockCopy(sealedBytes, 0, ct, 0, ctLen);
            Buffer.BlockCopy(sealedBytes, ctLen, tag, 0, 16);
            var pt = new byte[ctLen];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(iv, ct, tag, pt);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(pt));
            var root = doc.RootElement;
            var k = root.TryGetProperty("key", out var kk) ? kk.GetString() : null;
            if (string.IsNullOrWhiteSpace(k)) return null;
            var b = root.TryGetProperty("base", out var bb) && bb.ValueKind == JsonValueKind.String
                ? bb.GetString()! : MimoConfig.DefaultBase;
            var m = root.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String
                ? mm.GetString()! : MimoConfig.DefaultModel;
            return new MimoConfig(k, b.TrimEnd('/'), m);
        }
        catch (Exception)
        {
            return null; // 解密失败：静默降级为未配置，不影响主流程
        }
    }
}
