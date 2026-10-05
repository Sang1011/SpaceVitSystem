FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/SVS.Domain/SVS.Domain.csproj src/SVS.Domain/
COPY src/SVS.Application/SVS.Application.csproj src/SVS.Application/
COPY src/SVS.Infrastructure/SVS.Infrastructure.csproj src/SVS.Infrastructure/
COPY src/SVS.Api/SVS.Api.csproj src/SVS.Api/
RUN dotnet restore src/SVS.Api/SVS.Api.csproj
COPY src/ src/
RUN dotnet publish src/SVS.Api/SVS.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SVS.Api.dll"]
