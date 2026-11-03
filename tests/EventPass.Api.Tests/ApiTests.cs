using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace EventPass.Api.Tests;

/// <summary>API complète sur une base SQLite temporaire, avec le jeu de démonstration.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eventpass-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DATABASE_URL", "");
        builder.UseSetting("DB_HOST", "");
        builder.UseSetting("SQLITE_PATH", _dbPath);
        builder.UseSetting("JWT_SECRET", "tests-only-secret-0123456789-abcdefghijkl");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}

public class ApiTests
{
    private const string Admin = "admin@eventpass.test";
    private const string AdminPassword = "Admin123!";
    private const string Client = "client@eventpass.test";
    private const string ClientPassword = "Client123!";

    [Fact]
    public async Task Health_and_system_info_report_the_database()
    {
        using var factory = new ApiFactory();
        var http = factory.CreateClient();

        var health = await Json(await http.GetAsync("/health"));
        Assert.Equal("UP", health.GetProperty("status").GetString());
        Assert.Equal("UP", health.GetProperty("database").GetString());

        var info = await Json(await http.GetAsync("/api/system/info"));
        Assert.Equal("SQLite", info.GetProperty("database").GetProperty("provider").GetString());
        Assert.True(info.GetProperty("counts").GetProperty("events").GetInt32() >= 10);
    }

    [Fact]
    public async Task Public_catalog_hides_drafts_and_cancelled_events()
    {
        using var factory = new ApiFactory();
        var http = factory.CreateClient();

        var events = await Json(await http.GetAsync("/api/events?period=all"));
        Assert.True(events.GetArrayLength() >= 8);
        Assert.All(events.EnumerateArray(), e => Assert.Equal("Published", e.GetProperty("status").GetString()));

        var search = await Json(await http.GetAsync("/api/events?q=cinema"));
        Assert.Contains(search.EnumerateArray(), e => e.GetProperty("title").GetString()!.StartsWith("Cinéma"));
    }

    [Fact]
    public async Task Register_login_and_me()
    {
        using var factory = new ApiFactory();
        var http = factory.CreateClient();

        var register = await http.PostAsJsonAsync("/api/auth/register", new { fullName = "Test Utilisateur", email = "Test@Exemple.test", password = "motdepasse1" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var duplicate = await http.PostAsJsonAsync("/api/auth/register", new { fullName = "Autre", email = "test@exemple.test", password = "motdepasse1" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var wrong = await http.PostAsJsonAsync("/api/auth/login", new { email = "test@exemple.test", password = "mauvais" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var authed = await LoginAs(factory, "test@exemple.test", "motdepasse1");
        var me = await Json(await authed.GetAsync("/api/auth/me"));
        Assert.Equal("Test Utilisateur", me.GetProperty("fullName").GetString());
        Assert.Equal("Client", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Booking_reserves_seats_and_cancellation_releases_them()
    {
        using var factory = new ApiFactory();
        var client = await LoginAs(factory, Client, ClientPassword);

        var ev = (await Json(await client.GetAsync("/api/events"))).EnumerateArray().First(e => e.GetProperty("remaining").GetInt32() >= 5);
        var id = ev.GetProperty("id").GetInt32();
        var type = (await Json(await client.GetAsync($"/api/events/{id}"))).GetProperty("ticketTypes")[0];
        var remainingBefore = type.GetProperty("remaining").GetInt32();

        var created = await client.PostAsJsonAsync("/api/bookings", new { eventId = id, items = new[] { new { ticketTypeId = type.GetProperty("id").GetInt32(), quantity = 3 } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var booking = await Json(created);
        Assert.Equal(3, booking.GetProperty("tickets").GetArrayLength());
        Assert.StartsWith("EVP-", booking.GetProperty("tickets")[0].GetProperty("code").GetString());

        Assert.Equal(remainingBefore - 3, await Remaining(client, id));

        var cancelled = await client.PostAsync($"/api/bookings/{booking.GetProperty("id").GetInt32()}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal(remainingBefore, await Remaining(client, id));
    }

    [Fact]
    public async Task Booking_beyond_capacity_is_rejected()
    {
        using var factory = new ApiFactory();
        var admin = await LoginAs(factory, Admin, AdminPassword);
        var client = await LoginAs(factory, Client, ClientPassword);

        var start = DateTime.UtcNow.AddDays(7);
        var created = await admin.PostAsJsonAsync("/api/admin/events", new
        {
            title = "Petit comité", category = "Atelier", venue = "Salle 1", city = "Tunis",
            startsAt = start, endsAt = start.AddHours(2), status = "Published",
            ticketTypes = new[] { new { name = "Unique", price = 10m, capacity = 2 } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ev = await Json(created);
        var payload = new { eventId = ev.GetProperty("id").GetInt32(), items = new[] { new { ticketTypeId = ev.GetProperty("ticketTypes")[0].GetProperty("id").GetInt32(), quantity = 2 } } };

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/bookings", payload)).StatusCode);
        var full = await client.PostAsJsonAsync("/api/bookings", payload);
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Contains("complète", (await Json(full)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Ticket_can_be_checked_in_only_once()
    {
        using var factory = new ApiFactory();
        var admin = await LoginAs(factory, Admin, AdminPassword);
        var client = await LoginAs(factory, Client, ClientPassword);

        var mine = await Json(await client.GetAsync("/api/bookings/mine"));
        var ticket = mine.EnumerateArray()
            .SelectMany(b => b.GetProperty("tickets").EnumerateArray())
            .First(t => t.GetProperty("status").GetString() == "Valid");
        var code = ticket.GetProperty("code").GetString()!;

        var first = await Json(await admin.PostAsJsonAsync("/api/admin/checkin", new { code = code.Replace("-", " ").ToLowerInvariant() }));
        Assert.Equal("VALID", first.GetProperty("result").GetString());

        var second = await Json(await admin.PostAsJsonAsync("/api/admin/checkin", new { code }));
        Assert.Equal("ALREADY_USED", second.GetProperty("result").GetString());

        var unknown = await Json(await admin.PostAsJsonAsync("/api/admin/checkin", new { code = "EVP-0000-0000" }));
        Assert.Equal("UNKNOWN", unknown.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Organizer_area_requires_the_admin_role()
    {
        using var factory = new ApiFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/stats")).StatusCode);

        var client = await LoginAs(factory, Client, ClientPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/stats")).StatusCode);

        var admin = await LoginAs(factory, Admin, AdminPassword);
        var stats = await Json(await admin.GetAsync("/api/admin/stats"));
        Assert.True(stats.GetProperty("ticketsSold").GetInt32() > 0);
        Assert.Equal(14, stats.GetProperty("salesByDay").GetArrayLength());
    }

    private static async Task<HttpClient> LoginAs(ApiFactory factory, string email, string password)
    {
        var http = factory.CreateClient();
        var login = await http.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await Json(login)).GetProperty("token").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<int> Remaining(HttpClient http, int eventId) =>
        (await Json(await http.GetAsync($"/api/events/{eventId}"))).GetProperty("ticketTypes")[0].GetProperty("remaining").GetInt32();

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
