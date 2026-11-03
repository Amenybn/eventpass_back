using System.Security.Claims;
using EventPass.Api.Auth;
using EventPass.Api.Common;
using EventPass.Api.Data;
using EventPass.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPass.Api.Features;

public record BookingItemRequest(int TicketTypeId, int Quantity);
public record CreateBookingRequest(int EventId, List<BookingItemRequest>? Items);

public record TicketDto(int Id, string Code, string TicketType, decimal Price, string HolderName, TicketStatus Status, DateTime? CheckedInAt);

public record BookingDto(
    int Id, string Reference, BookingStatus Status, decimal Total, DateTime CreatedAt, DateTime? CancelledAt, bool CanCancel,
    EventSummaryDto Event, List<TicketDto> Tickets);

public static class BookingEndpoints
{
    public const int MaxTicketsPerBooking = 10;

    public static void MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bookings").WithTags("Réservations").RequireAuthorization();
        group.MapPost("/", Create);
        group.MapGet("/mine", Mine);
        group.MapGet("/{id:int}", Get);
        group.MapPost("/{id:int}/cancel", Cancel);
    }

    /// <summary>
    /// Réserve des billets. Chaque jauge est décrémentée par un UPDATE conditionnel atomique
    /// ("Sold + n &lt;= Capacity") : deux achats simultanés ne peuvent pas dépasser la capacité.
    /// </summary>
    private static async Task<IResult> Create(CreateBookingRequest request, ClaimsPrincipal principal, AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var lines = (request.Items ?? new())
            .Where(i => i.Quantity > 0)
            .GroupBy(i => i.TicketTypeId)
            .Select(g => (TicketTypeId: g.Key, Quantity: g.Sum(i => i.Quantity)))
            .ToList();

        if (lines.Count == 0) return ApiError.BadRequest("Choisissez au moins un billet.");
        if (lines.Sum(l => l.Quantity) > MaxTicketsPerBooking)
            return ApiError.BadRequest($"{MaxTicketsPerBooking} billets maximum par réservation.");

        var ev = await db.Events.AsNoTracking().Include(e => e.TicketTypes).FirstOrDefaultAsync(e => e.Id == request.EventId);
        if (ev is null || ev.Status != EventStatus.Published) return ApiError.NotFound("Événement introuvable.");
        if (ev.StartsAt <= now) return ApiError.Conflict("Cet événement a déjà commencé : la billetterie est fermée.");

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == principal.UserId());
        if (user is null) return ApiError.Unauthorized("Session invalide.");

        var selected = new List<(TicketType Type, int Quantity)>();
        foreach (var line in lines)
        {
            var type = ev.TicketTypes.FirstOrDefault(t => t.Id == line.TicketTypeId);
            if (type is null) return ApiError.BadRequest("Catégorie de billet inconnue pour cet événement.");
            selected.Add((type, line.Quantity));
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var (type, quantity) in selected)
        {
            var reserved = await db.TicketTypes
                .Where(t => t.Id == type.Id && t.Sold + quantity <= t.Capacity)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Sold, t => t.Sold + quantity));

            if (reserved == 0)
            {
                await tx.RollbackAsync();
                var remaining = await db.TicketTypes.AsNoTracking().Where(t => t.Id == type.Id)
                    .Select(t => t.Capacity - t.Sold).FirstAsync();
                return ApiError.Conflict(remaining <= 0
                    ? $"La catégorie « {type.Name} » est complète."
                    : $"Il ne reste que {remaining} place(s) en « {type.Name} ».");
            }
        }

        var booking = new Booking
        {
            Reference = Codes.BookingReference(),
            UserId = user.Id,
            EventId = ev.Id,
            CreatedAt = now,
            Total = selected.Sum(s => s.Type.Price * s.Quantity),
        };
        foreach (var (type, quantity) in selected)
        {
            for (var i = 0; i < quantity; i++)
            {
                booking.Tickets.Add(new Ticket
                {
                    Code = Codes.TicketCode(),
                    TicketTypeId = type.Id,
                    Price = type.Price,
                    HolderName = user.FullName,
                });
            }
        }

        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        return Results.Created($"/api/bookings/{booking.Id}", await Load(db, booking.Id));
    }

    private static async Task<IResult> Mine(ClaimsPrincipal principal, AppDbContext db)
    {
        var userId = principal.UserId();
        var now = DateTime.UtcNow;
        var bookings = await Query(db).Where(b => b.UserId == userId).OrderByDescending(b => b.CreatedAt).ToListAsync();
        return Results.Ok(bookings.Select(b => ToDto(b, now)));
    }

    private static async Task<IResult> Get(int id, ClaimsPrincipal principal, AppDbContext db)
    {
        var booking = await Query(db).FirstOrDefaultAsync(b => b.Id == id);
        return booking is null || (booking.UserId != principal.UserId() && !principal.IsAdmin())
            ? ApiError.NotFound("Réservation introuvable.")
            : Results.Ok(ToDto(booking, DateTime.UtcNow));
    }

    /// <summary>Annulation par le client (ou l'organisateur) avant le début de l'événement : les places sont remises en vente.</summary>
    private static async Task<IResult> Cancel(int id, ClaimsPrincipal principal, AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var booking = await db.Bookings.AsNoTracking().Include(b => b.Event).Include(b => b.Tickets)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking is null || (booking.UserId != principal.UserId() && !principal.IsAdmin()))
            return ApiError.NotFound("Réservation introuvable.");
        if (booking.Status == BookingStatus.Cancelled) return ApiError.Conflict("Cette réservation est déjà annulée.");
        if (booking.Event.StartsAt <= now) return ApiError.Conflict("L'événement a commencé : la réservation ne peut plus être annulée.");
        if (booking.Tickets.Any(t => t.Status == TicketStatus.Used))
            return ApiError.Conflict("Un billet a déjà été utilisé : annulation impossible.");

        await using var tx = await db.Database.BeginTransactionAsync();
        var changed = await db.Bookings
            .Where(b => b.Id == id && b.Status == BookingStatus.Confirmed)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Cancelled).SetProperty(b => b.CancelledAt, now));
        if (changed == 0)
        {
            await tx.RollbackAsync();
            return ApiError.Conflict("Cette réservation est déjà annulée.");
        }

        await db.Tickets.Where(t => t.BookingId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TicketStatus.Cancelled));
        foreach (var group in booking.Tickets.GroupBy(t => t.TicketTypeId))
        {
            var quantity = group.Count();
            await db.TicketTypes.Where(t => t.Id == group.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Sold, t => t.Sold - quantity));
        }
        await tx.CommitAsync();

        return Results.Ok(await Load(db, id));
    }

    private static IQueryable<Booking> Query(AppDbContext db) => db.Bookings.AsNoTracking()
        .Include(b => b.Event).ThenInclude(e => e.TicketTypes)
        .Include(b => b.Tickets).ThenInclude(t => t.TicketType)
        .AsSplitQuery();

    private static async Task<BookingDto?> Load(AppDbContext db, int id)
    {
        var booking = await Query(db).FirstOrDefaultAsync(b => b.Id == id);
        return booking is null ? null : ToDto(booking, DateTime.UtcNow);
    }

    public static BookingDto ToDto(Booking b, DateTime now) => new(
        b.Id, b.Reference, b.Status, b.Total, b.CreatedAt, b.CancelledAt,
        CanCancel: b.Status == BookingStatus.Confirmed && b.Event.StartsAt > now && b.Tickets.All(t => t.Status != TicketStatus.Used),
        b.Event.ToSummary(now),
        b.Tickets.OrderBy(t => t.Id)
            .Select(t => new TicketDto(t.Id, t.Code, t.TicketType.Name, t.Price, t.HolderName, t.Status, t.CheckedInAt))
            .ToList());
}
