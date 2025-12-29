# Build Jellyfin Server from your fork (server-only) and overlay it onto the official runtime image.
# This keeps the official image's ffmpeg/runtime/web assets, but replaces the server binaries.

ARG BASE_TAG=10.11.5

# ---- Build stage (needs .NET 9 per Jellyfin 10.11.x global.json) ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy repo
COPY . .

# Publish Jellyfin server (framework-dependent; runtime is provided by the base image)
RUN dotnet restore
RUN dotnet publish Jellyfin.Server \
  --configuration Release \
  --output /out \
  --self-contained false \
  -p:DebugSymbols=false \
  -p:DebugType=none

# ---- Runtime stage (official Jellyfin image) ----
FROM jellyfin/jellyfin:${BASE_TAG}

# Overlay published server binaries without deleting existing files (web, ffmpeg config, etc.)
COPY --from=build /out/ /jellyfin/

# Inherit ENTRYPOINT/CMD from base image
