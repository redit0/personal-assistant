using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddAssistantData(this IServiceCollection services, string databaseFile) =>
        services.AddDbContext<AssistantDbContext>(o => o.UseSqlite($"Data Source={databaseFile}"));

    /// <summary>Uses an already-open connection. Tests pass an in-memory SQLite connection here.</summary>
    public static IServiceCollection AddAssistantData(this IServiceCollection services, DbConnection connection) =>
        services.AddDbContext<AssistantDbContext>(o => o.UseSqlite(connection));

    public static async Task MigrateAssistantDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Lets <c>dotnet ef</c> create the context without starting the app.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AssistantDbContext>
{
    public AssistantDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AssistantDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
