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
managed/native coverage, packaging, and fail-safe lifecycle are present. The
internal-driver workflow detects the pinned OpenVR driver header from the
checked-out source. When present, it automatically enables the complete
Windows driver build, PE-import check, and portable-package path; only its
absence defers that full path while retaining the portable managed and native
checks.

No Windows hardware/runtime acceptance evidence is claimed yet. Every required
live check remains unchecked in the
[Windows internal-driver verification checklist](docs/windows-internal-driver-verification.md).
Automated build, test, package, and headless GUI checks do not replace Windows
Avalonia visual inspection or live SteamVR, Quest Link, and connected-hardware
verification.

## Start LTB

Use the complete packaged `Ltb.Gui.exe` directory on the Windows SteamVR host.
The supported daily sequence is:

1. Start the official Meta Horizon Link PC application and establish Quest
   Link or Air Link.
2. Keep both Touch controllers awake and responding in the Meta runtime.
3. Start SteamVR with the intended Lighthouse HMD as the sole HMD.
4. Power the two Lighthouse trackers mounted to the controllers and wait for
   valid raw poses. Other physical trackers may remain connected when saved
   profiles identify the mounted pair or a fresh association can select it
   unambiguously.
5. Run `Ltb.Gui.exe`, use the **Setup** tab, and press **Start**.

**Start** is the primary daily-use action and reuses an exact matching
left/right profile pair. **Calibrate / Recalibrate** is secondary, available
only while stopped, and bypasses saved profiles for a fresh two-hand capture.
Fresh association scores the connected physical tracker candidates during
separate left- and right-hand motion prompts and fails closed unless one
distinct pair wins unambiguously.

Before either action, **Setup** performs a live pre-press probe of prerequisites
that are safely knowable without starting a session. Rows show pass
(**Ready**), wait (**Waiting**), **Action required**, or **Deferred until
Start** states. A missing knowable prerequisite disables both **Start** and
**Calibrate / Recalibrate** and gives both actions the same specific
remediation from the same probe snapshot.

Driver registration or update is transactional and may not be provable or
performable by the read-only probe. Final verification that SteamVR loaded
exactly the two first-party controllers from the staged build may also be
deferred until **Start**. If the Start transaction changes registration, stop
LTB, restart SteamVR when prompted, and press **Start** again.

The persistent header keeps phase, overall status, and the evidence-origin badge
visible across the tabs:

- **Setup** — ordered prerequisites, guidance, and primary actions.
- **Status** — readiness groups, per-hand tracker/input/publication state,
  neutral reasons, and driver-feed health.
- **Calibration** — the guided two-hand capture workspace.
- **Diagnostics (Debug)** — opt-in, session-local evidence capped at 10 Hz,
  retaining at most 600 samples (60 seconds).

Diagnostics timing is a software-boundary lower bound. It excludes hardware
acquisition, the SteamVR compositor and display, and motion-to-photon
acceptance. Driver removal and the unsupported legacy/migration notice remain
the only contents of the collapsed, low-prominence **Advanced maintenance and
legacy migration** surface; it is not a legacy daily-use path. Stop from the
GUI before changing hardware.

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
