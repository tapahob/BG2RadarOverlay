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
- Per-viewer/per-restart data (OAuth tokens, the EventSub webhook secret, token balances) is
  written under `<app dir>/data/` - a plain-file store, not a database. Back it up before wiping
  the deploy directory, or viewers lose whatever token balance they'd bought.

## Summon tokens: Channel Points / Bits economy

A viewer buys "summon tokens" (with Bits and/or Channel Points, whichever price the streamer set
in `config.html`) and spends them on packs from the extension panel - one combined request
however many packs are picked, since neither Bits nor Channel Points can charge a viewer-computed
total in a single native transaction. `SpawnPack.Cost` (Twitch Integration tab, per pack) is a
token count, not raw Bits or points.

Three *different* Twitch credentials are involved - easy to mix up in the Dev Console, since two
of them are both just called "Secret":

- **Extension Secret** (Dev Console -> Extensions -> Manage -> Secret) - base64-encoded, signs
  every JWT the extension deals with (`onAuthorized`, Bits `transactionReceipt`). -> relay env var
  `TWITCH_EXTENSION_SECRET`. Required for both the Bits and the token-spend paths.
- **Extension client id/secret** (same Manage page, a *different* field - for the
  `client_credentials` OAuth grant) - lets the relay read what `config.html` saved (token prices,
  reward name) via Helix `GET /helix/extensions/configurations`, authenticated as the extension
  itself rather than as the broadcaster. -> `TWITCH_EXTENSION_CLIENT_ID` /
  `TWITCH_EXTENSION_CLIENT_SECRET`. Also needs `TWITCH_BROADCASTER_LOGIN` (her channel's login
  name, e.g. `yuna_maxwell`) to resolve her numeric broadcaster id.
- **A separate OAuth "Application"** (Dev Console -> register a new *Application*, not another
  Extension) - only needed for Channel Points, to create the EventSub subscription that reports
  redemptions. -> `TWITCH_CLIENT_ID` / `TWITCH_CLIENT_SECRET` / `TWITCH_OAUTH_REDIRECT_URI` (set
  to `https://<host>:8443/oauth/callback`). One-time setup: visit `/oauth/authorize` in a browser
  logged in as the broadcaster, approve, done - the relay persists the resulting tokens and
  refreshes them itself.

Bits needs no broadcaster authorization at all - only the Extension Secret above, since a purchase
is verified from a signed receipt the extension frontend already holds, not looked up separately.

Setting up each currency in the Dev Console:
- **Bits**: Monetization tab -> enable Bits -> add at least one Product (any price, 1-10,000
  Bits) - the viewer picks a listed product, the relay works out tokens from
  `product cost / bitsPerToken`.
- **Channel Points**: create exactly one Custom Reward whose title matches what's typed into
  config.html's "Token Reward Name" field (case-insensitive) - that's the only reward the relay
  treats as a token purchase; every other reward on the channel is ignored.

None of this needs a code change to add/rename packs or change prices - config.html's saved values
and the streamer's live pack list are both read fresh (config.html on a ~60s cache, packs from the
overlay's own snapshot), not baked into the relay's deployment.
