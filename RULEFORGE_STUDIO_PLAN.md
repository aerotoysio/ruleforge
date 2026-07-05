# RuleForge Studio — Implementation Plan

> Status: **APPROVED (2026-07-05)** — building, with a per-batch model-review checkpoint (see §5).
> Owner: Andrew Blake. Author: Claude. Date: 2026-07-05.
>
> **Review caveat (Andrew):** the web editor's rich model is the *starting point*, not a fixed
> spec — "very close, but not sure it's exactly what we want." Each node batch is a **review
> checkpoint**: Claude ports/proposes the model + config editors for that batch; Andrew reviews and
> refines before it's locked. We improve the model as we go rather than replicating it 1:1.

A **fully native** Windows desktop client for **RuleForge**, mirroring the treatment
DocumentForge got with DocumentForge Studio (`C:\DATA\documentforge\src\DocumentForge.Studio`).
Native WPF + a native **Nodify** node-graph rules designer — **no Node.js, no WebView2, no web
editor embed**. This app is intended to **become the primary, application-based authoring tool,
replacing the web `ruleforge-editor`** over time.

App name **RuleForge Studio** · own version starting **0.9.0** · own icon (Claude to create) ·
default data dir **`c:\data\ruleforge`** · lives in the **ruleforge GitHub repo**.

---

## 1. Context: RuleForge, DocumentForge, and the schema

RuleForge is a runtime rules engine (.NET / ASP.NET Core), sibling to DocumentForge. A **rule** is
a DAG of `nodes[]` (each `{ id, position:{x,y}, data:{ category, label, config, ... } }`, ~24 node
categories) + `edges[]` tagged `pass | fail | default`, with `inputSchema`/`outputSchema` (JSON
Schema), `endpoint`, `method`, `status` (draft/review/published), `currentVersion`. **Reference
sets** are versioned lookup tables.

**Source-of-truth & schema model (per Andrew):**
- The **client updates DocumentForge**, which is the **central source of truth**.
- The **RuleForge engine just runs *published* rules**, caching a **direct copy** of the published
  schema locally. It doesn't matter whether the engine points at prod DF or a synced copy — the
  schema is identical.
- Confirmed in code: `DocumentForgeRuleSource` reads the `rules` header (`id, endpoint, method,
  currentVersion`) + immutable **`ruleversions`** snapshots in the **engine `Rule` shape**. So
  "published" = a compiled engine-format rule version written to DF.

**Two representations (the crux):**
- **Authoring model (rich):** reusable **assets / templates / port-bindings**, multi-condition
  filters, schema-templates, node-defs — the convenient layer authors work in.
- **Engine model (flat):** nodes with inline `data.config`, `pass/fail/default` edges — what the
  engine actually runs. Produced by **compiling** the rich model.

**Decision:** Studio uses the **rich authoring model** and **compiles to the engine schema on
publish** (porting the editor's `compile-to-engine` logic to C#). Rationale: Studio is meant to
replace the web editor, so it needs full authoring parity, reusable assets, and asset-reuse
(edit-once-updates-many).

## 2. Decisions (locked)

| # | Decision | Choice |
|---|---|---|
| 1 | Desktop stack | **Native WPF**, .NET 9, CommunityToolkit.Mvvm, AvalonDock, AvalonEdit — same as DF Studio |
| 2 | Node editor | **Nodify** (native WPF node canvas). No Node.js / WebView2 / web-editor embed. |
| 3 | Rule storage / backend | **DocumentForge (HTTP)** as central source of truth; engine caches published copies |
| 4 | Authoring model | **Rich model** (assets/templates/bindings) → **compile to engine schema on publish** |
| 5 | Rule evaluation / testing | **In-process** via `RuleForge.Core` `RuleRunner` (no CLI shell-out) |
| 6 | Packaging | **Client + bundled engine service**, component-selectable Inno Setup (full DF playbook) |
| 7 | Repo | Lives in the **ruleforge repo** as `src/RuleForge.Studio` + `src/RuleForge.Studio.Core` |
| 8 | Strategic intent | Becomes the **application-based replacement** for the web `ruleforge-editor` |

## 3. Architecture (fully native)

```
┌─ RuleForge Studio (WPF .NET 9) ───────────────────────────────────────────┐
│  AvalonDock shell: menu/toolbar/status, Object Explorer tree, tabbed docs. │
│    Object Explorer → DocumentForge connection →                            │
│       Rules (category tree) · Reference sets · Environments · Assets ·      │
│       Templates · Schemas · Node library                                    │
│    Documents:                                                              │
│       • Rule Designer  → Nodify canvas + per-node config panels             │
│       • Test Harness   → sample payload editor + in-process run + trace     │
│       • Reference-set / Schema / Asset / Template editors (AvalonEdit JSON) │
│       • Engine dashboard (bindings / ready / refresh) — server-only         │
└───────────────┬────────────────────────────────────────────────────────────┘
                │ project references
     ┌──────────▼──────────────────────────────────────────────┐
     │ RuleForge.Studio.Core (UI-free)                          │
     │  • IRuleForgeConnection + DocumentForge HTTP impl         │
     │    (rules/ruleversions/referencesets/environments/assets/ │
     │     templates/schemas/nodes) + capability flags           │
     │  • Rich authoring model (Rule/Instances/Bindings/…)       │
     │  • Compile-to-engine (rich → engine Rule schema)          │
     │  • Settings/secrets: %AppData%\RuleForge Studio (DPAPI)    │
     │  • References RuleForge.Core → in-process RuleRunner eval  │
     └──────────┬───────────────────────────────┬────────────────┘
                │ read/write (source of truth)  │ optional, server-only
      ┌─────────▼──────────┐        ┌───────────▼──────────────┐
      │   DocumentForge     │  pub   │ RuleForge.Api (engine)   │
      │  (central store)    │◄───────┤ RULEFORGE_RULE_SOURCE=df │
      └─────────────────────┘  cache │ runs PUBLISHED rules;    │
                                     │ prod or synced copy      │
                                     └──────────────────────────┘
```

- **Native shell** reuses DF Studio patterns nearly verbatim (`IDfConnection`→
  `IRuleForgeConnection`, `StudioWorkspace`, `SecretStore`, AvalonDock MVVM, first-run seed,
  auto-reconnect).
- **`Studio.Core` references `RuleForge.Core`** so the **Test Harness evaluates rules in-process**
  through the real engine `RuleRunner` — no shell-out, no running service required for testing.
- **Publish** compiles the rich rule → engine `Rule`, writes a `ruleversions` snapshot + updates
  the `rules` header + endpoint binding in DocumentForge. The engine (bundled or remote) caches
  and runs those published versions.
- **Coexistence:** Studio mirrors the web editor's DocumentForge collection shapes so published
  rules keep working during the transition away from the web editor.

## 4. Core components to build (largest = the port)

| Component | Source to port from | Notes |
|---|---|---|
| Rich authoring types | editor `src/lib/types/*` (Rule, RuleNodeInstance, PortBinding ×12, NodeBindings, Asset, Template, SchemaTemplate, ReferenceSet, RuleTest, NodeDef) | → C# records in `Studio.Core` |
| Node catalog (~24 defs) | editor node-def JSON / `ruleforge-sample-workspace/nodes` | Reuse the JSON vocabulary rather than reinvent |
| **Compile-to-engine** | editor `src/lib/rule/compile-to-engine.ts` | The crown jewel; port incrementally per node batch. Implement `count-of` (editor TODO) for parity+ |
| DocumentForge client | `RuleForge.DocumentForge` `DfClient` + DF Studio `HttpConnection` | Read/write all collections + `ruleversions` publish |
| In-process evaluator | `RuleForge.Core` `RuleRunner` (referenced) | Feed compiled rule + ref/subrule sources + clock |
| Nodify canvas | Nodify library | Bind nodes/edges to the rich graph via MVVM |
| Per-node config editors | editor `NodeConfigDialog` + `src/components/bindings/*` | One native panel per category/binding kind |

## 5. Phased plan (node-coverage-driven, test harness first)

> **Per-batch review checkpoint:** before locking each node batch (Phases 2–4), Claude presents the
> ported/proposed model — types, binding kinds, config-editor UX — for Andrew to review and refine.
> The web editor's model is the baseline; we adjust where it isn't "exactly what we want."

### Phase 0 — Scaffold + de-risk the two hardest bets (compile + in-process eval)
- Create the solution in the ruleforge repo: `RuleForge.Studio` (WPF), `RuleForge.Studio.Core`
  (UI-free); reference `RuleForge.Core` (+ `RuleForge.DocumentForge`); add Nodify,
  CommunityToolkit.Mvvm, AvalonDock, AvalonEdit.
- Port the rich types + a **minimal compile slice** (input/output/filter/logic/product).
- Prove: load a fixture rich rule → **compile → `RuleRunner` evaluate → Envelope + trace**, and
  **render it in a Nodify canvas** (read-only).
- **Create the app `.ico`** (node-graph motif).
- **Exit:** a fixture rich rule compiles, renders natively, and runs in-process with a trace. This
  validates the whole native bet before building the shell.

### Phase 1 — Native shell + DocumentForge connection + Test Harness (first-class)
- Port DF Studio's AvalonDock shell (`MainWindow`/`MainViewModel`/`TreeNodes`/`DocumentViewModel`).
- `IRuleForgeConnection` + **DocumentForge HTTP impl**: list/read rules, reference sets,
  environments, assets, templates, schemas, node-defs; capability flags.
- Settings/secrets in `%AppData%\RuleForge Studio` (port `StudioWorkspace`/`SecretStore`, DPAPI);
  first-run seed a default DF connection; auto-reconnect; export/import bundle.
- Object Explorer tree — **hierarchical rules** (category tree, per Andrew), reference sets,
  environments, and a **defined home for Products / Outputs / Templates / Datasources** (a side
  accordion or dedicated Object-Explorer nodes — layout TBD in a Phase-1 review), schemas, node library.
- Canvas interactions to add next: **click → node inspector**, **right-click → context actions**
  (from the Phase-0 demo review).
- **Test Harness document** (built now, not late): choose a rule → edit sample payload (AvalonEdit)
  → **run in-process** (compile → `RuleRunner`, `debug=true`) → show Envelope result + **per-node
  trace** (pass/fail/skip/error, timing) → save/load scenarios (`tests[]`/`samples`) → "run all".
- Rule opens **read-only** in a Nodify Rule Designer tab (select node → inspector shows config).

### Phase 2 — Node batch 1: the spine (authoring begins)
- Canvas **authoring**: add / connect / delete / move nodes + edges, `pass/fail/default` tagging.
- Native config editors + compile + validation + DF round-trip + **harness tests** for:
  **input, output, constant, product** (asset + template modes), **logic** (and/or/xor/not),
  **filter** (string / number / date — the multi-condition "conditions-list" editors), reference-
  column binding.
- **Schema editors** (input/output/context) via AvalonEdit + visual field builder.
- Deliverable: author, save, publish-preview and **test each of these node types live**.

### Phase 3 — Node batch 2: data + compute + the rich reuse layer
- **mutator** (set-property + lookup-and-replace), **reference** (multi-row lookup), **calc**
  (NCalc expression builder with helper functions).
- First-class **assets** + **templates** + **schema-templates** as reusable entities (the rich
  model), `markets-select` + `date` binding kinds, `count-of`.
- Reference-set editor (columns/rows, versions). Each new node type demoed + tested in the harness.

### Phase 4 — Node batch 3: flow, arrays, iteration, sub-rules, external
- **switch, assert, bucket**; **iterator / merge**; **sort / limit / distinct / groupBy**;
  **filterList / join / textParse**; **api** (outbound HTTP); **ruleRef** (sub-rule calls with
  input/output mapping + `forEach`).
- Full node catalog now covered; every category has a config editor + harness test scenarios;
  in-process eval matches engine semantics (golden-file compile/eval comparison vs the editor's
  demo rules).

### Phase 5 — Publish + engine integration
- Publish flow: `draft → review → published` gating with **modal confirmation**; compile → write
  `ruleversions` + `rules` header + endpoint binding to DF; version bump; environments/bindings mgmt.
- Bundled/remote **RuleForge.Api** reading DF (`RULEFORGE_RULE_SOURCE=df`); live-eval + admin
  (`/ready`, `/admin/bindings`, `/admin/refresh`) as **server-only greyed capabilities**; compare
  in-process vs live results; point at any engine (prod or synced copy).

### Phase 6 — Packaging (full DF playbook)
- `scripts/build-studio-installer.ps1` (adapted): self-contained **win-x64** publish of
  `RuleForge.Studio` + bundled `RuleForge.Api`; then ISCC.
- `installer/RuleForgeStudio.iss` (from `DocumentForgeStudio.iss`): stable AppId, per-user install,
  **component-selectable** (client / engine service / full), **port-wizard** page, **file
  association** for rule/workspace files, **first-run** creates `c:\data\ruleforge`, optional
  **Windows service** (`UseWindowsService`), app **icon**, version **0.9.0**.

### Phase 7 — Polish + live verification
- Verify **every node + feature against a running DF + engine** (not just a build) — the DF lesson.
- Carry DF lessons: labeled form fields, dropdowns over raw enum/scope strings, guard
  destructive/publish/lock-out actions with modal confirms. Short usage doc.
- Later/optional (post-parity): **AI drafting** ported to the Anthropic .NET SDK (since Studio
  replaces the web editor, which had Claude authoring).

## 6. Open questions
1. **Node vocabulary reuse** — OK to seed Studio's node catalog from the editor's node-def JSON /
   `ruleforge-sample-workspace` (pin a commit) rather than redefining ~24 node types? (Recommend yes.)
2. **Compile parity guarantee** — accept a golden-file test suite (compile the editor's demo rules,
   diff engine JSON + compare in-process Envelopes) as the parity gate?
3. **AI drafting** — defer to a post-parity phase (Phase 7+)? (Assumed yes.)
4. **Web-editor coexistence** — during transition, both write DF: confirm Studio simply matches the
   editor's DF collection shapes (no migration), so nothing breaks.

## 7. Risks & mitigations
- **Large port** (rich model + compile-to-engine). → Node-coverage batches, each independently
  testable; port the compiler incrementally alongside each batch.
- **Compile/eval fidelity vs the engine.** → In-process `RuleRunner` + golden-file comparison
  against the editor's demo rules from day one.
- **Nodify learning curve.** → Spiked in Phase 0 before shell work.
- **Scope creep toward full editor parity.** → Batches are shippable; a useful subset (Phases 0–2)
  authors + tests real rules before the long tail of node types lands.

## 8. Reuse map (from DocumentForge Studio)
- Connection abstraction: `IDfConnection` → `IRuleForgeConnection` (+ capability flags).
- Settings/secrets: `StudioWorkspace`, `SecretStore` (DPAPI), settings JSON, export bundle.
- Shell: `MainWindow` + AvalonDock, `DocumentViewModel`/`TreeNodeViewModel`, first-run + reconnect.
- Packaging: `build-studio-installer.ps1`, `DocumentForgeStudio.iss` (component-selectable, port
  wizard, file association, service option, first-run data dir).
