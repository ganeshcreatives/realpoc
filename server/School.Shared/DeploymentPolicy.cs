using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace School.Shared;

public static class DeploymentPolicy
{
    public static bool RealDataEnabled(IConfiguration config, IHostEnvironment environment) =>
        config.GetValue("Deployment:RealDataEnabled", environment.IsDevelopment());

    public static bool IsDemoWrite(HttpRequest request)
    {
        if (request.Method != "POST") return false;
        var path = request.Path.Value ?? "";
        if (path.StartsWith("/api-proxy", StringComparison.Ordinal)) path = path["/api-proxy".Length..];
        return path is "/auth/register" or "/auth/verify" or "/api/students" or "/api/applications"
            || (path.StartsWith("/api/staff/applications/", StringComparison.Ordinal) && path.EndsWith("/decision", StringComparison.Ordinal));
    }
}
