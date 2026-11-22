using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Xrmd.Storage.PostgreSql;

/// <summary>
/// Entity Framework Core on a PostgreSQL organization (plan 2.7b, Task 8): the local identity provider's store
/// (<c>AuthDbContext</c>, the <c>auth</c> schema of the baseline), which the host configures with <see cref="UseXrmdPostgres"/>
/// where it would use SQL Server's provider.
/// </summary>
public static class PostgresEntityFramework
{
    /// <summary>The column type of every text column of an EF model on PostgreSQL, as the baseline creates them.</summary>
    public const string TextColumnType = "citext";

    /// <summary>
    /// Npgsql's provider on <paramref name="connectionString"/>, with the sessions of <see cref="NpgsqlConnectionFactory"/> (UTC,
    /// <c>standard_conforming_strings</c>) and every <see cref="string"/> property mapped to <c>citext</c>
    /// (<see cref="CitextModelCustomizer"/>).
    /// </summary>
    public static DbContextOptionsBuilder UseXrmdPostgres(this DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        PostgresStorage.Register(); // the dialect of the context's NpgsqlConnection (DataSession.Dialect)
        options.UseNpgsql(NpgsqlConnectionFactory.Normalize(connectionString));
        options.ReplaceService<IModelCustomizer, CitextModelCustomizer>();
        return options;
    }

    /// <summary>
    /// Maps every <see cref="string"/> property to <see cref="TextColumnType"/>, the type of the baseline's text columns: a
    /// parameter compared with such a column is then a <c>citext</c> one, which compares like the column (and uses its index),
    /// where a <c>text</c> parameter would turn the comparison into a case-sensitive one on <c>text</c>.
    /// </summary>
    public sealed class CitextModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
                if (property.ClrType == typeof(string)) property.SetColumnType(TextColumnType);
        }
    }
}
