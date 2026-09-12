---
name: codex-wallpaper-switcher
description: Apply a user-provided local image as the official Codex Desktop wallpaper and verify that the live renderer is using that exact image. Use when the user asks to换壁纸、用这张图做 Codex 背景、切换 Codex 壁纸 or similar.
---

# Codex Wallpaper Switcher

Use the installed `codex-dream-skin` Windows runtime. This skill changes only the reversible Dream Skin layer; never edit `WindowsApps`, `app.asar`, the official package, or the user's original image.

## Workflow

1. Identify the image path supplied by the user. Accept JPEG, PNG, or WebP. Preserve the source and create a separately named theme package.
2. Prepare a simple theme package with exactly `theme.json`, `theme.css`, and the copied image. Run `scripts/prepare-theme.ps1 -ImagePath <absolute-path>` and use its returned `ThemeDir`.
3. Prefer the package directory returned by the helper (normally under the user's Pictures folder), not `%LOCALAPPDATA%\CodexDreamSkin\active-theme`; Store-app virtualization can make the latter fail the runtime's reparse-point safety check.
4. If a verified Dream Skin state exists, read `port` and `browserId` from `%LOCALAPPDATA%\CodexDreamSkin\state.json` and run the installed injector in one-shot mode:

   ```powershell
   & $node $injector --once --port $port --browser-id $browserId --theme-dir $themeDir --timeout-ms 15000
   ```

   Resolve `$node` from `node` (Node.js 22+; the user's `D:\Node.JS\node.exe` is valid) and `$injector` from `$env:USERPROFILE\.codex\skills\windows\scripts\injector.mjs`.
5. Verify with the same `port`, `browserId`, and `themeDir`, saving a proof screenshot beside the package:

   ```powershell
   & $node $injector --verify --port $port --browser-id $browserId --theme-dir $themeDir --screenshot $proofPath --timeout-ms 15000
   ```

   Require `installed: true`, `themeId` matching the new package, `stylePresent: true`, and `pass: true`. Show the proof screenshot to the user.
6. If no verified state/CDP endpoint exists, use the official Dream Skin start/launcher path once, wait for Codex to return, then repeat steps 4–5. Do not claim success from a desktop wallpaper visible outside the Codex window; the proof must show the image inside the Codex renderer.

## Failure handling

- If the renderer still shows a previous image, stop the old injector before starting a watcher for the new package, then run one-shot apply and verify again.
- If output says `runtime not found`, the user launched an old copy; point to the current launcher or the installed `windows` skill path.
- If verification fails, report the exact failure and keep the original image and package intact. Do not repeatedly restart Codex.

The helper script is only for deterministic package creation; applying and verifying remain delegated to the installed Dream Skin injector so native Codex controls remain live.
