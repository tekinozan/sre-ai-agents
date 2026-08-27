# One Dockerfile, three images (ARCHITECTURE.md 19.1).
#
# The hosts share the application and infrastructure packages and differ only in
# composition, so they differ only in a build argument here too. A separate
# Dockerfile per host would drift: the day someone adds a runtime dependency,
# two of the three would get it.
#
#   docker build -f deploy/docker/Dockerfile --build-arg PROJECT=SreAiAgent.Api .

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
WORKDIR /src

# Restore before the source is copied, so a source-only change reuses the layer.
# The lock files and Directory.Packages.props come first because they are what
# restore actually reads.
# .editorconfig is a build input, not an editor preference: it carries the
# analyzer severities. Without it the container build applies defaults the repo
# has deliberately turned off and fails on rules CI never runs.
COPY .editorconfig global.json NuGet.config Directory.Build.props Directory.Build.targets ./
COPY Directory.Packages.props SreAiAgent.slnx BannedSymbols.txt ./
COPY src/Directory.Build.props src/
COPY src/*/*.csproj src/*/packages.lock.json ./tmp/
RUN for file in ./tmp/*.csproj; do \
        name=$(basename "${file}" .csproj); \
        mkdir -p "src/${name}"; \
        mv "./tmp/${name}.csproj" "src/${name}/"; \
        [ -f "./tmp/${name}.packages.lock.json" ] && mv "./tmp/${name}.packages.lock.json" "src/${name}/packages.lock.json" || true; \
    done; rm -rf ./tmp

COPY src/ src/

# Locked mode, exactly as CI runs it. An image built from a different dependency
# graph than the one that was tested is not the artefact that was tested.
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj" --locked-mode

RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" \
        --no-restore \
        --configuration Release \
        --output /app

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG PROJECT
WORKDIR /app

# The ASP.NET runtime for all three. The workers do not serve HTTP, but sharing
# one base image means one image to patch when a CVE lands rather than two.

# The registries load these at startup and fail fast if they are missing, so they
# are part of the image rather than mounted. A pod whose prompts came from
# elsewhere is not the artefact that was reviewed (ARCHITECTURE.md 8.2).
COPY config/ ./config/
COPY prompts/ ./prompts/

# Two packages the base image does not carry.
#
# curl is for the compose healthcheck, which runs inside the container and would
# otherwise have no HTTP client at all. Kubernetes probes the endpoint itself and
# does not need it, so this exists for local development.
#
# libgssapi-krb5-2 is probed by Npgsql on connect. Without it every connection
# logs a shared-object load failure that looks like a database error and is not
# one, which is exactly the kind of noise that trains people to ignore logs.
RUN apt-get update     && apt-get install --yes --no-install-recommends curl libgssapi-krb5-2     && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# The host is fixed at build time by a symlink rather than by an environment
# variable, so a running container cannot be pointed at a different host by
# changing its environment. Exec form, so the process gets PID 1 and receives
# SIGTERM directly, which is what the graceful shutdown in section 19.2 relies on.
RUN ln -s "/app/${PROJECT}.dll" /app/service.dll

# The base image already ships a non-root 'app' user. Adding another identity
# would be one more thing to keep in step with the Kubernetes securityContext for
# no gain. The application writes no files: evidence goes to PostgreSQL and
# telemetry goes over the network.
USER app

ENV DOTNET_EnableDiagnostics=0     ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "/app/service.dll"]
