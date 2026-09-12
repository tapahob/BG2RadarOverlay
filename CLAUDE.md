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
