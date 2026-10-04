using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using School.Shared;

var builder = WebApplication.CreateBuilder(args);
var configPath = Environment.GetEnvironmentVariable("SCHOOL_CONFIG");
if (configPath is not null) builder.Configuration.AddJsonFile(configPath, optional: false).AddEnvironmentVariables();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 32768);
var origin = new Uri(builder.Configuration["PublicOrigin"] ?? throw new InvalidOperationException("PublicOrigin required"));
if ((!builder.Environment.IsDevelopment() && origin.Scheme != "https") || origin.AbsolutePath != "/") throw new InvalidOperationException("Configure an HTTPS origin without a path.");
var backend = new Uri(builder.Configuration["ApiOrigin"] ?? "http://127.0.0.1:5081");
if (backend.AbsolutePath != "/" || (backend.Scheme != "https" && !(backend.Scheme == "http" && backend.IsLoopback))) throw new InvalidOperationException("API must use HTTPS or private loopback.");
builder.Configuration["AllowedHosts"] = origin.Host + ";localhost;127.0.0.1";
builder.Services.AddSchoolDatabase(builder.Configuration);
var upstreamTimeoutSeconds = builder.Configuration.GetValue("Timeouts:UpstreamSeconds", 8);
if (upstreamTimeoutSeconds is < 3 or > 55) throw new InvalidOperationException("Upstream timeout must be between 3 and 55 seconds.");
builder.Services.AddHttpClient("api", c => { c.BaseAddress = backend; c.Timeout = TimeSpan.FromSeconds(upstreamTimeoutSeconds); }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(3), AutomaticDecompression = DecompressionMethods.None });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
var keys = await KeyRing.Load(builder.Configuration, true);
var cookieName = origin.Scheme == "https" ? "__Host-sc_session_main" : "sc_dev_session";
var app = builder.Build();
app.UseSafeErrors();
if (origin.Scheme == "https") app.Use(async (ctx,next) => { ctx.Response.Headers.StrictTransportSecurity = "max-age=31536000"; await next(ctx); });
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (SchoolDb db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (!await db.Database.CanConnectAsync(ct)) return Results.StatusCode(503);
    using var response = await clients.CreateClient("api").GetAsync("/health/ready", ct);
    return response.IsSuccessStatusCode ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
});
app.UseDefaultFiles(); app.UseStaticFiles();

app.MapMethods("/api-proxy/{**path}", ["GET","POST","PUT","PATCH","DELETE","OPTIONS"], async (HttpContext ctx, SchoolDb db, IHttpClientFactory clients, CancellationToken ct) =>
{
    Crypto.SafeTarget(ctx.Request);
    var path = ctx.Request.Path.Value!["/api-proxy".Length..]; var method = ctx.Request.Method;
    ctx.Items["SafeRoute"] = SafeRoute(path);
    var isAuth = new[] { "/auth/register", "/auth/login", "/auth/verify", "/auth/forgot", "/auth/reset" }.Contains(path);
    var isContext = path == "/session/context";
    var allowed = isAuth || isContext ? new[] { "POST" } : AllowedMethods(path);
    if (allowed.Length == 0) throw new Rejection(404,"ROUTE_NOT_FOUND");
    if (!allowed.Contains(method)) { ctx.Response.Headers.Allow = string.Join(", ",allowed); throw new Rejection(405,"METHOD_NOT_ALLOWED"); }
    if (method != "GET" && ctx.Request.Headers.Origin.ToString() != origin.GetLeftPart(UriPartial.Authority)) throw new Rejection(403,"CSRF_ORIGIN");
    if (ctx.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site") throw new Rejection(403,"CSRF_ORIGIN");
    // Ignore forwarded IP headers unless a reviewed deployment adds a trusted-proxy policy.
    // Behind a proxy this deliberately becomes a shared ingress limit, plus independent account/user limits.
    var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    await db.Limit("source:" + source, 300, 60, ct);
    if (isAuth) await db.Limit("auth-source:" + source, 30,60,ct);
    var body = await Crypto.Body(ctx.Request,ct);
    BrowserSession? session = null; SessionPayload? payload = null;
    if (!isAuth)
    {
        (session,payload) = await LoadSession(ctx,db,ct);
        if (isContext)
        {
            await db.Limit("context:" + session.UserId,20,60,ct);
            var current = await Send(ctx,db,clients,"GET","/api/me",[],payload.Token,session,ct);
            await TouchSession(session,db,ct);
            var user = Deserialize<UserDto>(current.Bytes);
            return Results.Ok(new ContextDto(payload.SigningKey,session.ExpiresAt,Crypto.Now,user));
        }
        if (Crypto.Header(ctx.Request,"X-SC-App") != "main" || Crypto.Header(ctx.Request,"X-SC-Session") != "cookie") throw new Rejection(403,"CLIENT_UNKNOWN");
        Crypto.Verify(ctx.Request,body,Convert.FromBase64String(payload.SigningKey),false,"main");
        await db.Claim("browser:" + session.Id,Crypto.Header(ctx.Request,"X-SC-Request-Id"),ct);
        await db.Limit("user:" + session.UserId,120,60,ct);
        await TouchSession(session,db,ct);
    }
    var upstream = await Send(ctx,db,clients,method,path,body,payload?.Token,session,ct);
    if (path == "/auth/login")
    {
        var auth = Deserialize<AuthDto>(upstream.Bytes);
        if (!Regex.IsMatch(auth.Token,"^[a-f0-9]{64}$") || auth.ExpiresAt <= Crypto.Now || auth.ExpiresAt > Crypto.Now + 28_800_000 || !Guid.TryParse(auth.User.Id,out _)) throw new Rejection(502,"UPSTREAM_RESPONSE_INVALID");
        var browserKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var cookie = Crypto.Random(); var id = Crypto.HashText(cookie);
        var newPayload = new SessionPayload(auth.Token,browserKey,auth.User);
        var oldCookie = ctx.Request.Cookies[cookieName];
        if (oldCookie is not null)
        {
            var oldId = Crypto.HashText(oldCookie);
            var old = await db.Sessions.SingleOrDefaultAsync(x => x.Id == oldId,ct);
            if (old is not null)
            {
                var prior = JsonSerializer.Deserialize<SessionPayload>(Crypto.Decrypt(keys.EncryptionKey,old.EncryptedPayload,old.Id),Json.Options)!;
                var priorHash = Crypto.HashText(prior.Token); await db.Tokens.Where(x => x.Id == priorHash).ExecuteDeleteAsync(ct); db.Sessions.Remove(old);
            }
        }
        db.Sessions.Add(new() { Id = id, UserId = auth.User.Id, ExpiresAt = auth.ExpiresAt, LastSeen = Crypto.Now, EncryptedPayload = Crypto.Encrypt(keys.EncryptionKey,JsonSerializer.Serialize(newPayload,Json.Options),id) });
        await db.SaveChangesAsync(ct);
        ctx.Response.Cookies.Append(cookieName,cookie,CookieOptions(auth.ExpiresAt));
        return Results.Ok(new ContextDto(browserKey,auth.ExpiresAt,Crypto.Now,auth.User));
    }
    if (path == "/api/logout")
    {
        await db.Sessions.Where(x => x.Id == session!.Id).ExecuteDeleteAsync(ct);
        ctx.Response.Cookies.Delete(cookieName,CookieOptions(Crypto.Now)); return Results.NoContent();
    }
    // Explicit DTO allowlists; never forward arbitrary upstream headers, cookies or token fields.
    object output = path switch
    {
        "/api/me" => Deserialize<UserDto>(upstream.Bytes),
        "/api/schools" => Deserialize<SchoolDto[]>(upstream.Bytes),
        "/api/students" when method == "GET" => Deserialize<StudentDto[]>(upstream.Bytes),
        "/api/students" => Deserialize<StudentDto>(upstream.Bytes),
        "/api/applications" when method == "GET" => Deserialize<ApplicationDto[]>(upstream.Bytes),
        "/api/applications" => Deserialize<ApplicationDto>(upstream.Bytes),
        "/api/staff/applications" => Deserialize<StaffApplicationDto[]>(upstream.Bytes),
        _ when path.EndsWith("/balance") => CheckedBalance(path,upstream.Bytes),
        _ => Deserialize<MessageDto>(upstream.Bytes)
    };
    return Results.Json(output,Json.Options,statusCode:upstream.Status);
});
app.MapFallback(async ctx =>
{
    if (ctx.Request.Method != "GET" || ctx.Request.Path.StartsWithSegments("/api-proxy") || Path.HasExtension(ctx.Request.Path)) { await HttpPipeline.WriteProblem(ctx,404,"ROUTE_NOT_FOUND"); return; }
    var index = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath,"wwwroot"),"index.html");
    if (!File.Exists(index)) { await HttpPipeline.WriteProblem(ctx,503,"FRONTEND_NOT_BUILT"); return; }
    ctx.Response.ContentType = "text/html"; await ctx.Response.SendFileAsync(index);
});
await app.RunAsync();

CookieOptions CookieOptions(long expires) => new() { HttpOnly = true, Secure = origin.Scheme == "https", SameSite = SameSiteMode.Strict, Path = "/", Expires = DateTimeOffset.FromUnixTimeMilliseconds(expires), IsEssential = true };
async Task<(BrowserSession,SessionPayload)> LoadSession(HttpContext ctx,SchoolDb db,CancellationToken ct)
{
    var cookie = ctx.Request.Cookies[cookieName];
    if (cookie is null || !Regex.IsMatch(cookie,"^[a-f0-9]{64}$")) throw new Rejection(401,"SESSION_REQUIRED");
    var id = Crypto.HashText(cookie); var now = Crypto.Now;
    var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ExpiresAt > now && x.LastSeen > now - 1_800_000,ct) ?? throw new Rejection(401,"SESSION_EXPIRED");
    if (!await db.Accounts.AnyAsync(x => x.Id == session.UserId && x.Active && x.EmailVerified,ct)) throw new Rejection(401,"SESSION_EXPIRED");
    var payload = JsonSerializer.Deserialize<SessionPayload>(Crypto.Decrypt(keys.EncryptionKey,session.EncryptedPayload,id),Json.Options) ?? throw new Rejection(503,"SECURITY_STATE_UNAVAILABLE");
    return (session,payload);
}
static async Task TouchSession(BrowserSession session,SchoolDb db,CancellationToken ct)
{
    var now = Crypto.Now;
    if (await db.Sessions.Where(x => x.Id == session.Id && x.ExpiresAt > now && x.LastSeen > now - 1_800_000).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeen,now),ct) != 1) throw new Rejection(401,"SESSION_EXPIRED");
}
async Task<Upstream> Send(HttpContext ctx,SchoolDb db,IHttpClientFactory clients,string method,string path,byte[] body,string? token,BrowserSession? session,CancellationToken ct)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(upstreamTimeoutSeconds));
    using var request = new HttpRequestMessage(new HttpMethod(method),path);
    if (body.Length > 0) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
    var authorization = token is null ? "" : "Bearer " + token;
    if (token is not null) request.Headers.Authorization = new("Bearer",token);
    var stamp = Crypto.Now.ToString(); var requestId = Guid.NewGuid().ToString(); var key = keys.Active;
    request.Headers.Add("X-Timestamp",stamp); request.Headers.Add("X-Request-Id",requestId); request.Headers.Add("X-Client-Id",key.ClientId); request.Headers.Add("X-Key-Id",key.KeyId); request.Headers.Add("X-Signature-Version","2");
    request.Headers.Add("X-Signature",Crypto.Sign(Convert.FromBase64String(key.Secret),method,path,stamp,requestId,key.ClientId,body,authorization));
    request.Headers.Add("X-Correlation-Id",ctx.TraceIdentifier);
    using var response = await clients.CreateClient("api").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
    if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/problem+json") && response.StatusCode != HttpStatusCode.NoContent) throw new Rejection(502,"UPSTREAM_RESPONSE_INVALID");
    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); using var output = new MemoryStream();
    var buffer = new byte[4096]; int read;
    while ((read = await stream.ReadAsync(buffer,deadline.Token)) > 0) { if (output.Length + read > 65536) throw new Rejection(502,"UPSTREAM_RESPONSE_INVALID"); output.Write(buffer,0,read); }
    var bytes = output.ToArray(); var status = (int)response.StatusCode;
    if (!response.IsSuccessStatusCode)
    {
        var problem = Deserialize<ProblemDto>(bytes);
        if (status == 403 && problem.Code != "ACCESS_DENIED") throw new Rejection(502,"BFF_SERVICE_AUTH_FAILED");
        if (status == 401 && session is not null)
        {
            await db.Sessions.Where(x => x.Id == session.Id).ExecuteDeleteAsync(ct); ctx.Response.Cookies.Delete(cookieName,CookieOptions(Crypto.Now)); throw new Rejection(401,"SESSION_EXPIRED");
        }
        string[] safe = ["VALIDATION_FAILED","PASSWORD_POLICY","INVALID_CREDENTIALS","LINK_INVALID","RECORD_NOT_FOUND","ACCESS_DENIED","RATE_LIMITED","APPLICATION_ALREADY_EXISTS","IDEMPOTENCY_CONFLICT","VERSION_CONFLICT","STUDENT_LIMIT","MAIL_UNAVAILABLE"];
        if (status >= 500) throw new Rejection(503,"SERVICE_UNAVAILABLE");
        if (!safe.Contains(problem.Code) || status != problem.Status) throw new Rejection(502,"UPSTREAM_RESPONSE_INVALID");
        throw new Rejection(status,problem.Code);
    }
    return new(status,bytes);
}
static string[] AllowedMethods(string path)
{
    if (path is "/api/me" or "/api/schools" or "/api/staff/applications" || Regex.IsMatch(path,"^/api/students/[a-fA-F0-9-]{36}/balance$")) return ["GET"];
    if (path is "/api/students" or "/api/applications") return ["GET","POST"];
    if (path == "/api/logout" || Regex.IsMatch(path,"^/api/staff/applications/[a-fA-F0-9-]{36}/[a-fA-F0-9-]{36}/decision$")) return ["POST"];
    return [];
}
static string SafeRoute(string path)
{
    if (path is "/auth/register" or "/auth/login" or "/auth/verify" or "/auth/forgot" or "/auth/reset"
        or "/session/context" or "/api/me" or "/api/schools" or "/api/students"
        or "/api/applications" or "/api/staff/applications" or "/api/logout") return "/api-proxy" + path;
    if (Regex.IsMatch(path,"^/api/students/[a-fA-F0-9-]{36}/balance$")) return "/api-proxy/api/students/{id}/balance";
    if (Regex.IsMatch(path,"^/api/staff/applications/[a-fA-F0-9-]{36}/[a-fA-F0-9-]{36}/decision$")) return "/api-proxy/api/staff/applications/{id}/{studentId}/decision";
    return "/api-proxy/unmatched";
}
static T Deserialize<T>(byte[] bytes)
{
    try { return JsonSerializer.Deserialize<T>(bytes,Json.Options) ?? throw new Rejection(502,"UPSTREAM_RESPONSE_INVALID"); }
    catch (JsonException) { throw new Rejection(502,"UPSTREAM_RESPONSE_INVALID"); }
}
static BalanceDto CheckedBalance(string path,byte[] bytes)
{
    var value = Deserialize<BalanceDto>(bytes);
    if (value.StudentId != path.Split('/')[3] || value.Currency != "USD") throw new Rejection(502,"UPSTREAM_RESPONSE_INVALID"); return value;
}
record SessionPayload(string Token,string SigningKey,UserDto User);
record MessageDto(string Message);
record Upstream(int Status,byte[] Bytes);
