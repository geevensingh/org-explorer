# Explicit `/update-org` skill discovery (issue #50)

Bounded investigation of
[issue #50](https://github.com/geevensingh/org-explorer/issues/50), written
2026-11-11. It used first-party public sources, one locally generated synthetic
fixture, and no workplace service, employee data, desktop automation or external
filing. It changes no product, test, CI, skill, frontmatter or always-loaded
instruction file, and it makes no product validation claim. Its host-routing
conclusions do not depend on this repository's app test suites.

## Conclusion

**A repository discovery defect is not established.** Two different routes were
being conflated:

- the model's `skill` tool, which excludes a skill declared
  `disable-model-invocation: true`; and
- the user's explicit command route (`/skill-name` in the composer). CLI/SDK
  documentation and app changelog entries support this distinction; the app's
  complete routing contract is not public.

`Skill "update-org" not found` from the model tool is consistent with the
documented user-only declaration in `.github/skills/update-org/SKILL.md`. Whether
the desktop app routed the user's typed `/update-org from ...` is **not
established either way**: issue #50 records the tool result but not whether skill
context had already been supplied, and desktop automation was not authorized.

Routing belongs to the Copilot app/CLI runtime, not to this repository. There is
no repository correction, no product .NET TDD seam (this is neither workflow nor
storage behavior), no ADR/glossary change and no Documentation handoff. This
investigation and its diagnostic are the complete change; issue #50 stays
investigative and open.

## Revisions and versions

- Issue's inspected revision: `64da521be0a1d6332decd5e546009df8283eb209`
  (`Add /update-org skill and local import helpers (#40)`). Investigation
  baseline: `93c818e41beaa89da8dfcb54fe139f641fa67b67`.
- `.github/skills/update-org/SKILL.md` declares only `name`, `description` and
  `disable-model-invocation: true`; it declares no `allowed-tools`. `/implement`
  is also user-only, and its `agents/openai.yaml` says
  `allow_implicit_invocation: false`; that file is not assumed to be read by
  this host.
- Desktop app: 1.2.0, not a local source build (`get_app_info`; release
  `v1.2.0` is dated 2026-11-10 [S8]). The app version at the issue's original
  observation was not recorded.
- Conversation runtime announced `1.0.106-8`. This is **not** the desktop app
  version.
- Standalone CLI on `PATH`: `1.0.102-0.unstable.r40949665530.gab1eef5`.
  `copilot --no-auto-update` instead runs a bundled `1.0.39-0`. The standalone CLI
  is a proxy for runtime behavior, not the app's engine.

## What the primary sources say

Search scope: the `github/docs` source for docs.github.com (all 16 pages under
`github-copilot-app/` and `github-copilot-app-reference/`), `github/copilot-sdk`
docs and generated schemas, the `github/copilot-cli` changelog, and the public
`github/app` repository (README, changelog, feature flags; it publishes no
source). Search-engine summaries were used only to find URLs. VS Code and
Anthropic skill documentation were deliberately not used as app guarantees.

**Flag semantics (CLI/SDK runtime, owned by GitHub):**

- Skills are injected "via `/SKILL-NAME` or automatically by the agent".
  `user-invocable` is "Whether users can invoke the skill with `/SKILL-NAME`.
  Default: `true`"; `disable-model-invocation` is "Prevent the agent from
  automatically invoking this skill. Default: `false`" [S1]. The model-facing
  tool is `skill` ("Invoke custom skills") [S1].
- The SDK catalog carries `userInvocable` and `disableModelInvocation` as
  separate fields, "Whether users may invoke the skill directly" and "Whether
  model invocation is disabled"; the model "loads [skills] on demand through the
  `skill` tool, and users can invoke them like any other skill" [S6].
- Over ACP, "Enabled, user-invocable skills ... appear as `/SKILL-NAME`
  commands" [S2]; only user-invocable skills can be scheduled [S3]. Both
  describe user routes independent of the model catalog.
- Changelog: `disable-model-invocation` added in 0.0.412; "Fully honor the skill
  disable-model-invocation flag" in 1.0.74; and, listed under both 1.0.106 and
  1.0.107, "Skill calls explain when model invocation is disabled instead of
  reporting the skill as not found" [S7]. So `not found` was the older
  wording for a refused user-only skill. 1.0.108 also stops a prompt path such as
  `/docs/setup.md` from loading a skill named `docs` "as if you had invoked that
  skill" [S7], which implies the CLI treats a leading `/name` as an invocation
  (an inference; the probe's `-p` run at 1.0.102 did not expand it).

**App-owned statements (the only ones found):**

- App docs say skills configured for repositories or Copilot CLI "are
  automatically available in the GitHub Copilot app" [S4]. They do not document
  `disable-model-invocation`, `user-invocable` or tool-routing internals. The
  app slash-command and built-in-skill references cover only GitHub-provided
  commands and skills [S4].
- The public app changelog is the nearest contract: v1.1.3 (2026-08-04) "Fixed
  skills that are only invokable by the user (not by the model) failing to run
  when selected from the composer's skill suggestions"; v1.1.31 keeps a selected
  skill slash command as editable text for arguments; v1.1.24 and v1.1.4 fixed
  plugin skill slash commands; v1.2.0 fixed Customize not listing a repository
  skill added while the app was running [S8].

**Limit:** no public app-specific reference defines typed `/skill-name args`
routing for a user-only repository skill, or the app's tool-catalog behavior. The
app's intent is evidenced only by changelog fixes about selecting user-only skills
from suggestions. These are not borrowed from other products, and no app guarantee
is claimed beyond them.

## Observations

- **App, this investigation's session:** the user explicitly invoked `/implement`.
  Before the model's first tool call, the user message already contained a
  `<skill-context name="implement">` body, skill base path and the issue argument.
  The model's `skill({skill:'implement'})` call returned `Skill "implement" not
  found`, and the workflow continued from the supplied context. This shows
  explicit-context expansion for `/implement`; it does **not** prove `/update-org`
  selection, collection or routing works.
- **Inventory:** `copilot skill list --json` from the checkout shows
  `update-org` and `implement` enabled, source `project`. Inventory is not the
  model tool's catalog.
- **Unreconciled wording:** the runtime announcing `1.0.106-8` returned `not
  found`, while the changelog lists an explanatory message from 1.0.106. The
  build suffix, a different app engine, or a different path may explain it; no
  defect is asserted. The 1.0.102 probe predates that change, so its `not found`
  wording is version-specific.

## Repeatable CLI probe (not a desktop test)

`docs/research/skill-routing-probe.py` is an optional manual diagnostic, not CI or
a product test. It needs Python 3 and an installed supported `copilot`; it adds no
dependencies. It generates one synthetic user-only skill and one model-invocable
control in a temporary folder, runs real CLI prompt mode with
`COPILOT_OFFLINE=true`, an isolated `COPILOT_HOME`, no GitHub credentials, no MCP,
only the `skill` tool and a timeout, and answers through a loopback-only fake
responder, so there is no external inference. It prints minimal summaries and
source labels, never raw prompts, auth headers or tool catalogs, and removes its
fixtures.

The probe disables dynamic skill retrieval in its temporary configuration, so
catalog-marker absence is not merely deferred retrieval. Its loopback responder
deliberately requests the tool; no model is deciding whether to invoke a skill.

```powershell
py -3.9 .\docs\research\skill-routing-probe.py --version '1.0.102-0.unstable.r40949665530.gab1eef5'
py -3.9 .\docs\research\skill-routing-probe.py --version '1.0.102-0.unstable.r40949665530.gab1eef5' --require-expansion
```

(`python` was an unavailable Store alias on the investigation machine.)

Expected verdicts of the first command at the pinned CLI:

- Both synthetic skills are discovered and enabled.
- The user-only skill's description and body marker are absent from the implicit
  request; the control's description is present.
- `-p /skill-routing-probe` is passed to the model literally, with no body
  expansion; a deliberately requested `skill` call for the user-only skill is
  rejected (`not found` at this version).
- `-p /routing-control` is likewise not pre-expanded, but a requested `skill`
  call loads the control's actual body.
- Output includes a `LIMIT` statement; it is not reported as a desktop success.

The second command is an optional red check: `--require-expansion` fails when
CLI prompt mode does not inject the user-only body, which is the pinned
version's observed behavior. It measures prompt mode only; it is not an app
regression test, and `-p` is not asserted to equal the desktop slash route.

Limits: the responder is deterministic, so the probe shows host filtering, not
model understanding or business behavior. `COPILOT_HOME` isolates state, not
ancestor skill discovery: other inventory entries carried source labels
`builtin` and `inherited`, and the normal checkout inventory also showed
`personal-agents`, so a fully empty global-skill environment is not claimed. A
newer CLI has documented wording and prompt-matching changes [S7], but was not
run; no newer-version routing verdict is claimed.

## Diagnostic handoff to app/CLI owners

No issue was filed and no host fix is proposed. If the owners are engaged later
(the public `github/app` repository has issue templates), provide:

1. Expected: a `disable-model-invocation: true` skill is absent from the model
   catalog and refused by the `skill` tool, yet is reachable by an explicit user
   command with its arguments [S1][S2][S8].
2. Observed: for `/implement`, desktop 1.2.0 supplied the expanded context and
   the model tool refused with `not found` (runtime `1.0.106-8`); the CLI probe
   reproduces the refusal on 1.0.102 without expansion in `-p` mode.
3. Questions only the owners can answer: does a fully typed
   `/update-org from <name>` route like picking it from suggestions (the public
   changelog covers suggestions); why `not found` rather than the 1.0.106+
   explanation; and whether a skill committed while the app was running is in
   the session's skill list at session start (cf. the v1.2.0 Customize fix).
4. Include app and runtime versions, the skill `source` label, the two
   frontmatter flags and the synthetic fixture's text. Do not include workplace
   data.

## Safe, narrowly explicit workaround

This is an operator/agent procedure, not a repository change:

- If an explicit `/update-org` invocation already supplied matching skill
  context, use it. Do not call the model `skill` tool for it; a refusal there is
  not evidence the skill is missing.
- Otherwise, only when the user explicitly asks for `/update-org`, read exactly
  `.github/skills/update-org/SKILL.md` and the files it references
  (`collection.md` and the bundled `scripts/` helper) from the trusted checkout.
- Preserve every prerequisite in that skill: a fresh **local Windows** session;
  stop, without installing tools or switching accounts, if execution is
  remote/cloud or the source connection is unavailable.
- State that this is manual loading, not verification of host routing. It
  supplies no host skill base path or invocation event, and arguments are parsed by
  hand.
- Do not enable automatic invocation, change global flags, or add the skill body
  to `AGENTS.md` or other always-loaded instructions.

## Future manual app check

Not run: it needs an authorized, isolated desktop environment, and no desktop
automation was performed. Use only generated synthetic content:

1. In a disposable local folder outside this repository and cloud sync, create
   `.github/skills/route-probe-user-only/SKILL.md` (`name`, `description` with a
   unique marker, `disable-model-invocation: true`, and a body that only asks for
   the marker and the passed argument) and a matching model-invocable
   `route-probe-control`. Add the folder as a project.
2. Start a fresh local session with no workplace connector, MCP server or data.
   Send a neutral, non-explicit prompt. Inspect the model-facing skill catalog,
   not just the assistant's self-report: expect the control visible and the
   user-only skill absent.
3. In another fresh session, explicitly select the user-only skill from the
   composer and give it an argument, then repeat by typing the full command.
   Record app and runtime versions, whether it appears in suggestions, whether
   skill context was supplied, and the reply.
4. A failure is evidence for the app owners, not for this repository.

## Issue #50 acceptance mapping

- **Reproduction:** the synthetic CLI probe above; desktop route unverified.
- **Contract:** [S1]-[S8], with the app-specific limit stated.
- **Intentional vs broken:** model-tool exclusion is intended; explicit app routing
  is unproven in both directions for `/update-org`.
- **User-only preserved; owner/handoff/workaround:** see the sections above.
- **Coverage prerequisite:** a safe app-level check needs the isolated desktop
  environment described above.

## Sources

Pinned to commits; line numbers refer to those revisions.

- **S1** [`github/docs@2b4f2c7`](https://github.com/github/docs/blob/2b4f2c7398e04ee8d02adca7440e4286d20c78b9/content/copilot/reference/copilot-cli-reference/cli-command-reference.md)
  `cli-command-reference.md` L1325-1336 (skill frontmatter), L890 (`skill`
  tool), L1347 (parent skills are "Inherited").
- **S2** same revision, `acp-server.md` L202-209
  ([link](https://github.com/github/docs/blob/2b4f2c7398e04ee8d02adca7440e4286d20c78b9/content/copilot/reference/copilot-cli-reference/acp-server.md)).
- **S3** same revision, `schedule-prompts.md` L83
  ([link](https://github.com/github/docs/blob/2b4f2c7398e04ee8d02adca7440e4286d20c78b9/content/copilot/how-tos/copilot-cli/automate-copilot-cli/schedule-prompts.md)).
- **S4** same revision, app pages: `customize-github-copilot-app.md` L48,
  `slash-commands.md` L14-21 and L71, `built-in-skills.md` L14 and L18
  ([customize](https://github.com/github/docs/blob/2b4f2c7398e04ee8d02adca7440e4286d20c78b9/content/copilot/how-tos/github-copilot-app/customize-github-copilot-app.md),
  [slash commands](https://github.com/github/docs/blob/2b4f2c7398e04ee8d02adca7440e4286d20c78b9/content/copilot/reference/github-copilot-app-reference/slash-commands.md),
  [built-in skills](https://github.com/github/docs/blob/2b4f2c7398e04ee8d02adca7440e4286d20c78b9/content/copilot/reference/github-copilot-app-reference/built-in-skills.md)).
- **S5** (context) same revision, CLI
  [`add-skills.md`](https://github.com/github/docs/blob/2b4f2c7398e04ee8d02adca7440e4286d20c78b9/content/copilot/how-tos/copilot-cli/customize-copilot/add-skills.md)
  L32-35: naming `/skill-name` in a prompt tells Copilot to use it; it does not
  describe user-only skills.
- **S6** [`github/copilot-sdk@98c3eef`](https://github.com/github/copilot-sdk/blob/98c3eef9ffbcf090107852460482bb9b9ff425f7/docs/features/skills.md)
  `docs/features/skills.md` L341, L345, L689-694; generated
  `SkillProviderDescriptor.java` L28-31 and `SkillInvokedEvent.java` L49-56.
- **S7** [`github/copilot-cli@16e7863` `changelog.md`](https://github.com/github/copilot-cli/blob/16e78633fb9b246201dd1adfd89a0bd3811358dd/changelog.md)
  L4198-4208 (0.0.412), L2254-2269 (1.0.74), L884 and L997 (1.0.106), L244 and
  L357 (1.0.107), L162 and L197 (1.0.108).
- **S8** [`github/app@0dbea76` `changelog.md`](https://github.com/github/app/blob/0dbea76d99ca6df99eb2e4c98b65d587ab6145c3/changelog.md)
  L3568 (v1.1.3), L3433 (v1.1.4), L1707 and L1745 (v1.1.24), L1100 (v1.1.31),
  L79 and L154 (v1.2.0).
