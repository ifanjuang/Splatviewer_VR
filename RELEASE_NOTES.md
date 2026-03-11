# Splatviewer_VR Release Notes

## Version 1.0

Initial public release of the VR-focused Gaussian splat viewer.

### Included

- Windows standalone build.
- OpenXR VR support.
- Runtime loading for `.ply` and `.spz` splat files.
- VR locomotion with smooth movement and snap turn.
- Controller-based splat switching.
- Desktop keyboard and mouse fallback.

### Controls

#### VR

- Left stick: move.
- Right stick X: snap turn.
- Right stick Y: move up and down.
- Right controller `B`: next splat.
- Right controller `A`: previous splat.

#### Desktop fallback

- `W A S D`: move.
- `Q / E`: move down / up.
- Hold right mouse button: look.
- `PageDown` or `N`: next splat.
- `PageUp` or `P`: previous splat.

### Notes

- Bring your own Gaussian splat files.
- A D3D12-capable Windows system and an OpenXR runtime are recommended.
- The repository contains source and project files; the downloadable release package contains the built player.