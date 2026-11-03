using Microsoft.EntityFrameworkCore;

namespace EventPass.Api.Data;

public static class DatabaseInitializer
{
    /// <summary>
    /// Crée le schéma s'il n'existe pas puis insère les données initiales.
    /// Avec PostgreSQL, attend jusqu'à ~60 s que la base soit joignable (utile si elle démarre en même temps que l'API).
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var settings = provider.GetRequiredService<DatabaseSettings>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("EventPass.Database");
        var db = provider.GetRequiredService<AppDbContext>();

        logger.LogInformation("Base de données : {Provider} ({Location}), persistante : {Persistent}",
            settings.Provider, settings.Location, settings.Persistent ? "oui" : "non");
        if (settings.Warning is not null) logger.LogWarning("{Warning}", settings.Warning);

        var maxAttempts = settings.IsPostgres ? 30 : 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync(cancellationToken);
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Base injoignable (tentative {Attempt}/{Max}) : {Message} — nouvel essai dans 2 s",
                    attempt, maxAttempts, ex.GetBaseException().Message);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        await DemoData.SeedAsync(db, provider.GetRequiredService<IConfiguration>(), logger);
    }
}
