# syntax=docker/dockerfile:1.7

ARG BASE_TAG=10.11.5

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY . .

RUN dotnet restore

# Build a self-contained linux-x64 publish AND suppress CA1865 (and don't treat warnings as errors)
RUN dotnet publish Jellyfin.Server \
    -c Release \
    -o /out \
    -r linux-x64 \
    --self-contained true \
    -p:DebugSymbols=false \
    -p:DebugType=none \
    -p:TreatWarningsAsErrors=false \
    -p:NoWarn=CA1865

# Ensure we have the executable in the expected name/location
RUN set -eux; \
    if [ -f /out/Jellyfin.Server ] && [ ! -f /out/jellyfin ]; then mv /out/Jellyfin.Server /out/jellyfin; fi; \
    test -f /out/jellyfin; \
    chmod +x /out/jellyfin

# Final image: use official Jellyfin image as base to stay close to upstream runtime/libs
FROM jellyfin/jellyfin:${BASE_TAG}

# Overlay the built server bits into /jellyfin
COPY --from=build /out/ /jellyfin/
