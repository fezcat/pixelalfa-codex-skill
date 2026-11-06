---
name: pixelalfa
description: Review and clean pixel-art PNG transparency. Use for stray background pixels, uncertain edges, remove and protect marks, outline repair, or exact RGBA checks after export.
---

# PixelAlfa

Use local PNG tools to inspect decoded RGBA values, group plausible residue, and verify the export.

1. Identify the source PNG, requested edit, and details to preserve. Use native pixel coordinates and a separate output path.
2. Examine alpha, color, and neighboring pixels. Start with edge-connected background or repeated residue inside an opening. Use explicit remove and protect marks when the same color appears in background and artwork.
3. Preview the proposed changes. Leave ambiguous outline, fringe, and filled-hole candidates out of the applied edit until reviewed.
4. Apply the agreed changes. Verify dimensions and report removals, additions, alpha changes, and RGB changes separately. If the user supplies an allowed-change mask, compare every decoded RGBA pixel outside it exactly.

Read the reference guide for candidate review, outline repair, or precise RGBA comparison. [Editing and verification](references/editor.md)
