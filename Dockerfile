# syntax=docker/dockerfile:1

# Build stage runs on the build host's native architecture and cross-publishes for the target.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# global.json pins the SDK (rollForward: latestFeature); it must be present for every SDK resolution below.
COPY global.json ./
COPY src/MockGraph.Server/MockGraph.Server.csproj src/MockGraph.Server/
# .NET runtime identifiers use "x64", not "amd64". Restore and publish must use the same RID.
RUN arch="$TARGETARCH"; if [ "$arch" = "amd64" ]; then arch=x64; fi; \
    dotnet restore src/MockGraph.Server/MockGraph.Server.csproj -a "$arch"

COPY src/ src/
RUN arch="$TARGETARCH"; if [ "$arch" = "amd64" ]; then arch=x64; fi; \
    dotnet publish src/MockGraph.Server/MockGraph.Server.csproj -c Release -a "$arch" --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Served by default: the content root is /app, so the relative default "fixtures/sample.yaml" resolves to /app/fixtures/sample.yaml.
COPY fixtures ./fixtures
COPY LICENSE THIRD-PARTY-NOTICES.md ./
COPY LICENSES ./LICENSES

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# The aspnet image ships a non-root "app" user (APP_UID is set in the image).
USER $APP_UID
ENTRYPOINT ["dotnet", "MockGraph.Server.dll"]
