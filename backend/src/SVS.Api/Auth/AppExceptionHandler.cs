using Microsoft.AspNetCore.Diagnostics;
using SVS.Application.Common.Exceptions;

namespace SVS.Api.Auth;

/// <summary>Đổi AppException thành JSON {error, message} với đúng mã HTTP; các lỗi khác để pipeline xử lý như mặc định.</summary>
public sealed class AppExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not AppException app) return false;
        context.Response.StatusCode = app.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = app.Code, message = app.Message }, ct);
        return true;
    }
}
