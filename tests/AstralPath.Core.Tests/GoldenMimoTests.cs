using System.Text;
using System.Text.Json;
using AstralPath.Core.Agent;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// P0 迁移验证：AI 调用层 golden 契约（eval/golden/mimo.json）。
/// 覆盖 SSE 流式聚合、[DONE] 裸尾/不关流、思维链收集与兜底、非流式兜底、
/// HTTP 错误、空内容、JSON 容错解析、冷却分类。
/// </summary>
public class GoldenMimoTests
{
    private static JsonDocument Load()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_GOLDEN_DIR");
        var dir = !string.IsNullOrWhiteSpace(env) && Directory.Exists(env)
            ? env
            : FindGoldenDir();
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "mimo.json")));
    }

    private static string FindGoldenDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var full = Path.Combine(dir, "eval", "golden");
            if (Directory.Exists(full)) return Path.GetFullPath(full);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        }
        throw new DirectoryNotFoundException("eval/golden not found");
    }

    private static MimoClient ClientFromCase(JsonElement c)
    {
        var body = c.GetProperty("response").GetString() ?? "";
        var contentType = c.GetProperty("contentType").GetString() ?? "";
        var status = c.GetProperty("status").GetInt32();
        return new MimoClient((req, ct) => Task.FromResult(
            new MimoRawResponse(status, contentType, new MemoryStream(Encoding.UTF8.GetBytes(body)))));
    }

    private static readonly MimoConfig Cfg = new("test-key", "https://mock.local/v1", "mimo-v2.6-pro");
    private static readonly IReadOnlyList<MimoMessage> Msgs = new[] { new MimoMessage("user", "hi") };

    [Fact]
    public async Task Golden_Mimo_SseAndJson_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var expect = c.GetProperty("expect");
            var client = ClientFromCase(c);
            string? finalThinking = null, finalText = null;

            if (expect.TryGetProperty("throws", out var t) && t.GetBoolean())
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                    client.CompleteAsync(Cfg, Msgs, new MimoOptions(OnDelta: (s, thinking) =>
                    {
                        if (thinking) finalThinking = s; else finalText = s;
                    })));
                Assert.Contains(expect.GetProperty("messageContains").GetString()!, ex.Message);
                continue;
            }

            var result = await client.CompleteAsync(Cfg, Msgs, new MimoOptions(OnDelta: (s, thinking) =>
            {
                if (thinking) finalThinking = s; else finalText = s;
            }));
            Assert.Equal(expect.GetProperty("text").GetString(), result.Text);
            if (expect.TryGetProperty("model", out var m))
                Assert.Equal(m.GetString(), result.Model);
            if (expect.TryGetProperty("reasoning", out var r))
                Assert.Equal(r.GetString(), finalThinking);
        }
    }

    [Fact]
    public void Golden_Mimo_JsonLoose_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("jsonLooseCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var expect = c.GetProperty("expect");
            var parsed = JsonLoose.Parse(c.GetProperty("input").GetString());
            if (!expect.GetProperty("ok").GetBoolean())
            {
                Assert.Null(parsed);
                continue;
            }
            Assert.True(parsed != null, $"{id}: expected parse ok but got null");
            var root = parsed!.Value;
            if (expect.TryGetProperty("isArray", out var ia) && ia.GetBoolean())
            {
                Assert.Equal(JsonValueKind.Array, root.ValueKind);
                Assert.Equal(expect.GetProperty("len").GetInt32(), root.GetArrayLength());
            }
            else
            {
                Assert.Equal(JsonValueKind.Object, root.ValueKind);
                if (expect.TryGetProperty("a", out var a))
                    Assert.Equal(a.GetInt32(), root.GetProperty("a").GetInt32());
                if (expect.TryGetProperty("b", out var b))
                    Assert.Equal(b.GetString(), root.GetProperty("b").GetString());
            }
        }
    }

    [Fact]
    public void Golden_Mimo_CooldownClassification_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("cooldownCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var msg = c.GetProperty("message").GetString()!;
            var expect = c.GetProperty("expectTransport").GetBoolean();
            Assert.True(FaultCooldown.IsTransportMessage(msg) == expect,
                $"{id}: '{msg}' expectTransport={expect}");
        }
    }

    [Fact]
    public async Task MimoClient_ConcurrencyGate_QueuesInsteadOfRejecting()
    {
        // T13：并发闸 ≤2 路并行 + FIFO 排队；3 路并发全部成功、无相互拒绝
        var running = 0; var maxParallel = 0;
        var client = new MimoClient(async (req, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedExtensionsMax(ref maxParallel, now);
            await Task.Delay(80, ct);
            Interlocked.Decrement(ref running);
            return MimoRawResponse.Json(200, "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        }, maxConcurrency: 2);

        var tasks = Enumerable.Range(0, 3).Select(_ => client.CompleteAsync(Cfg, Msgs)).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal("ok", r.Text));
        Assert.True(maxParallel <= 2, $"maxParallel={maxParallel} exceeds gate 2");
    }

    [Fact]
    public async Task MimoClient_ReasoningFallback_WhenContentEmpty()
    {
        // T13：正文为空但思维链可用 → 以思维链为正文
        var client = new MimoClient((req, ct) => Task.FromResult(MimoRawResponse.Sse(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"只有推理\"}}]}\ndata: [DONE]\n")));
        var r = await client.CompleteAsync(Cfg, Msgs);
        Assert.Equal("只有推理", r.Text);
    }

    private static void InterlockedExtensionsMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) break;
        }
    }

    [Fact]
    public void KeyEnvelope_SealOpen_Roundtrip()
    {
        var dek = new byte[32];
        Random.Shared.NextBytes(dek);
        var env = KeyEnvelopeCrypto.Seal("sk-test-123456", dek, "https://api.example.com/v1", "mimo-v2.6-pro");
        Assert.Equal("sk-test-123456", KeyEnvelopeCrypto.Open(env, dek));
        // 错误 DEK → null（信封作废路径）
        var wrongDek = new byte[32];
        Random.Shared.NextBytes(wrongDek);
        Assert.Null(KeyEnvelopeCrypto.Open(env, wrongDek));
    }

    [Fact]
    public void DemoKeyProvider_ResolvesWithoutThrowing()
    {
        // 内置演示密钥：解密成功返回配置，失败返回 null（降级为未配置）——两者皆可，不得抛异常
        var cfg = DemoKeyProvider.Resolve();
        if (cfg != null)
        {
            Assert.False(string.IsNullOrWhiteSpace(cfg.Key));
            Assert.False(string.IsNullOrWhiteSpace(cfg.Base));
            Assert.False(string.IsNullOrWhiteSpace(cfg.Model));
        }
    }

    [Fact]
    public void MimoConfigResolver_CustomEnvelopeTakesPrecedence_ThenInvalidates()
    {
        var dek = new byte[32];
        Random.Shared.NextBytes(dek);
        var env = KeyEnvelopeCrypto.Seal("sk-custom-key-999", dek, "https://custom.example.com/v1", "custom-model");
        var store = new FakeStore { Envelope = env };
        var resolution = MimoConfigResolver.Resolve(store, () => dek);
        Assert.NotNull(resolution.Config);
        Assert.Equal("sk-custom-key-999", resolution.Config!.Key);
        Assert.Equal("https://custom.example.com/v1", resolution.Config.Base);
        Assert.False(resolution.CustomEnvelopeInvalid);

        // DEK 丢失 → 信封作废 + 回退内置（可能为 null）
        var resolution2 = MimoConfigResolver.Resolve(store, () => null);
        Assert.True(resolution2.CustomEnvelopeInvalid);
        Assert.True(store.Invalidated);
    }

    private sealed class FakeStore : IKeyEnvelopeStore
    {
        public KeyEnvelope? Envelope;
        public bool Invalidated;
        public KeyEnvelope? Load() => Envelope;
        public void Invalidate() { Invalidated = true; Envelope = null; }
    }
}
