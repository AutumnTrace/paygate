# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the dependency layer is cached between code changes.
COPY global.json Directory.Build.props Directory.Packages.props Paygate.slnx ./
COPY src/Paygate.Domain/Paygate.Domain.csproj src/Paygate.Domain/
COPY src/Paygate.Application/Paygate.Application.csproj src/Paygate.Application/
COPY src/Paygate.Infrastructure/Paygate.Infrastructure.csproj src/Paygate.Infrastructure/
COPY src/Paygate.Api/Paygate.Api.csproj src/Paygate.Api/
RUN dotnet restore src/Paygate.Api/Paygate.Api.csproj

COPY src/ src/
RUN dotnet publish src/Paygate.Api/Paygate.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false \
    && mkdir /data

# Chiseled Ubuntu: no shell, no package manager, runs as the non-root "app" user (UID 1654).
# The -extra variant adds ICU and time zone data.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=1654:1654 /data /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Paygate="Data Source=/data/paygate.db"
EXPOSE 8080
USER 1654
ENTRYPOINT ["dotnet", "Paygate.Api.dll"]
