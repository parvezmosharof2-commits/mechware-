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
        if (context.Response.HasStarted)
        {
            _logger.LogWarning("The response has already started, cannot write problem details for exception: {Message}", exception.Message);
            return;
        }

        string traceId = context.TraceIdentifier;

        var (statusCode, errorCode, message, details) = exception switch
        {
            ValidationException valEx => (
                HttpStatusCode.BadRequest,
                valEx.Code,
                valEx.Message,
                valEx.Errors.Select(e => (object)new { field = e.Field, message = e.Message }).ToArray()
            ),
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => (
                HttpStatusCode.Conflict,
                "CONCURRENT_MODIFICATION",
                "The record was modified by someone else. Reload and retry.",
                Array.Empty<object>()
            ),
            ForbiddenException forbiddenEx => (
                HttpStatusCode.Forbidden,
                forbiddenEx.Code,
                forbiddenEx.Message,
                Array.Empty<object>()
            ),
            ConflictException conflictEx => (
                HttpStatusCode.Conflict,
                conflictEx.Code,
                conflictEx.Message,
                Array.Empty<object>()
            ),
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
