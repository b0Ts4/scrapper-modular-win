namespace Prescriva.Agent.Domain.Configuration;

public sealed record ConfigurationValidationError(string Code, string Path, string Message);

public sealed record ConfigurationValidationResult(IReadOnlyList<ConfigurationValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(IEnumerable<ConfigurationValidationError> errors)
        : base("Invalid integration configuration: " + string.Join(", ", errors.Select(error => error.Code)))
    {
        Errors = errors.ToArray();
    }

    public IReadOnlyList<ConfigurationValidationError> Errors { get; }
}
