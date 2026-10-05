using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Domain.Tests.Configuration;

public sealed class ConfigurationDependencyBoundaryTests
{
    [Fact]
    public void Domain_configuration_has_no_json_serializer_dependency()
    {
        var references = typeof(TriggerActionDefinition).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => reference.Name == "System.Text.Json");
    }
}
