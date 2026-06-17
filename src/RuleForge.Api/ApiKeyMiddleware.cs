using System.Security.Cryptography;
using System.Text;

namespace RuleForge.Api;

/// <summary>
/// Minimal X-AERO-Key shared-secret check. Configure with the
/// <c>RULEFORGE_API_KEY</c> env var (or <c>RULEFORGE_API_KEY</c>
/// configuration entry). When unset, every request is allowed â€” useful for
/// local dev. When set, only requests whose <c>X-AERO-Key</c> header (or
/// <c>Authorization: Bearer ...</c>) matches are accepted.
///
/// Bypass paths: <c>/health</c> stays open so monitoring + load balancers
/// don't need to ship the key.
/// </summary>
public sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-AERO-Key";

    private static readonly HashSet<string> BypassPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/ready",
    };

    private readonly RequestDelegate _next;
    private readonly string? _expected;
    private readonly IApiKeyValidator _validator;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config, IApiKeyValidator validator)
    {
        _next = next;
        _expected = config["RULEFORGE_API_KEY"]
                    ?? Environment.GetEnvironmentVariable("RULEFORGE_API_KEY");
        _validator = validator;
    }

    public async Task InvokeAsync(HttpContext http)
    {
        if (BypassPaths.Contains(http.Request.Path))
        {
            await _next(http);
            return;
        }

        var hasEnvKey = !string.IsNullOrEmpty(_expected);

        // Enforcement is OFF only when there's no env master key AND no minted
        // DB key — the local-dev default. The moment either exists, a valid
        // X-AERO-Key is required.
        if (!hasEnvKey && !_validator.HasAnyKeys())
        {
            await _next(http);
            return;
        }

        // Always run the constant-time compare regardless of whether the
        // header is present / right-length. TryReadKey returns "" on miss,
        // and FixedTimeEquals pads to expected length internally — so an
        // attacker can no longer probe by timing whether they have the
        // right header name or the right key length. IsValid hashes the
        // supplied key with SHA-256 and matches it against the editor-minted
        // keys in workspace.db.
        TryReadKey(http.Request, out var supplied);
        var ok = (hasEnvKey && FixedTimeEquals(supplied, _expected!)) || _validator.IsValid(supplied);
        if (ok)
        {
            await _next(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.Headers["WWW-Authenticate"] = $"AeroKey realm=\"aero-engine\"";
        await http.Response.WriteAsJsonAsync(new
        {
            error = "missing or invalid X-AERO-Key",
        });
    }

    private static bool TryReadKey(HttpRequest req, out string supplied)
    {
        if (req.Headers.TryGetValue(HeaderName, out var fromHeader) &&
            fromHeader.Count > 0 && !string.IsNullOrEmpty(fromHeader[0]))
        {
            supplied = fromHeader[0]!;
            return true;
        }
        if (req.Headers.TryGetValue("Authorization", out var auth) && auth.Count > 0)
        {
            const string prefix = "Bearer ";
            var v = auth[0] ?? string.Empty;
            if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                supplied = v.Substring(prefix.Length).Trim();
                return supplied.Length > 0;
            }
        }
        supplied = string.Empty;
        return false;
    }

    /// <summary>
    /// Constant-time comparison that does not leak length. Compares
    /// <paramref name="supplied"/> against <paramref name="expected"/> as
    /// UTF-8 bytes. On length mismatch, compares against a zero buffer of
    /// the expected length so the comparison work is identical regardless
    /// of how wrong the supplied value is.
    /// </summary>
    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);

        var lengthMatch = suppliedBytes.Length == expectedBytes.Length;
        var compareBytes = lengthMatch ? suppliedBytes : new byte[expectedBytes.Length];
        var contentMatch = CryptographicOperations.FixedTimeEquals(compareBytes, expectedBytes);

        // & not &&: both must evaluate so a length mismatch can't short-circuit
        // out of the constant-time content compare.
        return lengthMatch & contentMatch;
    }
}
