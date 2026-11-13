# syntax=docker/dockerfile:1
# Build: docker buildx build -f deploy/Dockerfile --platform linux/amd64,linux/arm64 .
# The SDK stage runs on the build machine's own arch and cross-compiles, so arm64 needs no emulation.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Skimr.Domain/Skimr.Domain.csproj src/Skimr.Domain/
COPY src/Skimr.Infrastructure/Skimr.Infrastructure.csproj src/Skimr.Infrastructure/
COPY src/Skimr.Infrastructure.Sqlite/Skimr.Infrastructure.Sqlite.csproj src/Skimr.Infrastructure.Sqlite/
COPY src/Skimr.Infrastructure.Postgres/Skimr.Infrastructure.Postgres.csproj src/Skimr.Infrastructure.Postgres/
COPY src/Skimr.Web/Skimr.Web.csproj src/Skimr.Web/
RUN dotnet restore src/Skimr.Web/Skimr.Web.csproj -a $TARGETARCH

COPY src/ src/
# No --no-restore: the SDK decides whether to include Blazor's _framework assets (blazor.web.js)
# from the sources, which aren't present during the cached restore above.
RUN dotnet publish src/Skimr.Web/Skimr.Web.csproj -c Release -a $TARGETARCH -o /app \
    && mkdir /data

# -extra adds ICU and tzdata (time zones, culture-aware sorting) to the chiseled image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=$APP_UID:$APP_UID /data /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    SKIMR__Storage__DataDirectory=/data
USER $APP_UID
EXPOSE 8080
VOLUME /data

HEALTHCHECK --interval=30s --timeout=6s --start-period=20s --retries=3 \
    CMD ["/app/Skimr", "--healthcheck"]

ENTRYPOINT ["/app/Skimr"]
