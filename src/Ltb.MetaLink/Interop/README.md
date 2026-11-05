# LibOVR interop boundary

This directory contains an original managed declaration of only the public C
ABI surface used from Oculus PC SDK 32.0.0. No Meta SDK header, import library,
or runtime binary is copied or redistributed. At runtime, LTB loads the user's
installed `LibOVRRT64_1.dll` by its absolute Meta Quest Link installation path.

The targeted initialization contract is `ovrInit_Invisible |
ovrInit_RequestVersion` (`0x14`) with requested minor version `64`.
`ovrInit_Invisible` is `0x00000010` and `ovrInit_RequestVersion` is
`0x00000004`. The x64
layout guards pin `ovrBool` to 1 byte and the used structures to these sizes:

| Public C type | Bytes |
| --- | ---: |
| `ovrBool` | 1 |
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

These values are pinned in [`OvrAbi.cs`](OvrAbi.cs) and
[`OvrAbiLayoutTests.cs`](../../../tests/Ltb.MetaLink.Tests/OvrAbiLayoutTests.cs).
Runtime guards check sizes and contract-bearing offsets; the tests also assert
the initialization constants and parameters. Discovery uses the 32-bit
`HKLM\SOFTWARE\WOW6432Node\Oculus VR, LLC\Oculus` registration's `Base`
value plus `Support\oculus-runtime\LibOVRRT64_1.dll`, with no guessed-root
or filename-only fallback. See the
[operations guide](../../../docs/internal-drivers.md#exact-meta-horizon-link-discovery).

`ovr_GetTrackingState` returns the 312-byte `ovrTrackingState` by value. It is
isolated behind `IOvrTrackingStateReturnBoundary`; ordinary adapter code and
fakes never invoke its delegate directly. Managed layout tests on Linux prove
sizes and offsets, not the Windows x64 native calling convention. Release
acceptance therefore requires a Windows x64 ABI-oracle test compiled against
the official SDK 32.0.0 header and run against the installed Meta runtime,
followed by live Quest Link pose/input verification. A Linux test result must
never be reported as satisfying that gate. The oracle and live run remain
release requirements, not evidence supplied by the managed layout tests; retain
the results with the
[Windows checklist](../../../docs/windows-internal-driver-verification.md).
