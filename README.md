# Lighthouse Touch Bridge

Meta Touch inputs. Lighthouse tracking. Two first-party SteamVR controllers.

Lighthouse Touch Bridge (LTB) is a Windows utility for mixed-VR systems where
a Lighthouse HMD, initially Bigscreen Beyond 2/2e, is used with Quest Touch
controllers and two rigidly mounted Lighthouse trackers. The trackers provide
the runtime poses; the official Meta Horizon Link runtime provides the Touch
inputs and calibration-time Touch poses.

## Current supported path

The first-party internal-driver path is the project default. Its only accepted
external runtime dependencies are:

- SteamVR; and
- the official Meta Horizon Link PC runtime, using Quest Link or Air Link.

LTB supplies its own `driver_ltb` SteamVR driver. ALVR, Virtual Motion Tracker
(VMT), and SteamVR `TrackingOverrides` are not dependencies of the supported
path.

The internal-driver implementation, default Avalonia desktop flow, automated
managed/native coverage, packaging, and fail-safe lifecycle are present. No
Windows hardware/runtime acceptance evidence is claimed yet. Every required
live check remains unchecked in the
[Windows internal-driver verification checklist](docs/windows-internal-driver-verification.md).

## Start LTB

Use the packaged `Ltb.Gui.exe` on the Windows SteamVR host. The window opens on
the **First-party internal driver** flow; **Start** runs the supported path with
no device indexes, VMT slots, driver paths, or settings paths to enter.

1. Connect Quest to the official Meta Horizon Link runtime and keep both Touch
   controllers awake.
2. Start SteamVR with the intended Lighthouse HMD as its sole HMD and connect
   the two physical Lighthouse trackers mounted to the controllers. Other
   Lighthouse trackers used for full-body tracking may remain connected when
   saved left/right profiles identify the controller-mounted pair.
3. Run `Ltb.Gui.exe` and press **Start**.
4. If LTB registers or updates `driver_ltb`, restart SteamVR when the GUI asks,
   then press **Start** again.
5. Follow the separate left- and right-hand movement prompts if the mounts need
   calibration. Stop from the GUI before changing hardware.

Normal **Start** reuses an exact matching left/right profile pair. Press
**Calibrate / Recalibrate** while stopped to bypass saved profiles and capture
both hands again; fresh association requires exactly two tracker candidates, so
power off unrelated trackers first.

See [Internal driver operations](docs/internal-drivers.md) for discovery,
readiness, calibration, paths, keep-awake guidance, and failure behavior.

## Architecture

```text
Quest + Touch
  -> official Meta Horizon Link runtime
  -> Ltb.MetaLink
  -> Ltb.App calibration and pose composition
  -> same-user local named pipe
  -> first-party driver_ltb
  -> exactly two SteamVR controllers

Lighthouse HMD + two selected controller trackers (+ optional other trackers)
  -> SteamVR/OpenVR raw tracker poses
  -> Ltb.App
```

During calibration, LTB associates each mounted tracker with one hand, aligns
the Meta and Lighthouse streams in monotonic time, and estimates the fixed
mount transform. During active use it publishes
`T_output(t) = T_tracker(t) * X_mount`; Touch supplies controller inputs, while
the physical trackers supply the authoritative runtime poses.

## Calibration modes

- **Rotation-only** estimates mount orientation and uses the tracker origin as
  the controller position origin.
- **Full 6DoF** estimates mount orientation and translation when reliable Touch
  position and sufficiently rich motion are available.
- **Auto** validates rotation first, attempts translation only when observable,
  and retains rotation-only when translation is not reliable.

## Legacy migration material

The older ALVR/VMT/`TrackingOverrides` commands and documents remain buildable
only as historical migration material. They are unsupported, receive no new
production automation, and are not invoked by the GUI **Start** button. Their
references are clearly labeled under [legacy setup](docs/setup.md),
[legacy troubleshooting](docs/troubleshooting.md), and the
[legacy Windows checklist](docs/windows-verification.md).

## Repository layout

- `src/` contains the desktop/application layers and reusable runtime,
  calibration, configuration, Meta Link, protocol, and driver libraries.
- `native/driver_ltb/` contains the first-party SteamVR driver and its portable
  protocol/watchdog core.
- `tests/` contains managed unit, integration, and desktop tests.
- `tools/` contains recording-inspection and synthetic-data utilities.
- `docs/` contains the specification and focused architecture, operations,
  calibration, and verification documentation.

See the [complete specification](docs/specification.md) for the product,
coordinate, protocol, readiness, safety, and acceptance contracts.

## License

Lighthouse Touch Bridge is licensed under the
[GNU General Public License v3.0 or later](LICENSE).
