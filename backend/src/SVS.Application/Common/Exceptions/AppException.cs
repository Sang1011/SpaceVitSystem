namespace SVS.Application.Common.Exceptions;

/// <summary>Lỗi nghiệp vụ có mã HTTP và mã lỗi ổn định cho client. Được đổi thành phản hồi JSON ở một nơi duy nhất (tầng Api).</summary>
public class AppException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    public static AppException Validation(string message) => new(400, "VALIDATION", message);
    public static AppException Unauthorized(string code, string message) => new(401, code, message);
    public static AppException Forbidden(string message = "Bạn không đủ quyền thực hiện thao tác này") => new(403, "FORBIDDEN", message);
    public static AppException NotFound(string code, string message) => new(404, code, message);
    public static AppException Conflict(string code, string message) => new(409, code, message);
}
