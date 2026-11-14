# DB Cowboy / Rowdy identity

The mascot-led identity for DB Cowboy: the logo, the Windows icon, **three avatars** (Rowdy, Dash and Dot) and hero and sharing graphics.

- Start with `DESIGN.md` for roles, palette, type and usage, and `VOICE.md` for wording.
- `logo/package` is the logo kit: SVG masters, PNG renders, favicons, the Manrope font with its license, and color tokens.
- `icons/windows.ico` is the Windows icon; `app-1024.png` is its source. Supporting web icons include a separately framed maskable source.
- `graphics` includes matching transparent light/dark mascot heroes, social/share graphics, profile art and a banner.
- `packs/avatars` holds the three connection avatars.
- `brand-contract.json` is the color and type contract the app follows; `contrast.json` records the checked color pairs; `LICENSES.json` states where each asset comes from.

The app ships its own copies of the art it uses, in `src/DbCowboy/Assets`; the four Rowdy reactions live only there. The website is in `site/` at the repository root.

Windows binaries are unsigned. No trademark clearance is claimed for the artwork.
