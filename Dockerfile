# syntax=docker/dockerfile:1.7

ARG BASE_TAG=10.11.5

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy server repo
COPY . .

# Restore + publish (SELF-CONTAINED so the final image doesn't need a runtime installed)
RUN dotnet restore

# Build a linux-x64 self-contained publish to match your target (amd64)
RUN dotnet publish Jellyfin.Server \
    -c Release \
    -o /out \
    -r linux-x64 \
    --self-contained true \
    -p:DebugSymbols=false \
    -p:DebugType=none

# Normalize the output binary name to /out/jellyfin if needed
RUN set -eux; \
    if [ -f /out/Jellyfin.Server ] && [ ! -f /out/jellyfin ]; then mv /out/Jellyfin.Server /out/jellyfin; fi; \
    if [ -f /out/Jellyfin.Server.dll ] && [ ! -f /out/jellyfin.dll ]; then mv /out/Jellyfin.Server.dll /out/jellyfin.dll; fi; \
    test -f /out/jellyfin

# Final image: start from the official Jellyfin image to stay close to upstream
FROM jellyfin/jellyfin:${BASE_TAG}

# Overlay ONLY the published binaries into /jellyfin (web assets in the base image remain)
COPY --from=build /out/ /jellyfin/
