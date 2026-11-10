using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using SVS.Api.Auth;
using SVS.Api.BackgroundServices;
using SVS.Api.Endpoints;
using SVS.Application;
using SVS.Application.Auth;
using SVS.Application.Common.Interfaces;
using SVS.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");

// ---- Xác thực: JWT ngắn hạn ký HS256. Khóa là bí mật nên bắt buộc đặt ngoài file cấu hình và phải đủ dài.
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
if (Encoding.UTF8.GetByteCount(authOptions.Jwt.Key) < 32)
    throw new InvalidOperationException(
        "Thiếu hoặc quá ngắn Auth:Jwt:Key (cần ít nhất 32 ký tự). Đặt bằng: dotnet user-secrets set \"Auth:Jwt:Key\" \"<chuỗi ngẫu nhiên dài>\" " +
        "hoặc biến môi trường Auth__Jwt__Key.");

builder.Services.AddSvsApplication();
builder.Services.AddSvsInfrastructure(connectionString, builder.Configuration);
builder.Services.AddHostedService<AiJobPollingService>();
builder.Services.AddHostedService<LiveSessionPollingService>();
builder.Services.AddOpenApi();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<IPasswordService, AspNetPasswordService>();
builder.Services.AddSingleton<IAccessTokenFactory, JwtAccessTokenFactory>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // giữ nguyên tên claim ("sub", "email"...) thay vì ánh xạ sang ClaimTypes.*
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = authOptions.Jwt.Issuer,
            ValidateAudience = true, ValidAudience = authOptions.Jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.Jwt.Key)),
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }, // chặn đổi thuật toán (ví dụ "none")
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
    });

// Mặc định mọi endpoint đều yêu cầu đăng nhập; endpoint công khai phải khai báo AllowAnonymous() rõ ràng.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Chống dò mật khẩu: giới hạn tần suất theo IP cho nhóm /api/auth.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddProblemDetails();

// CORS cho frontend web. Chỉ origin trong Cors:AllowedOrigins mới được gọi (khớp nguyên chuỗi scheme+host+port).
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
if (corsOrigins.Length > 0) app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.MapHealthChecks("/health").AllowAnonymous();
app.MapAuthEndpoints();
app.MapRecordingEndpoints();
app.MapLiveEndpoints();
app.MapSpaceEndpoints();

app.Run();
