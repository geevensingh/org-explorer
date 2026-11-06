# Domain Docs

## Before exploring, read these

This is a single-context repository:

- Read `GLOSSARY.md` at the repository root.
- Read ADRs under `docs/adr/` that touch the area being explored.

If these files do not exist, proceed silently. The `/domain-modeling` skill, also reached through `/grill-with-docs` and `/improve-codebase-architecture`, creates them lazily when terms or decisions are resolved.

## File structure

- `GLOSSARY.md`: shared domain vocabulary.
- `docs/adr/`: architectural decisions, named such as `0001-decision-title.md`.

## Use the glossary's vocabulary

Use glossary terms in issue titles, proposals, hypotheses, and test names rather than synonyms the glossary avoids.

If a needed concept is absent, reconsider whether it belongs to the domain. Note genuine vocabulary gaps for `/domain-modeling`.

## Flag ADR conflicts

If a proposal contradicts an existing ADR, identify the ADR and explain why it merits reopening rather than silently overriding it.
