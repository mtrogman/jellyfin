# syntax=docker/dockerfile:1.7

ARG RUNTIME_IMAGE=jellyfin/jellyfin:10.11.5
FROM ${RUNTIME_IMAGE}

# overlay published server binaries onto the official image path
COPY out/ /jellyfin/
RUN chmod +x /jellyfin/jellyfin
