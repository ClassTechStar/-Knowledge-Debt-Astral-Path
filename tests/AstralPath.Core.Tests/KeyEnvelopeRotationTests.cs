using AstralPath.Core.Agent;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// P5 密钥信封三态验证：Seal / Open / Rotate / Invalidate（吊销与失效原因可感知）。
/// 不变量：信封失效时**必须**回退内置演示密钥且 <see cref="MimoConfigResolution.Issue"/> 如实报告，
/// 不再静默回退——这是 P5「失效可感知」的核心断言。
/// </summary>
public class KeyEnvelopeRotationTests
{
    private static byte[] NewDek()
    {
        var dek = new byte[32];
        Random.Shared.NextBytes(dek);
        return dek;
    }

    [Fact]
    public void Seal_WritesVersionAndFingerprint()
    {
        var dek = NewDek();
        var env = KeyEnvelopeCrypto.Seal("sk-abc-123", dek, "https://api.example.com/v1", "mimo-v2.6-pro");

        Assert.Equal(KeyEnvelopeCrypto.CurrentVersion, env.Version);
        Assert.NotNull(env.KeyId);
        Assert.Equal(16, env.KeyId!.Length); // SHA-256 前 8 字节 = 16 hex
        Assert.Equal(KeyEnvelopeCrypto.Fingerprint(env.CiphertextB64), env.KeyId);
        // 指纹只由密文决定，不含明文密钥材料
        Assert.DoesNotContain("abc", env.KeyId);
    }

    [Fact]
    public void Rotate_NewDekDecrypts_OldDekDoesNot()
    {
        var oldDek = NewDek();
        var newDek = NewDek();
        var env = KeyEnvelopeCrypto.Seal("sk-rotate-me", oldDek, null, null);

        var rotated = KeyEnvelopeCrypto.Rotate(env, oldDek, newDek);
        Assert.NotNull(rotated);
        Assert.Equal("sk-rotate-me", KeyEnvelopeCrypto.Open(rotated!, newDek));
        Assert.Null(KeyEnvelopeCrypto.Open(rotated!, oldDek));
        Assert.NotEqual(env.KeyId, rotated!.KeyId); // 轮换后指纹必须变（旧信封可被精确吊销）
    }

    [Fact]
    public void Rotate_ReturnsNull_WhenOldEnvelopeUnreadable()
    {
        var oldDek = NewDek();
        var env = KeyEnvelopeCrypto.Seal("sk-x", oldDek, null, null);
        Assert.Null(KeyEnvelopeCrypto.Rotate(env, NewDek(), NewDek()));
    }

    [Fact]
    public void Resolver_RevokedEnvelope_FallsBackAndReportsRevoked()
    {
        var dek = NewDek();
        var env = KeyEnvelopeCrypto.Seal("sk-leaked", dek, null, null);
        var store = new FakeStore(env);
        var revoked = new InMemoryKeyRevocationList(new[] { env.KeyId! });

        var r = MimoConfigResolver.Resolve(store, () => dek, revoked);

        Assert.True(r.CustomEnvelopeInvalid);
        Assert.Equal(EnvelopeIssue.Revoked, r.Issue);
        Assert.False(string.IsNullOrWhiteSpace(r.UserMessage));
        Assert.True(store.Invalidated);
        Assert.NotEqual("sk-leaked", r.Config?.Key); // 不得回吐被吊销的密钥
    }

    [Fact]
    public void Resolver_DekMissing_ReportsDekMissing()
    {
        var dek = NewDek();
        var store = new FakeStore(KeyEnvelopeCrypto.Seal("sk-a", dek, null, null));

        var r = MimoConfigResolver.Resolve(store, () => null);

        Assert.True(r.CustomEnvelopeInvalid);
        Assert.Equal(EnvelopeIssue.DekMissing, r.Issue);
        Assert.True(store.Invalidated);
    }

    [Fact]
    public void Resolver_TamperedCiphertext_ReportsUnreadable()
    {
        var dek = NewDek();
        var env = KeyEnvelopeCrypto.Seal("sk-a", dek, null, null);
        var tampered = env with { CiphertextB64 = env.CiphertextB64[..^4] + "AAAA" };
        var store = new FakeStore(tampered);

        var r = MimoConfigResolver.Resolve(store, () => dek);

        Assert.True(r.CustomEnvelopeInvalid);
        Assert.Equal(EnvelopeIssue.Unreadable, r.Issue);
        Assert.True(store.Invalidated);
    }

    [Fact]
    public void Resolver_LegacyEnvelopeWithoutKeyId_StillResolves()
    {
        // 旧信封（v1，无 keyId）：吊销清单管不到，但解密必须照旧成功——零迁移
        var dek = NewDek();
        var env = KeyEnvelopeCrypto.Seal("sk-legacy", dek, "https://legacy/v1", "legacy-model");
        var legacy = new KeyEnvelope(env.CiphertextB64, env.IvB64, env.Base, env.Model, env.UpdatedAt);
        var store = new FakeStore(legacy);

        var r = MimoConfigResolver.Resolve(store, () => dek, new InMemoryKeyRevocationList(new[] { "deadbeefdeadbeef" }));

        Assert.False(r.CustomEnvelopeInvalid);
        Assert.Equal(EnvelopeIssue.None, r.Issue);
        Assert.Equal("sk-legacy", r.Config!.Key);
        Assert.Equal("https://legacy/v1", r.Config.Base);
    }

    [Fact]
    public void Resolver_NoEnvelope_UsesBuiltinWithoutIssue()
    {
        var store = new FakeStore(null);
        var r = MimoConfigResolver.Resolve(store, () => null);
        Assert.False(r.CustomEnvelopeInvalid);
        Assert.Equal(EnvelopeIssue.None, r.Issue);
        Assert.False(store.Invalidated);
    }

    private sealed class FakeStore(KeyEnvelope? envelope) : IKeyEnvelopeStore
    {
        public KeyEnvelope? Envelope { get; private set; } = envelope;
        public bool Invalidated { get; private set; }

        public KeyEnvelope? Load() => Envelope;

        public void Invalidate()
        {
            Invalidated = true;
            Envelope = null;
        }
    }
}
