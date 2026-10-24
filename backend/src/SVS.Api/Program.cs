using Scalar.AspNetCore;
using SVS.Api.BackgroundServices;
using SVS.Api.Endpoints;
using SVS.Application;
using SVS.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");

builder.Services.AddSvsApplication();
builder.Services.AddSvsInfrastructure(connectionString, builder.Configuration);
builder.Services.AddHostedService<AiJobPollingService>();
builder.Services.AddHostedService<LiveSessionPollingService>();
builder.Services.AddOpenApi();

// CORS cho frontend web. Chỉ origin trong Cors:AllowedOrigins mới được gọi (khớp nguyên chuỗi scheme+host+port).
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

if (corsOrigins.Length > 0) app.UseCors();

app.MapHealthChecks("/health");
app.MapRecordingEndpoints();
app.MapLiveEndpoints();
app.MapSpaceEndpoints();

app.Run();
