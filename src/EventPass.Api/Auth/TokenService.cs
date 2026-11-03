using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EventPass.Api.Domain;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EventPass.Api.Auth;

public static class Policies
{
    public const string Admin = nameof(UserRole.Admin);
}

public static class ClaimsExtensions
{
    public static int UserId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue("sub"), out var id) ? id : 0;

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.IsInRole(Policies.Admin);
}

/// <summary>Émet et valide les jetons JWT (clé JWT_SECRET, ou clé aléatoire si absente).</summary>
public sealed class TokenService
{
    private const string Issuer = "eventpass";
    private readonly SymmetricSecurityKey _key;
    private readonly TimeSpan _lifetime;

    public TokenService(IConfiguration cfg, ILogger<TokenService> logger)
    {
        var secret = cfg["JWT_SECRET"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
        {
            logger.LogWarning(string.IsNullOrWhiteSpace(secret)
                ? "JWT_SECRET non défini : clé aléatoire générée, les sessions seront perdues au redémarrage."
                : "JWT_SECRET trop court (32 caractères minimum) : clé aléatoire générée.");
            secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            UsingGeneratedSecret = true;
        }

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        _lifetime = TimeSpan.FromHours(int.TryParse(cfg["JWT_HOURS"], out var hours) && hours > 0 ? hours : 8);

        ValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Issuer,
            IssuerSigningKey = _key,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "name",
            RoleClaimType = "role",
        };
    }

    public bool UsingGeneratedSecret { get; }

    public TokenValidationParameters ValidationParameters { get; }

    public (string Token, DateTime ExpiresAt) Create(User user)
    {
        var expiresAt = DateTime.UtcNow.Add(_lifetime);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(),
                ["email"] = user.Email,
                ["name"] = user.FullName,
                ["role"] = user.Role.ToString(),
            },
        });
        return (token, expiresAt);
    }
}
