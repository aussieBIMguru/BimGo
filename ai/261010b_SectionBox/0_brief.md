# Section box round: brief (agreed with Gavin, 2026-10-10)

**Baseline:** daylight build B + Fix 1 (Gavin: works).

## Decisions
- Tools: **section box** fitted to the current level, drag its 6 faces with handles (free cursor), **plus a quick
  plane** at the aimed surface.
- Cut look: **solid caps** (stencil), **dark grey** by default with an **element-colour** option.
- Clipped geometry: **tools ignore it, collision stays** (fly to move freely).
- Shadows / light: the **whole building still casts** (only the view is cut).
- Keys: **P** box editor, **Shift+P** plane at the aimed surface, **Ctrl+P** clear.
- Saved with the model, **bookmarks remember cuts** (and comment views), **BCF clipping planes** out and in.
- Shift+P **opens up what you aim at**: plane parallel to the surface, just behind the aimed element, removing your
  side (outside wall → the room behind; floor from above → the storey below).
- First box: the **current level** (footprint, level to just under the next level).
