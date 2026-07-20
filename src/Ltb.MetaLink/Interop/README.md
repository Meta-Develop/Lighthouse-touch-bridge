# LibOVR interop boundary

This directory contains an original managed declaration of only the public C
ABI surface used from Oculus PC SDK 32.0.0. No Meta SDK header, import library,
or runtime binary is copied or redistributed. At runtime, LTB loads the user's
installed `LibOVRRT64_1.dll` by its absolute Meta Quest Link installation path.

The targeted initialization contract is `ovrInit_Invisible |
ovrInit_RequestVersion` (`0x14`) with requested minor version `64`. The x64
layout guards pin `ovrBool` to 1 byte and the used structures to these sizes:

| Public C type | Bytes |
| --- | ---: |
| `ovrGraphicsLuid` | 8 |
| `ovrQuatf` | 16 |
| `ovrVector2f` | 8 |
| `ovrVector3f` | 12 |
| `ovrPosef` | 28 |
| `ovrPoseStatef` | 88 |
| `ovrTrackingState` | 312 |
| `ovrInputState` | 120 |
| `ovrSessionStatus` | 9 |
| `ovrInitParams` | 32 |
| `ovrErrorInfo` | 516 |

`ovr_GetTrackingState` returns the 312-byte `ovrTrackingState` by value. It is
isolated behind `IOvrTrackingStateReturnBoundary`; ordinary adapter code and
fakes never invoke its delegate directly. Managed layout tests on Linux prove
sizes and offsets, not the Windows x64 native calling convention. Release
acceptance therefore requires a Windows x64 ABI-oracle test compiled against
the official SDK 32.0.0 header and run against the installed Meta runtime,
followed by live Quest Link pose/input verification. A Linux test result must
never be reported as satisfying that gate.
