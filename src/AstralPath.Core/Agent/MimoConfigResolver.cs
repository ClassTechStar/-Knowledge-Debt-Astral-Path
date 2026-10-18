namespace AstralPath.Core.Agent;

/// <summary>
/// 配置解析（P0 迁移，源：Astralpath getConfig，L6242-6265）。
/// 优先级：用户自定义密钥信封（KeyEnvelope + DEK）→ 内置演示密钥（DemoKeyProvider）→ 未配置（null）。
/// <para>
/// P5（2026-10-18）：信封失效不再「静默回退」——返回 <see cref="MimoConfigResolution.Issue"/> 与
/// <see cref="MimoConfigResolution.UserMessage"/>，让前端能提示「自定义密钥需重新配置」。
/// 三种失效原因分开报告：DEK 丢失（换浏览器）、命中吊销清单（运维判定泄露）、密文不可解。
/// </para>
/// </summary>
public sealed record MimoConfigResolution(
    MimoConfig? Config,
    bool CustomEnvelopeInvalid,
    EnvelopeIssue Issue = EnvelopeIssue.None,
    string? UserMessage = null);

public interface IKeyEnvelopeStore
{
    /// <summary>读取用户自定义密钥信封；无则返回 null。</summary>
    KeyEnvelope? Load();

    /// <summary>作废（删除）当前信封——DEK 与信封不匹配时调用，避免死循环。</summary>
    void Invalidate();
}

public static class MimoConfigResolver
{
    public static MimoConfigResolution Resolve(
        IKeyEnvelopeStore store,
        Func<byte[]?> getDek,
        IKeyRevocationList? revocation = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var env = store.Load();
        if (env is null)
            return new MimoConfigResolution(DemoKeyProvider.Resolve(), CustomEnvelopeInvalid: false);

        // ① 吊销优先：信封可能完好但仍被运维判废（泄露/离职/换供应商）
        if (revocation?.IsRevoked(env) == true)
        {
            store.Invalidate();
            Diagnostics.Diag.Note($"envelope revoked: keyId={env.KeyId}");
            return new MimoConfigResolution(
                DemoKeyProvider.Resolve(),
                CustomEnvelopeInvalid: true,
                Issue: EnvelopeIssue.Revoked,
                UserMessage: "自定义密钥已被吊销，已回退内置演示密钥。请重新输入 API Key。");
        }

        // ② DEK 缺失：换浏览器 / 清理过 IndexedDB / 换机器
        var dek = getDek();
        if (dek is null)
        {
            store.Invalidate();
            Diagnostics.Diag.Note($"envelope DEK missing: keyId={env.KeyId}");
            return new MimoConfigResolution(
                DemoKeyProvider.Resolve(),
                CustomEnvelopeInvalid: true,
                Issue: EnvelopeIssue.DekMissing,
                UserMessage: "自定义密钥的加密材料不在本机（可能换过浏览器或清理过存储），已回退内置演示密钥。请重新输入 API Key。");
        }

        // ③ 密文不可解
        var key = KeyEnvelopeCrypto.Open(env, dek);
        if (key is null)
        {
            store.Invalidate();
            return new MimoConfigResolution(
                DemoKeyProvider.Resolve(),
                CustomEnvelopeInvalid: true,
                Issue: EnvelopeIssue.Unreadable,
                UserMessage: "自定义密钥无法解密（密文损坏），已回退内置演示密钥。请重新输入 API Key。");
        }

        return new MimoConfigResolution(
            new MimoConfig(key, (env.Base ?? MimoConfig.DefaultBase).TrimEnd('/'), env.Model ?? MimoConfig.DefaultModel),
            CustomEnvelopeInvalid: false);
    }
}
