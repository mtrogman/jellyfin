# syntax=docker/dockerfile:1.7

ARG JELLYFIN_BASE_TAG=10.11.5
ARG DOTNET_SDK_TAG=9.0

############################
# Build stage
############################
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG}-bookworm-slim AS build

WORKDIR /src

# Copy source
COPY . .

# (Optional but helps avoid doc/analyzer failures being treated as errors in some builds)
# If your repo already builds clean, you can remove these -p flags.
ARG DOTNET_BUILD_PROPS="-p:GenerateDocumentationFile=false -p:NoWarn=1591"

# Restore + publish
# NOTE: Jellyfin repo has Jellyfin.Server at repo root. If your path differs, adjust.
RUN dotnet restore Jellyfin.Server --runtime linux-x64

RUN dotnet publish Jellyfin.Server \
    -c Release \
    -o /out \
    -r linux-x64 \
    --self-contained false \
    -p:DebugSymbols=false \
    -p:DebugType=none \
    ${DOTNET_BUILD_PROPS}

############################
# Runtime stage (official layout)
############################
FROM jellyfin/jellyfin:${JELLYFIN_BASE_TAG} AS runtime

# Overlay your built server bits on top of the official image
# This keeps the official filesystem structure and dependencies.
COPY --from=build /out/ /jellyfin/

# Nothing else needed; inherit ENTRYPOINT/CMD from the official image
