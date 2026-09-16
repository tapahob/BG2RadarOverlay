# Hosting your own relay

> There is a friendlier, step-by-step version of this with screenshots-worth of detail at
> **<https://tapahob.github.io/BG2RadarOverlay/>**. This file is the terse reference.

The BG Radar Overlay Twitch integration needs a small server between the overlay running on your
PC and your viewers' browsers. This runs that server in Docker.

You don't have to host one to *use* the integration — if someone has already given you a relay URL,
put it in the overlay and in the extension's config page and you're done. Host your own if you'd
rather not depend on somebody else's, or if you want to run the relay for several streamers.

**One relay serves any number of streamers.** Nothing about a particular channel is baked into it:
each overlay tells the relay which Twitch channel it belongs to when it connects, and balances,
prices, and tokens are all tracked per channel from there.

## What you need

- A machine with Docker and a public IP, that can hold ports **80 and 443**.
- A **domain name pointing at it**. Not optional: the Twitch Extension will only talk to an
  `https://` relay, and Twitch won't deliver Channel Points events to a self-signed certificate.
- A Twitch Extension of your own in the [Developer Console](https://dev.twitch.tv/console) — see
  [Nothing points back at anyone else](#nothing-points-back-at-anyone-else) for why it has to be
  yours and not the published one.

Port 443 specifically is not negotiable — Twitch refuses to deliver EventSub notifications to a
callback on any other port. If something else on the host already owns 443, see
[Using your own reverse proxy](#using-your-own-reverse-proxy).

## Setting it up

```sh
git clone https://github.com/tapahob/BG2RadarOverlay.git
cd BG2RadarOverlay/TwitchIntegration/Relay/docker
cp .env.example .env
```

Fill in `.env` — see [Where the credentials come from](#where-the-credentials-come-from) below —
then:

```sh
docker compose up -d
```

Caddy gets a certificate on first start, which takes a few seconds. Check it worked:

```sh
curl https://your-domain.example.com/health     # -> OK
docker compose ps                               # relay should be "healthy"
```

Then point things at it:

- **The overlay**, Options → Twitch Integration → *Relay Server URL*: `https://your-domain.example.com`
- **The extension's config page**, *Relay Server URL*: the same thing.

## Nothing points back at anyone else

Hosting your own relay means hosting your own **Extension** as well. The two are a pair, and it is
worth understanding why before you start, because it is the part that costs real effort.

`TWITCH_EXTENSION_SECRET` signs every JWT that extension deals with, on every channel it runs on.
Whoever holds it can mint a token for any of those channels. So nobody can hand you theirs, and
you can't point your relay at somebody else's published extension — the signatures simply wouldn't
match.

What that means in practice:

1. **Create your own Extension** in the Developer Console and upload the contents of
   `TwitchIntegration/Extension/` as its files. Set the Video Component path to
   `video_component.html` and the config path to `config.html`.
2. **Add your relay domain to the extension's URL fetching allowlist** (Manage → Capabilities →
   *Allowlist for URL Fetching Domains*). Without it, Twitch's Content Security Policy blocks every
   request the panel makes to your relay — and it fails *silently*, so the panel just sits there
   looking like the stream isn't live.
3. **Enable Bits** on the Monetization tab and add at least one Bits Product, if you want the Bits
   path.
4. **Plan for Twitch review.** While the extension is in Local Test or Hosted Test, only accounts
   on its testing allowlist can see it — your ordinary viewers get nothing on your stream. That
   allowlist is capped (around 50 accounts), so it covers testing and not an audience. Reaching
   real viewers means submitting for review and getting the extension **Released**, which takes
   days and can come back with requested changes. You can build and verify everything else in
   Hosted Test with yourself allowlisted while you wait.

Everything else follows from that: the Extension Secret and client id/secret come from *your*
extension, the OAuth Application is *yours*, and `RELAY_DOMAIN` is *your* domain. No value in your
`.env` refers to anyone else's deployment, and nothing in this repository is pinned to one.

## Where the credentials come from

Three different Twitch credentials are involved and two of them are both called "Secret", so this
is the part worth reading slowly. Only the last one is per-streamer; the first two belong to the
Extension itself and are shared by everyone using your relay.

| `.env` variable | Where | What it does |
| --- | --- | --- |
| `TWITCH_EXTENSION_SECRET` | Dev Console → Extensions → Manage → **Secret** (base64) | Signs every JWT the extension deals with. Needed for Bits and for spending tokens. |
| `TWITCH_EXTENSION_CLIENT_ID` / `_SECRET` | The **same Manage page**, different fields | Lets the relay read the token prices each streamer saved in their config page. |
| `TWITCH_CLIENT_ID` / `_SECRET` | Dev Console → register a new **Application** (not another Extension) | Channel Points only. Leave blank for Bits-only. |

Register the Application under your own account — don't ask to be added to someone else's. A single
Application can hold several redirect URLs, so sharing one is technically possible, but it would
mean sharing its client secret, and registering your own takes a minute.

If you register the Application, set its **OAuth Redirect URL** to exactly
`https://your-domain.example.com/oauth/callback`. The relay builds the same URL from
`RELAY_DOMAIN`, and Twitch rejects the exchange if the two don't match character for character.

Each streamer using your relay then authorizes once, as themselves, via the **Authorize Channel
Points** button on the extension's config page. That's the only per-streamer step.

## Your data, and the one way to lose it badly

Everything that has to survive a restart lives in the `relay-data` volume: token balances viewers
paid real Bits for, each streamer's OAuth tokens, spent transaction ids, and the EventSub webhook
secret.

`docker compose down` leaves it alone. **`docker compose down -v` deletes it**, and that is worse
than it sounds. Balances are the obvious loss. The quieter one is the webhook secret: the relay
generates a new one, every EventSub subscription already registered with Twitch keeps signing with
the old one, and from then on every Channel Points redemption is rejected — while Twitch still
reports the subscription as perfectly healthy. Nothing looks broken. Redemptions just stop
crediting.

If that happens, each affected streamer clicks **Authorize Channel Points** again; that re-creates
their subscriptions with the current secret. The relay also counts rejected deliveries and shows
them on the config page, so the symptom is at least visible.

Back it up rather than find out:

```sh
docker run --rm -v relay-data:/data -v "$PWD:/backup" alpine \
  tar czf /backup/relay-data-$(date +%F).tar.gz -C /data .
```

## Updating

```sh
git pull
docker compose up -d --build
```

The volume is untouched by this, so balances and credentials carry over.

If you also update the overlay app, update the **Twitch Extension** at the same time — the two are
versioned together, and a relay expecting something the installed extension doesn't send yet will
turn purchases away.

## Using your own reverse proxy

If the host already runs nginx, Traefik, or another Caddy, use the second compose file instead -
it brings up the relay alone, on a loopback port, and no Caddy:

```sh
docker compose -f docker-compose.existing-proxy.yml up -d
```

Then proxy your domain to `127.0.0.1:5080`, making sure to:

- serve it over **HTTPS on port 443** with a real certificate,
- forward WebSocket upgrades (the overlay connects to `/ws/ingest/...` and holds it open).

At minimum, `/oauth/callback` and `/eventsub/callback` must be reachable on port 443. The rest can
live elsewhere if you have a reason, but there's rarely one.

### Moving an existing host install into Docker

If you already run the relay on the host and your proxy points at `127.0.0.1:5080`, the container
can take over that exact port and **your proxy configuration doesn't change at all**.

Point `RELAY_DATA_PATH` at the data you already have, and set `RELAY_UID`/`RELAY_GID` to whoever
owns it (`stat -c %u:%g /var/lib/twitch-relay`) so nothing on disk needs chowning — and so falling
back to the host install stays a matter of starting the service again. Then stop the old service
before starting the container, since both want the same port:

```sh
sudo systemctl disable --now twitch-relay
docker compose -f docker-compose.existing-proxy.yml up -d
```

Keep the old unit file around until you're satisfied; rolling back is `docker compose down` and
`systemctl enable --now twitch-relay`.

## Troubleshooting

**`docker: unknown command: docker compose`.** Ubuntu's `docker.io` package doesn't include the
compose plugin. `sudo apt install docker-compose-v2`.

**`docker compose up` exits complaining about a variable.** A required entry in `.env` is empty.
The message names it.

**No certificate.** The domain has to already resolve to this host, and ports 80 and 443 have to be
free. `docker compose logs caddy` will say which.

**The overlay says Error.** Check the URL matches what's in `.env`, and that the overlay's Twitch
Integration tab has a stream key. `docker compose logs relay` shows connection attempts.

**The panel never loads anything, and nothing errors.** Almost always the extension's *Allowlist
for URL Fetching Domains* — add your relay domain to it. Twitch's CSP blocks the request before it
leaves the browser, so the panel just waits forever. The browser console will show a CSP violation.

**Bits purchases fail.** Usually `TWITCH_EXTENSION_SECRET` — check it's the base64 *Secret* from the
Manage page, not the client secret from the same page. If the extension is one you built yourself,
also check it's the secret from *your* extension.

**OAuth says the redirect doesn't match.** The Application's registered *OAuth Redirect URL* has to
equal `https://<RELAY_DOMAIN>/oauth/callback` character for character — including `https://`, no
trailing slash, and no port.

**Channel Points redemptions don't credit.** Check the config page shows Channel Points as
authorized; if it reports rejected deliveries, re-authorize. Also confirm the reward's title
exactly matches the *Token Reward Name* field, and that the reward has **Skip Reward Requests
Queue** enabled.
