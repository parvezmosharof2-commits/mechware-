using FluentAssertions;
using Mechware.Infrastructure.Persistence;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Mechware.UnitTests.Architecture;

/// <summary>
/// Module-boundary ratchet (A6.3). Inside the Infrastructure assembly a "module" is the first namespace segment
/// below <c>Mechware.Infrastructure</c> (Search, Catalog, Inventory, Orders, Payments, Identity, Companies,
/// Approvals, Documents, ...). A module may not use types from another module's namespace (base types, interfaces,
/// fields, properties, method signatures, locals, member references and type tokens in method bodies, attributes).
///
/// Allowed for everyone (shared targets): <c>Persistence</c> (the single DbContext, configurations, outbox),
/// <c>Security</c>, <c>Caching</c> (cross-cutting Redis helpers) and the root namespace <c>Mechware.Infrastructure</c>
/// (DependencyInjection).
/// Allowed to use any module (sources): <c>Workers</c> (hosts background jobs that orchestrate modules), the root namespace (DependencyInjection composes every module) and
/// <c>Seed</c> (dev/demo data loader).
///
/// Today's cross-module pairs are frozen in <see cref="ExistingViolations"/>. The test fails on any NEW pair, and
/// also on a listed pair that no longer occurs, so the list can only shrink (ratchet).
/// </summary>
public class ModuleBoundaryTests
{
    private const string Root = "Mechware.Infrastructure";

    private static readonly HashSet<string> SharedTargets = new(StringComparer.Ordinal) { "", "Persistence", "Security", "Caching" };
    private static readonly HashSet<string> UnrestrictedSources = new(StringComparer.Ordinal) { "", "Workers", "Seed" };

    // "From -> To" pairs that exist today. Each is an existing violation, to remove in M01.
    private static readonly HashSet<string> ExistingViolations = new(StringComparer.Ordinal)
    {
        "Approvals -> Companies", // existing violation, to remove in M01
        "Orders -> Companies", // existing violation, to remove in M01
    };

    [Fact]
    public void InfrastructureModules_DoNotUseEachOthersTypes_ExceptTheFrozenList()
    {
        var found = FindCrossModuleUses();
        var pairs = found.Select(f => $"{f.From} -> {f.To}").ToHashSet(StringComparer.Ordinal);

        var added = found.Where(f => !ExistingViolations.Contains($"{f.From} -> {f.To}")).ToList();
        var stale = ExistingViolations.Where(p => !pairs.Contains(p)).OrderBy(p => p).ToList();

        added.Select(f => $"module {f.From} uses module {f.To} ({f.Via} -> {f.Target})").Distinct().OrderBy(x => x)
            .Should().BeEmpty("a module must not use another module's Infrastructure types; go through an Application port instead");
        stale.Should().BeEmpty("these allow-listed violations are gone; remove them from ExistingViolations (ratchet)");
    }

    private static List<(string From, string To, string Via, string Target)> FindCrossModuleUses()
    {
        var path = typeof(MechwareDbContext).Assembly.Location;
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var result = new List<(string, string, string, string)>();

        foreach (var type in asm.MainModule.GetTypes())
        {
            var from = ModuleOf(type);
            if (from is null || UnrestrictedSources.Contains(from)) continue;

            foreach (var (target, via) in ReferencedTypes(type))
            {
                var to = ModuleOf(target);
                if (to is null || to == from || SharedTargets.Contains(to)) continue;
                result.Add((from, to, via, target.FullName));
            }
        }

        return result;
    }

    /// <summary>Module name of a type if it lives in the Infrastructure namespace tree; "" for the root; null otherwise.</summary>
    private static string? ModuleOf(TypeReference type)
    {
        while (type.IsNested && type.DeclaringType is not null) type = type.DeclaringType;
        var ns = type.Namespace;
        if (ns == Root) return "";
        if (!ns.StartsWith(Root + ".", StringComparison.Ordinal)) return null;
        var rest = ns[(Root.Length + 1)..];
        var dot = rest.IndexOf('.');
        return dot < 0 ? rest : rest[..dot];
    }

    private static IEnumerable<(TypeReference Type, string Via)> ReferencedTypes(TypeDefinition type)
    {
        var name = type.FullName;
        var refs = new List<(TypeReference, string)>();
        void Add(TypeReference? t, string via)
        {
            foreach (var leaf in Expand(t)) refs.Add((leaf, $"{name}.{via}"));
        }

        Add(type.BaseType, "base");
        foreach (var i in type.Interfaces) Add(i.InterfaceType, "interface");
        foreach (var a in type.CustomAttributes) Add(a.AttributeType, "attribute");
        foreach (var f in type.Fields) { Add(f.FieldType, f.Name); }
        foreach (var p in type.Properties) { Add(p.PropertyType, p.Name); }

        foreach (var m in type.Methods)
        {
            Add(m.ReturnType, m.Name);
            foreach (var p in m.Parameters) Add(p.ParameterType, m.Name);
            foreach (var a in m.CustomAttributes) Add(a.AttributeType, m.Name);
            if (!m.HasBody) continue;
            foreach (var v in m.Body.Variables) Add(v.VariableType, m.Name);
            foreach (var ins in m.Body.Instructions)
            {
                switch (ins.Operand)
                {
                    case MethodReference mr:
                        Add(mr.DeclaringType, m.Name);
                        Add(mr.ReturnType, m.Name);
                        foreach (var p in mr.Parameters) Add(p.ParameterType, m.Name);
                        if (mr is GenericInstanceMethod gim) foreach (var g in gim.GenericArguments) Add(g, m.Name);
                        break;
                    case FieldReference fr:
                        Add(fr.DeclaringType, m.Name);
                        Add(fr.FieldType, m.Name);
                        break;
                    case TypeReference tr:
                        Add(tr, m.Name);
                        break;
                }
            }
        }

        return refs;
    }

    private static IEnumerable<TypeReference> Expand(TypeReference? t)
    {
        switch (t)
        {
            case null:
            case GenericParameter:
                yield break;
            case GenericInstanceType git:
                foreach (var x in Expand(git.ElementType)) yield return x;
                foreach (var a in git.GenericArguments) foreach (var x in Expand(a)) yield return x;
                break;
            case TypeSpecification spec: // arrays, byref, pointers, modifiers
                foreach (var x in Expand(spec.ElementType)) yield return x;
                break;
            default:
                yield return t;
                break;
        }
    }
}
