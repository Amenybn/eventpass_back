namespace EventPass.Api.Domain;

public enum UserRole { Client, Admin }
public enum EventStatus { Draft, Published, Cancelled }
public enum BookingStatus { Confirmed, Cancelled }
public enum TicketStatus { Valid, Used, Cancelled }

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Client;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Event
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Venue { get; set; } = "";
    public string City { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string CoverEmoji { get; set; } = "🎫";
    public string CoverColor { get; set; } = "indigo";
    public EventStatus Status { get; set; } = EventStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<TicketType> TicketTypes { get; set; } = new();
}

/// <summary>Catégorie de billet (Standard, VIP...) avec sa propre jauge.</summary>
public class TicketType
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Capacity { get; set; }
    public int Sold { get; set; }
}

public class Booking
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;
    public BookingStatus Status { get; set; } = BookingStatus.Confirmed;
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CancelledAt { get; set; }
    public List<Ticket> Tickets { get; set; } = new();
}

public class Ticket
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public int BookingId { get; set; }
    public Booking Booking { get; set; } = null!;
    public int TicketTypeId { get; set; }
    public TicketType TicketType { get; set; } = null!;
    /// <summary>Prix payé, figé au moment de l'achat.</summary>
    public decimal Price { get; set; }
    public string HolderName { get; set; } = "";
    public TicketStatus Status { get; set; } = TicketStatus.Valid;
    public DateTime? CheckedInAt { get; set; }
}
