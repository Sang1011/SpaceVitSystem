using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;

namespace SVS.Infrastructure.AiService;

public sealed class HttpAiJobClient(HttpClient http) : IAiJobClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task SubmitAsync(AiSubmitRequest request, CancellationToken ct)
    {
        var body = new
        {
            job_id = request.JobId,
            mode = request.Mode.ToString().ToLowerInvariant(),
            language = request.Language,
            output_language = request.OutputLanguage,
            media_key = request.MediaKey,
            segments = request.Segments,
        };
        using var resp = await http.PostAsJsonAsync("/v1/jobs", body, Json, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task<AiJobStatus?> GetStatusAsync(string jobId, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"/v1/jobs/{jobId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AiJobStatus>(Json, ct);
    }

    public async Task<AiSegmentsPage?> GetSegmentsAsync(string jobId, int after, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"/v1/jobs/{jobId}/segments?after={after}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AiSegmentsPage>(Json, ct);
    }

    public async Task<AiJobResult?> GetResultAsync(string jobId, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"/v1/jobs/{jobId}/result", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AiJobResult>(Json, ct);
    }

    public async Task DeleteAsync(string jobId, CancellationToken ct)
    {
        using var resp = await http.DeleteAsync($"/v1/jobs/{jobId}", ct);
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
