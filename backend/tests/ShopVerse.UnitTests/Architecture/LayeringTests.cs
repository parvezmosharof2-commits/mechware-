using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using ShopVerse.Application.Inventory;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.UnitTests.Architecture;

/// <summary>
/// Enforces the Clean Architecture dependency rule (brief §6.1): Domain ← Application ← Infrastructure ← Api.
/// Uses the compiled assembly references, which only list assemblies whose types are actually used.
/// </summary>
public class LayeringTests
{
    private static readonly Assembly Domain = typeof(Money).Assembly;
    private static readonly Assembly Application = typeof(IInventoryService).Assembly;
    private static readonly Assembly Infrastructure = typeof(ShopVerseDbContext).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;

    private static IEnumerable<string> References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

    private static IEnumerable<string> Violations(Assembly assembly, params string[] forbiddenPrefixes) =>
        References(assembly).Where(r => forbiddenPrefixes.Any(p => r.Equals(p, StringComparison.Ordinal) || r.StartsWith(p + ".", StringComparison.Ordinal)));

    [Fact]
    public void Domain_DependsOnNoOtherLayer_AndNoFrameworkOrIo()
    {
        Violations(Domain,
                "ShopVerse.Application", "ShopVerse.Infrastructure", "ShopVerse.Api", "ShopVerse.Payment", "ShopVerse.Worker",
                "Microsoft.EntityFrameworkCore", "Npgsql", "StackExchange.Redis", "Microsoft.AspNetCore", "System.Net.Http",
                "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging")
            .Should().BeEmpty("the domain model must stay pure and I/O-free");
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrDelivery()
    {
        Violations(Application,
                "ShopVerse.Infrastructure", "ShopVerse.Api", "ShopVerse.Payment", "ShopVerse.Worker",
                "Npgsql", "StackExchange.Redis", "Microsoft.AspNetCore", "RedLockNet")
            .Should().BeEmpty("use cases talk to ports, not to concrete adapters");
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnApi()
    {
        Violations(Infrastructure, "ShopVerse.Api", "ShopVerse.Worker").Should().BeEmpty();
    }

    [Fact]
    public void Controllers_OnlyInjectApplicationPortsOrFrameworkServices()
    {
        var controllers = Api.GetTypes().Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t)).ToList();
        controllers.Should().NotBeEmpty();

        var offending = controllers
            .SelectMany(c => c.GetConstructors().SelectMany(ctor => ctor.GetParameters())
                .Where(p => p.ParameterType.Assembly == Infrastructure)
                .Select(p => $"{c.Name}({p.ParameterType.Name} {p.Name})"))
            .ToList();

        offending.Should().BeEmpty("controllers must depend on Application interfaces, never on Infrastructure types such as the DbContext");
    }

    [Fact]
    public void Controllers_AreSealed_AndLiveInTheControllersNamespace()
    {
        var controllers = Api.GetTypes().Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t)).ToList();
        controllers.Where(c => !c.IsSealed).Select(c => c.Name).Should().BeEmpty();
        controllers.Where(c => c.Namespace is null || !c.Namespace.EndsWith(".Controllers", StringComparison.Ordinal))
            .Select(c => c.FullName).Should().BeEmpty();
    }

    [Fact]
    public void DomainExceptions_DeriveFromDomainException()
    {
        var exceptions = Domain.GetTypes().Where(t => typeof(Exception).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        exceptions.Should().NotBeEmpty();
        exceptions.Where(t => !typeof(DomainException).IsAssignableFrom(t)).Select(t => t.FullName)
            .Should().BeEmpty("the API error middleware maps DomainException subclasses to 4xx; anything else becomes a 500");
    }

    [Fact]
    public void Money_IsAnImmutableValueType()
    {
        typeof(Money).IsValueType.Should().BeTrue();
        typeof(Money).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && !p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit"))
            .Select(p => p.Name).Should().BeEmpty();
    }
}
