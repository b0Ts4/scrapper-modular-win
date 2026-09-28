using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Infrastructure.Configuration;

public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _directory;

    public JsonConfigurationStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public async Task<IntegrationConfiguration> LoadAsync(string id, CancellationToken cancellationToken)
    {
        var path = PathFor(id);
        IntegrationConfiguration? configuration;
        try
        {
            await using var stream = File.OpenRead(path);
            configuration = await JsonSerializer.DeserializeAsync<IntegrationConfiguration>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ConfigurationValidationException([new("MALFORMED_JSON", "$", "Configuration JSON is malformed or contains unsupported members.")]);
        }

        var validation = ConfigurationValidator.Validate(configuration);
        if (configuration is not null && !string.Equals(configuration.Id, id, StringComparison.Ordinal))
            throw new ConfigurationValidationException([new("CONFIGURATION_ID_MISMATCH", "id", "Configuration ID does not match the requested ID.")]);
        if (!validation.IsValid) throw new ConfigurationValidationException(validation.Errors);
        return configuration!;
    }

    public async Task SaveAsync(IntegrationConfiguration configuration, CancellationToken cancellationToken)
    {
        var validation = ConfigurationValidator.Validate(configuration);
        if (!validation.IsValid) throw new ConfigurationValidationException(validation.Errors);

        var path = PathFor(configuration.Id);
        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string PathFor(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            throw new ConfigurationValidationException([new("INVALID_CONFIGURATION_ID", "id", "Configuration ID cannot be used as a file name.")]);
        return Path.Combine(_directory, id + ".json");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new ImmutableArrayConverterFactory());
        return options;
    }

    private sealed class ImmutableArrayConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableArray<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ImmutableArrayConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }

    private sealed class ImmutableArrayConverter<T> : JsonConverter<ImmutableArray<T>>
    {
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return default;
            var items = JsonSerializer.Deserialize<T[]>(ref reader, options);
            return items?.ToImmutableArray() ?? default;
        }

        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
        {
            if (value.IsDefault) writer.WriteNullValue();
            else JsonSerializer.Serialize(writer, value.ToArray(), options);
        }
    }
}
