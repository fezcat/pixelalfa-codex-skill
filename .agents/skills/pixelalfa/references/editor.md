# Pixel-art editing and verification

## Review background candidates

Inspect the image at its native pixel grid. Group edge-connected background separately from disconnected patches inside the sprite. If the target color is known, compare it with representative pixels in the suspected patch. Check neighboring alpha and RGB values before expanding the selection.

When the sprite and background share a color, use separate remove and protect coordinates or masks. Keep protect marks out of the proposed edit. Record uncertain pixels with zero-based `(x, y)` coordinates from the top-left corner, their RGBA values, and the neighboring pixels that informed your decision.

Preview candidates with an overlay or a separate mask so individual pixels remain inspectable. Label the colors used in the preview and keep its highlights separate from the exported image.

## Repair outlines

Identify the boundary being repaired and follow the adjacent pixel steps, thickness, and shading. For a short gap, preview the smallest repair that connects the neighboring segments. For a longer gap, use a start, midpoint, and end to define the intended path. Keep a separate output and verify the pixels outside the approved repair area.

## Verify an export locally

1. Decode the source and result using a local PNG reader that preserves RGBA at the precision you intend to verify. Check width and height before comparing any coordinates.
2. Compare all four channels at each coordinate, including transparent pixels when the decoder preserves their RGB values. File hashes can differ for PNGs with identical pixels because PNG encoding and metadata can differ.
3. Count pixels that became fully transparent, pixels that became visible, alpha changes, and RGB changes. These counts can overlap. Clearing an opaque pixel to transparent black, for example, can count as a removal, an alpha change, and an RGB change.
4. If an allowed-change mask is supplied, confirm that it shares the source dimensions. Any mask pixel with nonzero alpha permits an edit. Report every difference outside that area or provide a complete coordinate list as an artifact when the list is large.

An 8-bit RGBA comparison checks 8-bit decoded channel values. To make a claim about a 16-bit source, use a reader and comparison that preserve 16-bit values throughout.

## Handle uncertain edges

An isolated pixel can be intentional shading. A filled interior can be part of the design. Treat their colors and neighboring pixels as evidence, and ask for visual confirmation before removing an ambiguous pixel. Keep protected pixels out of the candidate mask and check them in the exported result.
