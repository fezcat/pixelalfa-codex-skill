# PixelAlfa Codex Skill

Review pixel-art PNGs for stray background pixels, transparency issues, and outline damage. The `$pixelalfa` skill guides Codex through inspecting the source, proposing a small edit, and checking the result pixel by pixel. It works with local PNG tools and can guide the PixelAlfa editor when the app is already installed.

## Quick start

Clone the repository, open the cloned folder in Codex, and invoke `$pixelalfa`.

```sh
git clone https://github.com/fezcat/pixelalfa-codex-skill.git
cd pixelalfa-codex-skill
```

To use the skill in an existing project, copy `.agents/skills/pixelalfa/` into that project's `.agents/skills/` directory. Keep `references/editor.md` inside the skill folder. To install it for personal use, ask Codex to run `$skill-installer` with the repository `fezcat/pixelalfa-codex-skill` and the path `.agents/skills/pixelalfa`. [Codex skill discovery and installation](https://learn.chatgpt.com/docs/build-skills#where-codex-loads-local-skills)

## Example prompts

**Review a sprite before editing**

> $pixelalfa Inspect sprite.png at native pixel coordinates. Separate likely background residue from uncertain outline pixels. Report each group's size and representative coordinates.

**Remove confirmed residue**

> $pixelalfa Remove the confirmed background residue from sprite.png. Keep the dark outline and translucent edge pixels. Save the result as sprite-cleaned.png and report what changed.

**Verify an export against a mask**

> $pixelalfa Compare sprite.png and sprite-cleaned.png using allowed-edits.png. Check the dimensions and report any RGBA differences outside the mask.

**Repair a broken outline**

> $pixelalfa Inspect the gap in the outer outline of sprite.png near (45, 52). Preview a repair that follows the existing contour and preserves nearby shading.

## How the workflow works

1. Open the PNG at its original resolution and identify details to preserve.
2. Group plausible residue by location and color. An edge-connected background and isolated pixels inside an opening need different evidence. Mark a pixel for protection if it shares a background color but belongs to the sprite.
3. Inspect the proposed edit at high zoom. Check any uncertain edge pixels individually before applying a change.
4. Export to a separate PNG and compare it with the source. A change mask, when available, defines where pixel differences are allowed.

## What to look for in the report

- **Image dimensions** show whether the export kept the source pixel grid.
- **Removed and added pixels** count pixels that became fully transparent or became visible.
- **Alpha and RGB changes** report the channels that changed. A pixel can count in both totals.
- **Uncertain candidates** list changes that still need review.
- **Changes outside the mask** identify edits that reached beyond the allowed area. A zero count confirms that every decoded RGBA pixel outside the mask matches the source.

The repository contains the skill instructions and its reference guide. PixelAlfa is an optional local editor. When using Codex alone, the available PNG reader determines which bit depths can be checked exactly.

- [Skill instructions](.agents/skills/pixelalfa/SKILL.md)
- [Editor controls, candidate review, and RGBA verification](.agents/skills/pixelalfa/references/editor.md)
