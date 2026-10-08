using System.Net;

namespace SVS.Application.Common.Exceptions;

public sealed class MediaTooLargeException(long maxBytes)
    : Exception($"File vượt quá giới hạn {maxBytes / (1024 * 1024)} MB");

public sealed class UnsupportedMediaTypeException(string extension)
    : Exception($"Định dạng file không được hỗ trợ: '{extension}'");

/// <summary>AI service trả mã lỗi HTTP không thành công.</summary>
public sealed class AiServiceException(HttpStatusCode statusCode, string body)
    : Exception($"AI service trả {(int)statusCode}: {body}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Lỗi do yêu cầu sai (4xx, trừ 408/429): gửi lại cũng không khá hơn.</summary>
    public bool IsPermanent => (int)StatusCode is >= 400 and < 500 and not 408 and not 429;
}
