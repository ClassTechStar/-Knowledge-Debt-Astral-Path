namespace AstralPath.Core.Agent;

/// <summary>
/// 配置解析（P0 迁移，源：Astralpath getConfig，L6242-6265）。
/// 优先级：用户自定义密钥信封（KeyEnvelope + DEK）→ 内置演示密钥（DemoKeyProvider）→ 未配置（null）。
/// 自定义信封解密失败（DEK 丢失/换机器）时返回 Invalidate 信号，调用方应清理作废信封后回退内置密钥。
/// </summary>
public sealed record MimoConfigResolution(MimoConfig? Config, bool CustomEnvelopeInvalid);

public interface IKeyEnvelopeStore
{
    /// <summary>读取用户自定义密钥信封；无则返回 null。</summary>
    KeyEnvelope? Load();

    /// <summary>作废（删除）当前信封——DEK 与信封不匹配时调用，避免死循环。</summary>
    void Invalidate();
}

public static class MimoConfigResolver
{
    public static MimoConfigResolution Resolve(IKeyEnvelopeStore store, Func<byte[]?> getDek)
    {
        ArgumentNullException.ThrowIfNull(store);
        var env = store.Load();
        if (env != null)
        {
            var dek = getDek();
            if (dek != null)
            {
                var key = KeyEnvelopeCrypto.Open(env, dek);
                if (key != null)
                    return new MimoConfigResolution(
                        new MimoConfig(key, (env.Base ?? MimoConfig.DefaultBase).TrimEnd('/'), env.Model ?? MimoConfig.DefaultModel),
                        CustomEnvelopeInvalid: false);
            }
            // 密钥环不匹配：信封作废（源 L6252-6255 注释行为），回退内置演示密钥
            store.Invalidate();
            return new MimoConfigResolution(DemoKeyProvider.Resolve(), CustomEnvelopeInvalid: true);
        }
        return new MimoConfigResolution(DemoKeyProvider.Resolve(), CustomEnvelopeInvalid: false);
    }
}
