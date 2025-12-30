# syntax=docker/dockerfile:1.7

# Use the official Jellyfin image as the runtime base (brings web + ffmpeg + deps)
ARG BASE_IMAGE=jellyfin/jellyfin:10.11.5

# Match the server branch's TargetFramework (10.11.x is typically .NET 8; master may be newer)
ARG DOTNET_SDK_VERSION=8.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION}-bookworm-slim AS build
WORKDIR /src

# copy your fork source
COPY . .

# (optional) restore explicitly for linux-x64 to match publish RID
RUN dotnet restore Jellyfin.Server --runtime linux-x64

# build/publish server
RUN dotnet publish Jellyfin.Server \
    -c Release \
    -o /out \
    -r linux-x64 \
    --self-contained true \
    -p:DebugSymbols=false \
    -p:DebugType=none

# Runtime layer: keep everything from official image, override only the entrypoint binary set
FROM ${BASE_IMAGE} AS runtime

# Put your build somewhere that won't clobber base image layout
COPY --from=build /out /jellyfin-custom
RUN chmod +x /jellyfin-custom/jellyfin

# Keep base env vars (JELLYFIN_* dirs, web dir, etc.) and just run your binary
ENTRYPOINT ["/jellyfin-custom/jellyfin", "--ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffmpeg"]
