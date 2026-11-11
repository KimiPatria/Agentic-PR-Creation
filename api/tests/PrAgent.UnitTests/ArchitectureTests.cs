using System.Reflection;
using PrAgent.Application;
using PrAgent.Application.Sap;

namespace PrAgent.UnitTests;

/// <summary>
/// Guards the design decision that SAP is written to from exactly one place, after approval.
/// If this test fails, something new has gained the ability to create PRs in SAP.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(PrAgent.Domain.DomainException).Assembly,
        typeof(PrCreationWorker).Assembly,
        typeof(PrAgent.Infrastructure.OutboxDispatcher).Assembly,
        typeof(PrAgent.Api.SapReadTools).Assembly,
        typeof(PrAgent.Sap.Mock.MockSapPrWriter).Assembly,
        typeof(PrAgent.Sap.S4Hana.S4HanaPrWriter).Assembly,
    ];

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static bool IsWriter(Type type) => typeof(ISapPrWriter).IsAssignableFrom(type);

    /// <summary>The adapter classes themselves, and the closures the compiler nests inside them.</summary>
    private static bool IsInsideAdapter(Type? type)
    {
        for (; type is not null; type = type.DeclaringType)
            if (IsWriter(type)) return true;
        return false;
    }

    [Fact]
    public void Only_the_creation_worker_depends_on_the_sap_write_port()
    {
        var dependents = ProductionAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !IsInsideAdapter(t))
            .Where(t =>
                t.GetConstructors(All).SelectMany(c => c.GetParameters()).Any(p => IsWriter(p.ParameterType))
                || t.GetFields(All).Any(f => IsWriter(f.FieldType))
                || t.GetProperties(All).Any(p => IsWriter(p.PropertyType))
                || t.GetMethods(All).SelectMany(m => m.GetParameters()).Any(p => IsWriter(p.ParameterType)))
            .Select(t => t.FullName)
            .ToList();

        Assert.Equal([typeof(PrCreationWorker).FullName], dependents);
    }

    [Fact]
    public void The_agent_tools_cannot_reach_anything_that_writes()
    {
        // The MCP tool class may depend on read-side services only.
        var allowed = new[] { typeof(MasterDataService), typeof(EvaluationService) };
        var parameters = typeof(PrAgent.Api.SapReadTools).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();

        Assert.All(parameters, p => Assert.Contains(p, allowed));

        // And every tool it exposes is declared read-only.
        var tools = typeof(PrAgent.Api.SapReadTools).GetMethods()
            .Select(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>())
            .Where(a => a is not null)
            .ToList();
        Assert.Equal(3, tools.Count);
        Assert.All(tools, t => Assert.True(t!.ReadOnly));
    }

    [Fact]
    public void Read_side_services_do_not_depend_on_the_creation_worker()
    {
        foreach (var service in new[] { typeof(MasterDataService), typeof(EvaluationService), typeof(RequestService) })
        {
            var parameters = service.GetConstructors().Single().GetParameters().Select(p => p.ParameterType);
            Assert.DoesNotContain(typeof(PrCreationWorker), parameters);
        }
    }
}
