# Research Notes

Use this directory for private research notes related to development work that may later need reproducibility context, benchmark evidence, a technical report, or a paper.

Agents should write notes here in the live project they are already working in. The private `agent_files` registry can mirror this directory later through the normal capture/save workflow.

Suggested local layout:

```text
.agents/research/
  notes/YYYYMMDD-topic.md
  data/
  raw/
  figures/
  scratch/
```

Record notes only when they are useful. Prefer compact Markdown notes that capture the question, setup, commands or scripts, key parameters, data provenance, measurements, outcome, limitations, and next experiment. Keep bulky raw logs, generated dumps, binaries, and large figures out of the registry unless explicitly curated and useful.

Do not publish, export, or copy research notes/data outside this private `agent_files` registry and local `.agents` context without explicit owner approval.
