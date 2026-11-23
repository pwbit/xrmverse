# Web admin shell (served by the host from wwwroot). Pnpm comes from ui/package.json "packageManager" via corepack.
FROM node:22-bookworm-slim AS ui
WORKDIR /repo/ui
RUN corepack enable pnpm
COPY ui/package.json ui/pnpm-lock.yaml ui/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
# The typecheck in "pnpm build" also compiles the tests, which import ../../fixtures/ui (sibling of ui/).
COPY fixtures/ui/ /repo/fixtures/ui/
COPY ui/ ./
RUN pnpm build

# Build context: repository root (docker compose sets context: ..)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first (cached while project files and props are unchanged)
COPY xrmverse.sln Directory.Build.props ./
COPY src/Xrmd.Abstractions/*.csproj src/Xrmd.Abstractions/
COPY src/Xrmd.Model/*.csproj src/Xrmd.Model/
COPY src/Xrmd.Metadata/*.csproj src/Xrmd.Metadata/
COPY src/Xrmd.Audit/*.csproj src/Xrmd.Audit/
COPY src/Xrmd.Query/*.csproj src/Xrmd.Query/
COPY src/Xrmd.Security/*.csproj src/Xrmd.Security/
COPY src/Xrmd.Pipeline/*.csproj src/Xrmd.Pipeline/
COPY src/Xrmd.Core/*.csproj src/Xrmd.Core/
COPY src/Xrmd.Solution/*.csproj src/Xrmd.Solution/
COPY src/Xrmd.Schema/*.csproj src/Xrmd.Schema/
COPY src/Xrmd.Admin/*.csproj src/Xrmd.Admin/
COPY src/Xrmd.WebApi/*.csproj src/Xrmd.WebApi/
COPY src/Xrmd.Mcp/*.csproj src/Xrmd.Mcp/
COPY src/Xrmd.Migration/*.csproj src/Xrmd.Migration/
COPY src/Xrmd.Files/*.csproj src/Xrmd.Files/
COPY src/Xrmd.Storage.PostgreSql/*.csproj src/Xrmd.Storage.PostgreSql/
COPY src/Xrmd.Tds/*.csproj src/Xrmd.Tds/
COPY src/Xrmd.Workflows/*.csproj src/Xrmd.Workflows/
COPY src/Xrmd.Host/*.csproj src/Xrmd.Host/
RUN dotnet restore src/Xrmd.Host/Xrmd.Host.csproj
COPY src/ src/
# ui/dist is not in the build context (.dockerignore): the host csproj copies nothing here, the shell comes from the ui stage.
# Embedded as resources by Xrmd.Metadata (seed definitions); the build fails if missing.
COPY fixtures/ fixtures/
RUN dotnet publish src/Xrmd.Host/Xrmd.Host.csproj -c Release --no-restore -o /app/publish
# SkiaSharp (image thumbnails) brings native libraries for every platform, ~330 MB of them for Windows and macOS: the image
# runs Linux only (libSkiaSharp.so of SkiaSharp.NativeAssets.Linux.NoDependencies needs no system library).
RUN rm -rf /app/publish/runtimes/win-* /app/publish/runtimes/osx*

# The PostgreSQL client tools (plan 2.7b, ruling Q9; fix round 1 of Task 9), built from the official source tarball, pinned by
# version and SHA-256: only pg_dump, pg_restore and libpq ship in the runtime image, never psql, the server or a distribution
# package (distribution packages pull in readline and perl). Readline, GSSAPI, LDAP and ICU are left out; OpenSSL, zlib, zstd
# and LZ4 are linked from libraries the aspnet base image already has. The build tools (compiler, bison, flex, perl, -dev
# headers; apt in this stage only) stay in this stage. The tools look for libpq in /usr/lib/postgresql/18/lib through their
# rpath (configure's default --enable-rpath).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS pgtools
ARG PG_VERSION=18.7
ARG PG_SHA256=441dfae4280cae3e0425e5f876f80559fb0731c140f1aae22b3e5a54d0411bfd
ADD https://ftp.postgresql.org/pub/source/v${PG_VERSION}/postgresql-${PG_VERSION}.tar.bz2 /tmp/postgresql.tar.bz2
RUN echo "${PG_SHA256}  /tmp/postgresql.tar.bz2" | sha256sum -c - \
    && apt-get -o Acquire::Retries=5 update \
    && apt-get -o Acquire::Retries=5 install -y --no-install-recommends bzip2 gcc make pkg-config bison flex perl \
       libc6-dev libssl-dev zlib1g-dev libzstd-dev liblz4-dev \
    && mkdir /src && tar -xjf /tmp/postgresql.tar.bz2 -C /src --strip-components=1
WORKDIR /src
RUN ./configure --prefix=/usr/lib/postgresql/18 --without-readline --without-gssapi --without-ldap --without-icu \
       --with-openssl --with-zlib --with-zstd --with-lz4 \
    && make -C src/backend generated-headers \
    && make -C src/interfaces/libpq -j"$(nproc)" \
    && make -C src/bin/pg_dump -j"$(nproc)" \
    && make -C src/interfaces/libpq install \
    && make -C src/bin/pg_dump install \
    && mkdir -p /out/usr/lib/postgresql/18/bin /out/usr/lib/postgresql/18/lib /out/usr/share/doc/postgresql \
    && cp /usr/lib/postgresql/18/bin/pg_dump /usr/lib/postgresql/18/bin/pg_restore /out/usr/lib/postgresql/18/bin/ \
    && cp -P /usr/lib/postgresql/18/lib/libpq.so.5* /out/usr/lib/postgresql/18/lib/ \
    && strip /out/usr/lib/postgresql/18/bin/* /out/usr/lib/postgresql/18/lib/libpq.so.5.* \
    && cp COPYRIGHT /out/usr/share/doc/postgresql/COPYRIGHT \
    && /out/usr/lib/postgresql/18/bin/pg_dump --version

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
LABEL org.opencontainers.image.licenses="Apache-2.0"
LABEL org.opencontainers.image.vendor="Power Builders Italia S.r.l."
LABEL org.opencontainers.image.description="xrmverse, a Dataverse-compatible server. xrmverse is an independent project and is not affiliated with, endorsed by or sponsored by Microsoft. Microsoft, Dataverse, Dynamics 365 and Power Apps are trademarks of the Microsoft group of companies."
WORKDIR /app
# The PostgreSQL client tools of the pgtools stage: pg_dump and pg_restore (Storage:PgToolsPath), libpq, and PostgreSQL's
# COPYRIGHT. A client of a newer major dumps and restores every server from 16 up (PgTools tolerates the settings an older
# server does not know). The compose healthcheck still uses bash's /dev/tcp instead of curl: nothing else is installed.
COPY --from=pgtools /out/ /
RUN /usr/lib/postgresql/18/bin/pg_dump --version && /usr/lib/postgresql/18/bin/pg_restore --version
COPY --from=build /app/publish .
# The host serves the shell from Ui:RootPath, default "wwwroot" under the content root (/app).
COPY --from=ui /repo/ui/dist/ ./wwwroot/
# Third-party notices of everything the image ships (generated by eng/licenses.sh, checked in CI).
COPY THIRD_PARTY_NOTICES.md ./
# The project's license and notice (Apache License 2.0, section 4).
COPY LICENSE NOTICE ./
# Default root of the file-system store (Files:FileSystem:Root), and the backup directory of PostgreSQL organizations (the .dump
# and .files.tar of `xrmd org backup`, ruling Q8) and of the files archives with an external SQL Server
# (Admin:FilesBackupDirectory, `xrmd server init --sql-connection`). A named volume mounted here starts with this owner.
RUN mkdir -p /var/lib/xrmd/files /var/lib/xrmd/backups && chown $APP_UID:$APP_UID /var/lib/xrmd/files /var/lib/xrmd/backups
# The customer's own copy of the Microsoft SDK assemblies (`xrmd sdk install`) is mounted read-only here at run time; the image
# ships none. The directory is root-owned and world-readable: the container user only reads it.
RUN mkdir -p /var/lib/xrmd/sdk
ENV XRMD_SDK_DIR=/var/lib/xrmd/sdk
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
USER $APP_UID
ENTRYPOINT ["dotnet", "Xrmd.Host.dll"]
