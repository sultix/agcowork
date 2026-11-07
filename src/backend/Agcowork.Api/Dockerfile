# Build context is the repository root: docker build -f src/backend/Agcowork.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
# Restore layer: only project files, so source changes do not invalidate the NuGet restore.
COPY src/backend/Agcowork.Api/Agcowork.Api.csproj src/backend/Agcowork.Api/
COPY src/backend/Agcowork.Application/Agcowork.Application.csproj src/backend/Agcowork.Application/
COPY src/backend/Agcowork.Domain/Agcowork.Domain.csproj src/backend/Agcowork.Domain/
COPY src/backend/Agcowork.Infrastructure/Agcowork.Infrastructure.csproj src/backend/Agcowork.Infrastructure/
COPY src/backend/Agcowork.ServiceDefaults/Agcowork.ServiceDefaults.csproj src/backend/Agcowork.ServiceDefaults/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/backend/Agcowork.Api/Agcowork.Api.csproj
COPY src/backend ./src/backend
# The OpenAPI document is checked in (CI verifies it); no need to generate it here.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/backend/Agcowork.Api/Agcowork.Api.csproj -c Release -o /app/publish \
    --no-restore -p:OpenApiGenerateDocumentsOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# The image listens on 8080 by default (ASPNETCORE_HTTP_PORTS=8080).
EXPOSE 8080
# Writable, volume-backed location for the DataProtection keys (session cookies survive restarts).
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys && chown -R $APP_UID:$APP_UID /home/app/.aspnet
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=5 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health HTTP/1.0\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "Agcowork.Api.dll"]
