using System.Text;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using CashLoanManagement.Api.Middleware;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Infrastructure.Data;
using CashLoanManagement.Infrastructure.Jobs;
using CashLoanManagement.Infrastructure.Persistence;
using CashLoanManagement.Infrastructure.Services;

// ─── Npgsql global: treat all DateTime(Kind=Unspecified) as UTC ───────────────
// Prevents "Cannot write DateTime with Kind=Unspecified to timestamptz" errors
// when dates arrive from JSON (DueDate, filter dates, etc.)
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// ─── Serilog ─────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();
builder.Host.UseSerilog();

// ─── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins("http://localhost:3000", "https://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// ─── Controllers ─────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Serialize all enums as their string names (e.g. "Active" not 2)
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();

// ─── Swagger with JWT Bearer support ─────────────────────────────────────────
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Cash & Loan Management API",
        Version = "v1",
        Description = "API for managing operational cash and loans at a mining site."
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter: **Bearer {your JWT token}**"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ─── PostgreSQL Database ──────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;

builder.Services.AddDbContext<CashLoanDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        b => b.MigrationsAssembly(typeof(CashLoanDbContext).Assembly.FullName)));

builder.Services.AddScoped<ICashLoanDbContext>(p => p.GetRequiredService<CashLoanDbContext>());

// ─── Application Services ────────────────────────────────────────────────────
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<LoanStatusJob>();
// CashController registered as service so LoanController can call ComputeBalanceAsync
builder.Services.AddScoped<CashLoanManagement.Api.Controllers.CashController>();

// ─── Hangfire ─────────────────────────────────────────────────────────────────
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
    options.Queues = new[] { "default" };
});

// ─── JWT Authentication ───────────────────────────────────────────────────────
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var secret = jwtSettings.GetValue<string>("Secret") ?? throw new InvalidOperationException("JWT Secret missing.");
var key = Encoding.ASCII.GetBytes(secret);

builder.Services.AddAuthentication(x =>
{
    x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(x =>
{
    x.RequireHttpsMetadata = false;
    x.SaveToken = true;
    x.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = jwtSettings.GetValue<string>("Issuer"),
        ValidateAudience = true,
        ValidAudience = jwtSettings.GetValue<string>("Audience"),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

// ─── Build App ────────────────────────────────────────────────────────────────
var app = builder.Build();

// ─── Seed Database ────────────────────────────────────────────────────────────
await DataSeeder.SeedAsync(app.Services);

// ─── Middleware Pipeline ──────────────────────────────────────────────────────
// CORS must be first — before everything, especially before HTTPS redirect,
// so that preflight OPTIONS responses always carry Allow-Origin headers.
app.UseCors("Frontend");

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Cash & Loan Management API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseSerilogRequestLogging();

// Only redirect to HTTPS in production — in dev the browser calls http://localhost:5012
// and a 307 redirect kills CORS preflight before headers are written.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

// ─── Hangfire Dashboard (restrict to localhost / admin in prod) ───────────────
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IsReadOnlyFunc = _ => !app.Environment.IsDevelopment()
});

// ─── Register Recurring Jobs ─────────────────────────────────────────────────
RecurringJob.AddOrUpdate<LoanStatusJob>(
    "mark-overdue-loans",
    job => job.MarkOverdueLoansAsync(),
    Cron.Daily(1, 0));   // 01:00 UTC daily

RecurringJob.AddOrUpdate<LoanStatusJob>(
    "auto-blacklist-defaulters",
    job => job.AutoBlacklistDefaultersAsync(),
    Cron.Daily(1, 15));  // 01:15 UTC daily

RecurringJob.AddOrUpdate<LoanStatusJob>(
    "check-low-cash-balance",
    job => job.CheckLowCashBalanceAsync(),
    Cron.Daily(7, 0));   // 07:00 UTC daily

app.MapControllers();

try
{
    Log.Information("Starting Cash & Loan Management Web API...");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
