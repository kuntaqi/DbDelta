# syntax=docker/dockerfile:1

# UNVERIFIED. This file has never been built: the machine it was written on is a Hyper-V guest with no
# nested virtualisation, so no Docker daemon can run there and no image can be built or started. Treat
# every line as a proposal until someone runs `docker build .` and reports back. See docs/PLAN.md,
# "Two ways in: run it, or run it in Docker".

# The SPA gets its own stage so Node never reaches the runtime image. Vite is configured to emit into
# ../DbDelta.Api/wwwroot, so the working directory mirrors the repository layout and the output lands where
# the next stage expects to find it.
FROM node:20-alpine AS web
WORKDIR /build/src/DbDelta.Web
COPY src/DbDelta.Web/package.json src/DbDelta.Web/package-lock.json ./
RUN npm ci
COPY src/DbDelta.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /build
COPY *.slnx ./
COPY src/ src/
# Built assets rather than a build here: the API only serves them, and copying them in keeps this stage
# from needing Node.
COPY --from=web /build/src/DbDelta.Api/wwwroot/ src/DbDelta.Api/wwwroot/
RUN dotnet publish src/DbDelta.Api/DbDelta.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./

# The state that has to outlive the container: saved profiles and the run log. Without this setting both
# land in the container's own writable layer and disappear with it. Mount a volume here.
ENV DbDelta__DataDirectory=/var/lib/dbdelta
ENV ASPNETCORE_URLS=http://+:8080

# Nothing here needs root. The app opens outbound database connections and writes to one directory.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin dbdelta \
    && mkdir -p /var/lib/dbdelta \
    && chown -R dbdelta:dbdelta /var/lib/dbdelta
USER dbdelta

VOLUME ["/var/lib/dbdelta"]
EXPOSE 8080

# Read this before publishing the port anywhere but localhost: DbDelta has no authentication of any kind.
# It is a single-user local tool, and anyone who can reach this port can compare, script and — against a
# server not on the read-only list — apply changes to any database the container can connect to. Bind it to
# 127.0.0.1 on the host.
ENTRYPOINT ["dotnet", "DbDelta.Api.dll"]
