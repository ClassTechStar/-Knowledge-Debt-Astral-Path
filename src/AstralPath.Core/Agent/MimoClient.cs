using System.Net.Http.Headers;
using AstralPath.Core.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AstralPath.Core.Agent;

/// <summary>模型补全消息（OpenAI 兼容 messages 数组元素）。</summary>
public sealed record MimoMessage(string Role, string Content);

/// <summary>补全调用可选项（源：Astralpath complete opts）。</summary>
public sealed record MimoOptions(
    int MaxTokens = 1600,
    double? Temperature = null,
    int TimeoutMs = 60000,
    Action<string, bool>? OnDelta = null);

/// <summary>补全结果（源：Astralpath complete 返回 {text, model}）。</summary>
public sealed record MimoResult(string Text, string Model);

/// <summary>原始 HTTP 响应抽象——SSE 与非流式统一入口，便于 golden 测试注入 mock 网关。</summary>
public sealed record MimoRawResponse(int StatusCode, string ContentType, Stream Body)
{
    public static MimoRawResponse Json(int statusCode, string json, string model = "")
        => new(statusCode, "application/json", new MemoryStream(Encoding.UTF8.GetBytes(json)));

    public static MimoRawResponse Sse(string dataEvents)
        => new(200, "text/event-stream", new MemoryStream(Encoding.UTF8.GetBytes(dataEvents)));
}

/// <summary>HTTP 传输委托：发一条补全请求，返回原始响应流。</summary>
public delegate Task<MimoRawResponse> MimoTransport(MimoRequest request, CancellationToken ct);

/// <summary>补全请求体（POST {base}/chat/completions）。</summary>
public sealed record MimoRequest(string Base, string Key, string Model, IReadOnlyList<MimoMessage> Messages, int MaxTokens, double? Temperature, bool Stream);

/// <summary>
/// 通用模型调用层（P0 迁移，源：Astralpath __AP_MIMO__.complete，L6318-6376）。
/// 要点：
/// 1) 并发闸：最多 2 路并行，其余 FIFO 排队等待（T13：替代单飞拒绝，后台任务与用户操作互不阻塞）；
/// 2) SSE 流式优先 + 非流式兜底；[DONE] 可能不带换行尾、网关发完 [DONE] 不关流，须显式终止（P2-1 修复①②）；
/// 3) 推理型模型 reasoning_content 流式收集（onDelta 第二参 thinking=true），正文为空时以思维链兜底（T13）；
/// 4) 正文仍为空 → 抛错（模型内容类故障，不触发冷却，见 FaultCooldown）。
/// </summary>
public sealed class MimoClient
{
    private readonly MimoTransport _transport;
    private readonly SemaphoreSlim _gate; // 并发闸（≤2 路并行）

    public MimoClient(MimoTransport transport, int maxConcurrency = 2)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async Task<MimoResult> CompleteAsync(MimoConfig cfg, IReadOnlyList<MimoMessage> messages, MimoOptions? options = null, CancellationToken ct = default)
    {
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Key))
            throw new InvalidOperationException("MiMo 未配置");
        options ??= new MimoOptions();
        var onDelta = options.OnDelta;

        await _gate.WaitAsync(ct).ConfigureAwait(false); // T13：排队而非拒绝
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.TimeoutMs <= 0 ? 60000 : options.TimeoutMs);
            var request = new MimoRequest(cfg.Base, cfg.Key, cfg.Model, messages, options.MaxTokens, options.Temperature, Stream: true);
            var resp = await _transport(request, timeoutCts.Token).ConfigureAwait(false);
            if (resp.StatusCode < 200 || resp.StatusCode >= 300)
            {
                string errText;
                using (var sr = new StreamReader(resp.Body, Encoding.UTF8))
                    errText = await sr.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                throw new HttpRequestException($"HTTP {resp.StatusCode}{(string.IsNullOrEmpty(errText) ? "" : " " + Limit(errText, 140))}");
            }

            var contentType = resp.ContentType ?? "";
            var isSse = contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase);
            var outText = new StringBuilder();
            var reasoning = new StringBuilder();
            var model = cfg.Model;

            if (isSse)
            {
                // SSE 解析：逐行取 data: 载荷；[DONE] 裸尾也要终止（源 L6338-6359 三处修复）
                using var reader = new StreamReader(resp.Body, Encoding.UTF8);
                var buf = new StringBuilder();
                var sseDone = false;
                while (!sseDone)
                {
                    var ch = reader.Read();
                    if (ch < 0) break;
                    if (ch == '\n')
                    {
                        var line = buf.ToString().Trim();
                        buf.Clear();
                        if (line.Length == 0) continue;
                        if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                        var payload = line.Substring(5).Trim();
                        if (payload == "[DONE]") { sseDone = true; break; }
                        TryConsumeChunk(payload, outText, reasoning, ref model, onDelta);
                    }
                    else
                    {
                        buf.Append((char)ch);
                        // [DONE] 可能不带换行尾：缓冲探测
                        if (buf.ToString().Contains("[DONE]", StringComparison.Ordinal))
                        {
                            var tail = buf.ToString();
                            var doneIdx = tail.IndexOf("[DONE]", StringComparison.Ordinal);
                            var before = tail.Substring(0, doneIdx).Trim();
                            if (before.StartsWith("data:", StringComparison.Ordinal))
                                TryConsumeChunk(before.Substring(5).Trim(), outText, reasoning, ref model, onDelta);
                            sseDone = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                string bodyText;
                using (var sr = new StreamReader(resp.Body, Encoding.UTF8))
                    bodyText = await sr.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                try
                {
                    using var doc = JsonDocument.Parse(bodyText);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                        model = m.GetString() ?? model;
                    var msg = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                        ? choices[0].TryGetProperty("message", out var mm) ? mm : default
                        : default;
                    if (msg.ValueKind == JsonValueKind.Object)
                    {
                        if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            outText.Append(c.GetString());
                        if (outText.Length == 0 && msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String
                            && (rc.GetString() ?? "").Trim().Length >= 4)
                            outText.Append(rc.GetString()); // 推理型模型兜底
                    }
                    if (outText.Length > 0 && onDelta != null) SafeDelta(onDelta, outText.ToString(), false);
                }
                catch (JsonException)
                {
                    // 非 JSON 响应：按纯文本正文处理
                    outText.Append(bodyText);
                    if (outText.Length > 0 && onDelta != null) SafeDelta(onDelta, outText.ToString(), false);
                }
            }

            // T13：流式推理兜底——正文为空但思维链可用
            if (outText.Length == 0 && reasoning.Length >= 4)
            {
                outText.Append(reasoning.ToString().Trim());
                if (onDelta != null) SafeDelta(onDelta, outText.ToString(), false);
            }
            var text = outText.ToString().Trim();
            if (text.Length == 0)
                throw new InvalidOperationException("模型返回空内容（流式与非流式均无文本）");
            return new MimoResult(text, model);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryConsumeChunk(string payload, StringBuilder outText, StringBuilder reasoning, ref string model, Action<string, bool>? onDelta)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                model = m.GetString() ?? model;
            var d = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                ? (choices[0].TryGetProperty("delta", out var delta) ? delta
                 : choices[0].TryGetProperty("message", out var msg) ? msg : default)
                : default;
            if (d.ValueKind != JsonValueKind.Object) return;
            if (d.TryGetProperty("reasoning_content", out var rp) && rp.ValueKind == JsonValueKind.String && rp.GetString()!.Length > 0)
            {
                reasoning.Append(rp.GetString());
                if (onDelta != null) SafeDelta(onDelta, reasoning.ToString(), true); // T14：思维链流式回调
            }
            if (d.TryGetProperty("content", out var piece) && piece.ValueKind == JsonValueKind.String && piece.GetString()!.Length > 0)
            {
                outText.Append(piece.GetString());
                if (onDelta != null) SafeDelta(onDelta, outText.ToString(), false);
            }
        }
        catch (JsonException ex)
        {
            Diag.Recover(ex, "MimoClient");
        }
    }

    private static void SafeDelta(Action<string, bool> onDelta, string text, bool thinking)
    {
        try { onDelta(text, thinking); } catch (Exception ex)
        {
            Diag.Recover(ex, "MimoClient");
        }
    }

    private static string Limit(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
}

/// <summary>模型配置（密钥信封解密产物 + 网关参数）。</summary>
public sealed record MimoConfig(string Key, string Base, string Model)
{
    public const string DefaultBase = "https://api.xiaomimimo.com/v1";
    public const string DefaultModel = "mimo-v2.6-pro";
}
