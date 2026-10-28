using System.Net;
using System.Text.Json;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Api.Middleware;

public sealed class GlobalExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandlingMiddleware(
        RequestDelegate _next,
        ILogger<GlobalExceptionHandlingMiddleware> logger,
        IHostEnvironment env)
    {
        this._next = _next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        string traceId = context.TraceIdentifier;

        var (statusCode, errorCode, message, details) = exception switch
        {
            DomainException domainEx => (
                HttpStatusCode.BadRequest,
                domainEx.Code,
                domainEx.Message,
                Array.Empty<object>()
            ),
            DivideByZeroException divideEx => (
                HttpStatusCode.BadRequest,
                "DIVIDE_BY_ZERO",
                divideEx.Message,
                Array.Empty<object>()
            ),
            KeyNotFoundException notFoundEx => (
                HttpStatusCode.NotFound,
                "NOT_FOUND",
                notFoundEx.Message,
                Array.Empty<object>()
            ),
            UnauthorizedAccessException authEx => (
                HttpStatusCode.Unauthorized,
                "UNAUTHORIZED",
                authEx.Message,
                Array.Empty<object>()
            ),
            _ => (
                HttpStatusCode.InternalServerError,
                "INTERNAL_SERVER_ERROR",
                _env.IsDevelopment() ? exception.Message : "An unexpected server error occurred. Please contact support with the trace ID.",
                Array.Empty<object>()
            )
        };

        if (statusCode == HttpStatusCode.InternalServerError)
        {
            _logger.LogError(exception, "Unhandled Exception: {ErrorCode} - {Message} [TraceId: {TraceId}]", errorCode, exception.Message, traceId);
        }
        else
        {
            _logger.LogWarning("Handled Exception: {ErrorCode} - {Message} [TraceId: {TraceId}]", errorCode, message, traceId);
        }

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = (int)statusCode;

        var problemDetails = new
        {
            type = $"https://api.mechware.com.bd/errors/{errorCode}",
            title = message,
            status = (int)statusCode,
            detail = message,
            instance = context.Request.Path.Value,
            traceId,
            error = new
            {
                code = errorCode,
                message,
                details,
                traceId
            }
        };

        var json = JsonSerializer.Serialize(problemDetails, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await context.Response.WriteAsync(json);
    }
}
