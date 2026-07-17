# Technical Writing Pedagogy

Use this guidance for durable technical explanations, textbooks, tutorials,
course notes, design notes, and reviewable public documentation.

## Core Contract

- Teach from the reader's observable problem, not from the author's preferred
  formalism.
- Prefer one coherent explanatory path over a catalog of facts, formulas, and
  edge cases.
- Define symbols at the point of first serious use, and keep notation stable
  once introduced.
- Make every derivation answer a stated question.
- Close explanations by showing what the result changes in design,
  implementation, debugging, validation, or interpretation.

## Academic And Report Prose

Use this guidance for technical papers, reports, textbooks, durable design
notes, and other prose that must remain credible when read outside the current
chat. Prefer conventional precise wording over agent-made labels, coined
metaphors, decorative phrases, over-hyphenated compounds, slash-heavy noun
stacks, and paper-unfriendly slogans. Use domain-standard terms and concrete
verbs. If a compact coined term would help, introduce it only after defining the
phenomenon in ordinary technical language.

For Japanese academic or report writing, keep the prose plain, direct, and
evidence-first. The default tone is often the sober `である`, `した`, `示す`,
and `考えられる` style rather than casual chat phrasing. Avoid hype, vague
intensifiers, narrative flourish, and agent-invented terminology.

Use this order for result-bearing passages unless a project-local format is
stricter:

1. Purpose or question.
2. Method, assumptions, and conditions.
3. Result or observation.
4. Interpretation tied to the evidence.
5. Limitation, remaining uncertainty, or next work.

Introduce a figure, table, equation, or listing before interpreting it. State
what the reader should look at, then explain the result. Translate English
technical terms into Japanese only when a stable Japanese term exists. Otherwise
keep the English term or define the Japanese translation once, then use it
consistently. Do not optimize durable technical prose around graduation-thesis
padding conventions when a paper, textbook, engineering report, or reviewable
technical note would require tighter evidence and clearer claims.

## Visual-First Contract

Some topics are not complete as prose plus equations alone. If the explanation
depends on any of the following, include a diagram, plot, block diagram,
schematic, coordinate drawing, or equivalent visual artifact:

- geometric or spatial relationships;
- vector direction, projection, basis, or decomposition;
- coordinate transforms and frame changes;
- architecture, module boundaries, or data ownership;
- signal flow, feedback, pipelines, or control loops;
- frequency-domain behavior, spectra, modes, poles, zeros, or filters;
- parameter sensitivity, tuning, sweeps, tradeoffs, or limiting behavior.

If the visual cannot be produced in the same change, add a tracked figure
backlog item with the target section, visual purpose, suggested figure type,
required source data or construction method, and acceptance criteria. Do not
leave the missing visual only as an informal note.

## Reproducible Figures

- Prefer generated figures over hand-waved placeholders when a graph, spectrum,
  parameter sweep, or geometry sketch carries the explanation.
- For manuscript and paper quantitative graphs, default to Python/Matplotlib
  generation unless the project has a stronger plotting stack.
- MATLAB is acceptable when the data or analysis already lives there, or when
  MATLAB is the project-native reproducible plotting environment.
- Keep code-authored figure types separated: use TikZ itself for block
  diagrams and flow diagrams, `pgfplots` for LaTeX-native graphs, and
  `circuitikz` for circuit diagrams.
- Do not use raw TikZ paths for quantitative graph curves when Python or
  `pgfplots` is the appropriate tool.
- Keep figure source close to the figure artifact according to the project
  policy, or record the exact generation command in the figure backlog.
- Label axes, units, parameter values, normalization, and domains. State whether
  a plotted quantity is magnitude, phase, power, real part, imaginary part,
  error, state, input, output, or cost.
- Do not accept broken, blank, unlabeled, clipped, stale, decorative,
  geometrically invalid, text-overlapping, or unreadable figures. Render or
  build the target output and inspect the figure surface before reporting
  completion.

## Manuscript Graph Style

- Make legends, axis labels, tick labels, annotations, and line widths readable
  at the expected manuscript print or PDF size.
- Do not distinguish plotted series by color alone. Prefer grayscale-safe
  combinations of line style, dash pattern, marker shape, marker fill,
  hatch or pattern fills, dots, or direct labels where possible.
- Avoid embedded plot titles for paper figures because LaTeX captions and
  surrounding manuscript text should provide the title and context. Use axis
  labels, legends, and annotations only when they carry data interpretation.
- Use English for words inside figures by default, unless the project, journal,
  or conference explicitly requires another language or the figure is quoting
  source material.

## Pedagogy Sequence

Use this order unless there is a strong reader-facing reason to deviate:

1. Concrete phenomenon or motivation: show the real behavior, failure mode,
   observation, or task.
2. Visual or geometric interpretation: explain the shape, motion, flow,
   dependency, or mapping before formal notation.
3. Notation and definition: introduce only the names, symbols, assumptions, and
   units needed for the next step.
4. Derivation or mechanism: derive the result from the visual and definitions,
   keeping each step tied to meaning.
5. Parameter variation or example: change one important parameter and show how
   the behavior, plot, or conclusion moves.
6. Implementation or verification implication: explain what the reader should
   compute, check, simulate, test, or watch for.

## Frequency-Domain Explanations

- Start from time-domain signals readers can picture or measure.
- Bridge to sinusoidal decomposition before relying on spectral notation:
  explain that complex signals can be represented or analyzed through
  sinusoidal or complex-exponential components under stated assumptions.
- Distinguish Fourier and Laplace analysis responsibly. Fourier viewpoints are
  useful for frequency content and steady sinusoidal components; Laplace
  viewpoints are useful for dynamics, transients, initial conditions, poles,
  zeros, and stability. Do not claim one is merely a renamed form of the other.
- When teaching harmonic composition, show square-wave partial sums or an
  equivalent construction where adding harmonics visibly changes the waveform.
- Label axes, units, normalization, and domain assumptions. State whether a
  plot is magnitude, phase, power, real part, imaginary part, or time response.

## Exercises And Checkpoints

- Avoid detached problem dumps after the prose.
- Embed short checkpoints where readers need to test a concept before moving
  on.
- Use integrated tasks tied to the preceding explanation: interpret a plot,
  vary a parameter, predict a sign or trend, complete one derivation step,
  identify a failure case, or verify an implementation result.
- Prefer exercises that reveal misconceptions over exercises that only require
  substitution into a formula.
- Give enough surrounding context that the exercise still teaches when read
  later.

## Privacy And Genericity

- Keep reusable guidance project-agnostic.
- Do not expose local absolute paths, private source narratives, unpublished
  stakeholder context, internal workflows, agent coordination details, prompts,
  tool traces, hidden scratch notes, credentials, or environment-specific
  assumptions in public output.
- Generalize lessons before promoting them into shared prose. Name the
  transferable pattern, not the private incident that produced it.
