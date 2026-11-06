# Matt Pocock's skills

This directory contains the 27 published engineering and productivity skills from
[mattpocock/skills](https://github.com/mattpocock/skills), including their supporting
files. Miscellaneous and in-progress skills are not included.

- Upstream plugin version: `1.3.2`
- Upstream revision: [`4ae2317ddb22b046e26455e4f9c9b3aaaf62212d`](https://github.com/mattpocock/skills/commit/4ae2317ddb22b046e26455e4f9c9b3aaaf62212d)
- License: [MIT, copyright Matt Pocock](LICENSE)

## Usage

These are repository-local GitHub Copilot skills, not a global plugin installation.
Start a new agent session if the skills are not yet listed.

Run `/setup-matt-pocock-skills` once before using the engineering workflows to
configure the issue tracker, triage labels, and documentation layout. That
configuration is separate from installing these skills and has not been run.

Use `/ask-matt` to choose a workflow, or invoke a skill directly, such as `/tdd`
or `/grill-me`.

## Updating

The skills are vendored copies and do not update automatically. Copy the complete
skill directories listed in upstream
[`.claude-plugin/plugin.json`](https://github.com/mattpocock/skills/blob/main/.claude-plugin/plugin.json)
into this directory, preserving supporting files. Review any local customizations
before replacing files, refresh `LICENSE`, and update the version and revision
above.
