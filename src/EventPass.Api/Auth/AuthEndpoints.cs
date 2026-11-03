using System.Net.Mail;
using System.Security.Claims;
using EventPass.Api.Common;
using EventPass.Api.Data;
using EventPass.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EventPass.Api.Auth;

public record RegisterRequest(string? FullName, string? Email, string? Password);
public record LoginRequest(string? Email, string? Password);
public record UserDto(int Id, string Email, string FullName, UserRole Role);
public record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);

public static class AuthEndpoints
{
    private static readonly PasswordHasher<User> Hasher = new();
    // Hachage factice : la réponse prend le même temps, que l'email existe ou non.
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(new User(), Guid.NewGuid().ToString()));

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentification");
        group.MapPost("/register", Register);
        group.MapPost("/login", Login);
        group.MapGet("/me", Me).RequireAuthorization();
        group.MapGet("/demo", (IConfiguration cfg) => Results.Ok(new
        {
            enabled = DemoData.IsEnabled(cfg),
            accounts = DemoData.PublicAccounts(cfg),
        }));
    }

    public static UserDto ToDto(this User user) => new(user.Id, user.Email, user.FullName, user.Role);

    private static async Task<IResult> Register(RegisterRequest request, AppDbContext db, TokenService tokens)
    {
        var fullName = request.FullName?.Trim() ?? "";
        var email = NormalizeEmail(request.Email);
        var password = request.Password ?? "";

        if (fullName.Length is < 2 or > 120) return ApiError.BadRequest("Le nom complet est obligatoire (2 à 120 caractères).");
        if (!IsValidEmail(email)) return ApiError.BadRequest("Adresse email invalide.");
        if (password.Length is < 8 or > 128) return ApiError.BadRequest("Le mot de passe doit contenir au moins 8 caractères.");
        if (await db.Users.AnyAsync(u => u.Email == email)) return ApiError.Conflict("Un compte existe déjà avec cet email.");

        var user = new User { FullName = fullName, Email = email, Role = UserRole.Client };
        user.PasswordHash = Hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return Results.Created("/api/auth/me", Issue(user, tokens));
    }

    private static async Task<IResult> Login(LoginRequest request, AppDbContext db, TokenService tokens)
    {
        var email = NormalizeEmail(request.Email);
        var password = request.Password ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user is null)
        {
            Hasher.VerifyHashedPassword(new User(), DummyHash.Value, password);
            return ApiError.Unauthorized("Email ou mot de passe incorrect.");
        }

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed) return ApiError.Unauthorized("Email ou mot de passe incorrect.");
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = Hasher.HashPassword(user, password);
            await db.SaveChangesAsync();
        }

        return Results.Ok(Issue(user, tokens));
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, AppDbContext db)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == principal.UserId());
        return user is null ? ApiError.Unauthorized("Session invalide.") : Results.Ok(user.ToDto());
    }

    private static AuthResponse Issue(User user, TokenService tokens)
    {
        var (token, expiresAt) = tokens.Create(user);
        return new AuthResponse(token, expiresAt, user.ToDto());
    }

    private static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static bool IsValidEmail(string email) =>
        email.Length <= 200 && MailAddress.TryCreate(email, out var address) && address.Address == email && email.Contains('.');
}
