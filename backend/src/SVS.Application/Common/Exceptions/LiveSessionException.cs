namespace SVS.Application.Common.Exceptions;

/// <summary>Lỗi nghiệp vụ của phiên live, kèm mã HTTP và mã lỗi ổn định cho client.</summary>
public sealed class LiveSessionException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class InvalidChunkException(string message) : Exception(message);
