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

- Connect: the `TWITCH_IP` env var already holds the full SSH command, e.g. `ssh <ip> -l <user>` -
  run its value as-is (don't prepend another `ssh`, don't try to parse out just a host/user from
  it). Password is in `TWITCH_PWD`. Reference them as `%TWITCH_IP%`/`%TWITCH_PWD%` from the
  PowerShell tool, `$TWITCH_IP`/`$TWITCH_PWD` from the Bash tool (Git Bash) - same underlying
  Windows env vars, different shell syntax.
- Neither tool has stdin for interactive prompts, so running the bare command will hang waiting
  for the password. Prefix it with sshpass, e.g. (Bash) `sshpass -p "$TWITCH_PWD" $TWITCH_IP`
  (install `sshpass` first if it's not already available).
- Deploy layout on the VPS: app in `/opt/twitch-relay`, run via the systemd unit at
  `TwitchRelay/deploy/twitch-relay.service` (`sudo systemctl status/restart twitch-relay`).
