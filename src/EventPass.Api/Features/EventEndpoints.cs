using System.Security.Claims;
using EventPass.Api.Auth;
using EventPass.Api.Common;
using EventPass.Api.Data;
using EventPass.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPass.Api.Features;

public record TicketTypeDto(int Id, string Name, decimal Price, int Capacity, int Sold, int Remaining);

public record EventSummaryDto(
    int Id, string Title, string Category, string Venue, string City, DateTime StartsAt, DateTime EndsAt,
    string CoverEmoji, string CoverColor, EventStatus Status, decimal PriceFrom, int Capacity, int Sold, int Remaining, bool IsPast);

public record EventDetailDto(
    int Id, string Title, string Description, string Category, string Venue, string City, DateTime StartsAt, DateTime EndsAt,
    string CoverEmoji, string CoverColor, EventStatus Status, decimal PriceFrom, int Capacity, int Sold, int Remaining, bool IsPast,
    List<TicketTypeDto> TicketTypes);

public static class EventMapping
{
    public static EventSummaryDto ToSummary(this Event e, DateTime now)
    {
        var (capacity, sold) = Gauge(e);
        return new(e.Id, e.Title, e.Category, e.Venue, e.City, e.StartsAt, e.EndsAt, e.CoverEmoji, e.CoverColor, e.Status,
            PriceFrom(e), capacity, sold, capacity - sold, e.StartsAt <= now);
    }

    public static EventDetailDto ToDetail(this Event e, DateTime now)
    {
        var (capacity, sold) = Gauge(e);
        var types = e.TicketTypes.OrderBy(t => t.Price).ThenBy(t => t.Id)
            .Select(t => new TicketTypeDto(t.Id, t.Name, t.Price, t.Capacity, t.Sold, t.Capacity - t.Sold))
            .ToList();
        return new(e.Id, e.Title, e.Description, e.Category, e.Venue, e.City, e.StartsAt, e.EndsAt, e.CoverEmoji, e.CoverColor,
            e.Status, PriceFrom(e), capacity, sold, capacity - sold, e.StartsAt <= now, types);
    }

    private static (int Capacity, int Sold) Gauge(Event e) => (e.TicketTypes.Sum(t => t.Capacity), e.TicketTypes.Sum(t => t.Sold));

    private static decimal PriceFrom(Event e) => e.TicketTypes.Count == 0 ? 0 : e.TicketTypes.Min(t => t.Price);
}

public static class EventEndpoints
{
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events").WithTags("Événements");
        group.MapGet("/", List);
        group.MapGet("/filters", Filters);
        group.MapGet("/{id:int}", Get);
    }

    /// <summary>Événements publiés. period = upcoming (défaut), past ou all.</summary>
    private static async Task<IResult> List(AppDbContext db, string? q, string? category, string? city, string? period)
    {
        var now = DateTime.UtcNow;
        var events = await db.Events.AsNoTracking()
            .Include(e => e.TicketTypes)
            .Where(e => e.Status == EventStatus.Published)
            .ToListAsync();

        IEnumerable<Event> filtered = events
            .Where(e => TextSearch.Matches(q, e.Title, e.Description, e.Venue, e.City, e.Category))
            .Where(e => string.IsNullOrWhiteSpace(category) || TextSearch.Fold(e.Category) == TextSearch.Fold(category))
            .Where(e => string.IsNullOrWhiteSpace(city) || TextSearch.Fold(e.City) == TextSearch.Fold(city));

        filtered = (period ?? "upcoming").ToLowerInvariant() switch
        {
            "past" => filtered.Where(e => e.StartsAt <= now).OrderByDescending(e => e.StartsAt),
            "all" => filtered.OrderBy(e => e.StartsAt),
            _ => filtered.Where(e => e.StartsAt > now).OrderBy(e => e.StartsAt),
        };

        return Results.Ok(filtered.Select(e => e.ToSummary(now)));
    }

    private static async Task<IResult> Filters(AppDbContext db)
    {
        var published = await db.Events.AsNoTracking()
            .Where(e => e.Status == EventStatus.Published)
            .Select(e => new { e.Category, e.City })
            .ToListAsync();
        return Results.Ok(new
        {
            categories = published.Select(e => e.Category).Distinct().Order(),
            cities = published.Select(e => e.City).Distinct().Order(),
        });
    }

    /// <summary>Un événement publié ; l'organisateur voit aussi les brouillons et les annulés.</summary>
    private static async Task<IResult> Get(int id, AppDbContext db, ClaimsPrincipal principal)
    {
        var e = await db.Events.AsNoTracking().Include(x => x.TicketTypes).FirstOrDefaultAsync(x => x.Id == id);
        if (e is null || (e.Status == EventStatus.Draft && !principal.IsAdmin())) return ApiError.NotFound("Événement introuvable.");
        return Results.Ok(e.ToDetail(DateTime.UtcNow));
    }
}
