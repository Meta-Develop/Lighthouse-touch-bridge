---
name: technical-writing-pedagogy
description: "Use when writing or reviewing durable technical explanations, academic/report prose, textbooks, tutorials, course notes, design notes, or explanatory documentation."
---

# Technical Writing Pedagogy

Use this skill when the task asks for explanatory writing, a tutorial,
textbook-like prose, a technical note, a concept explanation, or a review of
reader-facing technical content.

## Inputs

1. Read the project-local writing, privacy, citation, and build rules.
2. Read `.agents/docs/TECHNICAL_WRITING_PEDAGOGY.md` when present.
3. If the project has a stricter local writing skill, use that local skill as
   authoritative and treat this skill as a baseline checklist.

## Writing Workflow

For every substantial concept:

1. Start from a concrete phenomenon, failure mode, observation, or task.
2. Add a visual or geometric interpretation before dense notation when the
   concept has shape, direction, flow, frequency content, or parameter motion.
3. Introduce notation, assumptions, units, and scope only when they become
   needed.
4. Derive the main result step by step, tying each transformation to its
   meaning.
5. Vary one important parameter and show the changed behavior, graph, region,
   or conclusion.
6. End with the design, implementation, debugging, validation, or
   interpretation consequence.

## Academic And Report Prose Rules

- For papers, reports, textbooks, durable explanations, and Japanese technical
  prose, prefer conventional precise wording, domain-standard terms, and
  concrete verbs.
- Avoid coined metaphors, invented labels, over-hyphenated compounds,
  slash-heavy noun piles, decorative phrasing, hype, vague intensifiers,
  casual phrases, and narrative flourish.
- For Japanese academic/report passages, use plain evidence-first prose, often
  in a sober `である`, `した`, `示す`, and `考えられる` style when Japanese is the
  target language.
- Order result-bearing passages as purpose or question, method and conditions,
  result, interpretation, and limitation or next work.
- Introduce figures, tables, equations, and listings before interpreting them.
- Translate English technical terms only when a stable Japanese term exists, or
  define the chosen translation once and use it consistently.
- Do not use graduation-thesis padding conventions as the primary style target
  when the requested artifact is a paper, textbook, report, or durable
  technical note.

## Visual And Figure Rules

- Require diagrams, plots, block diagrams, schematics, or coordinate drawings
  for geometry, vectors, coordinate transforms, signal flow, feedback,
  frequency behavior, spectra, tuning, parameter sweeps, or tradeoffs.
- For manuscript and paper quantitative graphs, default to Python/Matplotlib
  generation unless the project has a stronger plotting stack. MATLAB is
  acceptable when data or analysis already lives there, or when MATLAB is the
  project-native reproducible plotting environment.
- Make legends, axis labels, tick labels, annotations, and line widths readable
  at the expected manuscript print or PDF size.
- Do not distinguish series by color alone; prefer grayscale-safe line styles,
  dash patterns, marker shapes, marker fills, hatch or pattern fills, dots, or
  direct labels where possible.
- Avoid embedded plot titles for paper figures because LaTeX captions and
  surrounding manuscript text should provide the title and context.
- Use English for words inside figures by default, unless a project, journal,
  or conference explicitly requires another language or the figure is quoting
  source material.
- If the project has no plotting stack and a figure cannot be generated in the
  current change, add a precise figure backlog item with the intended
  generation method.
- Keep code-authored figure types separated: use TikZ itself for block diagrams
  and flow diagrams, `pgfplots` for LaTeX-native graphs, and `circuitikz` for
  circuit diagrams.
- Do not use raw TikZ paths for quantitative graph curves when Python or
  `pgfplots` is the appropriate tool.
- Do not accept broken, blank, unlabeled, clipped, stale, or purely decorative
  visuals as satisfying the explanation. Text overlap, invalid geometry, and
  unreadable labels are figure failures.
- Check rendered output when the target format can be built or previewed.

## Frequency-Domain Rules

- Bridge from time-domain signals to sinusoidal or complex-exponential
  decomposition before using frequency-domain notation.
- Distinguish Fourier analysis from Laplace analysis when both are relevant:
  Fourier for frequency content and steady components; Laplace for dynamics,
  transients, initial conditions, poles, zeros, and stability.
- Use harmonic partial sums, such as square-wave approximations, when they help
  readers see how composition changes a waveform.

## Exercise Rules

- Replace detached problem dumps with embedded checkpoints or integrated design
  tasks tied to the preceding explanation.
- Good checkpoints ask the reader to interpret a plot, predict a trend, vary a
  parameter, finish a derivation step, find a failure case, or verify a result.

## Review Output

```text
Pedagogy reviewed:
Missing motivation or flow:
Missing visuals or figure generation:
Tooling separation issues:
Missing parameter variation:
Frequency-domain bridge issues:
Exercise integration issues:
Academic/report prose issues:
Required edits:
Completion status: pass / needs revision
```
