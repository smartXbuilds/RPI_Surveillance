using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RpiCam.Resources;
using RpiCam.Services;

namespace RpiCam;

public sealed record LoginForm(string? Username, string? Password, string? ReturnUrl);

public static class Endpoints
{
    public const int MaxZipImages = 200;

    /// <summary>Cookie login/logout. Login is a plain form post because a Blazor circuit cannot set cookies.</summary>
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/account/login", async (
            HttpContext ctx,
            IAntiforgery antiforgery,
            IOptions<AuthOptions> auth,
            LoginThrottle throttle) =>
        {
            // Validated by hand (the endpoint opts out below): with the automatic check, a stale login
            // page ends in an unhandled exception and a 500, here it is a normal "please retry".
            try
            {
                await antiforgery.ValidateRequestAsync(ctx);
            }
            catch (AntiforgeryValidationException)
            {
                return LoginRedirect("expired", "/");
            }

            var posted = await ctx.Request.ReadFormAsync();
            var form = new LoginForm(posted["Username"], posted["Password"], posted["ReturnUrl"]);
            var returnUrl = SafeReturnUrl(form.ReturnUrl);
            var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (string.IsNullOrEmpty(auth.Value.User) || string.IsNullOrEmpty(auth.Value.Password))
            {
                return LoginRedirect("config", returnUrl);
            }
            if (throttle.IsLocked(client))
            {
                return LoginRedirect("locked", returnUrl);
            }

            // Both values are always compared, so timing does not reveal which one was wrong.
            var userOk = FixedTimeEquals(form.Username, auth.Value.User);
            var passwordOk = FixedTimeEquals(form.Password, auth.Value.Password);
            if (!(userOk & passwordOk))
            {
                throttle.RegisterFailure(client);
                await Task.Delay(1000);
                return LoginRedirect("invalid", returnUrl);
            }

            throttle.Reset(client);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, auth.Value.User)], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
            return Results.LocalRedirect(returnUrl);
        }).DisableAntiforgery().AllowAnonymous();

        // GET on purpose: the navigation button is a plain link. The worst a forged request can do is log you out.
        app.MapGet("/account/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync();
            return Results.Redirect("/login");
        }).AllowAnonymous();
    }

    /// <summary>Image, thumbnail and ZIP download. All of these require the login (fallback policy).</summary>
    public static void MapImageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/thumbs/{name}", async (string name, ImageStore store, HttpContext ctx, CancellationToken ct) =>
        {
            var path = await store.GetThumbnailPathAsync(name, ct);
            if (path is null)
            {
                return Results.NotFound();
            }
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(path, "image/jpeg");
        });

        // Registered before {name}; a literal segment wins anyway, and "zip" is no valid image name.
        app.MapGet("/api/images/zip", (
            [FromQuery(Name = "name")] string[] names,
            ImageStore store,
            IOptions<RpiCamOptions> options) =>
        {
            var existing = names.Distinct().Take(MaxZipImages)
                .Select(n => (Name: n, Path: store.ResolveImagePath(n)))
                .Where(x => x.Path is not null)
                .ToList();
            if (existing.Count == 0)
            {
                return Results.NotFound();
            }

            // Built in a file below the data directory: /tmp is RAM on DietPi and the Pi has 512 MB.
            var tempDir = Path.Combine(Path.GetFullPath(options.Value.DataDir), "tmp");
            Directory.CreateDirectory(tempDir);
            var temp = new FileStream(
                Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".zip"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            using (var zip = new ZipArchive(temp, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, path) in existing)
                {
                    // JPEGs are already compressed, deflating them again only costs CPU.
                    zip.CreateEntryFromFile(path!, name, CompressionLevel.NoCompression);
                }
            }
            temp.Position = 0;
            return Results.File(temp, "application/zip", $"rpicam-{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        });

        app.MapGet("/api/images/{name}", (string name, bool? download, ImageStore store) =>
        {
            var path = store.ResolveImagePath(name);
            return path is null
                ? Results.NotFound()
                : Results.File(path, "image/jpeg", download == true ? name : null, enableRangeProcessing: true);
        });
    }

    /// <summary>Motion-triggered video clip download.</summary>
    public static void MapMotionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/motion/clips/{name}", (string name, bool? download, ClipStore clips) =>
        {
            var path = clips.ResolvePath(name);
            return path is null
                ? Results.NotFound()
                : Results.File(path, "video/mp4", download == true ? name : null, enableRangeProcessing: true);
        });
    }

    /// <summary>
    /// The live view: rpicam-vid piped through ffmpeg into a fragmented MP4, relayed straight to the
    /// response body. Nothing is written to disk. Only one viewer at a time (the camera gate), a
    /// second request is told the camera is busy instead of queueing indefinitely.
    /// </summary>
    public static void MapStreamEndpoints(this WebApplication app)
    {
        var log = app.Logger;
        app.MapGet("/api/stream/live", async (HttpContext ctx, CameraGate gate, IVideoPipeline video, LiveStreamSession session) =>
        {
            if (!gate.TryAcquire(out var handle))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync(Locals.LiveBusy);
                return;
            }
            using var acquired = handle;

            // The Live page's Stop button cancels this token directly (same process, Blazor
            // Server): removing the <video> element alone does not reliably abort the underlying
            // HTTP connection quickly enough, verified on the device (rpicam-vid/ffmpeg kept
            // running for a long time afterwards). RequestAborted stays linked in as a fallback
            // for a closed tab or lost connection.
            var sessionToken = session.Begin();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, sessionToken);
            var ct = linked.Token;

            ILiveStream? stream = null;
            try
            {
                stream = await video.StartLiveAsync(ct);
                ctx.Response.ContentType = "video/mp4";
                ctx.Response.Headers.CacheControl = "no-store";
                await stream.Output.CopyToAsync(ctx.Response.Body, ct);
            }
            catch (OperationCanceledException)
            {
                // The viewer stopped the stream or navigated away; nothing to report.
            }
            catch (CameraException ex)
            {
                log.LogWarning("Live stream failed: {Message}", ex.Message);
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsync(ex.Message);
                }
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync();
                }
                session.End(sessionToken);
            }
        });
    }

    private static bool FixedTimeEquals(string? provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided ?? "")),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    /// <summary>Only same-site paths are allowed as return target, never an absolute or protocol-relative URL.</summary>
    private static string SafeReturnUrl(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";

    private static IResult LoginRedirect(string error, string returnUrl) =>
        Results.Redirect($"/login?error={error}&returnUrl={Uri.EscapeDataString(returnUrl)}");
}
