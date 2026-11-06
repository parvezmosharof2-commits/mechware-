using System.IO;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using ShopVerse.Api.Middleware;

namespace ShopVerse.UnitTests.Documents;

/// <summary>A body over the request limit is refused by Kestrel or the form reader before any action runs; it must be a 413, not a 500.</summary>
public class OversizeRequestMappingTests
{
    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<(int Status, string Body)> RunAsync(Exception thrown)
    {
        var middleware = new GlobalExceptionHandlingMiddleware(_ => throw thrown, NullLogger<GlobalExceptionHandlingMiddleware>.Instance, new ProductionEnvironment());
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task AKestrelBodyOverTheLimit_Is413()
    {
        var (status, body) = await RunAsync(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));
        status.Should().Be(413);
        body.Should().Contain("FILE_TOO_LARGE");
    }

    [Fact]
    public async Task AMultipartBodyOverTheFormLimit_Is413()
    {
        var (status, body) = await RunAsync(new InvalidDataException("Multipart body length limit 67108864 exceeded."));
        status.Should().Be(413);
        body.Should().Contain("FILE_TOO_LARGE");
    }

    [Fact]
    public async Task AnotherBadRequest_KeepsItsOwnStatus_NotA500()
    {
        var (status, body) = await RunAsync(new BadHttpRequestException("Malformed request.", StatusCodes.Status400BadRequest));
        status.Should().Be(400);
        body.Should().Contain("BAD_REQUEST").And.NotContain("Malformed");
    }

    [Fact]
    public async Task AnUnrelatedInvalidData_StaysA500_AndLeaksNothing()
    {
        var (status, body) = await RunAsync(new InvalidDataException("Something else is wrong with /secret/path"));
        status.Should().Be(500);
        body.Should().NotContain("secret");
    }
}
