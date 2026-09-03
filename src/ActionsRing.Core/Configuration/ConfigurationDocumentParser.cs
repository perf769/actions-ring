using System.Text.Json;
using System.Text.Json.Nodes;

namespace ActionsRing.Core.Configuration;

public sealed record ParsedConfigurationDocument(
    ActionsRingConfiguration Configuration,
    bool WasMigrated,
    IReadOnlyList<ConfigurationIssue> Issues);

/// <summary>Reads, migrates, normalizes, and validates an Actions Ring settings document.</summary>
public static class ConfigurationDocumentParser
{
    public const long MaximumFileSizeBytes = 8 * 1024 * 1024;

    public static ParsedConfigurationDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumFileSizeBytes)
        {
            throw new InvalidDataException($"Configuration data exceeds {MaximumFileSizeBytes} bytes.");
        }

        var node = JsonNode.Parse(json, NodeOptions(), DocumentOptions());
        return Materialize(node);
    }

    public static async Task<ParsedConfigurationDocument> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length > MaximumFileSizeBytes)
        {
            throw new InvalidDataException($"Configuration data exceeds {MaximumFileSizeBytes} bytes.");
        }

        JsonNode? node;
        if (stream.CanSeek)
        {
            node = await JsonNode.ParseAsync(
                    stream,
                    NodeOptions(),
                    DocumentOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            using var bounded = await BufferBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            node = await JsonNode.ParseAsync(
                    bounded,
                    NodeOptions(),
                    DocumentOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return Materialize(node);
    }

    private static async Task<MemoryStream> BufferBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream(capacity: 64 * 1024);
        try
        {
            var block = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaximumFileSizeBytes)
                {
                    throw new InvalidDataException(
                        $"Configuration data exceeds {MaximumFileSizeBytes} bytes.");
                }

                await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static ParsedConfigurationDocument Materialize(JsonNode? node)
    {
        var migration = ConfigurationMigrator.Migrate(node);
        var configuration = migration.Document.Deserialize<ActionsRingConfiguration>(ConfigurationJson.Options)
                            ?? throw new InvalidDataException("Configuration document is JSON null.");
        var normalized = ConfigurationNormalizer.Normalize(configuration);
        var validation = ConfigurationValidator.Validate(normalized.Configuration);
        if (!validation.IsValid)
        {
            throw new InvalidDataException("Configuration remains invalid after normalization.");
        }

        return new ParsedConfigurationDocument(
            normalized.Configuration,
            migration.WasMigrated,
            normalized.Issues);
    }

    private static JsonNodeOptions NodeOptions() => new() { PropertyNameCaseInsensitive = true };

    private static JsonDocumentOptions DocumentOptions() => new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128,
    };
}
