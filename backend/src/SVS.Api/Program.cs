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
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthChecks("/health");
app.MapRecordingEndpoints();

app.Run();
