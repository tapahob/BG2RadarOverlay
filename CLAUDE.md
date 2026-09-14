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

## TwitchRelay VPS

`TwitchRelay/` (the relay server for the Twitch integration, see `TwitchRelayClient.cs`) is
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
- Redeploying: `dotnet publish TwitchRelay/TwitchRelay.csproj -c Release -o publish_relay`, stop
  the service, SFTP the published files over (skip `TwitchRelay.pdb` and `web.config` - debug
  symbols and an IIS-only file), `chown -R www-data:www-data /opt/twitch-relay`, start again.
- Reading command output in Python: decode with `errors="replace"` and
  `sys.stdout.reconfigure(encoding="utf-8")`. `systemctl status` prints a U+25CF bullet that the
  console's cp1251 codec can't encode, which otherwise kills the script *after* it has already
  deployed.
- Deploy layout on the VPS: app in `/opt/twitch-relay`, run via the systemd unit at
  `TwitchRelay/deploy/twitch-relay.service` (`sudo systemctl status/restart twitch-relay`). It
  listens on `localhost:5080`; Caddy fronts it on port 8443 with a real certificate, so the public
  base URL is `https://<host>:8443` - that is what the overlay and the extension config use.
  Twitch's EventSub, though, rejects a webhook callback on a non-standard port - `/oauth/callback`
  and `/eventsub/callback` are additionally reachable on the standard port 443 via a path-based
  route added to this VPS's *other*, pre-existing Caddy instance (a third-party VPN admin panel
  that already owns 443 for this hostname) - everything else on that port still falls through to
  that panel's own routing untouched. `TWITCH_OAUTH_REDIRECT_URI` must point at the port-443 form.
- Per-viewer/per-restart data (OAuth tokens, the EventSub webhook secret, token balances) is
  written under `<app dir>/data/` - a plain-file store, not a database. Back it up before wiping
  the deploy directory, or viewers lose whatever token balance they'd bought.

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

Bits needs no broadcaster authorization at all - only the Extension Secret above, since a purchase
is verified from a signed receipt the extension frontend already holds, not looked up separately.

Setting up each currency in the Dev Console (per streamer, on their own channel):
- **Bits**: Monetization tab -> enable Bits -> add at least one Product (any price, 1-10,000
  Bits) - the viewer picks a listed product, the relay works out tokens from
  `product cost / bitsPerToken`.
- **Channel Points**: create exactly one Custom Reward whose title matches what's typed into
  config.html's "Token Reward Name" field (case-insensitive) - that's the only reward the relay
  treats as a token purchase; every other reward on the channel is ignored. Then do the
  `/oauth/authorize` step above, once, as that channel's broadcaster.

None of this needs a code change to add/rename packs, change prices, or onboard another streamer -
config.html's saved values and each streamer's live pack list are both read fresh (config.html on
a ~60s cache per broadcaster, packs from that streamer's own overlay snapshot), not baked into the
relay's deployment.
