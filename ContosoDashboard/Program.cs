using Microsoft.EntityFrameworkCore;
using ContosoDashboard.Data;
using ContosoDashboard.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using ContosoDashboard.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Add authentication state provider for Blazor
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();

// Configure Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Configure Mock Authentication (Cookie-based for training purposes)
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

// Add authorization
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Employee", policy => policy.RequireRole("Employee", "TeamLead", "ProjectManager", "Administrator"));
    options.AddPolicy("TeamLead", policy => policy.RequireRole("TeamLead", "ProjectManager", "Administrator"));
    options.AddPolicy("ProjectManager", policy => policy.RequireRole("ProjectManager", "Administrator"));
    options.AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));
});

// Register application services
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ITaskService, TaskService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();

// Document storage (local filesystem outside wwwroot; swap the provider for cloud storage later)
builder.Services.Configure<DocumentStorageOptions>(builder.Configuration.GetSection(DocumentStorageOptions.SectionName));
builder.Services.AddSingleton<IFileStorageService>(sp =>
{
    var options = sp.GetRequiredService<IOptions<DocumentStorageOptions>>().Value;
    var environment = sp.GetRequiredService<IHostEnvironment>();

    if (!string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase))
    {
        throw new NotSupportedException($"Document storage provider '{options.Provider}' is not available in this build.");
    }

    return new LocalFileStorageService(
        LocalFileStorageService.ResolveRoot(options, environment.ContentRootPath),
        sp.GetRequiredService<ILogger<LocalFileStorageService>>());
});
builder.Services.AddScoped<DocumentAccess>();

// Asynchronous malware scanning: in-process queue and worker (training build), same contract as the cloud pipeline
builder.Services.Configure<MalwareScanOptions>(builder.Configuration.GetSection(MalwareScanOptions.SectionName));
var malwareScan = builder.Configuration.GetSection(MalwareScanOptions.SectionName).Get<MalwareScanOptions>() ?? new MalwareScanOptions();

if (!string.Equals(malwareScan.Mode, "InProcess", StringComparison.OrdinalIgnoreCase))
{
    throw new NotSupportedException($"Malware scan mode '{malwareScan.Mode}' is not available in this build.");
}

if (!builder.Environment.IsDevelopment() &&
    (string.Equals(malwareScan.Scanner, "Eicar", StringComparison.OrdinalIgnoreCase) || malwareScan.SimulatedDelaySeconds > 0))
{
    throw new InvalidOperationException("MalwareScan:Scanner 'Eicar' and MalwareScan:SimulatedDelaySeconds are for the Development environment only.");
}

if (string.Equals(malwareScan.Scanner, "Eicar", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IMalwareScanner, EicarScanner>();
}
else
{
    builder.Services.AddSingleton<IMalwareScanner, NoOpMalwareScanner>();
}

builder.Services.AddSingleton<InProcessScanQueue>();
builder.Services.AddSingleton<IScanQueue>(sp => sp.GetRequiredService<InProcessScanQueue>());
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<IDocumentService>(sp => sp.GetRequiredService<DocumentService>());
builder.Services.AddScoped<IScanResultHandler>(sp => sp.GetRequiredService<DocumentService>());
builder.Services.AddScoped<IScanMaintenance>(sp => sp.GetRequiredService<DocumentService>());
builder.Services.AddHostedService<ScanWorker>();
builder.Services.AddHostedService<ScanTimeoutSweeper>();

// Add HttpContextAccessor for accessing user claims
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        context.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred creating the database.");
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
else
{
    // Use HSTS even in development for training purposes
    app.UseHsts();
}

// Add security headers
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    
    // Content Security Policy for Blazor Server
    context.Response.Headers["Content-Security-Policy"] = 
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
        "font-src 'self' https://cdn.jsdelivr.net; " +
        "img-src 'self' data: https:; " +
        "connect-src 'self' wss: ws:;";
    
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Enable authentication and authorization
app.UseAuthentication();
app.UseAuthorization();

app.MapDocumentEndpoints();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
