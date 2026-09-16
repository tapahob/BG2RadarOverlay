# BG2RadarOverlay

## Building

Use `build.bat` to build and package a release. It clears `publish_output`, publishes without
`.pdb` files, and 7-zips the result into `publish_output\BG Radar Overlay <version>.7z` (skipping
the archive step if 7-Zip isn't installed).

For a plain dev build/compile check (no packaging), `dotnet build BGOverlay.sln -c Debug` is
enough - close the running "BG Radar Overlay.exe" first if it's open, since it locks its own
output DLL.

## Screenshots

Look for screenshots in the folder at the `%SCREENSHOTS%` environment variable.

## Twitch Integration layout

Everything Twitch-specific lives under `TwitchIntegration/`:

- `Relay/` - the ASP.NET relay deployed to the VPS. The **project and assembly are still named
  `TwitchRelay`** even though the folder isn't: the systemd unit runs `/opt/twitch-relay/TwitchRelay.dll`,
  so renaming the assembly would break the running deployment for no benefit.
- `Relay.Tests/` - `TwitchRelay.Tests.csproj`, same reasoning (the relay's `InternalsVisibleTo`
  names that assembly).
- `Extension/` - the Twitch Extension front-end. **Only the contents of this folder** are zipped
  and uploaded to Twitch, so nothing in the page may reference a path above it.
- `TwitchRelayClient.cs`, `SpawnPack.cs` - the overlay-side halves, compiled into the main
  BGOverlay project (which lists sources explicitly, so `BGOverlay.csproj` names these paths).

`GameSpawnBridge.cs` and `EEexMod/` deliberately stay at the root: they're the game/EEex side of
the bridge, used by the app whether or not Twitch is switched on.

## Tests

Two suites, both fully mocked - neither needs Twitch, real Bits, or the VPS:

- `dotnet test TwitchIntegration/Relay.Tests` - the relay's payment paths end to end. `FakeTwitch` stands in
  for Helix and mints the JWTs Twitch would (it holds the same extension secret the relay is
  configured with, so a receipt it signs is genuine as far as the relay can tell); `RelayHarness`
  runs the relay in-process against it. `TWITCH_HELIX_BASE_URL` / `TWITCH_ID_BASE_URL` are what
  point the relay at the stub - leave both unset in production.
- `node TwitchIntegration/Extension/tests/pending-receipts.test.js` - the viewer-side half: what happens to a
  Bits receipt between Twitch taking the money and the relay crediting it. Loads
  `video_overlay.js` into a stub browser (no DOM, no Twitch, no network).

## Running the relay in Docker

`TwitchIntegration/Relay/Dockerfile` plus `TwitchIntegration/Relay/docker/` (compose, Caddy, an
`.env` template and a setup guide) package the relay so anyone can host their own - the relay is
already multi-tenant, so the only thing stopping other streamers from using it was having to stand
a server up by hand. `docker/README.md` is written for those self-hosters, not for this repo's
maintainer.

The container puts `RELAY_DATA_DIR` on a named volume and runs as a non-root uid. The compose file
also brings up Caddy on 80/443 for automatic certificates; everything sits on 443 because Twitch
refuses to deliver EventSub notifications to a callback on any other port.

Nothing in the repo is pinned to a particular relay: `TWITCH_OAUTH_REDIRECT_URI` comes from the
environment (compose derives it from `RELAY_DOMAIN`) and the EventSub callback is derived from
that in turn. A self-hoster does have to run their own **Extension** as well, though - the
extension secret signs JWTs for every channel that extension serves, so it can't be shared, and
their relay domain has to be in that extension's URL-fetching allowlist or Twitch's CSP blocks
every call from the panel silently. `docker/README.md` spells this out.

`docker-compose.existing-proxy.yml` is the variant for a host that already terminates TLS - the
relay alone, on a loopback port, no Caddy. That is what `yunamaxwell.shit.vc` now runs, since
another Caddy there owns 443 for the VPN admin panel.

## TwitchRelay VPS

`TwitchIntegration/Relay/` (the relay server, see `TwitchIntegration/TwitchRelayClient.cs`) is
deployed on a separate Ubuntu VPS, not on this machine.

- Connect: the `TWITCH_IP` env var holds the SSH *arguments* - `<host> -l <user>`, with no `ssh`
  in front of them - so it goes after the command, as `ssh $TWITCH_IP`. Password is in
  `TWITCH_PWD`. Reference them as `%TWITCH_IP%`/`%TWITCH_PWD%` from the PowerShell tool,
  `$TWITCH_IP`/`$TWITCH_PWD` from the Bash tool (Git Bash) - same underlying Windows env vars,
  different shell syntax.
- Neither tool has stdin for interactive prompts, so the bare command hangs waiting for the
  password, and there is no key on this machine (`BatchMode=yes` gets "Permission denied").
  `sshpass` is *not* installed and Git Bash has no package manager to install it with. What works
  is Python + paramiko (already installed): connect with `password=os.environ["TWITCH_PWD"]`, run
  commands with `exec_command`, copy files with `open_sftp()`.
- **The relay now runs in Docker on that VPS**, not as the `twitch-relay` systemd unit. The repo
  is checked out at `/opt/bg2radar-relay`; redeploying is `git pull && docker compose -f
  docker-compose.existing-proxy.yml up -d --build` from `TwitchIntegration/Relay/docker`. The
  container binds `127.0.0.1:5080` - the same address both Caddys already proxy to - so neither
  Caddy config had to change, and it runs as uid 33 (www-data) to match the existing owner of
  `/var/lib/twitch-relay`, so the data needed no chown.
- Rollback is still available: the `twitch-relay` unit and `/opt/twitch-relay` are intact, just
  disabled. `docker compose -f docker-compose.existing-proxy.yml down && systemctl enable --now
  twitch-relay`.
- `docker kill` will **not** trigger `restart: unless-stopped` - Docker treats it as a manual stop,
  same as `docker stop`, and the container stays down. A real crash does restart it (verified).
  Don't use `docker kill` to test resilience.
- The old bare-metal recipe, if ever needed: `dotnet publish
  TwitchIntegration/Relay/TwitchRelay.csproj -c Release -o publish_relay`, stop the service, SFTP
  the published files over (skip `TwitchRelay.pdb` and `web.config` - debug symbols and an
  IIS-only file), `chown -R www-data:www-data /opt/twitch-relay`, start again.
- Reading command output in Python: decode with `errors="replace"` and
  `sys.stdout.reconfigure(encoding="utf-8")`. `systemctl status` prints a U+25CF bullet that the
  console's cp1251 codec can't encode, which otherwise kills the script *after* it has already
  deployed.
- Deploy layout on the VPS: app in `/opt/twitch-relay`, run via the systemd unit at
  `TwitchIntegration/Relay/deploy/twitch-relay.service` (`sudo systemctl status/restart twitch-relay`). It
  listens on `localhost:5080`; Caddy fronts it on port 8443 with a real certificate, so the public
  base URL is `https://<host>:8443` - that is what the overlay and the extension config use.
  Twitch's EventSub, though, rejects a webhook callback on a non-standard port - `/oauth/callback`
  and `/eventsub/callback` are additionally reachable on the standard port 443 via a path-based
  route added to this VPS's *other*, pre-existing Caddy instance (a third-party VPN admin panel
  that already owns 443 for this hostname) - everything else on that port still falls through to
  that panel's own routing untouched. `TWITCH_OAUTH_REDIRECT_URI` must point at the port-443 form.
- Per-viewer/per-restart data (OAuth tokens, the EventSub webhook secret, token balances, spent
  Bits transaction ids) is written under `<app dir>/data/` - a plain-file store, not a database.
  Set `RELAY_DATA_DIR` to somewhere outside the deploy directory (the systemd unit is the place
  for it) so a redeploy can't take it with it; otherwise back it up before wiping, or viewers lose
  whatever token balance they'd bought. Losing `used-bits-transactions.json` specifically is worse
  than losing balances: it is what stops a receipt a viewer's browser still holds from being
  cashed in a second time.

## Summon tokens: Channel Points / Bits economy

A viewer buys "summon tokens" (with Bits and/or Channel Points, whichever price the streamer set
in `config.html`) and spends them on packs from the extension panel - one combined request
however many packs are picked, since neither Bits nor Channel Points can charge a viewer-computed
total in a single native transaction. `SpawnPack.Cost` (Twitch Integration tab, per pack) is a
token count, not raw Bits or points.

**This relay is multi-tenant: one deployment serves however many streamers run the Radar app
against it, not just one.** Nothing about a specific broadcaster is baked into the relay's own
config - a stream key alone never identifies who it belongs to (there's no relation between the
two), so each Radar app declares its own Twitch channel login on the **Twitch Integration tab**
("Twitch Channel" field, `Configuration.TwitchBroadcasterLogin`) and sends it to the relay in the
same WebSocket handshake that already carries the control key. The relay resolves that login to a
numeric broadcaster id via Helix and keys everything per-streamer from there: token balances
(`<broadcasterId>:<viewerId>`), cached config.html prices, and OAuth tokens for the Channel Points
path are all dictionaries, not singletons.

Three *different* Twitch credentials are involved, and only one of the three needs a value **per
streamer** - the other two are relay-wide, shared by everyone using this deployment. Easy to mix
up in the Dev Console, since two of the three are both just called "Secret":

- **Extension Secret** (Dev Console -> Extensions -> Manage -> Secret) - base64-encoded, signs
  every JWT the extension deals with (`onAuthorized`, Bits `transactionReceipt`). -> relay env var
  `TWITCH_EXTENSION_SECRET`. One value, shared by every streamer on this relay (it belongs to the
  Extension itself, not to any one channel). Required for both the Bits and the token-spend paths.
- **Extension client id/secret** (same Manage page, a *different* field - for the
  `client_credentials` OAuth grant) - lets the relay read what each streamer's `config.html` saved
  (token prices, reward name) via Helix `GET /helix/extensions/configurations`, authenticated as
  the extension itself rather than as any particular broadcaster. -> `TWITCH_EXTENSION_CLIENT_ID`
  / `TWITCH_EXTENSION_CLIENT_SECRET`. Also relay-wide - one pair, not one per streamer.
- **A separate OAuth "Application"** (Dev Console -> register a new *Application*, not another
  Extension) - only needed for Channel Points, to create the EventSub subscription that reports
  redemptions. -> `TWITCH_CLIENT_ID` / `TWITCH_CLIENT_SECRET` / `TWITCH_OAUTH_REDIRECT_URI`.
  Registered once, shared by every streamer who authorizes against it. Twitch requires this
  callback on the standard HTTPS port (443) even though the relay itself is otherwise fronted on
  8443 - see the Caddy config below. **This is the one per-streamer step**: each of them
  individually authorizes, logged in as themselves, and the relay resolves their broadcaster id
  from the resulting token and files everything (their OAuth tokens, their EventSub subscription)
  under that id, independently of every other streamer who's done the same thing against this
  relay. The easiest way to do this is the "Authorize Channel Points" button on config.html itself
  (it shows whether a subscription already exists for the current channel, via
  `GET /api/eventsub-status/{broadcasterId}`, and opens `/oauth/authorize` in a new tab only when
  it doesn't) - visiting `<relay base URL>/oauth/authorize` directly works the same way, the button
  is just a convenience wrapper around it.

**Max Tokens per Viewer** (config.html, `maxTokenBalance`, 0 = no limit) caps how many tokens one
viewer may hold, so nobody can bank a stockpile and spend a whole run at once. It binds Channel
Points only: a redemption credits just what fits under the ceiling and nothing once a viewer is at
it, because points are earned by watching and a redemption that won't fit costs them nothing real.
Bits always credit in full even when that carries someone past the ceiling - the money is taken by
Twitch before the relay ever sees the receipt, so refusing part of a purchase would mean charging
for tokens never handed over. Tokens held over the ceiling spend normally; points simply resume
crediting once the viewer is back under. A refund takes back what a redemption *actually* credited,
which for a partly-capped one is less than its nominal value.

The two currencies are verified in completely different ways, which is worth keeping straight.
**Channel Points** never involves the viewer's browser at all: Twitch POSTs the redemption to
`/eventsub/callback`, a public URL, and an HMAC over (message id + timestamp + body) using the
relay's own webhook secret is the entire authentication - plus a ten-minute freshness window, since
the signature covers the timestamp and would otherwise stay valid forever. Which broadcaster it
belongs to comes from the event itself (`broadcaster_user_id`), so nothing has to assume there is
only one streamer.

That webhook secret lives in `<data dir>/eventsub-secret.txt` and is handed to Twitch when the
subscription is created. **Losing it breaks Channel Points silently and permanently**: the relay
generates a new one, every already-registered subscription keeps signing with the old one, every
redemption fails its signature check, and re-running `/oauth/authorize` won't fix it because the
subscription already exists and its secret can't be changed. Recovering means deleting the
subscription at Twitch's end first. This is the main reason `RELAY_DATA_DIR` is worth setting.

**Bits** is the opposite: a purchase is credited by the extension posting the receipt Twitch handed it, together with the
viewer's own `onAuthorized` token - the receipt proves a purchase happened, the token proves which
channel it happened on (the extension secret is relay-wide, so a receipt alone cannot say). The
receipt is kept in the viewer's `localStorage` until the relay confirms and retried on the next
panel open, because Twitch hands it over exactly once and a failed POST would otherwise lose a
paid-for purchase outright; the relay keys credits on the transaction id and answers
`duplicate: true` rather than crediting twice, which is what lets that retry be safe.

Bits needs no broadcaster authorization at all - only the Extension Secret above, since a purchase
is verified from a signed receipt the extension frontend already holds, not looked up separately.

Setting up each currency in the Dev Console (per streamer, on their own channel):
- **Bits**: Monetization tab -> enable Bits -> add at least one Product (any price, 1-10,000
  Bits) - the viewer picks a listed product, the relay works out tokens from
  `product cost / bitsPerToken`.
- **Channel Points**: create exactly one Custom Reward whose title matches what's typed into
  config.html's "Token Reward Name" field (case-insensitive) - that's the only reward the relay
  treats as a token purchase; every other reward on the channel is ignored. Then do the
  `/oauth/authorize` step above, once, as that channel's broadcaster. Turn **"Skip Reward Requests
  Queue"** on for that reward: the relay credits tokens the moment the redemption event arrives,
  and there is no claw-back if the redemption is *later* refunded from the queue (that would need
  a second EventSub subscription on the `.update` event, plus a policy for what to do when the
  tokens have already been spent - neither exists today). With the queue skipped, the points are
  final at redemption time and the two can't disagree.

None of this needs a code change to add/rename packs, change prices, or onboard another streamer -
config.html's saved values and each streamer's live pack list are both read fresh (config.html on
a ~60s cache per broadcaster, packs from that streamer's own overlay snapshot), not baked into the
relay's deployment.
