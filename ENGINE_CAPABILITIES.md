# RuleForge Engine — Capabilities (current state)

**Snapshot for the editor / consumer track to compare against UI coverage.**
Updated through commit `f8e6592` (post-production-grade bundle).
Test count: **265 passing, 0 warnings, 0 errors.**

This is the engine's authoritative surface as of today. If a feature is in
this doc the engine supports it. If you don't see UI for it, that's a UI
gap. If you see UI for something not in this doc, the engine doesn't
support it (yet).

For historical context — how we got here — see `ENGINE_HANDOFF.md`.
For schema artifacts (consumed by editor type generation) run:

```
dotnet run --project src/RuleForge.Cli -- schemas --out _schemas
```

---

## Node catalog (20 categories, all implemented)

Every category below has a switch handler in `RuleRunner.cs:308`. The
editor needs an authoring affordance for each, plus a forms / config-edit
surface for each that has a Config record.

### Data flow nodes

| Category | Config | Purpose |
|---|---|---|
| `input` | none | Receives the request payload. Exactly one per rule. |
| `output` | none | Emits the rule result. Exactly one per rule. |
| `constant` | `ConstantConfig` | Emits a literal JSON value (number, string, bool, object, array). `{ "value": <any-json> }`. |
| `product` | `ProductConfig` | Defines the rule's output shape — the rule's "product". Two modes: `{ "output": <literal-json-shape-with-${...}-placeholders> }` OR `{ "outputSchema": [{key, value}, ...] }` for object assembly. Domain-agnostic — could be an airline product, error envelope, warning, etc. |
| `mutator` | `MutatorConfig` | In-place writes onto the upstream object. Two kinds: `set-property` (literal or `from: $.path`) and `lookup-and-replace` (resolve refset row, write one column). |

### Filter / decision nodes

| Category | Config | Purpose |
|---|---|---|
| `filter` | one of `StringFilterConfig` / `NumberFilterConfig` / `DateFilterConfig` (selected by config shape) | Single comparison: `source` × `compare` × `arraySelector` (any/all/none/first/only) × `onMissing` (fail/pass/skip). Routes downstream via pass/fail edge. |
| `logic` | `templateId` (`and`/`or`/`xor`/`not`) + `label` on the node — no Config record | Combines incoming edge verdicts. Routes via pass/fail edge. |
| `switch` | `SwitchConfig` | Multi-way branch. `{ input, cases: [{match, name}, ...], default? }`. Resolves input once, emits matched case name as a JSON string. Downstream filters route on it. |
| `assert` | `AssertConfig` | Invariant guard. `{ condition: <ncalc>, errorCode?, errorMessage? }`. Falsy → rule fails with structured error. Truthy → pass-through upstream. |
| `bucket` | `BucketConfig` | Sticky-hash A/B assignment. `{ hashKey, buckets: [{name, weight}] }`. FNV-1a hash of resolved key → consistent bucket every time. Pairs with shadow mode (when shipped). |

### Computation nodes

| Category | Config | Purpose |
|---|---|---|
| `calc` | `CalcConfig` | NCalc expression. `{ expression, target? }`. Variable resolution: upstream → ctx → request → iteration frames. Has its own 5s default timeout. |
| `reference` | `ReferenceConfig` | Multi-row lookup against a refset. `{ referenceId, matchOn?: { col: $.path, ... } }`. Returns matched rows as JSON array. Per-request memo cache. |
| `api` | `ApiConfig` | Outbound HTTP. `{ url, method, timeoutMs, headers?, body?, responseMap? }`. URL/headers can be literal or `$.path`. Body supports `${...}` placeholders. `timeoutMs` mandatory. |

### Iteration nodes

| Category | Config | Purpose |
|---|---|---|
| `iterator` | `IteratorConfig` | Fan out the downstream subgraph N times. `{ source: <path-to-array>, as: <frame-name> }`. Frame becomes accessible as `$<as>.field` to downstream nodes. |
| `merge` | `MergeConfig` | Closes the innermost open iterator and reduces outputs. Modes: `collect` / `count` / `sum` / `avg` / `min` / `max`. With `field?` for non-collect modes. |

### Array transform nodes

All read a single upstream array, transform, and emit.

| Category | Config | Purpose |
|---|---|---|
| `sort` | `SortConfig` | `{ sortKey?, direction?: asc/desc, nulls?: first/last/error }`. Stable. SortKey is JSONPath relative to each element; null/missing = sort whole element. |
| `limit` | `LimitConfig` | `{ count, offset? }`. Take-N after optional skip. |
| `distinct` | `DistinctConfig` | `{ key?, keep?: first/last }`. Dedupe by key (or whole element). |
| `groupBy` | `GroupByConfig` | `{ groupKey }`. Partitions into `{ key1: [...], key2: [...] }`. Preserves first-seen group order. |

### Sub-rule node

| Category | Config | Purpose |
|---|---|---|
| `ruleRef` | `SubRuleCall` on the node (not under `data.config`) | Invokes another rule. Supports `forEach` for fan-out per item. `inputMapping` / `outputMapping` for explicit data flow. `onError`: `skip` / `fail` / `default` with `defaultValue?`. |

---

## Edge model

Edges have a `branch`: `pass` | `fail` | `default`. Activation rules:

- `pass` edges fire when the source node's verdict is **Pass**.
- `fail` edges fire when verdict is **Fail**.
- `default` edges fire on **either** Pass or Fail (universal forwarder).
- Verdict `Skip` doesn't activate any edge — graph effectively halts on that path.
- Verdict `Error` aborts the whole rule (envelope decision becomes `Error`).

Note: `switch` doesn't emit per-case edges yet — it emits the matched
case's name as output, and downstream filters route on it. (Native N-way
case edges flagged as future work.)

---

## Bindings & placeholders

Two conventions in node configs:

**Raw JSONPath strings** (for fields like `$.path` / `$ctx.x`):
- `$.foo` — reads from request
- `$ctx.bar` — reads from context dict
- `$pax.x` — reads from current iteration frame named `pax`
- Anything not starting with `$` — literal value

**Placeholder substitution inside JsonElement values** (`${...}` syntax):
- `${$.foo}` — splice request field's value into a JSON shape
- `${ctx.bar}` — splice context value
- `${$pax.x}` — splice iteration-frame value
- Used by `product.output`, `api.body`, etc.

---

## HTTP API

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | bypass | Liveness — always 200, doesn't probe deps. |
| GET | `/ready` | bypass | Readiness — probes `IRuleSource` with 2s budget. 200 with `{ ok, ruleSource, bindingCount, referenceSource }`, or 503 with `{ ok: false, ruleSource: "error"|"timeout", detail? }`. |
| GET | `/admin/bindings` | required | Lists currently bound endpoints + cache stats. |
| POST | `/admin/refresh` | required | Drops source caches; new rule versions and new endpoints become live. |
| GET / POST | `/{**path}` | required | Catch-all → looks up the bound rule for `(method, path)`, parses body, validates input schema, evaluates, validates output schema (warn-only), returns `Envelope` JSON. |

**Special query/headers on the catch-all:**
- `?debug=true` or `X-Debug: true` — populates `envelope.trace` with per-node entries.

---

## Auth

- Two acceptable forms: `X-AERO-Key: <key>` header OR `Authorization: Bearer <key>`.
- Constant-time UTF-8 byte compare (`CryptographicOperations.FixedTimeEquals`), padded to expected length so length-mismatch doesn't leak via timing.
- Configured via `RULEFORGE_API_KEY` env var. **If unset, all paths are open** (dev-only mode).
- `/health` and `/ready` always bypass.

---

## Envelope shape (response)

```jsonc
{
  "ruleId": "rule-bag-policy",
  "ruleVersion": 7,
  "decision": "apply",            // apply | skip | error
  "evaluatedAt": "2026-04-27T12:00:00.000Z",
  "result": { /* the rule's output product */ },
  "trace": [ /* TraceEntry[], only when ?debug=true */ ],
  "durationMs": 47
}
```

### TraceEntry fields

| Field | Type | Notes |
|---|---|---|
| `nodeId` | string | The node that fired |
| `startedAt` | string (ISO UTC ms) |  |
| `durationMs` | long | Per-node wall time |
| `outcome` | enum | `pass` / `fail` / `skip` / `error` |
| `input` | JSON? | When set, node's resolved input |
| `output` | JSON? | Node's emitted value |
| `ctxRead` | dict? | Context keys the node consumed |
| `ctxWritten` | dict? | Context keys the node wrote |
| `subRuleRunId` | string? | When the node invoked a sub-rule |
| `error` | string? | When outcome=error. **Redacted to a stable code** when `RedactTraceErrors=true`. |

### Redacted error codes

When `RULEFORGE_REDACT_TRACE_ERRORS=true` (default in API host), trace
errors are classified to one of these stable codes. Editor can switch on
them; full message stays in server-side logs.

```
SUBRULE_CYCLE              SUBRULE_DEPTH_EXCEEDED
PER_NODE_TIMEOUT           EVALUATION_TIMEOUT
API_NODE_ERROR             CALC_EVAL_ERROR
FILTER_CONFIG_ERROR        MUTATOR_ERROR
REFERENCE_ERROR            BUCKET_ERROR
ASSERT_FAILED              SORT_ERROR
LIMIT_ERROR                DISTINCT_ERROR
SWITCH_ERROR               GROUP_BY_ERROR
INVALID_JSON               NODE_EXECUTION_ERROR
```

---

## Engine Options (runtime knobs)

`RuleRunner.Options` record. Hosts (Api / Cli) construct this per-call.
Most have sensible defaults — surface in admin UI only the ones operators
might want to tune.

| Option | Default | Purpose |
|---|---|---|
| `Debug` | `false` | Populate trace |
| `SubRuleSource` | required for ruleRef | DF or local file |
| `ReferenceSetSource` | required for reference / mutator-lookup | DF or local file |
| `Clock` | `DateTimeOffset.UtcNow` | Inject for date-filter testing |
| `HttpClient` | required for api node | Shared client for outbound HTTP |
| `MaxSubRuleDepth` | `16` | Prevents infinite sub-rule recursion / cycles across rules |
| `RedactTraceErrors` | `false` (Api host: `true`) | Replace raw error messages with stable codes in trace |
| `MaxReferenceSetRows` | `100_000` | Reject oversize refsets at fetch time |
| `PerNodeTimeoutMs` | `30_000` | Hard ceiling per node; `0` disables |

---

## Validation behavior

**Input gate (always on at the API host):**
- Validates request body against `rule.inputSchema` (JSON Schema 2020-12 via `JsonSchema.Net`).
- On violation: 400 with `{ error: "schema_validation_failed", detail: "$.field: <msg>" }`.
- Empty / missing schema = no constraints.

**Output gate (default on, opt-out via env):**
- Validates `envelope.result` against `rule.outputSchema` after evaluation.
- On violation: **logs warning, doesn't 5xx**. Engine still ships the result.
- Disable via `RULEFORGE_VALIDATE_OUTPUT=false`.

---

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `RULEFORGE_RULE_SOURCE` | `local` | `df` to use DocumentForge, `local` for file fixtures |
| `RULEFORGE_DF_BASE_URL` | `https://documentforge.onrender.com` | DF base URL |
| `RULEFORGE_DF_API_KEY` | (required when `df`) | DF bearer token |
| `RULEFORGE_API_KEY` | (unset) | Caller auth shared secret. Unset = open mode. |
| `RULEFORGE_ENV` | `staging` | Which environment's bindings to load |
| `RULEFORGE_COLLECTION_PREFIX` | `""` | Multi-instance namespacing in DF |
| `RULEFORGE_FIXTURES_DIR` | bundled | Local mode rule fixtures |
| `RULEFORGE_REFS_DIR` | bundled | Local mode reference sets |
| `RULEFORGE_REDACT_TRACE_ERRORS` | `true` | Redact trace `error` to stable codes |
| `RULEFORGE_VALIDATE_OUTPUT` | `true` | Output schema gate |

**Limits hard-coded (not env-tunable yet — file an issue if you need them):**
- Request body cap: 5 MB (Kestrel)
- JSON parse depth: 32 (`JsonDocumentOptions.MaxDepth`)
- Calc expression timeout: 5 s (`CalcEvaluator.DefaultTimeoutMs`)

---

## Schema artifacts published by `schemas` CLI verb

These are the source of truth for editor type generation. Currently
emitted (run `dotnet run --project src/RuleForge.Cli -- schemas --out _schemas`):

```
rule.schema.json                envelope.schema.json
sub-rule-call.schema.json       constant-config.schema.json
string-filter-config.schema.json    number-filter-config.schema.json
date-filter-config.schema.json      mutator-config.schema.json
calc-config.schema.json         iterator-config.schema.json
merge-config.schema.json        reference-config.schema.json
api-config.schema.json          bucket-config.schema.json
assert-config.schema.json       sort-config.schema.json
limit-config.schema.json        distinct-config.schema.json
switch-config.schema.json       group-by-config.schema.json
product-config.schema.json
```

19 schema files. Logic node has no separate config schema (uses node-level
`templateId` + `label`).

---

## Performance characteristics

- Warm steady-state p50 < 1 ms (NVMe Linux: ~0.07 ms; Windows: ~0.27 ms).
- Throughput: 14k req/s single worker on NVMe Linux, ~5k on Windows.
- Per-request reference lookup memo cache (skips redundant row scans on per-pax iteration patterns).
- Perf regression tests gate every PR — fails on >10× slowdown.

---

## What's NOT yet in the engine

UI shouldn't expose these — they don't exist yet. Roughly ordered by
likely value if/when prioritised.

| Feature | Status | Notes |
|---|---|---|
| **Trace enrichment** (per-filter both-sides + operator + per-node duration in trace `evaluatedSource` / `evaluatedLiteral` / `operator` fields) | not built | Editor's explainability UI is gated on this shape |
| **`sensitive` field tag** on `inputSchema` properties for trace masking | not built | Adds a contract; engine masks at trace-emit time |
| **Audit log** (write-only journal of who-changed-what-when) | not built | Belongs partially in DocumentForge layer |
| **Diff API** (`GET /v1/rules/[id]/diff?from=v3&to=v4`) | not built | Structured diff of nodes + bindings |
| **Rollback API** (`POST /v1/rules/[id]/rollback`) | not built |  |
| **Approval workflow** (review → published gate, distinct approver) | not built | `Rule.status` field exists; no enforcement yet |
| **Shadow mode** (`POST /v1/rules/[id]/shadow-eval`) | not built | Pairs naturally with the bucket node |
| **Circuit breaker** per ruleRef call (auto fail-fast on cascading failures) | not built | Polly-based; ~1 hour |
| **Shared-condition dedupe** across sub-rule fan-out (the 1500-rules pattern perf win) | not built | Tier 3 in original brief; 3-5 days |
| **Native N-way `switch` edges** (skip downstream filter chain) | not built | Today switch emits the case name; downstream filters route |
| **Try / error-boundary** node | declined | Covered by `ruleRef + onError=default + defaultValue` |
| **DSL escape hatch** (inline scripting node) | declined | Calc + NCalc functions cover most cases; positioning trap |
| **Decision-table coverage analysis** | declined | DMN-style; defer until volume justifies |
| **Parallel iterator** | declined | Profile first; sequential is correct by default |
| **`sql` / `product` (Cartesian)** node categories | declined | `sql` removed (#4); `product` repurposed (output shape) |

---

## Editor team — what to look for as gaps

Run through this list against your current UI:

1. **Authoring affordance for every category in the table above.** Especially the new ones from this pass: `bucket`, `assert`, `sort`, `limit`, `distinct`, `switch`, `groupBy`, `api`. Each needs a node template under `test-workspace/nodes/` and a config-edit form mirroring its Config record.
2. **Constant + product nodes.** Existed before but recently typed; make sure your form surfaces both `output` and `outputSchema` modes for product.
3. **Schema fields needing UI:**
   - `inputSchema` editor (your existing JSON Schema editor)
   - `outputSchema` editor (same)
   - No `sensitive` tag yet (engine work pending)
4. **Trace UI** — current shape is the basic TraceEntry above. Enriched fields (`evaluatedSource` / `evaluatedLiteral` / `operator`) are NOT present yet — don't render them yet.
5. **Operator / admin UI:**
   - `/admin/bindings` viewer (engine ships it; editor could surface)
   - `/admin/refresh` button
   - `/ready` / `/health` status indicators
6. **Auth setup UI** — surface `RULEFORGE_API_KEY` configuration somewhere (or the equivalent in your deploy flow).
7. **Per-rule debug / explain** — call any bound endpoint with `?debug=true` to get the trace. Editor should expose a "test this rule" affordance that sets that flag.
8. **Validation feedback** — when authoring, the engine's `schemas` CLI emits the schemas that drive form validation. Make sure editor regenerates types from those schemas (engine → editor handshake).

If you find gaps not covered by this doc, the engine probably doesn't
support them yet — file an issue and we'll add it to the "What's NOT yet
in the engine" table.
