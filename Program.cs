using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using MudBlazor.Services;
using RpiCam;
using RpiCam.Components;
using RpiCam.Services;
using Serilog;
using SixLabors.ImageSharp.Memory;

var supportedCultures = new[]
{
    new CultureInfo("de-DE"),
    new CultureInfo("en-US"),
};

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logger) => logger
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console());

builder.Services.Configure<RpiCamOptions>(builder.Configuration.GetSection(RpiCamOptions.Section));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
var options = builder.Configuration.GetSection(RpiCamOptions.Section).Get<RpiCamOptions>() ?? new RpiCamOptions();

// The Pi has 512 MB of RAM: keep ImageSharp from holding on to large pooled buffers.
SixLabors.ImageSharp.Configuration.Default.MemoryAllocator =
    MemoryAllocator.Create(new MemoryAllocatorOptions { MaximumPoolSizeMegabytes = 16 });

// Notifications at the bottom: at the top right they would cover the "Capture now" button.
builder.Services.AddMudServices(config =>
    config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomCenter);

builder.Services.AddLocalization();
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.SupportedCultures = supportedCultures;
    o.SupportedUICultures = supportedCultures;
});

// Persist the cookie keys, otherwise everybody is logged out whenever the service restarts.
var keyDir = Path.Combine(options.DataDir, "keys");
Directory.CreateDirectory(keyDir);
builder.Services.AddDataProtection()
    .SetApplicationName("RpiCam")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDir));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "rpicam.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // The Pi serves plain HTTP (its own hotspot), so the cookie cannot be marked Secure.
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
// Everything requires a login unless it opts out with AllowAnonymous.
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<AccountStore>();

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<CameraGate>();
builder.Services.AddSingleton<CameraSettingsStore>();
builder.Services.AddSingleton<MotionSettingsStore>();
builder.Services.AddSingleton<MotionEventLog>();
builder.Services.AddSingleton<ImageStore>();
builder.Services.AddSingleton<ClipStore>();
builder.Services.AddSingleton<LiveStreamSession>();
builder.Services.AddSingleton<ISystemTimeService, SystemTimeService>();
if (options.Simulate)
{
    builder.Services.AddSingleton<ICameraService, SimulatedCameraService>();
    builder.Services.AddSingleton<INetworkService, SimulatedNetworkService>();
    builder.Services.AddSingleton<IVideoPipeline, SimulatedVideoPipeline>();
}
else
{
    builder.Services.AddSingleton<ICameraService, RpiCameraService>();
    builder.Services.AddSingleton<INetworkService, NmcliNetworkService>();
    builder.Services.AddSingleton<IVideoPipeline, RpiVideoPipeline>();
}
builder.Services.AddHostedService<CaptureScheduler>();
builder.Services.AddHostedService<HotspotBootstrapper>();
builder.Services.AddSingleton<MotionDetectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MotionDetectionService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Created right away, so an old Auth__* login is taken over at startup and not only on the first request.
if (!app.Services.GetRequiredService<AccountStore>().HasAccount)
{
    app.Logger.LogWarning("No account yet: the first visitor creates it on /setup");
}

// Leftovers of an interrupted ZIP download.
var tempDir = Path.Combine(options.DataDir, "tmp");
if (Directory.Exists(tempDir))
{
    Directory.Delete(tempDir, recursive: true);
}

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("de-DE"),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedCultures,
    // Only the language picker's cookie decides, not the browser header: the page and the
    // interactive circuit then always agree, and the default is German.
    RequestCultureProviders = [new CookieRequestCultureProvider()],
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// No HTTPS redirection: the device is only reachable over its own network, without a certificate.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapAccountEndpoints();
app.MapImageEndpoints();
app.MapMotionEndpoints();
app.MapStreamEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Sets the culture cookie and redirects back; UseRequestLocalization picks it up on the next request.
app.MapGet("Culture/Set", (HttpRequest request, string culture, string redirectUri) =>
{
    request.HttpContext.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture, culture)),
        new CookieOptions
        {
            IsEssential = true,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddYears(1),
        });

    return Results.LocalRedirect(redirectUri);
});

app.Run();
