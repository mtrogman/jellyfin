# syntax=docker/dockerfile:1.7

ARG BASE_TAG=10.11.5

########################
# Build Jellyfin server #
########################
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY . .

# --- Fix the build-blocking analyzer errors (CA1865) without changing upstream history ---
# Your error:
#   Use string.StartsWith(char) instead of string.StartsWith(string) for single char
# Patch only if the file exists.
RUN set -eux; \
    f="src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite/PragmaConnectionInterceptor.cs"; \
    if [ -f "$f" ]; then \
      sed -i \
        -e "s/\.StartsWith(\"\/\")/.StartsWith('\/')/g" \
        -e "s/\.EndsWith(\"\/\")/.EndsWith('\/')/g" \
        "$f"; \
    fi

RUN dotnet restore

# IMPORTANT:
# - self-contained true so you do NOT depend on a runtime existing in the final image
# - rid linux-x64 because you're building linux/amd64
RUN dotnet publish Jellyfin.Server \
    -c Release \
    -o /out \
    -r linux-x64 \
    --self-contained true \
    -p:DebugSymbols=false \
    -p:DebugType=none

#######################
# Runtime image (base) #
#######################
FROM jellyfin/jellyfin:${BASE_TAG} AS runtime

# Force explicit directories to avoid marker collisions:
# Your crash:
#   Expected to find only .jellyfin-data but found marker for /config/.jellyfin-config
# That happens when config/data dirs are being interpreted/mapped wrong.
ENV JELLYFIN_CONFIG_DIR=/config/config \
    JELLYFIN_DATA_DIR=/config/data \
    JELLYFIN_LOG_DIR=/config/log \
    JELLYFIN_CACHE_DIR=/cache

# Make sure dirs exist (works fine even when volumes are mounted later)
RUN set -eux; \
    mkdir -p /config/config /config/data /config/log /cache || true

# Copy the built server over the Jellyfin install path used by the image
COPY --from=build /out/ /jellyfin/

# Keep startup deterministic regardless of base image entrypoint behavior
ENTRYPOINT ["/jellyfin/jellyfin"]
CMD ["--configdir","/config/config","--datadir","/config/data","--logdir","/config/log","--cachedir","/cache"]
