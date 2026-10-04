using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace School.Shared;

public static class HttpPipeline
{
    public static void UseSafeErrors(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var callerAborted = ctx.RequestAborted;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            ctx.RequestAborted = deadline.Token;
            ctx.TraceIdentifier = Guid.NewGuid().ToString("N");
            ctx.Response.Headers["X-Correlation-Id"] = ctx.TraceIdentifier;
            ctx.Response.Headers["X-SC-Server-Time"] = Crypto.Now.ToString();
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            var started = System.Diagnostics.Stopwatch.StartNew();
            try { await next(ctx); }
            catch (OperationCanceledException) when (callerAborted.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                var (status, code) = ex switch
                {
                    Rejection r => (r.Status, r.Code),
                    JsonException or ArgumentException => (400, "VALIDATION_FAILED"),
                    BadHttpRequestException b => (b.StatusCode, b.StatusCode == 413 ? "PAYLOAD_TOO_LARGE" : "VALIDATION_FAILED"),
                    DbUpdateException or DbException => (503, "SECURITY_STATE_UNAVAILABLE"),
                    CryptographicException => (503, "SECURITY_STATE_UNAVAILABLE"),
                    HttpRequestException => (502, "UPSTREAM_UNAVAILABLE"),
                    OperationCanceledException => (504, "UPSTREAM_TIMEOUT"),
                    _ => (500, "INTERNAL_ERROR")
                };
                ctx.RequestAborted = callerAborted;
                if (!ctx.Response.HasStarted) await WriteProblem(ctx, status, code);
                app.Logger.LogWarning("Request rejected {Code} {Trace}", code, ctx.TraceIdentifier);
            }
            finally
            {
                ctx.RequestAborted = callerAborted;
                // Only route templates, status, duration and server-generated correlation IDs.
                app.Logger.LogInformation("HTTP {Method} {Route} {Status} {ElapsedMs} {Trace}", ctx.Request.Method,
                    (ctx.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText ?? "unmatched",
                    ctx.Response.StatusCode, started.ElapsedMilliseconds, ctx.TraceIdentifier);
            }
        });
        app.UseStatusCodePages(async status =>
        {
            var code = status.HttpContext.Response.StatusCode;
            await WriteProblem(status.HttpContext, code, code == 405 ? "METHOD_NOT_ALLOWED" : "ROUTE_NOT_FOUND");
        });
    }
    public static Task WriteProblem(HttpContext ctx, int status, string code)
    {
        ctx.Response.StatusCode = status;
        if (status == 429) ctx.Response.Headers.RetryAfter = "60";
        return ctx.Response.WriteAsJsonAsync(new ProblemDto("urn:school:problem:" + code.ToLowerInvariant().Replace('_','-'), code.Replace('_',' '), status, code, ctx.TraceIdentifier), options: Json.Options, contentType: "application/problem+json", cancellationToken: ctx.RequestAborted);
    }
    public static T Read<T>(HttpContext ctx) => JsonSerializer.Deserialize<T>((byte[])ctx.Items["body"]!, Json.Options) ?? throw new Rejection(400, "VALIDATION_FAILED");
    public static readonly SchoolDto[] Schools = [new("oakwood", "Oakwood Academy", "Hyderabad"), new("riverside", "Riverside Public School", "Bengaluru"), new("cedar", "Cedar Grove School", "Pune")];
}
