# syntax=docker/dockerfile:1.7

# IMPORTANT: ARGs used in FROM must be declared before the first FROM
ARG DOTNET_SDK_TAG=8.0-bookworm-slim
ARG RUNTIME_IMAGE=jellyfin/jellyfin:10.11.5

##
## Build stage: compile your fork
##
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG} AS build

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_XMLDOC_MODE=skip \
    DOTNET_NOLOGO=1

WORKDIR /src
COPY . .

# Useful for debugging what SDK is being used in CI
RUN dotnet --info

# Cache NuGet packages between builds
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore Jellyfin.Server --runtime linux-x64

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Jellyfin.Server \
      -c Release \
      -o /out \
      -r linux-x64 \
      --self-contained true \
      -p:DebugSymbols=false \
      -p:DebugType=none

##
## Runtime stage: start from the official Jellyfin image and overlay your server binaries
##
FROM ${RUNTIME_IMAGE} AS runtime

# Official Jellyfin image expects binaries in /jellyfin
COPY --from=build /out/ /jellyfin/

RUN chmod +x /jellyfin/jellyfin

# Keep the official image’s ENTRYPOINT/CMD behavior intact
