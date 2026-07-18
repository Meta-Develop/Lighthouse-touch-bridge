# Lighthouse Touch Bridge

Meta Touch inputs. Lighthouse tracking. One SteamVR controller.

Lighthouse Touch Bridge is a Windows utility that combines Meta Touch controller inputs with poses from Lighthouse-tracked devices. It is designed for mixed-VR setups where a Lighthouse HMD such as Bigscreen Beyond is used with Quest Touch controllers whose runtime position and orientation are replaced by mounted Vive Trackers.

LTB automatically associates controllers and trackers, aligns their pose streams in time, and calibrates the fixed mount transform. It supports rotation-only calibration when Quest position is unavailable and full 6DoF calibration when reliable position data is present. At runtime, only the Lighthouse tracker pose is used; the Quest system remains connected to provide controller inputs.

## Status

The v0.1 implementation is complete. The calibration core, live recorder, VMT and TrackingOverrides integration, two-hand calibration wizard, reliability runtime, and win-x64 packaging are implemented, and the full test suite passes on Linux. Live Windows/SteamVR hardware verification is the remaining step, tracked in the [Windows verification checklist](docs/windows-verification.md).

## How It Works

During calibration, LTB compares synchronized Meta Touch and Lighthouse tracker pose streams to estimate the fixed transform between the mounted tracker and the controller. At runtime it combines that mount transform with the authoritative tracker pose using `T_output(t) = T_tracker(t) · X_mount`, then coordinates VMT and SteamVR TrackingOverrides so Touch supplies the inputs and Lighthouse supplies the pose.

## Calibration Modes

- **Rotation-only** estimates mount orientation and uses the physical tracker origin as the virtual controller position origin.
- **Full 6DoF** estimates both mount orientation and translation when reliable Touch position data and sufficiently rich motion are available.
- **Auto** solves and validates rotation first, attempts translation only when observable, and falls back to rotation-only when the full transform is not reliably better.

## Repository Layout

- `src/` contains the application and reusable runtime, calibration, and configuration libraries.
- `tests/` contains calibration, configuration, and integration test projects.
- `tools/` contains recording-inspection and synthetic-data command-line utilities.
- `docs/` contains the specification and focused architecture, calibration, setup, and troubleshooting documentation.

See the [complete specification](docs/specification.md) for product requirements, coordinate conventions, calibration design, integration constraints, and acceptance criteria.

## Roadmap

- **Milestone 0 — Offline calibration proof:** recover known transforms from synthetic data and report calibration quality.
- **Milestone 1 — Live recorder:** discover SteamVR devices, record pose streams, replay captures, and estimate stream lag.
- **Milestone 2 — One-hand live bridge:** apply one VMT transform and one safe hand override.
- **Milestone 3 — Two-hand calibration wizard:** automate association, guided capture, model selection, and profile persistence.
- **Milestone 4 — Reliable daily use:** add startup sequencing, recovery, watchdog behavior, rollback, packaging, and complete documentation.
- **Milestone 5 — Generalization:** support additional Meta Touch, Lighthouse tracker, and Lighthouse HMD combinations.

## License

Lighthouse Touch Bridge is licensed under the [GNU General Public License v3.0 or later](LICENSE).
