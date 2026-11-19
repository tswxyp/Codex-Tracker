FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY shared/Tracker.Protocol/ shared/Tracker.Protocol/
COPY server/Tracker.Server/ server/Tracker.Server/
RUN dotnet restore server/Tracker.Server --locked-mode && dotnet publish server/Tracker.Server -c Release --no-restore -o /publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
WORKDIR /app
COPY --from=build /publish/ .
COPY LICENSE docs/phase-3-dependencies.md ./
COPY docs/licenses/phase3/ /app/licenses/
RUN mkdir -p /data/keys /data/security && chown -R app:app /data
USER app
ENTRYPOINT ["dotnet","Tracker.Server.dll"]
