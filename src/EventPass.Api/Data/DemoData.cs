using System.Security.Cryptography;
using EventPass.Api.Common;
using EventPass.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EventPass.Api.Data;

public record DemoAccount(string Label, string Email, string Password);

/// <summary>
/// Données initiales. Le compte organisateur est toujours créé s'il n'y en a aucun (ADMIN_EMAIL / ADMIN_PASSWORD).
/// Le jeu de démonstration (SEED_DEMO_DATA, activé par défaut) n'est inséré que si la base ne contient aucun événement :
/// il n'écrase jamais de données existantes.
/// </summary>
public static class DemoData
{
    private const string DefaultAdminEmail = "admin@eventpass.test";
    private const string DefaultAdminPassword = "Admin123!";
    private const string DemoClientEmail = "client@eventpass.test";
    private const string DemoClientPassword = "Client123!";

    public static bool IsEnabled(IConfiguration cfg) =>
        !string.Equals(cfg["SEED_DEMO_DATA"], "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Comptes affichés sur l'écran de connexion (uniquement en mode démo et avec les mots de passe par défaut).</summary>
    public static IReadOnlyList<DemoAccount> PublicAccounts(IConfiguration cfg)
    {
        if (!IsEnabled(cfg)) return [];
        var accounts = new List<DemoAccount>();
        if (string.IsNullOrWhiteSpace(cfg["ADMIN_PASSWORD"]))
            accounts.Add(new("Organisateur", AdminEmail(cfg), DefaultAdminPassword));
        accounts.Add(new("Client", DemoClientEmail, DemoClientPassword));
        return accounts;
    }

    private static string AdminEmail(IConfiguration cfg) =>
        (string.IsNullOrWhiteSpace(cfg["ADMIN_EMAIL"]) ? DefaultAdminEmail : cfg["ADMIN_EMAIL"]!).Trim().ToLowerInvariant();

    public static async Task SeedAsync(AppDbContext db, IConfiguration cfg, ILogger logger)
    {
        var hasher = new PasswordHasher<User>();

        if (!await db.Users.AnyAsync(u => u.Role == UserRole.Admin))
        {
            var admin = new User
            {
                Email = AdminEmail(cfg),
                FullName = string.IsNullOrWhiteSpace(cfg["ADMIN_NAME"]) ? "Équipe EventPass" : cfg["ADMIN_NAME"]!.Trim(),
                Role = UserRole.Admin,
            };
            admin.PasswordHash = hasher.HashPassword(admin, string.IsNullOrWhiteSpace(cfg["ADMIN_PASSWORD"]) ? DefaultAdminPassword : cfg["ADMIN_PASSWORD"]!);
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            logger.LogInformation("Compte organisateur créé : {Email}", admin.Email);
        }

        if (!IsEnabled(cfg) || await db.Events.AnyAsync()) return;

        var stats = new Generator(hasher).Generate(db);
        await db.SaveChangesAsync();
        logger.LogInformation("Jeu de démonstration inséré : {Events} événements, {Bookings} réservations, {Tickets} billets",
            stats.Events, stats.Bookings, stats.Tickets);
    }

    private sealed record TypeSpec(string Name, decimal Price, int Capacity);

    private sealed record EventSpec(
        string Title, string Category, string Venue, string City, int Day, double Hour, double DurationHours,
        string Emoji, string Color, double Fill, string Description, TypeSpec[] Types,
        EventStatus Status = EventStatus.Published);

    private static readonly EventSpec[] Events =
    [
        new("Jazz sous les étoiles", "Musique", "Amphithéâtre de plein air", "Hammamet", 12, 19.5, 3, "🎷", "indigo", 0.62,
            "Trois formations de jazz se succèdent face à la mer : quartet acoustique, big band et jam session finale ouverte aux musiciens du public.",
            [new("Standard", 45, 240), new("Carré VIP", 120, 40)]),
        new("Sommet Cloud & DevOps", "Conférence", "Palais des congrès", "Tunis", 20, 8, 9, "☁️", "sky", 0.48,
            "Une journée de conférences et d'ateliers sur Kubernetes, l'automatisation des déploiements, la sécurité des conteneurs et l'observabilité.",
            [new("Pass journée", 80, 300), new("Pass + atelier pratique", 150, 60)]),
        new("Stand-up : rires en série", "Humour", "Théâtre municipal", "Sousse", 6, 19, 2, "🎤", "amber", 0.74,
            "Cinq humoristes de la nouvelle scène enchaînent leurs meilleurs passages pour une soirée 100 % rires.",
            [new("Standard", 30, 180), new("Premier rang", 55, 20)]),
        new("Semi-marathon de la Médina", "Sport", "Esplanade centrale", "Tunis", 30, 6, 4, "🏃", "emerald", 0.35,
            "Parcours de 21 km ou de 10 km à travers la ville, ravitaillements tous les 5 km, médaille pour chaque finisher.",
            [new("Dossard 21 km", 35, 400), new("Course 10 km", 20, 300)]),
        new("Atelier photo de rue", "Atelier", "Galerie Lumière", "Sidi Bou Saïd", 9, 9, 3, "📷", "rose", 0.9,
            "Balade photographique encadrée par un photographe professionnel : composition, lumière naturelle et portrait de rue.",
            [new("Participant", 60, 20)]),
        new("Nuit symphonique", "Musique", "Grand théâtre", "Carthage", 40, 19, 2.5, "🎻", "violet", 0.22,
            "L'orchestre symphonique interprète les grandes musiques de films, de l'aventure à la science-fiction.",
            [new("Catégorie 2", 40, 300), new("Catégorie 1", 70, 200), new("Loge", 150, 30)]),
        new("Hackathon IA — 48 h", "Technologie", "Technopole", "Sfax", 15, 8, 48, "🤖", "slate", 0.55,
            "48 heures pour concevoir un prototype utile avec l'intelligence artificielle. Mentors, repas et prix pour les trois meilleures équipes.",
            [new("Participant", 15, 150)]),
        new("Cinéma en plein air : grands classiques", "Cinéma", "Jardin public", "Bizerte", 3, 19.5, 3, "🎬", "orange", 1.0,
            "Projection sur écran géant de deux classiques restaurés. Transats et couvertures fournis.",
            [new("Entrée", 12, 120)]),
        new("Salon du livre jeunesse", "Culture", "Centre des expositions", "Monastir", 25, 9, 8, "📚", "emerald", 0.3,
            "Rencontres d'auteurs, ateliers d'illustration et lectures contées pour les 6–14 ans.",
            [new("Adulte", 8, 400), new("Enfant", 4, 400)]),
        new("Soirée électro — clôture de saison", "Musique", "Plage du Nord", "Hammamet", -5, 20, 5, "🎧", "violet", 0.85,
            "Quatre DJ pour clôturer la saison au bord de l'eau, jusqu'au bout de la nuit.",
            [new("Early bird", 25, 150), new("Standard", 35, 250)]),
        new("Festival street food", "Festival", "Parc des expositions", "Nabeul", 18, 11, 10, "🌮", "orange", 0,
            "Une trentaine de stands de cuisine de rue, concerts acoustiques et animations pour les enfants.",
            [new("Entrée", 5, 1000)], EventStatus.Cancelled),
        new("Gala de fin d'année", "Gala", "Hôtel Les Jasmins", "Tunis", 60, 19, 5, "🥂", "slate", 0,
            "Dîner de gala, remise des trophées de l'année et soirée dansante.",
            [new("Invitation", 90, 250)], EventStatus.Draft),
    ];

    private static readonly string[] FirstNames =
        ["Amine", "Sarra", "Mehdi", "Ines", "Karim", "Nour", "Youssef", "Leila", "Omar", "Rania", "Hichem", "Salma",
         "Walid", "Mariem", "Bilel", "Emna", "Fares", "Asma", "Skander", "Hela", "Lucas", "Camille", "Thomas", "Chloé"];

    private static readonly string[] LastNames =
        ["Ben Salah", "Gharbi", "Jebali", "Mansouri", "Haddad", "Chaabane", "Ayari", "Bouzid", "Khelifi", "Dridi", "Martin", "Bernard"];

    /// <summary>Génère des ventes réalistes (déterministes) sur les 14 derniers jours.</summary>
    private sealed class Generator(PasswordHasher<User> hasher)
    {
        private readonly Random _rng = new(2026);
        private readonly DateTime _now = DateTime.UtcNow;
        private int _bookings;
        private int _tickets;

        public (int Events, int Bookings, int Tickets) Generate(AppDbContext db)
        {
            var client = new User { Email = DemoClientEmail, FullName = "Yasmine Trabelsi", CreatedAt = _now.AddDays(-30) };
            client.PasswordHash = hasher.HashPassword(client, DemoClientPassword);

            // Les acheteurs fictifs ne peuvent pas se connecter (mot de passe aléatoire)
            var buyerHash = hasher.HashPassword(new User(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
            var buyers = Enumerable.Range(0, 36).Select(i =>
            {
                var first = FirstNames[i % FirstNames.Length];
                var last = LastNames[(i * 5 + i / FirstNames.Length) % LastNames.Length];
                return new User
                {
                    FullName = $"{first} {last}",
                    Email = $"{Slug(first)}.{Slug(last)}{i + 1}@exemple.test",
                    PasswordHash = buyerHash,
                    CreatedAt = _now.AddDays(-_rng.Next(20, 90)),
                };
            }).ToList();

            db.Users.Add(client);
            db.Users.AddRange(buyers);

            var today = _now.Date;
            foreach (var spec in Events)
            {
                var start = today.AddDays(spec.Day).AddHours(spec.Hour);
                var ev = new Event
                {
                    Title = spec.Title, Category = spec.Category, Venue = spec.Venue, City = spec.City,
                    Description = spec.Description, CoverEmoji = spec.Emoji, CoverColor = spec.Color, Status = spec.Status,
                    StartsAt = start, EndsAt = start.AddHours(spec.DurationHours), CreatedAt = _now.AddDays(-21),
                    TicketTypes = spec.Types.Select(t => new TicketType { Name = t.Name, Price = t.Price, Capacity = t.Capacity }).ToList(),
                };
                db.Events.Add(ev);

                // Le client de démo a des billets à venir (Jazz, Cinéma) et des billets d'un événement passé (Soirée électro)
                if (spec.Title.StartsWith("Jazz")) Book(db, ev, client, ev.TicketTypes[0], 2);
                if (spec.Title.StartsWith("Cinéma")) Book(db, ev, client, ev.TicketTypes[0], 1);
                if (spec.Title.StartsWith("Soirée")) Book(db, ev, client, ev.TicketTypes[1], 2);

                foreach (var type in ev.TicketTypes)
                {
                    var target = (int)Math.Round(type.Capacity * spec.Fill);
                    while (type.Sold < target)
                    {
                        var quantity = Math.Min(_rng.Next(1, 5), target - type.Sold);
                        var buyer = buyers[_rng.Next(buyers.Count)];
                        Book(db, ev, buyer, type, quantity);
                        // Quelques annulations, pour le réalisme (les places sont remises en vente)
                        if (_rng.NextDouble() < 0.03 && ev.StartsAt > _now) Book(db, ev, buyer, type, 1, cancelled: true);
                    }
                }
            }

            return (Events.Length, _bookings, _tickets);
        }

        private void Book(AppDbContext db, Event ev, User buyer, TicketType type, int quantity, bool cancelled = false)
        {
            // Achat dans les 14 jours précédant aujourd'hui (ou l'événement, s'il est passé)
            var latest = ev.StartsAt < _now ? ev.StartsAt.AddDays(-1) : _now.AddMinutes(-5);
            var createdAt = latest.AddMinutes(-_rng.Next(0, 13 * 24 * 60));
            var past = ev.StartsAt <= _now;

            var booking = new Booking
            {
                Reference = Codes.BookingReference(),
                User = buyer,
                Event = ev,
                CreatedAt = createdAt,
                Total = type.Price * quantity,
                Status = cancelled ? BookingStatus.Cancelled : BookingStatus.Confirmed,
                CancelledAt = cancelled ? createdAt.AddHours(_rng.Next(1, 48)) : null,
            };

            for (var i = 0; i < quantity; i++)
            {
                var used = !cancelled && past && _rng.NextDouble() < 0.88;
                booking.Tickets.Add(new Ticket
                {
                    Code = Codes.TicketCode(),
                    TicketType = type,
                    Price = type.Price,
                    HolderName = buyer.FullName,
                    Status = cancelled ? TicketStatus.Cancelled : used ? TicketStatus.Used : TicketStatus.Valid,
                    CheckedInAt = used ? ev.StartsAt.AddMinutes(_rng.Next(-30, 120)) : null,
                });
            }

            if (!cancelled)
            {
                type.Sold += quantity;
                _tickets += quantity;
            }
            _bookings++;
            db.Bookings.Add(booking);
        }

        private static string Slug(string value) =>
            new(TextSearch.Fold(value).Where(char.IsAsciiLetterOrDigit).ToArray());
    }
}
