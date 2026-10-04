using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;

namespace School.Shared;

public static class Crypto
{
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static string Random() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string HashText(string text) => Hash(Encoding.UTF8.GetBytes(text));
    // v1 browser contract: LF separators, lowercase hexadecimal SHA-256, no trailing LF.
    // v2 service contract additionally binds the exact Authorization value to the signature.
    public static string Sign(byte[] key, string method, string path, string timestamp, string requestId, string clientId, byte[] body, string? authorization = null)
    {
        var canonical = string.Join('\n', method, path, timestamp, requestId, clientId, Hash(body));
        if (authorization is not null) canonical = "sc-service-v2\n" + canonical + "\n" + HashText(authorization);
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }
    public static void Verify(HttpRequest request, byte[] body, byte[] key, bool service, string clientId)
    {
        var prefix = service ? "X-" : "X-SC-";
        var stamp = Header(request, prefix + "Timestamp");
        var id = Header(request, prefix + "Request-Id");
        var signature = Header(request, prefix + "Signature");
        if (!long.TryParse(stamp, out var time) || time < Now - 300_000 || time > Now + 300_000) throw new Rejection(403, "TIMESTAMP_SKEW");
        if (!Guid.TryParseExact(id, "D", out _)) throw new Rejection(403, "REQUEST_ID_INVALID");
        var expected = Sign(key, request.Method, request.Path.Value!, stamp, id, clientId, body, service ? request.Headers.Authorization.ToString() : null);
        byte[] supplied;
        try { supplied = Convert.FromBase64String(signature); }
        catch (FormatException) { throw new Rejection(403, "SIGNATURE_INVALID"); }
        if (supplied.Length != 32 || !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(expected), supplied)) throw new Rejection(403, "SIGNATURE_INVALID");
    }
    public static string Header(HttpRequest request, string name)
    {
        if (!request.Headers.TryGetValue(name, out var value)) throw new Rejection(403, "SIGNATURE_MISSING");
        if (value.Count != 1 || string.IsNullOrWhiteSpace(value[0]) || value[0]!.Length > 256 || value[0]!.Contains(',')) throw new Rejection(403, "SIGNATURE_DUPLICATED");
        return value[0]!;
    }
    public static string Encrypt(byte[] key, string plaintext, string sessionId)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(sessionId));
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    public static string Decrypt(byte[] key, string value, string sessionId)
    {
        var bytes = Convert.FromBase64String(value); var plain = new byte[bytes.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(bytes.AsSpan(0,12), bytes.AsSpan(28), bytes.AsSpan(12,16), plain, Encoding.UTF8.GetBytes(sessionId));
        return Encoding.UTF8.GetString(plain);
    }
    public static async Task<byte[]> Body(HttpRequest request, CancellationToken ct)
    {
        const int limit = 32768;
        if (request.ContentLength > limit) throw new Rejection(413, "PAYLOAD_TOO_LARGE");
        using var output = new MemoryStream(); var buffer = new byte[4096]; int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > limit) throw new Rejection(413, "PAYLOAD_TOO_LARGE");
            output.Write(buffer, 0, read);
        }
        if (request.Method == "GET" && output.Length != 0) throw new Rejection(400, "VALIDATION_FAILED");
        if (output.Length > 0 && !string.Equals(request.ContentType?.Split(';')[0], "application/json", StringComparison.OrdinalIgnoreCase)) throw new Rejection(415, "UNSUPPORTED_MEDIA_TYPE");
        return output.ToArray();
    }
    public static void SafeTarget(HttpRequest request)
    {
        var raw = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (request.QueryString.HasValue) throw new Rejection(400, "INVALID_QUERY");
        if (raw != request.Path.Value || raw is null || raw.Contains('%') || raw.Contains('\\') || raw.Contains("//") || raw.Contains("..")) throw new Rejection(400, "BAD_TARGET");
    }
}

public record ServiceKey(string ClientId, string KeyId, string Secret);
public sealed record KeyRing(string ActiveKeyId, ServiceKey[] Keys, string SessionEncryptionKey)
{
    public ServiceKey Active => Keys.Single(x => x.KeyId == ActiveKeyId);
    public byte[] EncryptionKey => Convert.FromBase64String(SessionEncryptionKey);
    public static async Task<KeyRing> Load(IConfiguration configuration, bool needsSessionKey)
    {
        string raw;
        if (configuration["Secrets:Provider"] == "Infisical")
        {
            var origin = new Uri(configuration["Infisical:Url"] ?? "https://us.infisical.com");
            if (origin.Scheme != "https") throw new InvalidOperationException("Infisical requires HTTPS.");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.PostAsJsonAsync("/api/v1/auth/universal-auth/login", new { clientId = configuration["Infisical:ClientId"], clientSecret = configuration["Infisical:ClientSecret"] });
            response.EnsureSuccessStatusCode();
            using var auth = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            http.DefaultRequestHeaders.Authorization = new("Bearer", auth.RootElement.GetProperty("accessToken").GetString());
            var project = Uri.EscapeDataString(configuration["Infisical:ProjectId"] ?? throw new InvalidOperationException("Infisical project missing"));
            var environment = Uri.EscapeDataString(configuration["Infisical:Environment"] ?? "dev");
            var secretName = needsSessionKey ? "SCHOOL_BFF_KEYRING" : "SCHOOL_API_KEYRING";
            using var secret = await http.GetAsync($"/api/v4/secrets/{secretName}?projectId={project}&environment={environment}&secretPath=%2F&type=shared");
            secret.EnsureSuccessStatusCode();
            using var data = JsonDocument.Parse(await secret.Content.ReadAsStringAsync());
            raw = data.RootElement.GetProperty("secret").GetProperty("secretValue").GetString()!;
        }
        else raw = configuration["Secrets:KeyRing"] ?? throw new InvalidOperationException("Signing keyring is required; no generated runtime fallback.");
        var ring = JsonSerializer.Deserialize<KeyRing>(raw, Json.Options) ?? throw new InvalidOperationException("Invalid keyring");
        if (ring.Keys.Length is < 1 or > 3 || ring.Keys.Select(x => x.KeyId).Distinct().Count() != ring.Keys.Length) throw new InvalidOperationException("Invalid key versions");
        foreach (var key in ring.Keys)
            if (Convert.FromBase64String(key.Secret).Length != 32 || !System.Text.RegularExpressions.Regex.IsMatch(key.KeyId, "^[a-zA-Z0-9-]{1,64}$") || key.ClientId != "sc-bff-main") throw new InvalidOperationException("Invalid service key");
        _ = ring.Active;
        if (needsSessionKey && ring.EncryptionKey.Length != 32) throw new InvalidOperationException("Invalid encryption key");
        return ring;
    }
}
