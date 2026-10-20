using System.Text.Json;

namespace TokenPulse.Core;

public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient client;
    public OllamaClient(HttpMessageHandler? handler = null)
    {
        // Stay on loopback, even if the system has a proxy or the endpoint returns a redirect.
        client = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(1.2) };
    }
    public async Task<OllamaResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetAsync("http://127.0.0.1:11434/api/ps", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var models = LogParser.Field(document.RootElement, "models");
            if (models.ValueKind != JsonValueKind.Array) return new([], "Ollama 返回了无效的模型列表");
            long Bytes(JsonElement item, string key) => LogParser.Field(item, key) is var value && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
            var result = models.EnumerateArray().Where(m => LogParser.Text(m, "name") is not null)
                .Select(m => new LocalModel(LogParser.Text(m, "name")!, Bytes(m, "size"), Bytes(m, "size_vram"))).ToArray();
            return new(result, result.Length == 0 ? "Ollama 已连接，暂无运行模型" : $"Ollama：{result.Length} 个运行模型");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new([], "Ollama 连接超时"); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { return new([], "Ollama 未连接或响应无效"); }
    }
    public void Dispose() => client.Dispose();
}
