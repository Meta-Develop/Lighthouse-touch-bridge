# Custom SteamVR Driver Evaluation

## Decision

Milestone 5 does not justify a custom SteamVR driver. Lighthouse Touch Bridge
should continue to use VMT for the tracker-local virtual pose and SteamVR
`TrackingOverrides` for pose substitution while Windows hardware verification
collects evidence about that integration. This is the investigation requested
by [specification section 24, "Milestone 5 — Generalization"](specification.md);
it is not a driver design or implementation plan.

The decision follows the scope set by [specification section 21, "MVP
Scope"](specification.md), which excludes a custom controller driver, and
[section 26, "Project Assessment"](specification.md), which says to reconsider
one only if empirical testing shows that VMT or `TrackingOverrides` prevents
reliable input/pose composition. The current Linux environment proves
deterministic adapters and coordinator behavior but cannot supply that
empirical SteamVR and hardware evidence.

## Existing integration boundaries

The current path deliberately keeps calibration independent of SteamVR:

```text
physical tracked pose + T_T_C
              |
              v
  VMT serial-following virtual pose
              |
              v
 SteamVR TrackingOverrides mapping
              |
              v
 semantic left/right hand with Meta Touch inputs
```

[Specification section 15, "VMT Integration"](specification.md), assigns one
stable VMT device to each hand and requires any VMT convention conversion to
remain in one adapter. [Section 16, "SteamVR
TrackingOverrides"](specification.md), assigns settings backup, safe merge,
validation, rollback, and stale-source cleanup to the coordinator. The
implemented boundaries expose the following concrete limitations.

| Boundary | Current responsibility | Limitation visible in the codebase |
| --- | --- | --- |
| VMT OSC control | `VmtClient` enables automatic pose updates and sends the serial-following `T_T_C` transform with `/VMT/Joint/Driver`. | VMT is an external runtime dependency. LTB must observe its heartbeat, use bounded slots `0..57`, and own loopback response port `39571`, which cannot be shared with VMT Manager during a run. |
| VMT device registration | LTB requests Tracker mode and then discovers the actual registered OpenVR path. | VMT honors a slot's device class on its first registration in a SteamVR process. A slot first registered in another mode requires a SteamVR restart before LTB can obtain the required tracker class. |
| Virtual-source identity | The coordinator re-enumerates the VMT device and verifies its path, class, connectivity, identity, and composed pose. | Activation depends on a separately registered virtual device and its runtime path. The physical-pose capability model must explicitly exclude VMT paths so a virtual output cannot be selected as its own physical source. |
| `TrackingOverrides` | `SteamVrSettingsManager` maps one discovered `/devices/<driver>/<device>` source to one semantic hand, preserves unrelated settings, writes atomically, verifies the result, and supports reviewed rollback. | The mapping is persistent JSON owned by SteamVR, not a transaction provided by the runtime. External writers do not honor LTB's sibling lock, conflicting source/hand owners must fail closed, and abrupt process or OS termination can prevent cleanup while leaving the persistent mapping in place. |
| Input/pose composition | The original Meta Touch controller remains the input device while the VMT source supplies the overridden pose. | Linux fakes cannot prove real input provenance, haptics, application bindings, restart behavior, or end-to-end pose timing. Those are still unchecked Windows hardware requirements rather than demonstrated failures. |

These constraints increase orchestration work, but none is currently evidence
that correct input/pose composition is impossible. The coordinator already
contains bounded dependency checks, exact-path ownership checks, composed-pose
verification, watchdog handling, SafeDisable, transactional two-hand apply,
settings backups, and recovery diagnostics around them.

## What a custom driver could change

A purpose-built driver could register LTB-owned virtual controller devices
directly. If it also implemented the required SteamVR input components and
received the Meta input state, it could remove the VMT OSC port, VMT slot, and
first-registration dependencies from the active path. Owning the final virtual
controller devices could also avoid persistent `TrackingOverrides` edits for
those devices and place pose publication and freshness policy at one driver
boundary.

Those are potential architectural benefits, not verified fixes. A driver would
not by itself:

- keep Meta Touch controllers connected or provide their input state; ALVR or
  another explicitly designed input transport would still be required;
- calibrate `T_T_C`, associate a physical tracker with a hand, or determine
  whether translation is observable;
- remove the need for stable identity matching, capability checks, profile
  persistence, tracker-loss handling, SafeDisable behavior, and diagnostics;
- prove compatibility with existing SteamVR applications, action bindings,
  controller component layouts, capacitive inputs, or haptics; or
- eliminate per-controller, per-tracker, per-HMD, SteamVR-version, reconnect,
  and failure-recovery hardware validation.

The driver therefore changes the integration boundary, not the calibration or
reliability problem. It may also create a second input-composition problem if
its virtual devices cannot reproduce the components and behavior applications
expect from the selected Meta Touch input profile.

## Engineering effort and risk

A supported custom driver would add a SteamVR driver lifecycle, device
registration, pose publication, input-component creation and update, optional
haptics routing, driver installation and removal, add-on enable/disable
recovery, version compatibility, and driver-specific diagnostics. It would
also require an authenticated boundary for any process-to-driver control
channel and strict validation of transforms, identities, and input messages;
moving code into a SteamVR-loaded component increases the consequence of
crashes and malformed data.

Maintenance would expand from the current narrow OpenVR client, VMT OSC, and
settings adapters to an installed runtime component whose behavior must be
revalidated across SteamVR changes and every supported device combination.
Packaging, signing, rollback, and security review would become release gates.
The repository currently has no Windows driver harness or hardware evidence
that would make this cost proportional to an observed defect.

## Decision triggers

Reopen this decision only when redacted Windows evidence demonstrates at least
one reproducible limitation that remains after the existing adapter and safety
paths are corrected. Relevant triggers are:

- `TrackingOverrides` cannot preserve the selected Meta Touch input components
  while VMT supplies pose for a supported combination;
- VMT cannot meet a measured pose-timing or stability requirement despite a
  valid tracker stream and verified transform;
- VMT slot registration, heartbeat, or OSC ownership prevents reliable daily
  use in a way that cannot be contained by startup cleanup and recovery;
- persistent settings ownership produces an unresolvable correctness or safety
  failure under the documented transaction and rollback rules; or
- multiple supported hardware families reproduce the same integration failure,
  indicating a boundary problem rather than one descriptor or runtime profile.

Any reconsideration should include a minimal failing capture, affected versions
and device families, redacted settings and event evidence, the acceptance
criterion the current path misses, and a comparison with the smallest adapter
or orchestration correction. A preference for fewer dependencies, by itself,
is not sufficient.

## Recommendation

Retain VMT and `TrackingOverrides` for Milestone 5, complete the capability-
based device generalization, and use the expanded Windows matrix to determine
whether the current path actually fails. Do not implement a custom driver or
an ALVR fork now. Reassess only after a documented, repeatable input/pose,
timing, or recovery failure satisfies one of the decision triggers above and a
narrower correction has been ruled out.
