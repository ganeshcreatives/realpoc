using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using School.Shared;
using School.Api;

var builder = WebApplication.CreateBuilder(args);
var configPath = Environment.GetEnvironmentVariable("SCHOOL_CONFIG");
if (configPath is not null) builder.Configuration.AddJsonFile(configPath, optional: false).AddEnvironmentVariables();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 32768);
builder.Services.AddSchoolDatabase(builder.Configuration);
builder.Services.AddSingleton<IPasswordHasher<Account>>(new PasswordHasher<Account>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 210_000 })));
builder.Services.AddSingleton<AccountMail>();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
var keys = await KeyRing.Load(builder.Configuration, false);
if (!builder.Environment.IsDevelopment())
{
    var mode = builder.Configuration["Mail:Mode"];
    if (mode is not ("Smtp" or "Resend") || string.IsNullOrWhiteSpace(builder.Configuration["Mail:From"]) || (mode == "Smtp" && string.IsNullOrWhiteSpace(builder.Configuration["Mail:Host"])) || (mode == "Resend" && string.IsNullOrWhiteSpace(builder.Configuration["Mail:ApiKey"]))) throw new InvalidOperationException("Production requires configured email delivery.");
}
var app = builder.Build();
app.UseSafeErrors();
// Database creation is an explicit operator command, never performed by a running web service.
if (args.Contains("--migrate-db"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<SchoolDb>().Database.MigrateAsync();
    Console.WriteLine("Reviewed database migrations applied."); return;
}
if (args.Contains("--grant-staff"))
{
    var index = Array.IndexOf(args, "--grant-staff");
    if (index + 1 >= args.Length) throw new InvalidOperationException("Supply the verified staff email.");
    using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SchoolDb>();
    var email = args[index + 1].Trim().ToLowerInvariant();
    var user = await db.Accounts.SingleOrDefaultAsync(x => x.Email == email && x.Active && x.EmailVerified) ?? throw new InvalidOperationException("Verified account not found.");
    user.Role = "staff";
    await db.Tokens.Where(x => x.UserId == user.Id).ExecuteDeleteAsync();
    await db.Sessions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync();
    db.AuditEvents.Add(new() { ActorId = "operator", Action = "staff.grant", RecordId = user.Id }); await db.SaveChangesAsync();
    Console.WriteLine("Staff permission granted; existing sessions revoked."); return;
}
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (SchoolDb db, CancellationToken ct) => await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
var dummy = new Account();
var dummyHash = app.Services.GetRequiredService<IPasswordHasher<Account>>().HashPassword(dummy, Crypto.Random());

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/health")) { await next(ctx); return; }
    Crypto.SafeTarget(ctx.Request);
    var body = await Crypto.Body(ctx.Request, ctx.RequestAborted); ctx.Items["body"] = body;
    var client = Crypto.Header(ctx.Request, "X-Client-Id"); var keyId = Crypto.Header(ctx.Request, "X-Key-Id");
    var key = keys.Keys.SingleOrDefault(k => k.KeyId == keyId && k.ClientId == client) ?? throw new Rejection(403, "CLIENT_UNKNOWN");
    if (Crypto.Header(ctx.Request, "X-Signature-Version") != "2") throw new Rejection(403, "SIGNATURE_INVALID");
    Crypto.Verify(ctx.Request, body, Convert.FromBase64String(key.Secret), true, client);
    var db = ctx.RequestServices.GetRequiredService<SchoolDb>();
    await db.Claim("service:" + client, Crypto.Header(ctx.Request, "X-Request-Id"), ctx.RequestAborted);
    await db.Limit("api:" + client, 1200, 60, ctx.RequestAborted);
    if (!ctx.Request.Path.StartsWithSegments("/auth"))
    {
        var token = ctx.Request.Headers.Authorization.ToString();
        if (!token.StartsWith("Bearer ", StringComparison.Ordinal) || token.Length != 71) throw new Rejection(401, "AUTH_REQUIRED");
        var hash = Crypto.HashText(token[7..]); var now = Crypto.Now;
        var account = await (from t in db.Tokens join u in db.Accounts on t.UserId equals u.Id where t.Id == hash && t.ExpiresAt > now && u.Active && u.EmailVerified select u).SingleOrDefaultAsync(ctx.RequestAborted) ?? throw new Rejection(401, "AUTH_REQUIRED");
        ctx.Items["user"] = account;
        if (ctx.Request.Path.StartsWithSegments("/api/staff") && account.Role != "staff") throw new Rejection(403, "ACCESS_DENIED");
        if (!ctx.Request.Path.StartsWithSegments("/api/staff") && account.Role != "parent" && ctx.Request.Method != "GET" && ctx.Request.Path != "/api/logout") throw new Rejection(403, "ACCESS_DENIED");
    }
    await next(ctx);
});

app.MapPost("/auth/register", async (HttpContext ctx, SchoolDb db, IPasswordHasher<Account> hasher, AccountMail mail, CancellationToken ct) =>
{
    var input = HttpPipeline.Read<RegisterInput>(ctx); var email = ValidEmail(input.Email);
    await db.Limit("account:" + email, 5, 3600, ct);
    var user = await db.Accounts.SingleOrDefaultAsync(x => x.Email == email, ct);
    if (user is null)
    {
        user = new Account { Email = email, Name = "Unverified" }; user.PasswordHash = hasher.HashPassword(user, Crypto.Random());
        db.Accounts.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DatabaseSetup.IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); user = await db.Accounts.SingleOrDefaultAsync(x => x.Email == email, ct); if (user is null) throw; }
    }
    if (!user.EmailVerified && user.Active) await SendAction(user, "verify", db, mail, ct);
    return Results.Accepted(value: new { message = "If registration is available, check your email to verify your account." });
});
app.MapPost("/auth/login", async (HttpContext ctx, SchoolDb db, IPasswordHasher<Account> hasher, CancellationToken ct) =>
{
    var input = HttpPipeline.Read<LoginInput>(ctx); var email = ValidEmail(input.Email);
    if (string.IsNullOrEmpty(input.Password) || input.Password.Length > 128) throw new Rejection(400, "VALIDATION_FAILED");
    await db.Limit("login:" + email, 10, 900, ct);
    var user = await db.Accounts.SingleOrDefaultAsync(x => x.Email == email, ct);
    var result = hasher.VerifyHashedPassword(user ?? dummy, user?.PasswordHash ?? dummyHash, input.Password);
    if (result == PasswordVerificationResult.Failed || user is null || !user.Active || !user.EmailVerified) throw new Rejection(401, "INVALID_CREDENTIALS");
    if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, input.Password);
    var token = Crypto.Random(); var expiry = Crypto.Now + 28_800_000;
    db.Tokens.Add(new() { Id = Crypto.HashText(token), UserId = user.Id, ExpiresAt = expiry });
    db.AuditEvents.Add(new() { ActorId = user.Id, Action = "login.success", TraceId = ctx.TraceIdentifier });
    await db.SaveChangesAsync(ct); return Results.Ok(new AuthDto(token, expiry, ToUser(user)));
});
app.MapPost("/auth/verify", async (HttpContext ctx, SchoolDb db, IPasswordHasher<Account> hasher, CancellationToken ct) =>
{
    var input = HttpPipeline.Read<VerifyInput>(ctx); ValidPassword(input.Password);
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80) throw new Rejection(400,"VALIDATION_FAILED");
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var action = await Consume(input.Token, "verify", db, ct);
    var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == action.UserId,ct);
    var passwordHash = hasher.HashPassword(account,input.Password);
    var chosenName = input.Name.Trim();
    var changed = await db.Accounts.Where(x => x.Id == action.UserId && x.Active && !x.EmailVerified).ExecuteUpdateAsync(s => s.SetProperty(x => x.EmailVerified, true).SetProperty(x => x.PasswordHash,passwordHash).SetProperty(x => x.Name,chosenName), ct);
    if (changed != 1) throw new Rejection(400,"LINK_INVALID");
    await db.AccountActions.Where(x => x.UserId == action.UserId && x.Purpose == "verify").ExecuteDeleteAsync(ct);
    await tx.CommitAsync(ct); return Results.Ok(new { message = "Email verified. You can now sign in." });
});
app.MapPost("/auth/forgot", async (HttpContext ctx, SchoolDb db, AccountMail mail, CancellationToken ct) =>
{
    var email = ValidEmail(HttpPipeline.Read<EmailInput>(ctx).Email); await db.Limit("reset:" + email, 3, 3600, ct);
    var user = await db.Accounts.SingleOrDefaultAsync(x => x.Email == email && x.Active && x.EmailVerified, ct);
    if (user is not null) await SendAction(user, "reset", db, mail, ct);
    return Results.Accepted(value: new { message = "If this account is eligible, a reset link has been sent." });
});
app.MapPost("/auth/reset", async (HttpContext ctx, SchoolDb db, IPasswordHasher<Account> hasher, CancellationToken ct) =>
{
    var input = HttpPipeline.Read<ResetInput>(ctx); ValidPassword(input.Password);
    await using var tx = await db.Database.BeginTransactionAsync(ct); var action = await Consume(input.Token, "reset", db, ct);
    var user = await db.Accounts.SingleAsync(x => x.Id == action.UserId && x.Active, ct); user.PasswordHash = hasher.HashPassword(user, input.Password);
    await db.Tokens.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct); await db.Sessions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
    await db.AccountActions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
    db.AuditEvents.Add(new() { ActorId = user.Id, Action = "password.reset", TraceId = ctx.TraceIdentifier });
    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(new { message = "Password updated. Sign in again." });
});
app.MapGet("/api/me", (HttpContext ctx) => Results.Ok(ToUser(User(ctx))));
app.MapPost("/api/logout", async (HttpContext ctx, SchoolDb db, CancellationToken ct) =>
{
    var hash = Crypto.HashText(ctx.Request.Headers.Authorization.ToString()[7..]); await db.Tokens.Where(x => x.Id == hash).ExecuteDeleteAsync(ct); return Results.NoContent();
});
app.MapGet("/api/schools", () => Results.Ok(HttpPipeline.Schools));
app.MapGet("/api/students", async (HttpContext ctx, SchoolDb db, CancellationToken ct) =>
{ var owner = User(ctx).Id; return Results.Ok(await db.Students.Where(x => x.OwnerId == owner).OrderBy(x => x.Name).Select(x => new StudentDto(x.Id,x.Name,x.Grade)).Take(100).ToListAsync(ct)); });
app.MapPost("/api/students", async (HttpContext ctx, SchoolDb db, CancellationToken ct) =>
{
    var input = HttpPipeline.Read<StudentInput>(ctx);
    if (!Guid.TryParseExact(input.Id,"D",out _) || string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80 || input.Grade is < 1 or > 12) throw new Rejection(400,"VALIDATION_FAILED");
    var owner = User(ctx).Id;
    if (await db.Students.CountAsync(x => x.OwnerId == owner, ct) >= 20) throw new Rejection(409,"STUDENT_LIMIT");
    var student = new Student { Id = input.Id, OwnerId = owner, Name = input.Name.Trim(), Grade = input.Grade, BalanceMinor = 0 };
    db.Students.Add(student);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (DatabaseSetup.IsUniqueViolation(ex))
    {
        db.ChangeTracker.Clear(); var existing = await db.Students.SingleOrDefaultAsync(x => x.Id == input.Id && x.OwnerId == owner, ct);
        if (existing is null || existing.Name != student.Name || existing.Grade != student.Grade) throw new Rejection(409,"IDEMPOTENCY_CONFLICT");
    }
    return Results.Ok(new StudentDto(student.Id, student.Name, student.Grade));
});
app.MapGet("/api/students/{id:guid}/balance", async (string id, HttpContext ctx, SchoolDb db, CancellationToken ct) =>
{ var owner = User(ctx).Id; return Results.Ok(await db.Students.Where(x => x.Id == id && x.OwnerId == owner).Select(x => new BalanceDto(x.Id,x.BalanceMinor,"USD")).SingleOrDefaultAsync(ct) ?? throw new Rejection(404,"RECORD_NOT_FOUND")); });
app.MapGet("/api/applications", async (HttpContext ctx, SchoolDb db, CancellationToken ct) =>
{ var owner = User(ctx).Id; return Results.Ok(await db.Applications.Where(x => x.UserId == owner).OrderByDescending(x => x.SubmittedAt).Select(x => new ApplicationDto(x.Id,x.StudentId,x.SchoolId,x.AcademicYear,x.Status,x.SubmittedAt)).Take(100).ToListAsync(ct)); });
app.MapPost("/api/applications", async (HttpContext ctx, SchoolDb db, CancellationToken ct) =>
{
    var input = HttpPipeline.Read<ApplicationInput>(ctx); var owner = User(ctx).Id;
    if (!Guid.TryParseExact(input.SubmissionId,"D",out _) || !HttpPipeline.Schools.Any(x => x.Id == input.SchoolId) || input.AcademicYear != "2026-2027") throw new Rejection(400,"VALIDATION_FAILED");
    if (!await db.Students.AnyAsync(x => x.Id == input.StudentId && x.OwnerId == owner, ct)) throw new Rejection(404,"RECORD_NOT_FOUND");
    var hash = Crypto.Hash((byte[])ctx.Items["body"]!);
    var application = new SchoolApplication { Id = input.SubmissionId, UserId = owner, StudentId = input.StudentId, SchoolId = input.SchoolId, AcademicYear = input.AcademicYear, Fingerprint = hash, SubmittedAt = Crypto.Now };
    db.Applications.Add(application);
    db.AuditEvents.Add(new() { ActorId = owner, Action = "application.submit", RecordId = application.Id, TraceId = ctx.TraceIdentifier });
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (DatabaseSetup.IsUniqueViolation(ex))
    {
        db.ChangeTracker.Clear(); var existing = await db.Applications.SingleOrDefaultAsync(x => x.UserId == owner && x.Id == input.SubmissionId, ct);
        if (existing is null) throw new Rejection(409,"APPLICATION_ALREADY_EXISTS");
        if (existing.Fingerprint != hash) throw new Rejection(409,"IDEMPOTENCY_CONFLICT"); application = existing;
    }
    return Results.Ok(new ApplicationDto(application.Id,application.StudentId,application.SchoolId,application.AcademicYear,application.Status,application.SubmittedAt));
});
app.MapGet("/api/staff/applications", async (SchoolDb db, CancellationToken ct) => Results.Ok(await (from a in db.Applications join s in db.Students on a.StudentId equals s.Id orderby a.SubmittedAt descending select new StaffApplicationDto(a.Id,a.UserId,s.Name,a.SchoolId,a.AcademicYear,a.Status,a.SubmittedAt,a.Version)).Take(100).ToListAsync(ct)));
app.MapPost("/api/staff/applications/{userId:guid}/{id:guid}/decision", async (string userId, string id, HttpContext ctx, SchoolDb db, CancellationToken ct) =>
{
    var input = HttpPipeline.Read<DecisionInput>(ctx);
    if (input.Status is not ("Approved" or "Declined") || input.Version < 1) throw new Rejection(400,"VALIDATION_FAILED");
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var changed = await db.Applications.Where(x => x.Id == id && x.UserId == userId && x.Status == "Submitted" && x.Version == input.Version).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status,input.Status).SetProperty(x => x.Version,x => x.Version + 1),ct);
    if (changed != 1) throw new Rejection(409,"VERSION_CONFLICT");
    db.AuditEvents.Add(new() { ActorId = User(ctx).Id, Action = "application." + input.Status.ToLowerInvariant(), RecordId = id, TraceId = ctx.TraceIdentifier });
    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(new { message = "Decision saved." });
});
await app.RunAsync();

static Account User(HttpContext ctx) => (Account)ctx.Items["user"]!;
static UserDto ToUser(Account user) => new(user.Id,user.Name,user.Email,user.Role);
static string ValidEmail(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || !MailAddress.TryCreate(value, out var address) || address.Address != value.Trim() || !value.Contains('@')) throw new Rejection(400,"VALIDATION_FAILED");
    return value.Trim().ToLowerInvariant();
}
static void ValidPassword(string? value)
{
    if (value is null || value.Length is < 15 or > 128) throw new Rejection(400,"PASSWORD_POLICY");
}
static async Task SendAction(Account user, string purpose, SchoolDb db, AccountMail mail, CancellationToken ct)
{
    var token = Crypto.Random(); db.AccountActions.Add(new() { Id = Crypto.HashText(token), UserId = user.Id, Purpose = purpose, ExpiresAt = Crypto.Now + 1_800_000 });
    await db.SaveChangesAsync(ct); await mail.Send(user.Email,purpose,token,ct);
}
static async Task<AccountAction> Consume(string token, string purpose, SchoolDb db, CancellationToken ct)
{
    if (token is null || !Regex.IsMatch(token,"^[a-f0-9]{64}$")) throw new Rejection(400,"LINK_INVALID");
    var id = Crypto.HashText(token); var now = Crypto.Now;
    var action = await db.AccountActions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Purpose == purpose && x.ExpiresAt > now,ct) ?? throw new Rejection(400,"LINK_INVALID");
    if (await db.AccountActions.Where(x => x.Id == id && x.ExpiresAt > now).ExecuteDeleteAsync(ct) != 1) throw new Rejection(400,"LINK_INVALID");
    return action;
}
