using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using School.Shared;

namespace School.Api;

// Migration scaffolding requires no running service or secret-store connection.
public sealed class DesignTimeDatabase : IDesignTimeDbContextFactory<SchoolDb>
{
    public SchoolDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SchoolDb>();
        if (Environment.GetEnvironmentVariable("Database__Provider") == "Postgres")
            options.UseNpgsql("Host=localhost;Database=school;Username=schema_only",x => x.MigrationsAssembly("School.Migrations.Postgres"));
        else options.UseSqlite("Data Source=schema-only.db",x => x.MigrationsAssembly("School.Migrations.Sqlite"));
        return new SchoolDb(options.Options);
    }
}
