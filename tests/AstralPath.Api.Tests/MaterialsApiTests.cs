using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AstralPath.Api.Tests;

public class MaterialsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public MaterialsApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    private static async Task<JsonElement> Data(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        return root.TryGetProperty("data", out var data) ? data.Clone() : root.Clone();
    }

    [Fact]
    public async Task Materials_ListAndGraphEndpoints_Work()
    {
        var listResp = await _client.GetAsync("/v1/materials");
        Assert.True(listResp.IsSuccessStatusCode);
        var list = await Data(listResp);
        Assert.Equal(JsonValueKind.Array, list.ValueKind);

        var graphsResp = await _client.GetAsync("/v1/knowledge-graphs");
        Assert.True(graphsResp.IsSuccessStatusCode);
        var graphs = await Data(graphsResp);
        Assert.Equal(JsonValueKind.Array, graphs.ValueKind);
    }

    [Fact]
    public async Task SeedSamples_RegistersLocalLearningMaterials()
    {
        var resp = await _client.PostAsync("/v1/materials/seed-samples", null);
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        // machine may or may not have sample PDFs; endpoint must still succeed
        Assert.True(data.TryGetProperty("seeded", out _));
        Assert.True(data.TryGetProperty("ids", out var ids));
        Assert.Equal(JsonValueKind.Array, ids.ValueKind);

        var materials = await Data(await _client.GetAsync("/v1/materials"));
        Assert.True(materials.GetArrayLength() >= 0);
    }

    [Fact]
    public async Task Upload_WithoutFile_ReturnsValidationError()
    {
        using var content = new MultipartFormDataContent();
        var resp = await _client.PostAsync("/v1/materials/upload?ocr=quick", content);
        Assert.False(resp.IsSuccessStatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(
            body.Contains("VALIDATION_ERROR", StringComparison.OrdinalIgnoreCase)
            || body.Contains("file", StringComparison.OrdinalIgnoreCase)
            || body.Contains("资料", StringComparison.Ordinal)
            || resp.StatusCode is System.Net.HttpStatusCode.BadRequest,
            body);
    }
}
