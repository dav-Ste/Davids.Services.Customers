# Build from workspace parent directory so sibling ../Davids.Shared is in build context:
#   docker build -f Davids.Services.Customers/Dockerfile -t ghcr.io/davids/davids-services-customers:1.0.0 .
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /workspace

COPY Davids.Shared/Directory.Build.props Davids.Shared/Directory.Packages.props Davids.Shared/global.json ./Davids.Shared/
COPY Davids.Shared/eng/ ./Davids.Shared/eng/
COPY Davids.Shared/src/Davids.ServiceDefaults/Davids.ServiceDefaults.csproj Davids.Shared/src/Davids.ServiceDefaults/packages.lock.json ./Davids.Shared/src/Davids.ServiceDefaults/
COPY Davids.Services.Customers/Directory.Build.props Davids.Services.Customers/Directory.Packages.props Davids.Services.Customers/global.json ./Davids.Services.Customers/
COPY Davids.Services.Customers/src/Davids.Services.Customers.Core/Davids.Services.Customers.Core.csproj Davids.Services.Customers/src/Davids.Services.Customers.Core/packages.lock.json ./Davids.Services.Customers/src/Davids.Services.Customers.Core/
COPY Davids.Services.Customers/src/Davids.Services.Customers.Application/Davids.Services.Customers.Application.csproj Davids.Services.Customers/src/Davids.Services.Customers.Application/packages.lock.json ./Davids.Services.Customers/src/Davids.Services.Customers.Application/
COPY Davids.Services.Customers/src/Davids.Services.Customers.Infrastructure/Davids.Services.Customers.Infrastructure.csproj Davids.Services.Customers/src/Davids.Services.Customers.Infrastructure/packages.lock.json ./Davids.Services.Customers/src/Davids.Services.Customers.Infrastructure/
COPY Davids.Services.Customers/src/Davids.Services.Customers.Api/Davids.Services.Customers.Api.csproj Davids.Services.Customers/src/Davids.Services.Customers.Api/packages.lock.json ./Davids.Services.Customers/src/Davids.Services.Customers.Api/

RUN dotnet restore ./Davids.Services.Customers/src/Davids.Services.Customers.Api/Davids.Services.Customers.Api.csproj --locked-mode

COPY Davids.Shared/src/Davids.ServiceDefaults/ ./Davids.Shared/src/Davids.ServiceDefaults/
COPY Davids.Services.Customers/src/ ./Davids.Services.Customers/src/

RUN dotnet publish ./Davids.Services.Customers/src/Davids.Services.Customers.Api/Davids.Services.Customers.Api.csproj \
      --no-restore -c Release /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

COPY --from=build --chown=10001:10001 /workspace/Davids.Services.Customers/artifacts/publish/Davids.Services.Customers.Api/release .
USER 10001:10001
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "--info"]

ENTRYPOINT ["dotnet", "Davids.Services.Customers.Api.dll"]
