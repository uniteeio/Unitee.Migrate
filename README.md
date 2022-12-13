# Unitee.Migrate

Play https://github.com/golang-migrate/migrate migrations in .NET Core.

Use the [Result<UnitResult, String>](https://github.com/vkhorikov/CSharpFunctionalExtensions) instead of throwing an exception.

```cs
await Migrate.MigrateAsync(connectionString, "./migration-directory");
```

If you need some log, you can pass an instance of ILogger<T>:

```cs
Maybe<ILogger<Program>> maybeLogger = app.Services.GetRequiredService<ILogger<Program>>() is null 
  ? Maybe<ILogger<Program>>.None 
  : Maybe<ILogger<Program>>.From(app.Services.GetRequiredService<ILogger<Program>>());

await Migrate.MigrateAsync(connectionString, "../db", maybeLogger.GetValueOrDefault(null))
    .TapError(e => logger.Execute(l => l.LogError("{error}", e)));
```
