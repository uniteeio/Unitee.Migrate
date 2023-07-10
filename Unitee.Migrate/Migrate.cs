using Microsoft.Data.SqlClient;
using System.Numerics;
using CSharpFunctionalExtensions;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Unitee.Migrate;

public static class Migrate
{
    public static readonly string TableName = "schema_migrations";

    private static async Task<Result> CreateMigrationTableIfNotExist(string connectionString)
    {
        return await Result.Try(async () =>
        {
            using var conn = new SqlConnection(connectionString);
            await conn.ExecuteAsync($@"
                IF (OBJECT_ID('{TableName}') IS NULL )
                BEGIN
                    SET ANSI_NULLS ON
                    SET QUOTED_IDENTIFIER ON
                    CREATE TABLE [dbo].[{TableName}](
                        [version] [bigint] NOT NULL,
                        [dirty] [bit] NOT NULL
                    ) ON [PRIMARY]
                    ALTER TABLE [dbo].[{TableName}] ADD PRIMARY KEY CLUSTERED
                    (
                        [version] ASC
                    )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
                END");

                await conn.ExecuteAsync($@"
                    IF NOT EXISTS (SELECT * FROM {TableName})
                    BEGIN
                        INSERT INTO [{TableName}] (version, dirty) VALUES (0, 0)
                    END
                ");
            });

    }

    private static bool IsNotDirty(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        var dirty = conn.QueryFirstOrDefault<bool>($"SELECT dirty FROM {TableName}");
        return !dirty;
    }

    public static async Task<Result> MigrateAsync<T>(string connectionString, string migrationPath, ILogger<T>? logger = null)
    {
        var maybeLogger = logger.AsMaybe();

        return await CreateMigrationTableIfNotExist(connectionString)
            .BindTry(() =>
            {
                return GetCurrentMigrationVersion(connectionString).Map(currentMigration =>
                {
                    var migrations =
                        Directory.GetFiles(migrationPath, "*.up.sql")
                            .Select(path => new
                            {
                                Filename = Path.GetFileName(path),
                                MigrationVersion = BigInteger.Parse(Path.GetFileName(path)[0..14]),
                                Path = path
                            })
                            .OrderBy(x => x.MigrationVersion)
                            .Where(x => x.MigrationVersion > currentMigration)
                            .ToList();
                    return migrations;
                });
            })
            .Ensure(x => IsNotDirty(connectionString), "Database is in a dirty state, cannot execute migrations")
            .Tap(migrations =>
            {
                maybeLogger.Execute(log =>
                {
                    log.LogInformation("Found {count} migrations to execute: ", migrations.Count);

                    foreach (var migration in migrations)
                    {
                        log.LogInformation("  ==> {filename}", migration.Filename);
                    }
                });
            })
            .BindTry(async migrations =>
            {
                using var conn = new SqlConnection(connectionString);

                foreach (var migration in migrations)
                {
                    var sqlraw = File.ReadAllText(migration.Path);

                    if (string.IsNullOrEmpty(sqlraw) || string.IsNullOrWhiteSpace(sqlraw))
                    {
                        maybeLogger.Execute(log => log.LogWarning("Skipping empty migration file {filename}", migration.Filename));
                        continue;
                    }

                    try
                    {
                        await conn.ExecuteAsync(sqlraw);

                        conn.Execute("UPDATE schema_migrations SET version = @version, dirty = 0", new { version = $"{migration.MigrationVersion}" });
                    } catch (SqlException e)
                    {
                        conn.Execute("UPDATE schema_migrations SET version = @version, dirty = 1", new { version = $"{migration.MigrationVersion}" });
                        return Result.Failure(e.Message);
                    }
                }

                return Result.Success();
            });
    }

    private static Result<BigInteger> GetCurrentMigrationVersion(string connectionString)
    {
        return Result.Try(() =>
        {
            using var conn = new SqlConnection(connectionString);
            var version = conn.QueryFirstOrDefault<string>($"SELECT version FROM {TableName}");
            return BigInteger.Parse(version);
        });
    }
}

