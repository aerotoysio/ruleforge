# RuleForge Studio — Gap Plan vs the React Editor

> Drafted 2026-07-07. Companion to `RULEFORGE_STUDIO_PLAN.md`. The React editor
> (`C:\DATA\14. ruleForge\ruleforge-editor`, pinned mentally at ~f7a2e67) is the feature
> baseline Studio is replacing; the engine (`RuleForge.Core`) is the source of truth for
> semantics. **Node types are the first review item (§1, per Andrew).**

## 0. What Studio already has (baseline)

Native shell (AvalonDock) · Object Explorer (rules by category, datasources) · connector
canvas (Nodify): typed pass/fail outputs, drag-to-connect edges, toolbox drag-drop,
double-click config dialogs, node faces w/ name + plain-English description, zoom clamp ·
**string-filter editor** (full: operators, multi-value chips, any/all/none list judgement,
on-missing, case/trim) · in-process test harness w/ per-node trace · standalone Rule
Tester · reference-data manager (key-value / lookup tables, CSV import, editable grid) ·
connections (local workspace + DocumentForge wired [unverified], DPAPI keys, first-run,
auto-reconnect).

## 1. Node types (REVIEW FIRST)

Engine = 23 categories (`NodeCategory`). "Editor" = React affordance. Batches below are
the proposed build order.

| Category | React editor | Studio today | Batch |
|---|---|---|---|
| input / output | fixed terminals | rendered; output in toolbox | — |
| filter · string | multi-condition (ALL/ANY), ref-column lists | **full single-condition editor** | done · multi-cond **B2** |
| filter · number | multi-condition (gt/lt/between/in) | render only | **B1** |
| filter · date | day-of-week, within-last/next, calendar UI, timezone | render only | **B1** |
| filter · markets | hierarchical market/airport picker | render only | B3 |
| logic (and/or/xor/not) | template picker | render only (pass/fail connectors OK) | **B1** |
| constant | literal / template-fill | render only; toolbox | **B1** |
| product | asset-only dropdown (output shape) | render only; toolbox | B2 (needs assets) |
| mutator | set-property (multi-field Sets) + lookup-and-replace | render only; toolbox | **B1** (set) · B2 (lookup) |
| calc | NCalc expression builder + helper fns | render only; toolbox | B2 |
| reference | refset lookup w/ matchOn | render only | B2 |
| ruleRef | input/output mapping (raw JSON in editor too), forEach, onError | render only | B3 |
| iterator / merge | for-each + collect/count/sum/avg/min/max | render only | B2 |
| switch / assert / bucket | forms | render only | B3 |
| sort / limit / distinct / groupBy | forms | render only | B3 |
| textParse | {token} pattern editor + live preview + asset map | render only | B3 |
| join / filterList | schema-aware keys; per-element conditions | render only | B3 |
| api | URL/method/headers/body/responseMap form | render only | B3 |

**B1 (next):** number + date filters, logic, constant, mutator-set — direct extensions of
the proven dialog pattern, no new entities.
**B2:** the reuse layer (assets/templates/schema-templates + binding kinds) then
product/calc/reference/iterator/merge + multi-condition filters.
**B3:** long tail (switch/assert/bucket/sort/limit/distinct/groupBy/textParse/join/
filterList/api/ruleRef/markets).

## 2. Information architecture — Andrew's hierarchy redesign (2026-07-07)

Direction: **the level above the canvas is an overview document; the canvas is clean.**

- Overview shows: request (input) + response (output) schema view, rule general details
  (endpoint, method, status, description/tags), and **version history — every version is
  immutable**; editing produces the next version. Matches the engine model (`rules`
  header + immutable `ruleversions`).
- The rule graph view itself carries no configuration clutter — just the endpoint + the
  editable canvas.

**DECIDED (2026-07-07): schema is per RULE** — a group can contain rules with different
schemas, so the overview document lives at the rule level (Category stays a folder).
Additionally: a **settings area to ASSIGN schemas to rules** (pick/reuse schemas rather
than only inline-editing them) — pairs with a schema library. Build with the Rule
Overview document.

## 3. Cross-cutting gaps (React editor → Studio)

| Area | React editor has | Studio has | Notes / phase |
|---|---|---|---|
| Reusable layer | assets, output templates, schema templates, node-def library, **12 port-binding kinds** compiled to engine JSON on publish | nothing (engine-schema configs only) | B2 cornerstone; rich model per approved plan |
| Schemas | visual schema editor, from-sample inference, raw JSON; schema templates | read-only (drives filter field picker) | pairs with §2 overview doc |
| Persistence / publish | save to workspace (SQLite/folder), compile→`compiled_rules`, publish/versioning, releases log + scheduling, endpoint bindings | **edits are in-memory only** | top gap after B1; publish = compile→`ruleversions`→DF |
| Tests per rule | saved tests (`tests[]`), samples library, run-all, AI scenario gen (planned) | ad-hoc harness + tester pane | save/load scenarios next; run-all |
| Trace UX | canvas highlighting of pass/fail path, per-node trace details | text trace list | color node borders from trace = cheap win |
| Canvas extras | node grouping boxes, edge labels, auto-layout (AI drafts), palette search | core canvas | edge delete gesture also pending |
| AI authoring | policy→rule drafting (Claude), per-node explanations, summary tab, NL correction (planned) | none | post-parity (approved plan) |
| Datasources | categories, versioned refsets, per-rule usage | flat list, edit/CSV/save | categories + versions later |
| Fleet / ops | DF sync (push/pull), fleet heartbeat/publish, engine status/reload, dashboard, playground/bench | engine client not wired (LiveEngine capability reserved) | Phase 5 of master plan |
| AuthZ | users/roles/RBAC, API keys | n/a (desktop, DF key) | likely N/A for Studio v1 |
| Validation | compile-time validate + repair loop | engine throws at eval | pre-run validation pass |

## 4. Sequencing proposal

1. **B1 node editors** (number/date filters, logic, constant, mutator-set)
2. **§2 overview document** (once the family-vs-rule question is answered) + versioning
   model surfaced
3. **Save/publish** (immutability per §2; DF verify when tailnet reachable)
4. **B2** reuse layer + editors; saved tests + trace-on-canvas
5. **B3** long tail; ops/fleet; AI later

## 5. Rendering note (for Andrew's styling investigation)

Nodes are rendered by **Nodify 7.3.0** (`miroiu/nodify`): `NodifyEditor` canvas +
`nodify:Node`/`NodeInput`/`NodeOutput`. All visuals are OUR templates —
`NodeTemplate` / `ConnectionTemplate` / `NodeContainerStyle` in
`src/RuleForge.Studio/MainWindow.xaml`, backed by `ViewModels/GraphViewModels.cs`.
Any WPF content works as a node face (the template need not use `nodify:Node` at all);
connectors only need `Anchor` bindings. Samples: Nodify repo `Examples/` (Playground,
Calculator, StateMachine) + wiki.
