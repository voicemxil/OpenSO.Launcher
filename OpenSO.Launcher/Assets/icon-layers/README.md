# macOS app icon — Icon Composer layers

Source layers for building the **Liquid Glass** app icon (`openso.icon`) in Apple's **Icon Composer**
(Xcode 26 / macOS Tahoe era). Each is a 1024-rendered SVG sharing a `0 0 512 512` viewBox, so they align
when stacked.

**Layer order (bottom → top):**
1. `openso-background.svg` — navy gradient fill (the icon background).
2. `openso-ring.svg` — the teal ring.
3. `openso-plumbob.svg` (faceted) **or** `openso-plumbob-flat.svg` (flat) — the plumbob.
   Prefer the **flat** one for Liquid Glass and let Icon Composer add the depth/specular.

## Building it (no Xcode build needed)

1. Open **Icon Composer**, New → macOS icon (1024 canvas).
2. Set/replace the **Background** with `openso-background.svg` (or a gradient fill in the inspector).
3. Add `openso-ring.svg` then a plumbob layer as **foreground groups**; order ring under plumbob, center them.
4. In the inspector, tune the **Liquid Glass** material per group — specular highlight, shadow, blur, opacity.
5. Preview the **Default / Dark / Mono(Tinted) / Clear** variants and adjust so each reads.
6. **File → Export** → save as `openso.icon` into `OpenSO.Launcher/Assets/` and commit it.

The release CI then compiles it with `xcrun actool` into `Assets.car`, references it via `CFBundleIconName`,
and ships it in the `.app`/DMG — with the legacy `.icns` (rendered from `openso-glyph.svg`) as the pre-26
fallback. No workflow change needed; the build auto-detects `Assets/openso.icon`.
