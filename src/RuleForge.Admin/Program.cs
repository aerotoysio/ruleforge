using System.Text.Json.Nodes;
using RuleForge.Admin;
using RuleForge.DocumentForge;

// RuleForge.Admin — the C# intermediate layer for Policy Studio.
//
// Policy Studio (Next.js) keeps no serious logic: this service owns storage
// (policy tree, documents, schemas, data references, test cases — all in
// DocumentForge) and evaluation (running test cases against a policy's parsed
// rule statements). The statement→RuleForge-graph compiler will land here too.
//
// Config (env or appsettings):
//   ADMIN_DF_BASE_URL          default http://localhost:4300
//   ADMIN_DF_API_KEY           default "dev" (local DF runs --insecure-dev-mode)
//   ADMIN_COLLECTION_PREFIX    default "policystudio."

var builder = WebApplication.CreateBuilder(args);

string Cfg(string key, string fallback) =>
    builder.Configuration[key] ?? Environment.GetEnvironmentVariable(key) ?? fallback;

var dfBaseUrl = Cfg("ADMIN_DF_BASE_URL", "http://localhost:4300");
var dfApiKey = Cfg("ADMIN_DF_API_KEY", "dev");
var prefix = Cfg("ADMIN_COLLECTION_PREFIX", "policystudio.");

builder.Services.AddSingleton(_ => new DfClient(new HttpClient(), dfBaseUrl, dfApiKey));
builder.Services.AddSingleton(sp => new AdminStore(sp.GetRequiredService<DfClient>(), prefix));

// Dev CORS: the Studio dev server runs on arbitrary localhost ports.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.IsLoopback)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

var today = () => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

app.MapGet("/health", () => Results.Json(new { ok = true, documentForge = dfBaseUrl, prefix }));

// ─── tree ────────────────────────────────────────────────────────────────────

app.MapGet("/api/tree", async (AdminStore store, CancellationToken ct) =>
{
    var spaces = await store.ListAsync("spaces", ct: ct);
    var folders = await store.ListAsync("folders", ct: ct);
    var policies = await store.ListAsync("policies", ct: ct);
    foreach (var p in policies)
    {
        p.Remove("content");
        p.Remove("statements");
    }
    return Results.Json(new { spaces, folders, policies });
});

// ─── spaces / folders ────────────────────────────────────────────────────────

app.MapPost("/api/spaces", async (JsonObject body, AdminStore store, CancellationToken ct) =>
    Results.Json(await store.InsertAsync("spaces", body, "sp", ct)));

app.MapPost("/api/folders", async (JsonObject body, AdminStore store, CancellationToken ct) =>
    Results.Json(await store.InsertAsync("folders", body, "f", ct)));

app.MapPut("/api/folders/{id}", async (string id, JsonObject body, AdminStore store, CancellationToken ct) =>
{
    var existing = await store.GetByIdAsync("folders", id, ct);
    if (existing is null) return Results.NotFound();
    foreach (var key in new[] { "name", "spaceId" })
        if (body[key] is not null) existing[key] = body[key]!.DeepClone();
    return Results.Json(await store.ReplaceByIdAsync("folders", id, existing, ct));
});

app.MapDelete("/api/folders/{id}", async (string id, AdminStore store, CancellationToken ct) =>
    await store.DeleteByIdAsync("folders", id, ct) ? Results.NoContent() : Results.NotFound());

// ─── policies ────────────────────────────────────────────────────────────────

app.MapPost("/api/policies", async (JsonObject body, AdminStore store, CancellationToken ct) =>
{
    var doc = new JsonObject
    {
        ["folderId"] = body["folderId"]?.DeepClone(),
        ["title"] = body["title"]?.DeepClone() ?? "Untitled policy",
        ["summary"] = body["summary"]?.DeepClone() ?? "",
        ["status"] = "draft",
        ["version"] = 1,
        ["versions"] = new JsonArray(),
        ["owner"] = body["owner"]?.DeepClone() ?? "Policy Studio",
        ["updatedAt"] = today(),
        ["content"] = body["content"]?.DeepClone() ?? DefaultContent(),
    };
    return Results.Json(await store.InsertAsync("policies", doc, "d", ct));
});

app.MapGet("/api/policies/{id}", async (string id, AdminStore store, CancellationToken ct) =>
{
    var doc = await store.GetByIdAsync("policies", id, ct);
    return doc is null ? Results.NotFound() : Results.Json(doc);
});

app.MapPut("/api/policies/{id}", async (string id, JsonObject body, AdminStore store, CancellationToken ct) =>
{
    var existing = await store.GetByIdAsync("policies", id, ct);
    if (existing is null) return Results.NotFound();

    var contentChanged = body["content"] is not null;
    foreach (var key in new[] { "title", "summary", "content", "statements", "schemaId", "folderId", "drift" })
        if (body[key] is not null) existing[key] = body[key]!.DeepClone();

    // Published documents are immutable — editing content opens the next draft.
    if (contentChanged && existing["status"]?.GetValue<string>() == "published")
    {
        existing["status"] = "draft";
        existing["version"] = (existing["version"]?.GetValue<int>() ?? 1) + 1;
    }
    existing["updatedAt"] = today();
    return Results.Json(await store.ReplaceByIdAsync("policies", id, existing, ct));
});

app.MapDelete("/api/policies/{id}", async (string id, AdminStore store, CancellationToken ct) =>
{
    var deleted = await store.DeleteByIdAsync("policies", id, ct);
    if (!deleted) return Results.NotFound();
    await store.DeleteWhereAsync("tests", $"policyId = '{AdminStore.Escape(id)}'", ct);
    return Results.NoContent();
});

// Publish = freeze the policy version AND cut an engine release: the
// statements compile to a RuleForge rule graph, which lands in the same
// prefixed collections RuleForge.Api reads (rules / ruleversions /
// environments). A compile error BLOCKS the publish (HANDOVER open question
// 1 resolved: compiler error, not lint) — parse *warnings* still publish.
app.MapPost("/api/policies/{id}/publish", async (string id, JsonObject body, AdminStore store, CancellationToken ct) =>
{
    var existing = await store.GetByIdAsync("policies", id, ct);
    if (existing is null) return Results.NotFound();

    var endpoint = existing["endpoint"]?.GetValue<string>()
                   ?? "/v1/policies/" + Slug(existing["title"]?.GetValue<string>() ?? id);

    RuleCompiler.CompileResult compiled;
    try
    {
        compiled = RuleCompiler.Compile(existing, id, endpoint);
    }
    catch (RuleCompiler.CompileException ex)
    {
        return Results.UnprocessableEntity(new { errors = ex.Errors });
    }

    var version = existing["version"]?.GetValue<int>() ?? 1;
    var ruleId = $"rule-{id}";

    // Engine release — header, immutable version snapshot, env binding.
    await UpsertAsync(store, "rules", ruleId, new JsonObject
    {
        ["id"] = ruleId,
        ["name"] = existing["title"]?.DeepClone() ?? id,
        ["endpoint"] = endpoint,
        ["method"] = "POST",
        ["status"] = "published",
        ["currentVersion"] = version,
    }, ct);

    await UpsertAsync(store, "ruleversions", $"{ruleId}@v{version}", new JsonObject
    {
        ["id"] = $"{ruleId}@v{version}",
        ["ruleId"] = ruleId,
        ["version"] = version,
        ["snapshot"] = compiled.Rule.DeepClone(),
    }, ct);

    var env = await store.GetByIdAsync("environments", "env-dev", ct)
              ?? new JsonObject { ["id"] = "env-dev", ["name"] = "dev", ["ruleBindings"] = new JsonObject() };
    var bindings = env["ruleBindings"] as JsonObject ?? new JsonObject();
    bindings[ruleId] = version;
    env["ruleBindings"] = bindings;
    await UpsertAsync(store, "environments", "env-dev", env, ct);

    var release = new JsonObject
    {
        ["policyId"] = id,
        ["policyVersion"] = version,
        ["ruleId"] = ruleId,
        ["ruleVersion"] = version,
        ["endpoint"] = endpoint,
        ["note"] = body["note"]?.DeepClone() ?? "",
        ["warnings"] = new JsonArray(compiled.Warnings.Select(w => (JsonNode)w).ToArray()),
        ["publishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
    };
    release = await store.InsertAsync("releases", release, "rel", ct);

    // Policy bookkeeping — mirror of the pre-compiler behavior.
    var versions = existing["versions"] as JsonArray ?? new JsonArray();
    foreach (var v in versions)
        if (v is JsonObject vo && vo["status"]?.GetValue<string>() == "published")
            vo["status"] = "superseded";

    versions.Add(new JsonObject
    {
        ["v"] = version,
        ["date"] = today(),
        ["author"] = existing["owner"]?.DeepClone() ?? "Policy Studio",
        ["note"] = body["note"]?.DeepClone() ?? "",
        ["status"] = "published",
    });

    existing["versions"] = versions;
    existing["status"] = "published";
    existing["drift"] = false;
    existing["endpoint"] = endpoint;
    existing["lastRelease"] = release.DeepClone();
    existing["updatedAt"] = today();
    return Results.Json(await store.ReplaceByIdAsync("policies", id, existing, ct));
});

// Compile preview — the rule graph a publish would release, without releasing.
app.MapGet("/api/policies/{id}/compiled", async (string id, AdminStore store, CancellationToken ct) =>
{
    var policy = await store.GetByIdAsync("policies", id, ct);
    if (policy is null) return Results.NotFound();
    var endpoint = policy["endpoint"]?.GetValue<string>()
                   ?? "/v1/policies/" + Slug(policy["title"]?.GetValue<string>() ?? id);
    try
    {
        var compiled = RuleCompiler.Compile(policy, id, endpoint);
        return Results.Json(new
        {
            rule = compiled.Rule,
            warnings = compiled.Warnings,
            endpoint,
        });
    }
    catch (RuleCompiler.CompileException ex)
    {
        return Results.UnprocessableEntity(new { errors = ex.Errors });
    }
});

app.MapGet("/api/releases", async (AdminStore store, CancellationToken ct) =>
{
    var releases = await store.ListAsync("releases", ct: ct);
    return Results.Json(releases
        .OrderByDescending(r => r["publishedAt"]?.GetValue<string>() ?? "")
        .ToList());
});

// ─── schemas ─────────────────────────────────────────────────────────────────

app.MapGet("/api/schemas", async (AdminStore store, CancellationToken ct) =>
    Results.Json(await store.ListAsync("schemas", ct: ct)));

app.MapPost("/api/schemas", async (JsonObject body, AdminStore store, CancellationToken ct) =>
    Results.Json(await store.InsertAsync("schemas", body, "sch", ct)));

app.MapPut("/api/schemas/{id}", async (string id, JsonObject body, AdminStore store, CancellationToken ct) =>
{
    var replaced = await store.ReplaceByIdAsync("schemas", id, body, ct);
    return replaced is null ? Results.NotFound() : Results.Json(replaced);
});

app.MapDelete("/api/schemas/{id}", async (string id, AdminStore store, CancellationToken ct) =>
    await store.DeleteByIdAsync("schemas", id, ct) ? Results.NoContent() : Results.NotFound());

// ─── data references ─────────────────────────────────────────────────────────

app.MapGet("/api/datarefs", async (string? policyId, AdminStore store, CancellationToken ct) =>
{
    var where = policyId is null
        ? null
        : $"scope = 'global' OR policyId = '{AdminStore.Escape(policyId)}'";
    return Results.Json(await store.ListAsync("datarefs", where, ct));
});

app.MapPost("/api/datarefs", async (JsonObject body, AdminStore store, CancellationToken ct) =>
    Results.Json(await store.InsertAsync("datarefs", body, "ref", ct)));

app.MapPut("/api/datarefs/{id}", async (string id, JsonObject body, AdminStore store, CancellationToken ct) =>
{
    var replaced = await store.ReplaceByIdAsync("datarefs", id, body, ct);
    return replaced is null ? Results.NotFound() : Results.Json(replaced);
});

app.MapDelete("/api/datarefs/{id}", async (string id, AdminStore store, CancellationToken ct) =>
    await store.DeleteByIdAsync("datarefs", id, ct) ? Results.NoContent() : Results.NotFound());

// ─── tests ───────────────────────────────────────────────────────────────────

app.MapGet("/api/policies/{id}/tests", async (string id, AdminStore store, CancellationToken ct) =>
    Results.Json(await store.ListAsync("tests", $"policyId = '{AdminStore.Escape(id)}'", ct)));

app.MapPost("/api/policies/{id}/tests", async (string id, JsonObject body, AdminStore store, CancellationToken ct) =>
{
    body["policyId"] = id;
    return Results.Json(await store.InsertAsync("tests", body, "t", ct));
});

app.MapPut("/api/tests/{testId}", async (string testId, JsonObject body, AdminStore store, CancellationToken ct) =>
{
    var replaced = await store.ReplaceByIdAsync("tests", testId, body, ct);
    return replaced is null ? Results.NotFound() : Results.Json(replaced);
});

app.MapDelete("/api/tests/{testId}", async (string testId, AdminStore store, CancellationToken ct) =>
    await store.DeleteByIdAsync("tests", testId, ct) ? Results.NoContent() : Results.NotFound());

app.MapPost("/api/policies/{id}/tests/run", async (string id, JsonObject? body, AdminStore store, CancellationToken ct) =>
{
    var policy = await store.GetByIdAsync("policies", id, ct);
    if (policy is null) return Results.NotFound(new { error = $"policy {id} not found" });

    var statements = policy["statements"] as JsonArray ?? new JsonArray();
    var tests = await store.ListAsync("tests", $"policyId = '{AdminStore.Escape(id)}'", ct);

    var requested = (body?["testIds"] as JsonArray)?
        .Select(n => n?.GetValue<string>())
        .Where(s => s is not null)
        .ToHashSet();
    if (requested is { Count: > 0 })
        tests = tests.Where(t => requested.Contains(t["id"]?.GetValue<string>())).ToList();

    var ranAt = DateTimeOffset.UtcNow.ToString("O");
    var results = new List<JsonObject>();

    foreach (var test in tests)
    {
        var testId = test["id"]?.GetValue<string>() ?? "?";
        JsonObject result;
        try
        {
            var input = test["input"] as JsonObject ?? new JsonObject();
            var evalDate = test["evaluationDate"]?.GetValue<string>() is { } d
                ? DateOnly.Parse(d)
                : DateOnly.FromDateTime(DateTime.UtcNow);

            var run = TestRunner.Run(statements, input, evalDate);
            var expected = test["expect"]?["verdict"]?.GetValue<string>() ?? "VALID";

            result = new JsonObject
            {
                ["testId"] = testId,
                ["status"] = run.Verdict == expected ? "pass" : "fail",
                ["verdict"] = run.Verdict,
                ["statements"] = new JsonArray(run.Statements.Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id,
                    ["text"] = s.Text,
                    ["pass"] = s.Pass,
                    ["conditions"] = new JsonArray(s.Conditions.Select(c => (JsonNode)new JsonObject
                    {
                        ["expr"] = c.Expr,
                        ["pass"] = c.Pass,
                        ["actual"] = c.Actual,
                    }).ToArray()),
                }).ToArray()),
                ["ranAt"] = ranAt,
            };
        }
        catch (Exception ex)
        {
            result = new JsonObject
            {
                ["testId"] = testId,
                ["status"] = "error",
                ["statements"] = new JsonArray(),
                ["error"] = ex.Message,
                ["ranAt"] = ranAt,
            };
        }

        test["lastResult"] = result.DeepClone();
        await store.ReplaceByIdAsync("tests", testId, test, ct);
        results.Add(result);
    }

    return Results.Json(new { results });
});

// ─── settings ────────────────────────────────────────────────────────────────

app.MapGet("/api/settings", async (AdminStore store, CancellationToken ct) =>
    Results.Json(await store.GetByIdAsync("settings", "global", ct) ?? new JsonObject { ["id"] = "global" }));

app.MapPut("/api/settings", async (JsonObject body, AdminStore store, CancellationToken ct) =>
{
    body["id"] = "global";
    var replaced = await store.ReplaceByIdAsync("settings", "global", body, ct);
    return Results.Json(replaced ?? await store.InsertAsync("settings", body, "settings", ct));
});

// ─── seed ────────────────────────────────────────────────────────────────────
//
// Idempotent dev seeding: each collection in the payload is only written when
// currently empty. ?force=true wipes and rewrites the payload's collections.

app.MapPost("/api/seed", async (JsonObject body, bool? force, AdminStore store, CancellationToken ct) =>
{
    var report = new JsonObject();
    var collections = new[]
    {
        ("spaces", "sp"), ("folders", "f"), ("policies", "d"),
        ("schemas", "sch"), ("datarefs", "ref"), ("tests", "t"),
    };

    foreach (var (name, idPrefix) in collections)
    {
        if (body[name] is not JsonArray items) continue;

        var existing = await store.CountAsync(name, ct);
        if (existing > 0 && force != true)
        {
            report[name] = $"skipped ({existing} existing)";
            continue;
        }
        if (existing > 0) await store.DeleteWhereAsync(name, "id != ''", ct);

        var inserted = 0;
        foreach (var item in items.ToArray())
        {
            if (item is not JsonObject obj) continue;
            await store.InsertAsync(name, (JsonObject)obj.DeepClone(), idPrefix, ct);
            inserted++;
        }
        report[name] = $"seeded {inserted}";
    }

    return Results.Json(report);
});

await app.RunAsync();

static async Task UpsertAsync(AdminStore store, string collection, string id, JsonObject doc, CancellationToken ct)
{
    doc["id"] = id;
    var replaced = await store.ReplaceByIdAsync(collection, id, doc, ct);
    if (replaced is null) await store.InsertAsync(collection, doc, id, ct);
}

static string Slug(string s) =>
    string.Join("-",
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        is { Length: > 0 } slug ? slug : "policy";

static JsonNode DefaultContent() => new JsonObject
{
    ["type"] = "doc",
    ["content"] = new JsonArray(new JsonObject
    {
        ["type"] = "paragraph",
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = "Describe the policy in plain language, then formalise key sentences as rules. Type @ inside a rule sentence to bind a schema field.",
        }),
    }),
};
