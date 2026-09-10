# Skilly Snap

Selected concept A.

![Skilly logo](skilly-logo.png)

- [Horizontal logo](skilly-logo.png), PNG with a light background.
- [App icon](skilly-icon.png), PNG with transparent outer padding.
- [Generation prompts](generation.md), including the source concept and export instructions.

The README uses the horizontal logo. The app embeds the PNG icon for its header and windows. Windows Explorer uses `src/Skilly/skilly.ico`, generated from the same PNG with 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixel frames.

After changing the source icon, regenerate the Windows icon from the repository root:

```powershell
.\scripts\Update-BrandIcon.ps1
```
