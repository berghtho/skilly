# Skilly Snap artwork

Selected direction: A / Snap, chosen by the owner on 2026-09-10.

The logo export is 2172 x 724 pixels, opaque RGB. The icon export is 1254 x 1254 pixels with an alpha channel. Checked outer padding samples have zero alpha; checked interior samples have alpha 254 of 255.

## Icon transparency correction

Use case: background-extraction.
Input image: edit target, the chosen Skilly desktop app icon.
Remove the entire gray checkerboard-pattern background outside the charcoal rounded-square tile. The checkerboard in this input is incorrectly baked into an opaque RGB image. Replace ALL pixels outside the tile with actual transparent alpha, opacity zero. This includes all four outer corners and all padding beyond the tile edges. DO NOT paint another checkerboard, gray color, white color, or black color to simulate transparency. Deliver a PNG cutout with a real alpha channel.
Keep the charcoal tile and bright chartreuse interlocking S symbol absolutely unchanged. Preserve the exact silhouette, proportions, color, padding, geometry, and orientation. The interior of the tile, including the black spaces between the S strokes, stays fully opaque. Clean antialiased tile edges. No glow, no shadow, no backdrop, no labels, no additional art.
This task is only removal of the fake background and preservation of the existing app icon. True transparent background required.

- `skilly-logo.png`: horizontal logo on a light background.
- `skilly-icon.png`: lime symbol on a charcoal app tile, with transparent outer corners.

Generated with the built-in image_gen tool from the owner-selected A / Snap concept. The PNG files in this directory are the source artwork used by the app and README.

## skilly-logo.png

Use case: precise-object-edit.
The supplied image is the approved Skilly concept A / SNAP. Use it as the edit target. The user selected THIS EXACT design. Preserve the interlocking angular S symbol, its negative-space center, optical proportions, softened external corners and crisp inner cuts. Preserve the logo's bright chartreuse/lime color. Clean crisp flat graphic edges, no visible texture, glow, bloom, lighting, shadows, gradients or 3D effects. Do not redesign the mark, introduce other imagery or invent a new symbol. Remove presentation labels and any unwanted extra copies.
Create the final standalone horizontal logo image: one lime S symbol on the left, followed by the EXACT same custom heavy geometric lowercase "skilly" wordmark from the reference hero. Preserve its angular k, cut/slanted i dot, upright double l, letter spacing and y shape. Wordmark solid charcoal #1D1F20. Spell skilly exactly.
Use a wide 3:1 canvas with a fully OPAQUE near-white #F2F2F3 background, including every pixel in all four corners. This light field is part of the artwork and must remain visible. Center the entire horizontal lockup with generous even margins; occupy approximately 78% of canvas width. The mark is slightly taller than the text. One logo only; no A / SNAP label, no additional icon tile, no sample marks, no captions or frame. Preserve the approved design while making the contrast clear. Final polished two-dimensional brand artwork.

## skilly-icon.png

Use case: precise-object-edit.
The supplied image is the approved Skilly concept A / SNAP. Use it as the edit target. The user selected THIS EXACT design. Preserve the interlocking angular S symbol, its negative-space center, optical proportions, softened external corners and crisp inner cuts. Preserve the logo's bright chartreuse/lime color. Clean crisp flat graphic edges, no visible texture, glow, bloom, lighting, shadows, gradients or 3D effects. Do not redesign the mark, introduce other imagery or invent a new symbol. Remove presentation labels and any unwanted extra copies.
Create the final standalone square desktop app icon. Use ONLY the bottom-right app-icon application of A / SNAP: a solid charcoal #1D1F20 rounded-square tile containing the identical bright lime interlocking S symbol. No lettering anywhere. No A / SNAP label, no wordmark, no sample icons.
Square image at high resolution. Center a rounded-square tile at 84% of canvas width/height, leaving even padding outside; the S should occupy approximately 64% of the tile width and 74% of the tile height. Preserve the exact approved S construction. Tile is opaque charcoal, symbol opaque solid lime, holes in the S reveal the charcoal. Corners outside the tile must be genuinely transparent alpha with no shadow or halo. Sharp silhouette designed to remain readable at small desktop-icon sizes. Flat colors only, perfectly clean.
