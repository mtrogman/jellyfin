# syntax=docker/dockerfile:1.7

ARG RUNTIME_IMAGE=jellyfin/jellyfin:10.11.5
FROM ${RUNTIME_IMAGE}

# This assumes the workflow published to ./out
COPY out/ /jellyfin/
RUN chmod +x /jellyfin/jellyfin

# Keep the official image’s ENTRYPOINT/CMD intact
