using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace School.Shared;

public enum SignatureVerificationMode { Off, Shadow, Enforce }

public static class SignatureVerification
{
    private static readonly HashSet<string> ObservedCodes =
    [
        "CLIENT_UNKNOWN", "SIGNATURE_MISSING", "SIGNATURE_DUPLICATED", "SIGNATURE_INVALID",
        "TIMESTAMP_SKEW", "REQUEST_ID_INVALID", "REPLAY_DETECTED"
    ];

    public static SignatureVerificationMode Configure(IConfiguration config, IHostEnvironment environment, string hop)
    {
        var value = config[$"Verification:{hop}"] ?? "Enforce";
        if (!Enum.TryParse<SignatureVerificationMode>(value, true, out var mode) ||
            !Enum.GetNames<SignatureVerificationMode>().Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Verification:{hop} must be Off, Shadow or Enforce.");
        if (environment.IsProduction() && mode != SignatureVerificationMode.Enforce)
            throw new InvalidOperationException($"Production requires Verification:{hop}=Enforce.");
        return mode;
    }

    // The callback contains only signature/header/replay checks. Authorization, CSRF,
    // rate limits, request validation and database failures stay outside this policy.
    public static async Task<bool> CheckAsync(SignatureVerificationMode mode, HttpContext ctx,
        ILogger logger, string hop, Func<Task> verify)
    {
        if (mode == SignatureVerificationMode.Off) return false;
        try { await verify(); return true; }
        catch (Rejection ex) when (mode == SignatureVerificationMode.Shadow && ObservedCodes.Contains(ex.Code))
        {
            logger.LogWarning("Signature shadow {Hop} {Code} {Method} {Route} {Trace}",
                hop, ex.Code, ctx.Request.Method, ctx.Items["SafeRoute"] as string ?? "unmatched", ctx.TraceIdentifier);
            return false;
        }
    }
}
