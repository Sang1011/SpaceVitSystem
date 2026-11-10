namespace SVS.Application.Common.Exceptions;

/// <summary>Lỗi nghiệp vụ của phiên live, kèm mã HTTP và mã lỗi ổn định cho client.</summary>
public sealed class LiveSessionException(int statusCode, string code, string message) : AppException(statusCode, code, message);

public sealed class InvalidChunkException(string message) : Exception(message);
