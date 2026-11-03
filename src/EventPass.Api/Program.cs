using System.Diagnostics;
using System.Text.Json.Serialization;
using EventPass.Api.Auth;
using EventPass.Api.Common;
using EventPass.Api.Data;
using EventPass.Api.Features;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Port : 8080 par défaut (ASPNETCORE_HTTP_PORTS de l'image), surchargeable par PORT
var port = builder.Configuration["PORT"];
if (!string.IsNullOrWhiteSpace(port)) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// Base de données : PostgreSQL si DB_HOST / DATABASE_URL, sinon SQLite (voir DatabaseSettings)
builder.Services.AddSingleton(sp => DatabaseSettings.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    var db = sp.GetRequiredService<DatabaseSettings>();
    if (db.IsPostgres) options.UseNpgsql(db.ConnectionString);
    else options.UseSqlite(db.ConnectionString);
});

// Authentification JWT (rôles Client / Admin)
builder.Services.AddSingleton<TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TokenService>((options, tokens) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = tokens.ValidationParameters;
    });
builder.Services.AddAuthorizationBuilder().AddPolicy(Policies.Admin, p => p.RequireRole(Policies.Admin));

// CORS facultatif, seulement si le front appelle l'API directement (sinon nginx relaie /api)
var corsOrigins = (builder.Configuration["CORS_ORIGINS"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));
}

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "EventPass API",
        Version = "v1",
        Description = "Billetterie d'événements : catalogue, réservations, billets QR, contrôle d'accès et statistiques.",
    });
    var bearer = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Jeton obtenu avec POST /api/auth/login",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    o.AddSecurityDefinition("Bearer", bearer);
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = Array.Empty<string>() });
});

var app = builder.Build();
app.Logger.LogInformation("EventPass API v{Version} : démarrage", AppInfo.Version);

app.UseExceptionHandler();
app.UseStatusCodePages();

app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    await next(context);
    var path = context.Request.Path;
    if (path.StartsWithSegments("/health") || path.StartsWithSegments("/api/health")) return;
    app.Logger.LogInformation("{Method} {Path} -> {Status} ({Elapsed:0} ms)",
        context.Request.Method, path.Value, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
});

// Documentation sous /api/docs : accessible directement ou à travers le front (qui relaie /api)
if (!string.Equals(app.Configuration["SWAGGER_ENABLED"], "false", StringComparison.OrdinalIgnoreCase))
{
    app.UseSwagger(o => o.RouteTemplate = "api/docs/{documentName}/swagger.json");
    app.UseSwaggerUI(o =>
    {
        o.RoutePrefix = "api/docs";
        o.SwaggerEndpoint("v1/swagger.json", "EventPass API v1");
        o.DocumentTitle = "EventPass API";
    });
    app.MapGet("/", () => Results.Redirect("/api/docs")).ExcludeFromDescription();
}

if (corsOrigins.Length > 0) app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapSystemEndpoints();
app.MapAuthEndpoints();
app.MapEventEndpoints();
app.MapBookingEndpoints();
app.MapAdminEndpoints();

await DatabaseInitializer.InitializeAsync(app.Services, app.Lifetime.ApplicationStopping);

app.Run();

public partial class Program { }
