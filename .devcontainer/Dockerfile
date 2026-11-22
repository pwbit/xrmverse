# amd64 on every host: SQL Server publishes no arm64 image, and the features are built on top of this stage, so the
# platform must be fixed here, not only at `docker run` (an arm64 build would leave no amd64 image to run).
# Image major 2: the 1-10.0 tag stopped at the 10.0 RC SDK, which global.json (10.0.100, latestFeature) rejects.
FROM --platform=linux/amd64 mcr.microsoft.com/devcontainers/dotnet:2-10.0
# The base image ships the Yarn apt repository, whose signing key has expired: apt-get update then fails inside the
# docker-in-docker feature. xrmverse uses pnpm (corepack), not Yarn, so the source is removed.
RUN rm -f /etc/apt/sources.list.d/yarn.list
