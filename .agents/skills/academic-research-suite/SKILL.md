---
name: academic-research-suite
description: >
  Codex-native Academic Research Skills suite for deep research, academic paper
  writing, manuscript review, full research-to-paper pipelines, and experiment
  planning or validation. Use when the user asks for literature review,
  systematic review, meta-analysis, research question refinement, academic paper
  drafting or revision, citation or integrity checks, reviewer simulation,
  editorial decision letters, experiment planning, statistical interpretation,
  human study protocol support, or ARS aliases such as ars-plan, ars-outline,
  ars-lit-review, ars-citation-check, ars-reviewer, and ars-full.
metadata:
  package: academic-research-skills-codex
  package_source: https://github.com/Imbad0202/academic-research-skills-codex.git
  package_version: v0.1.15
  package_ref: efdbc2a4a74ae3482e63535bbbcb2a125066d5de
  license: CC BY-NC 4.0
---

# Academic Research Suite Shim

This registry-managed skill exposes the pinned Codex-native Academic Research
Skills package at upstream `v0.1.15`
(`efdbc2a4a74ae3482e63535bbbcb2a125066d5de`). It intentionally points to the
Codex sibling repository `Imbad0202/academic-research-skills-codex`, not the
Claude-specific `Imbad0202/academic-research-skills` repository.

Before doing academic research-suite work, load the attached package router:

`.agents/external/academic-research-skills-codex/plugins/academic-research-skills/skills/academic-research-suite/SKILL.md`

Read that router first, then follow its workflow routing. Do not load the whole
suite by default. After the router selects a workflow, load only the relevant
`WORKFLOW.md` plus the specific agent, reference, template, command, or shared
files needed for the current user request.

If the attached router is missing, report that the required external package is
not attached or materialized. Do not silently fall back to the Claude-specific
ARS repository, a different branch, or an unpinned checkout. In a registry
maintenance context, the repair command is:

```bash
scripts/attach-agent-package --all --registry-only academic-research-skills-codex
```

For live projects, follow that project's registry synchronization policy before
attaching or syncing external packages.

The upstream package is copyright Cheng-I Wu and licensed under Creative
Commons Attribution-NonCommercial 4.0 International. Preserve attribution in
redistributed or adapted material, and do not treat this package as cleared for
commercial use unless the user confirms they have appropriate permission.
