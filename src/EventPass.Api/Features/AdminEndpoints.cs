using EventPass.Api.Auth;
using EventPass.Api.Common;
using EventPass.Api.Data;
using EventPass.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPass.Api.Features;

public record TicketTypeInput(int? Id, string? Name, decimal Price, int Capacity);

public record EventInput(
    string? Title, string? Description, string? Category, string? Venue, string? City,
    DateTime? StartsAt, DateTime? EndsAt, string? CoverEmoji, string? CoverColor, EventStatus? Status,
    List<TicketTypeInput>? TicketTypes);

public record AdminEventDto(EventSummaryDto Event, int Bookings, decimal Revenue, int CheckedIn);

public record CheckInRequest(string? Code, int? EventId);
public record CheckInTicketDto(string Code, string HolderName, string TicketType, string EventTitle, int EventId, DateTime? CheckedInAt);
public record CheckInResult(string Result, bool Accepted, string Message, CheckInTicketDto? Ticket);

/// <summary>Espace organisateur : gestion des événements, statistiques, contrôle d'accès.</summary>
public static class AdminEndpoints
{
    public static readonly string[] CoverColors = ["indigo", "violet", "rose", "amber", "emerald", "sky", "slate", "orange"];

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Organisateur").RequireAuthorization(Policies.Admin);
        group.MapGet("/events", ListEvents);
        group.MapPost("/events", CreateEvent);
        group.MapPut("/events/{id:int}", UpdateEvent);
        group.MapDelete("/events/{id:int}", DeleteEvent);
        group.MapGet("/stats", Stats);
        group.MapPost("/checkin", CheckIn);
        group.MapGet("/checkins", RecentCheckIns);
    }

    // ---------- Événements ----------

    private static async Task<IResult> ListEvents(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var events = await db.Events.AsNoTracking().Include(e => e.TicketTypes).ToListAsync();
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.Status != TicketStatus.Cancelled)
            .Select(t => new { t.Booking.EventId, t.BookingId, t.Price, t.Status })
            .ToListAsync();
        var byEvent = tickets.ToLookup(t => t.EventId);

        // À venir d'abord (le plus proche en premier), puis les passés (le plus récent en premier)
        var ordered = events.Where(e => e.StartsAt > now).OrderBy(e => e.StartsAt)
            .Concat(events.Where(e => e.StartsAt <= now).OrderByDescending(e => e.StartsAt));

        return Results.Ok(ordered.Select(e =>
        {
            var sold = byEvent[e.Id].ToList();
            return new AdminEventDto(
                e.ToSummary(now),
                sold.Select(t => t.BookingId).Distinct().Count(),
                sold.Sum(t => t.Price),
                sold.Count(t => t.Status == TicketStatus.Used));
        }));
    }

    private static async Task<IResult> CreateEvent(EventInput input, AppDbContext db)
    {
        if (Validate(input) is { } error) return ApiError.BadRequest(error);

        var e = new Event();
        Apply(e, input);
        e.TicketTypes = input.TicketTypes!
            .Select(t => new TicketType { Name = t.Name!.Trim(), Price = t.Price, Capacity = t.Capacity })
            .ToList();
        db.Events.Add(e);
        await db.SaveChangesAsync();

        return Results.Created($"/api/events/{e.Id}", e.ToDetail(DateTime.UtcNow));
    }

    private static async Task<IResult> UpdateEvent(int id, EventInput input, AppDbContext db)
    {
        if (Validate(input) is { } error) return ApiError.BadRequest(error);

        var e = await db.Events.Include(x => x.TicketTypes).FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return ApiError.NotFound("Événement introuvable.");

        var inputs = input.TicketTypes!;
        foreach (var existing in e.TicketTypes.ToList())
        {
            var match = inputs.FirstOrDefault(t => t.Id == existing.Id);
            if (match is null)
            {
                if (existing.Sold > 0)
                    return ApiError.Conflict($"Impossible de supprimer la catégorie « {existing.Name} » : {existing.Sold} billet(s) déjà vendu(s).");
                e.TicketTypes.Remove(existing);
                continue;
            }
            if (match.Capacity < existing.Sold)
                return ApiError.Conflict($"La capacité de « {existing.Name} » ne peut pas être inférieure aux billets vendus ({existing.Sold}).");
            existing.Name = match.Name!.Trim();
            existing.Price = match.Price;
            existing.Capacity = match.Capacity;
        }
        foreach (var added in inputs.Where(t => t.Id is null || e.TicketTypes.All(x => x.Id != t.Id)))
        {
            e.TicketTypes.Add(new TicketType { Name = added.Name!.Trim(), Price = added.Price, Capacity = added.Capacity });
        }

        Apply(e, input);
        await db.SaveChangesAsync();
        return Results.Ok(e.ToDetail(DateTime.UtcNow));
    }

    private static async Task<IResult> DeleteEvent(int id, AppDbContext db)
    {
        var e = await db.Events.FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return ApiError.NotFound("Événement introuvable.");
        if (await db.Bookings.AnyAsync(b => b.EventId == id))
            return ApiError.Conflict("Des réservations existent pour cet événement : passez-le plutôt au statut « Annulé ».");

        db.Events.Remove(e);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static string? Validate(EventInput i)
    {
        var title = i.Title?.Trim() ?? "";
        if (title.Length is < 3 or > 150) return "Le titre est obligatoire (3 à 150 caractères).";
        if (string.IsNullOrWhiteSpace(i.Category) || i.Category.Trim().Length > 80) return "La catégorie est obligatoire (80 caractères maximum).";
        if (string.IsNullOrWhiteSpace(i.Venue) || i.Venue.Trim().Length > 120) return "Le lieu est obligatoire (120 caractères maximum).";
        if (string.IsNullOrWhiteSpace(i.City) || i.City.Trim().Length > 80) return "La ville est obligatoire (80 caractères maximum).";
        if ((i.Description ?? "").Length > 4000) return "La description ne doit pas dépasser 4000 caractères.";
        if (i.StartsAt is null || i.EndsAt is null) return "Les dates de début et de fin sont obligatoires.";
        if (Clock.ToUtc(i.EndsAt.Value) <= Clock.ToUtc(i.StartsAt.Value)) return "La fin doit être postérieure au début.";
        if (i.CoverColor is not null && !CoverColors.Contains(i.CoverColor)) return "Couleur de couverture inconnue.";
        if ((i.CoverEmoji ?? "").Length > 16) return "Emoji de couverture invalide.";

        var types = i.TicketTypes ?? new();
        if (types.Count == 0) return "Ajoutez au moins une catégorie de billet.";
        if (types.Count > 6) return "6 catégories de billets maximum.";
        foreach (var t in types)
        {
            var name = t.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 60) return "Chaque catégorie de billet doit avoir un nom (60 caractères maximum).";
            if (t.Price is < 0 or > 10000) return $"Prix invalide pour « {name} » (entre 0 et 10 000).";
            if (t.Capacity is < 1 or > 100000) return $"Capacité invalide pour « {name} » (entre 1 et 100 000).";
        }
        if (types.Select(t => TextSearch.Fold(t.Name!.Trim())).Distinct().Count() != types.Count)
            return "Deux catégories de billets portent le même nom.";
        return null;
    }

    private static void Apply(Event e, EventInput i)
    {
        e.Title = i.Title!.Trim();
        e.Description = (i.Description ?? "").Trim();
        e.Category = i.Category!.Trim();
        e.Venue = i.Venue!.Trim();
        e.City = i.City!.Trim();
        e.StartsAt = Clock.ToUtc(i.StartsAt!.Value);
        e.EndsAt = Clock.ToUtc(i.EndsAt!.Value);
        e.CoverEmoji = string.IsNullOrWhiteSpace(i.CoverEmoji) ? "🎫" : i.CoverEmoji.Trim();
        e.CoverColor = i.CoverColor ?? "indigo";
        e.Status = i.Status ?? EventStatus.Draft;
    }

    // ---------- Statistiques ----------

    private static async Task<IResult> Stats(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var events = await db.Events.AsNoTracking().Include(e => e.TicketTypes).ToListAsync();
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.Status != TicketStatus.Cancelled)
            .Select(t => new { t.Price, t.Status, t.Booking.EventId, t.Booking.CreatedAt })
            .ToListAsync();
        var bookings = await db.Bookings.CountAsync(b => b.Status == BookingStatus.Confirmed);
        var recent = await db.Bookings.AsNoTracking()
            .OrderByDescending(b => b.CreatedAt)
            .Take(6)
            .Select(b => new
            {
                b.Id, b.Reference, b.Status, b.Total, b.CreatedAt,
                customer = b.User.FullName,
                eventTitle = b.Event.Title,
                coverEmoji = b.Event.CoverEmoji,
                tickets = b.Tickets.Count,
            })
            .ToListAsync();

        var eventsById = events.ToDictionary(e => e.Id);
        var published = events.Where(e => e.Status == EventStatus.Published).ToList();
        var capacity = published.Sum(e => e.TicketTypes.Sum(t => t.Capacity));
        var soldPublished = published.Sum(e => e.TicketTypes.Sum(t => t.Sold));
        var pastTickets = tickets.Where(t => eventsById[t.EventId].StartsAt <= now).ToList();
        var revenue = tickets.Sum(t => t.Price);

        var byDay = tickets.ToLookup(t => t.CreatedAt.Date);
        var firstDay = now.Date.AddDays(-13);
        var salesByDay = Enumerable.Range(0, 14).Select(i =>
        {
            var day = firstDay.AddDays(i);
            return new { date = day.ToString("yyyy-MM-dd"), tickets = byDay[day].Count(), revenue = byDay[day].Sum(t => t.Price) };
        });

        var soldByEvent = tickets.ToLookup(t => t.EventId);
        var topEvents = events.Where(e => e.Status != EventStatus.Draft)
            .Select(e => new
            {
                e.Id, e.Title, e.CoverEmoji, e.CoverColor, e.StartsAt,
                sold = soldByEvent[e.Id].Count(),
                capacity = e.TicketTypes.Sum(t => t.Capacity),
                revenue = soldByEvent[e.Id].Sum(t => t.Price),
            })
            .OrderByDescending(e => e.revenue)
            .Take(5);

        var byCategory = tickets.GroupBy(t => eventsById[t.EventId].Category)
            .Select(g => new { category = g.Key, tickets = g.Count(), revenue = g.Sum(t => t.Price) })
            .OrderByDescending(c => c.revenue);

        return Results.Ok(new
        {
            revenue,
            ticketsSold = tickets.Count,
            bookings,
            averageBasket = bookings == 0 ? 0 : Math.Round(revenue / bookings, 2),
            upcomingEvents = published.Count(e => e.StartsAt > now),
            publishedEvents = published.Count,
            draftEvents = events.Count(e => e.Status == EventStatus.Draft),
            fillRate = capacity == 0 ? 0 : Math.Round((double)soldPublished / capacity, 4),
            checkInRate = pastTickets.Count == 0 ? 0 : Math.Round((double)pastTickets.Count(t => t.Status == TicketStatus.Used) / pastTickets.Count, 4),
            salesByDay,
            topEvents,
            byCategory,
            recentBookings = recent,
        });
    }

    // ---------- Contrôle d'accès ----------

    /// <summary>Valide un billet à l'entrée. Le passage Valid -> Used est atomique : un billet ne peut entrer qu'une fois.</summary>
    private static async Task<IResult> CheckIn(CheckInRequest request, AppDbContext db)
    {
        var code = Codes.NormalizeTicketCode(request.Code);
        if (code.Length == 0) return ApiError.BadRequest("Saisissez le code du billet.");

        var ticket = await db.Tickets.AsNoTracking()
            .Include(t => t.TicketType)
            .Include(t => t.Booking).ThenInclude(b => b.Event)
            .FirstOrDefaultAsync(t => t.Code == code);

        if (ticket is null) return Results.Ok(new CheckInResult("UNKNOWN", false, $"Aucun billet ne correspond au code {code}.", null));

        var ev = ticket.Booking.Event;
        CheckInTicketDto Dto(DateTime? checkedInAt) =>
            new(ticket.Code, ticket.HolderName, ticket.TicketType.Name, ev.Title, ev.Id, checkedInAt);

        if (request.EventId is int eventId && ev.Id != eventId)
            return Results.Ok(new CheckInResult("WRONG_EVENT", false, $"Ce billet est valable pour « {ev.Title} ».", Dto(ticket.CheckedInAt)));
        if (ev.Status == EventStatus.Cancelled)
            return Results.Ok(new CheckInResult("EVENT_CANCELLED", false, "Cet événement a été annulé.", Dto(ticket.CheckedInAt)));
        if (ticket.Status == TicketStatus.Cancelled)
            return Results.Ok(new CheckInResult("CANCELLED", false, "Ce billet a été annulé.", Dto(ticket.CheckedInAt)));

        var now = DateTime.UtcNow;
        var accepted = ticket.Status == TicketStatus.Valid && await db.Tickets
            .Where(t => t.Id == ticket.Id && t.Status == TicketStatus.Valid)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.Used).SetProperty(t => t.CheckedInAt, now)) == 1;

        if (accepted) return Results.Ok(new CheckInResult("VALID", true, "Entrée autorisée.", Dto(now)));

        var usedAt = await db.Tickets.AsNoTracking().Where(t => t.Id == ticket.Id).Select(t => t.CheckedInAt).FirstAsync();
        return Results.Ok(new CheckInResult("ALREADY_USED", false, "Billet déjà utilisé.", Dto(usedAt)));
    }

    private static async Task<IResult> RecentCheckIns(AppDbContext db, int? eventId)
    {
        var used = db.Tickets.AsNoTracking().Where(t => t.Status == TicketStatus.Used);
        var all = db.Tickets.AsNoTracking().Where(t => t.Status != TicketStatus.Cancelled);
        if (eventId is int id)
        {
            used = used.Where(t => t.Booking.EventId == id);
            all = all.Where(t => t.Booking.EventId == id);
        }

        var items = await used.OrderByDescending(t => t.CheckedInAt).Take(15)
            .Select(t => new CheckInTicketDto(t.Code, t.HolderName, t.TicketType.Name, t.Booking.Event.Title, t.Booking.EventId, t.CheckedInAt))
            .ToListAsync();

        return Results.Ok(new { items, checkedIn = await used.CountAsync(), total = await all.CountAsync() });
    }
}
