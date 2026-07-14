# RuleForge.Admin

The C# intermediate layer for **Policy Studio** (the policy-documents-that-compile-to-rules
authoring UI). The Next.js front end keeps no serious logic — this service owns:

- **Storage** — policy tree (spaces / folders / policies), policy documents
  (TipTap JSON + parsed rule statements), input schemas, data references, test
  cases and settings. Everything persists to DocumentForge over HTTP via
  `DfClient`, namespaced under the `policystudio.` collection prefix.
- **Evaluation** — running test cases against a policy's parsed rule
  statements (`TestRunner`). Verdict semantics mirror the Studio graph:
  every statement gates VALID / else REJECT.
- **Compilation** — `RuleCompiler` turns statements into a RuleForge rule
  graph (input → filters → logic → verdict constants → output). Publishing a
  policy cuts an engine release: rule header + immutable version snapshot +
  `env-dev` binding land in the same prefixed collections `RuleForge.Api`
  reads, so the policy goes live as a REST endpoint without a redeploy
  (`POST /admin/refresh` on the engine picks up new versions).
  `age()` compiles to `within_last` date filters (years → months×12) with a
  logic `not` for `>=`/`>`; `<=`/`>` carry a boundary warning.

## Run (dev)

```powershell
# needs DocumentForge on http://localhost:4300 (see C:\Tools\DocumentForge\start-dfdb.ps1)
dotnet run --launch-profile http     # http://localhost:4310
```

Config (env vars or appsettings):

| Key | Default |
|---|---|
| `ADMIN_DF_BASE_URL` | `http://localhost:4300` |
| `ADMIN_DF_API_KEY` | `dev` (local DF runs `--insecure-dev-mode`) |
| `ADMIN_COLLECTION_PREFIX` | `policystudio.` |

CORS allows any loopback origin (dev posture — revisit before any deploy).

## API surface

| Route | Purpose |
|---|---|
| `GET /health` | liveness + config echo |
| `GET /api/tree` | spaces + folders + policy summaries (no content) |
| `POST /api/spaces` · `POST /api/folders` · `PUT/DELETE /api/folders/{id}` | tree CRUD |
| `POST /api/policies` | create (server supplies default TipTap content) |
| `GET/PUT/DELETE /api/policies/{id}` | document CRUD; `PUT` accepts `title, summary, content, statements, schemaId, folderId, drift`; editing published content auto-drafts `v+1` |
| `POST /api/policies/{id}/publish` | freeze version + **cut an engine release** (compile → rules/ruleversions/environments); compile errors block with 422 |
| `GET /api/policies/{id}/compiled` | compile preview — the rule graph a publish would release |
| `GET /api/releases` | release history, newest first |
| `GET/POST /api/schemas` · `PUT/DELETE /api/schemas/{id}` | schema registry (global + policy-scoped) |
| `GET /api/datarefs?policyId=` · `POST` · `PUT/DELETE /api/datarefs/{id}` | reference data (global + policy-scoped) |
| `GET/POST /api/policies/{id}/tests` · `PUT/DELETE /api/tests/{id}` | test-case CRUD |
| `POST /api/policies/{id}/tests/run` | `{ testIds?: [] }` — run some or all; persists `lastResult` per case |
| `GET/PUT /api/settings` | single global settings document |
| `POST /api/seed?force=` | idempotent dev seeding (skips non-empty collections unless `force=true`) |

Document shapes are owned by the TypeScript client (`policy-studio/lib/api/types.ts`);
this service is deliberately schema-tolerant (raw `JsonObject` storage) and only
types the fields it computes on. See `TestRunner` for the statement/condition
shapes the evaluator expects.
