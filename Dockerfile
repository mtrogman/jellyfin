# syntax=docker/dockerfile:1.7

##
## Build stage: compile your fork
##
ARG DOTNET_SDK_TAG=8.0-bookworm-slim
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG} AS build

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_XMLDOC_MODE=skip \
    DOTNET_NOLOGO=1

WORKDIR /src
COPY . .

# Optional but VERY useful when builds fail:
# Shows which SDK is actually being used.
RUN dotnet --info

# Cache NuGet packages between builds
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore Jellyfin.Server --runtime linux-x64

# Publish self-contained server for linux-x64
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Jellyfin.Server \
      -c Release \
      -o /out \
      -r linux-x64 \
      --self-contained true \
      -p:DebugSymbols=false \
      -p:DebugType=none

##
## Runtime stage: start from the official Jellyfin image and replace /jellyfin with your build output
##
ARG RUNTIME_IMAGE=jellyfin/jellyfin:10.11.5
FROM ${RUNTIME_IMAGE} AS runtime

# Overlay your fork’s server binaries onto the official image path.
# (The official image expects Jellyfin binaries under /jellyfin.)
COPY --from=build /out/ /jellyfin/

# Ensure the binary is executable
RUN chmod +x /jellyfin/jellyfin

# DO NOT set ENTRYPOINT/CMD here.
# We keep the official image’s entrypoint behavior intact.
