using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RuleForge.Core.Loader;

namespace RuleForge.Api;

/// <summary>
/// Periodically POSTs a heartbeat to the control plane (the editor) so its fleet
/// registry knows this engine exists — its URL, version, live binding count, the
/// sync generation it's on, and uptime. Self-registration: no central list of
/// engine URLs to maintain. Disabled unless RULEFORGE_CONTROL_URL is set.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _cfg;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public HeartbeatService(IServiceProvider sp, IConfiguration cfg)
    {
        _sp = sp;
        _cfg = cfg;
    }

    private string? Get(string key) => _cfg[key] ?? Environment.GetEnvironmentVariable(key);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var controlUrl = Get("RULEFORGE_CONTROL_URL")?.TrimEnd('/');
        if (string.IsNullOrEmpty(controlUrl)) return; // heartbeat disabled

        var token = Get("RULEFORGE_SYNC_TOKEN");
        var selfUrl = (Get("RULEFORGE_PUBLIC_URL") ?? Get("ASPNETCORE_URLS") ?? "http://localhost:5050").Split(';')[0].TrimEnd('/');
        var engineId = Get("RULEFORGE_ENGINE_ID") ?? selfUrl;
        var name = Get("RULEFORGE_ENGINE_NAME") ?? engineId;
        var ruleSource = (Get("RULEFORGE_RULE_SOURCE") ?? "local").ToLowerInvariant();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var endpoint = $"{controlUrl}/api/fleet/heartbeat";

        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch { return; } // let the rule source load

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var src = _sp.GetService<IRuleSource>();
                var bindingCount = src is null ? 0 : (await src.ListBindingsAsync(ct)).Count;
                var generation = src is RemoteSyncRuleSource sync ? sync.LastGeneration : null;
                var payload = new
                {
                    engineId,
                    name,
                    url = selfUrl,
                    version,
                    ruleSource,
                    bindingCount,
                    generation,
                    uptimeSeconds = (int)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(token)) req.Headers.Add("X-Sync-Token", token);
                await _http.SendAsync(req, ct);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[heartbeat] {e.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(12), ct); } catch { break; }
        }
    }
}
