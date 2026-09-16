# The relay is a self-contained ASP.NET project with no project references, so the build context
# is just this directory - nothing else in the repo is needed to produce a runnable image.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against the project file alone first: editing a .cs file then leaves this layer cached
# instead of re-resolving every package.
COPY TwitchRelay.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish TwitchRelay.csproj -c Release -o /app --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# The runtime image ships with neither curl nor wget, and the healthcheck below needs one of them.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Everything that has to outlive the container lives here: token balances viewers paid real Bits
# for, each streamer's OAuth tokens, spent Bits transaction ids, and the EventSub webhook secret.
#
# Created and chowned in the image on purpose - a fresh named volume inherits the ownership of the
# directory it shadows, so without this Docker would create it root-owned and the relay, running
# as `app`, could not write to it.
#
# Losing this directory is not a clean slate. The webhook secret in particular cannot be
# regenerated: every EventSub subscription already registered with Twitch keeps signing with the
# old one, so Channel Points redemptions would silently stop crediting while Twitch still reports
# the subscription as healthy. See docker/README.md.
# $APP_UID is set by the base image (1654) and is what Microsoft's own docs use - referring to
# the uid rather than the `app` username keeps this working if the image ever renames it.
RUN mkdir -p /data && chown -R $APP_UID /data
VOLUME ["/data"]
ENV RELAY_DATA_DIR=/data

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "TwitchRelay.dll"]
