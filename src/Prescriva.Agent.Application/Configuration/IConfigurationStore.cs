using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Configuration;

public interface IConfigurationStore
{
    Task<IntegrationConfiguration> LoadAsync(string id, CancellationToken cancellationToken);

    Task SaveAsync(IntegrationConfiguration configuration, CancellationToken cancellationToken);
}
