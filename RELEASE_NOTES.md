# Splatviewer_VR Release Notes

## Version 1.0

Initial public release of the VR-focused Gaussian splat viewer.

### Included

- Windows standalone build.
- OpenXR VR support.
- Runtime loading for `.ply` and `.spz` splat files.
- Command-line opening of `.ply` and `.spz` files.
- VR locomotion with smooth movement and snap turn.
- Controller-based splat switching.
- Desktop mode with mouse and keyboard controls when no headset is active.
- Fullscreen windowed startup.

### Controls

#### VR

- Left stick: move.
- Right stick X: snap turn.
- Right stick Y: move up and down.
- Right controller `B`: next splat.
- Right controller `A`: previous splat.

#### Desktop fallback

- `W A S D`: move.
- Mouse: look.
- `SPACE / C`: move down / up.
- `R / F`: next / previous splat.
- `Q / E`: rotate the current splat.
- `Home`: reset splat rotation.
- `End`: flip upside down.

### Notes

- Bring your own Gaussian splat files.
- A D3D12-capable Windows system and an OpenXR runtime are recommended.
- The repository contains source and project files; the downloadable release package contains the built player.
- Windows file associations can launch the viewer directly with `.ply` or `.spz` files when the executable is registered as the open command.