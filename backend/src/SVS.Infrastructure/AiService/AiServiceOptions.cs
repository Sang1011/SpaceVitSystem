namespace SVS.Infrastructure.AiService;

public sealed class AiServiceOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public string ApiKey { get; set; } = string.Empty;

    public int PollIntervalSeconds { get; set; } = 3;
    public int RequestTimeoutSeconds { get; set; } = 30;
}
