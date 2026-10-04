using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace School.Shared;

public class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Active { get; set; } = true;
    public bool EmailVerified { get; set; }
    public string Role { get; set; } = "parent";
}
public class AccessToken
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public long ExpiresAt { get; set; }
}
public class BrowserSession
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string EncryptedPayload { get; set; } = "";
    public long ExpiresAt { get; set; }
    public long LastSeen { get; set; }
}
public class Student
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Grade { get; set; }
    public long BalanceMinor { get; set; }
}
public class SchoolApplication
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string SchoolId { get; set; } = "";
    public string AcademicYear { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "Submitted";
    public long SubmittedAt { get; set; }
    public int Version { get; set; } = 1;
}
public class AccountAction
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public long ExpiresAt { get; set; }
}
public class AuditEvent
{
    public long Id { get; set; }
    public string ActorId { get; set; } = "";
    public string Action { get; set; } = "";
    public string RecordId { get; set; } = "";
    public string TraceId { get; set; } = "";
    public long At { get; set; } = Crypto.Now;
}
public class ReplayClaim
{
    public string Id { get; set; } = "";
    public long ExpiresAt { get; set; }
}
public class RateBucket
{
    public string Id { get; set; } = "";
    public long Count { get; set; }
    public long ExpiresAt { get; set; }
}
public class SchoolDb(DbContextOptions<SchoolDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccessToken> Tokens => Set<AccessToken>();
    public DbSet<BrowserSession> Sessions => Set<BrowserSession>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<SchoolApplication> Applications => Set<SchoolApplication>();
    public DbSet<ReplayClaim> Replays => Set<ReplayClaim>();
    public DbSet<RateBucket> Rates => Set<RateBucket>();
    public DbSet<AccountAction> AccountActions => Set<AccountAction>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>().HasIndex(x => x.Email).IsUnique();
        b.Entity<SchoolApplication>().HasKey(x => new { x.UserId, x.Id });
        b.Entity<SchoolApplication>().HasIndex(x => new { x.UserId, x.StudentId, x.SchoolId, x.AcademicYear }).IsUnique();
        b.Entity<AccessToken>().HasOne<Account>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<BrowserSession>().HasOne<Account>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Student>().HasOne<Account>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SchoolApplication>().HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ReplayClaim>().HasIndex(x => x.ExpiresAt);
        b.Entity<RateBucket>().HasIndex(x => x.ExpiresAt);
        b.Entity<BrowserSession>().HasIndex(x => x.ExpiresAt);
        b.Entity<AccessToken>().HasIndex(x => x.ExpiresAt);
        b.Entity<AccountAction>().HasIndex(x => x.ExpiresAt);
    }
    public async Task Claim(string scope, string requestId, CancellationToken ct)
    {
        var id = Crypto.HashText(scope + ":" + requestId);
        var expiry = Crypto.Now + 600_000;
        var count = await Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Replays\" (\"Id\", \"ExpiresAt\") VALUES ({id}, {expiry}) ON CONFLICT (\"Id\") DO NOTHING", ct);
        if (count != 1) throw new Rejection(403, "REPLAY_DETECTED");
    }
    public async Task Limit(string scope, int maximum, int seconds, CancellationToken ct)
    {
        var now = Crypto.Now;
        var window = now / (seconds * 1000L);
        var id = Crypto.HashText(scope + ":" + window);
        var expiry = (window + 2) * seconds * 1000L;
        await Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Database.GetDbConnection().CreateCommand();
            cmd.CommandTimeout = 3;
            cmd.CommandText = "INSERT INTO \"Rates\" (\"Id\", \"Count\", \"ExpiresAt\") VALUES (@id,1,@expires) ON CONFLICT (\"Id\") DO UPDATE SET \"Count\" = \"Rates\".\"Count\" + 1 RETURNING \"Count\"";
            var p = cmd.CreateParameter(); p.ParameterName = "id"; p.Value = id; cmd.Parameters.Add(p);
            p = cmd.CreateParameter(); p.ParameterName = "expires"; p.Value = expiry; cmd.Parameters.Add(p);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > maximum) throw new Rejection(429, "RATE_LIMITED");
        }
        finally { await Database.CloseConnectionAsync(); }
    }
}
public static class DatabaseSetup
{
    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } or Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };
    public static void AddSchoolDatabase(this IServiceCollection services, IConfiguration config)
    {
        var connection = config["Database:Connection"] ?? throw new InvalidOperationException("Database connection required.");
        services.AddDbContext<SchoolDb>(o =>
        {
            if (config["Database:Provider"] == "Postgres") o.UseNpgsql(connection, x => x.CommandTimeout(3).MigrationsAssembly("School.Migrations.Postgres"));
            else if (config["Database:Provider"] == "Sqlite") o.UseSqlite(connection, x => x.CommandTimeout(3).MigrationsAssembly("School.Migrations.Sqlite"));
            else throw new InvalidOperationException("Database provider must be Sqlite or Postgres.");
        });
        services.AddHostedService<StateCleanup>();
    }
}
public sealed class StateCleanup(IServiceScopeFactory scopes, Microsoft.Extensions.Logging.ILogger<StateCleanup> log) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SchoolDb>();
                var now = Crypto.Now;
                var r = await db.Replays.Where(x => x.ExpiresAt < now).Take(1000).ToListAsync(stop); db.Replays.RemoveRange(r);
                var b = await db.Rates.Where(x => x.ExpiresAt < now).Take(1000).ToListAsync(stop); db.Rates.RemoveRange(b);
                var s = await db.Sessions.Where(x => x.ExpiresAt < now || x.LastSeen < now - 1_800_000).Take(1000).ToListAsync(stop); db.Sessions.RemoveRange(s);
                var t = await db.Tokens.Where(x => x.ExpiresAt < now).Take(1000).ToListAsync(stop); db.Tokens.RemoveRange(t);
                var a = await db.AccountActions.Where(x => x.ExpiresAt < now).Take(1000).ToListAsync(stop); db.AccountActions.RemoveRange(a);
                await db.SaveChangesAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception) { Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(log, "State cleanup unavailable"); }
        }
    }
}
