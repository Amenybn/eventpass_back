using System.Diagnostics;
using System.Runtime.InteropServices;
using EventPass.Api.Auth;
using EventPass.Api.Common;
using EventPass.Api.Data;
using EventPass.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPass.Api.Features;

/// <summary>Supervision : sondes de santé et informations affichées dans l'écran « Système » du front.</summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness : le processus répond (l'état de la base est indiqué sans faire échouer la sonde)
        app.MapGet("/health", Health).WithTags("Système");
        app.MapGet("/api/health", Health).WithTags("Système");
        // Readiness : 503 tant que la base est injoignable
        app.MapGet("/health/ready", Ready).WithTags("Système");
        app.MapGet("/api/system/info", Info).WithTags("Système");
    }

    private static async Task<IResult> Health(AppDbContext db) =>
        Results.Ok(new { status = "UP", database = await CanConnect(db) ? "UP" : "DOWN" });

    private static async Task<IResult> Ready(AppDbContext db) => await CanConnect(db)
        ? Results.Ok(new { status = "UP", database = "UP" })
        : Results.Json(new { status = "DOWN", database = "DOWN" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> Info(AppDbContext db, DatabaseSettings settings, TokenService tokens, IHostEnvironment env)
    {
        var watch = Stopwatch.StartNew();
        var up = await CanConnect(db);
        var latencyMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);

        object? counts = null;
        if (up)
        {
            counts = new
            {
                users = await db.Users.CountAsync(),
                events = await db.Events.CountAsync(),
                bookings = await db.Bookings.CountAsync(b => b.Status == BookingStatus.Confirmed),
                tickets = await db.Tickets.CountAsync(t => t.Status != TicketStatus.Cancelled),
            };
        }

        return Results.Ok(new
        {
            application = "EventPass API",
            version = AppInfo.Version,
            environment = env.EnvironmentName,
            hostname = Environment.MachineName,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            startedAt = AppInfo.StartedAt,
            uptimeSeconds = (long)(DateTime.UtcNow - AppInfo.StartedAt).TotalSeconds,
            database = new
            {
                provider = settings.Provider,
                location = settings.Location,
                persistent = settings.Persistent,
                warning = settings.Warning,
                status = up ? "UP" : "DOWN",
                latencyMs,
            },
            security = new { jwtSecretConfigured = !tokens.UsingGeneratedSecret },
            counts,
        });
    }

    private static async Task<bool> CanConnect(AppDbContext db)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await db.Database.CanConnectAsync(timeout.Token);
        }
        catch
        {
            return false;
        }
    }
}
