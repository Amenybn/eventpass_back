using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace EventPass.Api.Common;

public static class AppInfo
{
    public static readonly DateTime StartedAt = DateTime.UtcNow;
    public static readonly string Version = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}

public static class Clock
{
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

/// <summary>Réponses d'erreur homogènes : { "message": "..." }.</summary>
public static class ApiError
{
    public static IResult Error(int status, string message) => Results.Json(new { message }, statusCode: status);
    public static IResult BadRequest(string message) => Error(StatusCodes.Status400BadRequest, message);
    public static IResult Unauthorized(string message) => Error(StatusCodes.Status401Unauthorized, message);
    public static IResult NotFound(string message) => Error(StatusCodes.Status404NotFound, message);
    public static IResult Conflict(string message) => Error(StatusCodes.Status409Conflict, message);
}

/// <summary>Codes lisibles et non devinables (sans 0/O, 1/I/L, U).</summary>
public static class Codes
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Random(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static string TicketCode()
    {
        var r = Random(8);
        return $"EVP-{r[..4]}-{r[4..]}";
    }

    public static string BookingReference() => $"RES-{Random(6)}";

    /// <summary>Accepte "evp 7k3d 9qxm", "EVP7K3D9QXM"... et renvoie "EVP-7K3D-9QXM".</summary>
    public static string NormalizeTicketCode(string? input)
    {
        var raw = new string((input ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return raw.Length == 11 && raw.StartsWith("EVP", StringComparison.Ordinal)
            ? $"EVP-{raw[3..7]}-{raw[7..]}"
            : (input ?? "").Trim().ToUpperInvariant();
    }
}

/// <summary>Recherche insensible à la casse et aux accents (sans dépendre d'ICU).</summary>
public static class TextSearch
{
    private static readonly Dictionary<char, char> Folding = BuildFolding();

    private static Dictionary<char, char> BuildFolding()
    {
        var map = new Dictionary<char, char>();
        void Add(string chars, char target) { foreach (var c in chars) map[c] = target; }
        Add("àâäáãåÀÂÄÁÃÅ", 'a');
        Add("çÇ", 'c');
        Add("éèêëÉÈÊË", 'e');
        Add("îïíìÎÏÍÌ", 'i');
        Add("ñÑ", 'n');
        Add("ôöóòõÔÖÓÒÕ", 'o');
        Add("ùûüúÙÛÜÚ", 'u');
        Add("ÿýŸÝ", 'y');
        return map;
    }

    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) sb.Append(Folding.TryGetValue(c, out var f) ? f : char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static bool Matches(string? query, params string?[] fields)
    {
        var q = Fold(query).Trim();
        return q.Length == 0 || fields.Any(f => Fold(f).Contains(q, StringComparison.Ordinal));
    }
}
