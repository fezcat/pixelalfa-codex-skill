# Pixel-art editing and verification

## Review background candidates

Use **Auto Scan** to find background candidates. Use **Eyedropper** when the target color is known, or **Connected Area** to isolate a single patch. When a color is shared by the background and the sprite, paint examples with **Remove** and **Protect**, then run **Suggest from Marks**.

Turn on **Show Differences** to inspect the proposed edits. Clicking a candidate row frames it in the preview. The checkbox decides whether it will be applied. Record uncertain pixels with zero-based `(x, y)` coordinates from the top-left corner, their RGBA values, and the neighboring pixels that informed your decision.

| Control | Label in the Turkish editor |
| --- | --- |
| Cleanup tab | `Temizle` |
| Auto Scan | `Otomatik tara` |
| Eyedropper | `Damlalık` |
| Connected Area | `Bağlı alan` |
| Remove mark | `Temizle` |
| Protect mark | `Koru` |
| Suggest from Marks | `İşaretlerden öner` |
| Show Differences | `Farkları göster` |
| Apply Selection | `Seçimi uygula` |

## Edit marks and repair outlines

- The **Cleanup** and **Pixel** tabs place the brush toolbar to the left of the image. The green outline in **Protect** mode shows the pixels the brush will cover. Press **+** or **−** over the canvas to adjust the size. The Cleanup eraser removes marks. The Pixel eraser changes the image.
- Use **Ctrl + mouse wheel** to zoom around the pointer. **Ctrl + Z** dismisses a pending preview first, then moves back through edit history.
- In the **Outline** tab (`Kontur`), choose the inner boundary, outer boundary, or both. Check thickness, opacity, and softness in the live preview. Use **Inner Repair** (`İç onarım`) or **Outer Repair** (`Dış onarım`) for short breaks. Use **Bridge** (`Köprüle`) for a longer gap by selecting its start, midpoint, and end.
- Save a `.pxa` project to keep the source pixels, marks, and layers. Export a PNG when you need the composited result of the visible layers.

## Verify an export locally

1. Decode the source and result using a local PNG reader that preserves RGBA at the precision you intend to verify. Check width and height before comparing any coordinates.
2. Compare all four channels at each coordinate, including transparent pixels when the decoder preserves their RGB values. File hashes can differ for PNGs with identical pixels because PNG encoding and metadata can differ.
3. Count pixels that became fully transparent, pixels that became visible, alpha changes, and RGB changes. These counts can overlap. Clearing an opaque pixel to transparent black, for example, can count as a removal, an alpha change, and an RGB change.
4. If an allowed-change mask is supplied, confirm that it shares the source dimensions. Any mask pixel with nonzero alpha permits an edit. Report every difference outside that area or provide a complete coordinate list as an artifact when the list is large.

An 8-bit RGBA comparison checks 8-bit decoded channel values. To make a claim about a 16-bit source, use a reader and comparison that preserve 16-bit values throughout.

## Handle uncertain edges

An isolated pixel can be intentional shading. A filled interior can be part of the design. Treat their colors and neighboring pixels as evidence, and ask for visual confirmation before removing an ambiguous pixel. Keep protected pixels out of the candidate mask and check them in the exported result.
