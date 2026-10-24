using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;

namespace SVS.Infrastructure.AiService;

/// <summary>Gọi /v1/sessions của AI service (ghi âm trực tiếp). JSON dùng snake_case theo hợp đồng.</summary>
public sealed class HttpLiveAiClient(HttpClient http) : ILiveAiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task CreateSessionAsync(string sessionId, string language, string outputLanguage, double chunkSec, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("/v1/sessions", new
        {
            session_id = sessionId, language, output_language = outputLanguage, chunk_sec = chunkSec,
        }, Json, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task PutChunkAsync(string sessionId, int seq, string mediaKey, CancellationToken ct)
    {
        using var resp = await http.PutAsJsonAsync($"/v1/sessions/{sessionId}/chunks/{seq}", new { media_key = mediaKey }, Json, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task<AiLiveStatus?> GetStatusAsync(string sessionId, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"/v1/sessions/{sessionId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AiLiveStatus>(Json, ct);
    }

    public async Task<AiSegmentsPage?> GetSegmentsAsync(string sessionId, int after, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"/v1/sessions/{sessionId}/segments?after={after}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AiSegmentsPage>(Json, ct);
    }

    public async Task FinalizeAsync(string sessionId, int totalChunks, string mode, double waitMissingSec, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync($"/v1/sessions/{sessionId}/finalize", new
        {
            total_chunks = totalChunks, mode, wait_missing_sec = waitMissingSec,
        }, Json, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task<AiLiveResult?> GetResultAsync(string sessionId, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"/v1/sessions/{sessionId}/result", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AiLiveResult>(Json, ct);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        using var resp = await http.DeleteAsync($"/v1/sessions/{sessionId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(resp, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync(ct);
        throw new AiServiceException(resp.StatusCode, text.Length > 500 ? text[..500] : text);
    }
}
