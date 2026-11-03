using EventPass.Api.Common;
using EventPass.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EventPass.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<TicketType> TicketTypes => Set<TicketType>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.FullName).HasMaxLength(120);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Event>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(150);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Category).HasMaxLength(80);
            e.Property(x => x.Venue).HasMaxLength(120);
            e.Property(x => x.City).HasMaxLength(80);
            e.Property(x => x.CoverEmoji).HasMaxLength(16);
            e.Property(x => x.CoverColor).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.StartsAt);
            e.HasMany(x => x.TicketTypes).WithOne(t => t.Event).HasForeignKey(t => t.EventId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TicketType>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Price).HasPrecision(10, 2);
        });

        b.Entity<Booking>(e =>
        {
            e.HasIndex(x => x.Reference).IsUnique();
            e.Property(x => x.Reference).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Total).HasPrecision(10, 2);
            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tickets).WithOne(t => t.Booking).HasForeignKey(t => t.BookingId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Ticket>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.HolderName).HasMaxLength(120);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Price).HasPrecision(10, 2);
            e.HasOne(x => x.TicketType).WithMany().HasForeignKey(x => x.TicketTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        // Toutes les dates sont en UTC, quel que soit le moteur (SQLite ne conserve pas le "Kind").
        var utc = new ValueConverter<DateTime, DateTime>(v => Clock.ToUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? Clock.ToUtc(v.Value) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utc);
                else if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(utcNullable);
            }
        }
    }
}
