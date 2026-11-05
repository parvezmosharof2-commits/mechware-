namespace ShopVerse.Api.Middleware;

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("X-Frame-Options", "DENY");
        headers.TryAdd("X-XSS-Protection", "0");
        headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
        headers.TryAdd("Permissions-Policy", "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()");

        var isSwagger = context.Request.Path.StartsWithSegments("/swagger") ||
                        context.Request.Path.StartsWithSegments("/openapi");
        headers.TryAdd("Content-Security-Policy", isSwagger
            ? "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'"
            : "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
        headers.TryAdd("Strict-Transport-Security", "max-age=31536000; includeSubDomains; preload");
        // Raised by the first ZAP scan (2026-11-04): other sites may not embed API responses as images or scripts.
        // same-site, because the storefront and console call the API from sibling subdomains of the same site.
        headers.TryAdd("Cross-Origin-Resource-Policy", "same-site");

        return _next(context);
    }
}
