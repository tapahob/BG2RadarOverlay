# Wanted posters (not shipped)

For a while the party list rendered each member as a wild-west bounty poster: aged paper, torn
edges, WANTED over the name, DEAD OR ALIVE, and a reward figure scaled off their remaining HP.
The panel now uses the plain list it had before that — name, race/class, HP, separated by hairline
rules. Everything the poster design needed is kept here so it can be looked at, reused, or put
back without digging through history.

Nothing in this folder is loaded by the extension, and none of it goes into `Extension.zip`.

## What's here

| File | What it is |
| --- | --- |
| `wanted-poster.svg` | The paper itself — 6 KB, scales to any card size, no web font (an extension's CSP blocks external fonts, so all the lettering is CSS text over the artwork). |
| `generate-poster.py` | Generates the SVG. The torn edge is seeded noise rather than hand-placed points, so it reproduces byte for byte and doesn't read as a decorative zigzag. |
| `poster.css` | The `.member` rules as they shipped — paper background, the WANTED/name/meta/DEAD OR ALIVE/bounty stack, and the half-degree alternating tilt. |
| `poster-render.js` | The `renderParty()` that emitted that markup, plus `bounty()` — the flavour reward figure. |
| `wanted-preview.html` | Renders a full party of posters at the panel's real 280px width. Open it directly in a browser, no server needed. |

## Putting it back

Three edits, all in the shipped component:

1. In `video_component.html`, replace the `.member` block in the stylesheet with `poster.css`.
2. In `video_component.js`, replace `renderParty()` with the contents of `poster-render.js` —
   it brings `bounty()` with it.
3. Copy `wanted-poster.svg` up into `TwitchIntegration/Extension/` and add it to the file list in
   the `Extension.zip` build. **Without it the background 404s once uploaded** — the zip is the
   only thing Twitch serves, so an asset that isn't in it doesn't exist as far as the panel is
   concerned.

The poster CSS assumes a card around 280px wide. It was never tried at the component sizes the
extension now uses, so expect the WANTED lettering and the bounty row to need re-tuning if the
panel is much wider or narrower than that.

## Why it was rolled back

A design call, not a technical one — the plain list reads faster at a glance, which is what a
viewer actually wants from a party panel they're watching out of the corner of one eye.
