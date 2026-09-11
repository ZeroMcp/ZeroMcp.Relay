# syntax=docker/dockerfile:1

# ZeroMcp.Relay (mcprelay) container image.
#
# Multi-arch: the build stage always runs on the build host's native
# architecture ($BUILDPLATFORM) and produces a framework-dependent,
# architecture-neutral publish output, so a single compile serves both
# linux/amd64 and linux/arm64. Only the runtime base image differs per
# platform. Build both with:
#
#   docker buildx build --platform linux/amd64,linux/arm64 -t zeromcp/relay .
#
# Runtime behaviour is controlled by MCPRELAY_MODE:
#   dev  -> config UI enabled  (mcprelay run --enable-ui)
#   prod -> config UI disabled (default)

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# README.md is packed by the csproj via ..\README.md, so keep the layout.
COPY README.md ./
COPY ZeroMcp.Relay/ ZeroMcp.Relay/

# Framework-dependent, portable publish (no apphost -> arch-neutral output).
RUN dotnet publish ZeroMcp.Relay/ZeroMcp.Relay.csproj \
      -f net10.0 \
      -c Release \
      -o /app/publish \
      -p:TargetFrameworks=net10.0 \
      -p:UseAppHost=false \
      -v minimal

# Prepare the entrypoint and an empty config dir here so the final stage needs
# no RUN instructions (keeps cross-platform image assembly QEMU-free).
COPY docker/entrypoint.sh /staging/entrypoint.sh
RUN chmod +x /staging/entrypoint.sh && mkdir -p /staging/config

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ENV MCPRELAY_MODE=prod \
    MCPRELAY_HOST=0.0.0.0 \
    MCPRELAY_PORT=8080 \
    MCPRELAY_CONFIG=/config/relay.config.json

WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=build --chown=$APP_UID:$APP_UID /staging/entrypoint.sh /app/entrypoint.sh
COPY --from=build --chown=$APP_UID:$APP_UID /staging/config /config

USER $APP_UID
VOLUME ["/config"]
EXPOSE 8080

ENTRYPOINT ["/app/entrypoint.sh"]
