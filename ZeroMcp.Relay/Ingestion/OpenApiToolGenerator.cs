using System.Text.RegularExpressions;
using Microsoft.OpenApi.Models;
using ZeroMcp.Relay.Config;

namespace ZeroMcp.Relay.Ingestion;

public sealed class OpenApiToolGenerator
{
    public ToolGenerationResult Generate(ApiConfig api, OpenApiDocument document)
    {
        var result = new ToolGenerationResult();
        var prefix = string.IsNullOrWhiteSpace(api.Prefix) ? api.Name : api.Prefix;

        foreach (var pathEntry in document.Paths)
        {
            var path = pathEntry.Key;
            var item = pathEntry.Value;

            foreach (var operationEntry in item.Operations)
            {
                var method = operationEntry.Key.ToString().ToUpperInvariant();
                var operation = operationEntry.Value;

                var operationId = operation.OperationId;
                if (string.IsNullOrWhiteSpace(operationId))
                {
                    operationId = BuildFallbackOperationId(operationEntry.Key, path);
                    result.Warnings.Add(new ToolGenerationWarning(
                        "missing_operation_id",
                        $"Operation on '{method} {path}' has no operationId. Generated '{operationId}'.",
                        api.Name,
                        operationId));
                }

                var resolvedName = BuildToolName(prefix, operationId);
                if (!GlobMatcher.IsIncluded(resolvedName, api.Include, api.Exclude))
                {
                    continue;
                }

                var description = ResolveDescription(operation, resolvedName, result, api.Name, operationId);
                var schemaWarnings = new List<ToolGenerationWarning>();
                var inputSchema = BuildInputSchema(operation, document, schemaWarnings);

                foreach (var warning in schemaWarnings)
                {
                    result.Warnings.Add(warning with { ApiName = api.Name, OperationId = operationId });
                }

                result.Tools.Add(new ToolDefinition
                {
                    Name = resolvedName,
                    Description = description,
                    ApiName = api.Name,
                    OperationId = operationId,
                    HttpMethod = method,
                    Path = path,
                    InputSchema = inputSchema
                });
            }
        }

        return result;
    }

    private static string ResolveDescription(
        OpenApiOperation operation,
        string toolName,
        ToolGenerationResult result,
        string apiName,
        string operationId)
    {
        if (!string.IsNullOrWhiteSpace(operation.Summary))
        {
            return operation.Summary;
        }

        if (!string.IsNullOrWhiteSpace(operation.Description))
        {
            var description = operation.Description.Length > 200
                ? operation.Description[..200]
                : operation.Description;
            return description;
        }

        result.Warnings.Add(new ToolGenerationWarning(
            "missing_description",
            "Operation is missing summary and description; tool name is used as description.",
            apiName,
            operationId));
        return toolName;
    }

    private static Dictionary<string, object?> BuildInputSchema(
        OpenApiOperation operation,
        OpenApiDocument document,
        List<ToolGenerationWarning> warnings)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in operation.Parameters)
        {
            properties[parameter.Name] = ConvertSchema(parameter.Schema, document, warnings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (parameter.Required || parameter.In == ParameterLocation.Path)
            {
                required.Add(parameter.Name);
            }
        }

        var requestBodySchema = operation.RequestBody?.Content
            .Where(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value.Schema)
            .FirstOrDefault();

        if (requestBodySchema is not null)
        {
            if (requestBodySchema.Type?.Equals("object", StringComparison.OrdinalIgnoreCase) == true
                || requestBodySchema.Properties.Count > 0)
            {
                var resolved = ResolveSchema(requestBodySchema, document, warnings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                foreach (var property in resolved.Properties)
                {
                    properties[property.Key] = ConvertSchema(property.Value, document, warnings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                foreach (var req in resolved.Required)
                {
                    required.Add(req);
                }
            }
            else
            {
                properties["body"] = ConvertSchema(requestBodySchema, document, warnings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                required.Add("body");
            }
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static Dictionary<string, object?> ConvertSchema(
        OpenApiSchema? schema,
        OpenApiDocument document,
        List<ToolGenerationWarning> warnings,
        HashSet<string> visitedRefs)
    {
        if (schema is null)
        {
            return [];
        }

        schema = ResolveSchema(schema, document, warnings, visitedRefs);

        var output = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(schema.Type))
        {
            output["type"] = schema.Type;
        }

        if (!string.IsNullOrWhiteSpace(schema.Description))
        {
            output["description"] = schema.Description;
        }

        if (schema.Enum.Count > 0)
        {
            output["enum"] = schema.Enum
                .Select(openApiAny => openApiAny?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        if (schema.Properties.Count > 0)
        {
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in schema.Properties)
            {
                properties[property.Key] = ConvertSchema(property.Value, document, warnings, new HashSet<string>(visitedRefs, StringComparer.OrdinalIgnoreCase));
            }

            output["properties"] = properties;
        }

        if (schema.Required.Count > 0)
        {
            output["required"] = schema.Required.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        if (schema.Items is not null)
        {
            output["items"] = ConvertSchema(schema.Items, document, warnings, new HashSet<string>(visitedRefs, StringComparer.OrdinalIgnoreCase));
        }

        return output;
    }

    private static OpenApiSchema ResolveSchema(
        OpenApiSchema schema,
        OpenApiDocument document,
        List<ToolGenerationWarning> warnings,
        HashSet<string> visitedRefs)
    {
        if (schema.Reference is null || string.IsNullOrWhiteSpace(schema.Reference.Id))
        {
            return schema;
        }

        var refId = schema.Reference.Id;
        if (!visitedRefs.Add(refId))
        {
            warnings.Add(new ToolGenerationWarning(
                "schema_cycle_detected",
                $"Detected circular schema reference at '{refId}'. Using open schema '{{}}'."));
            return new OpenApiSchema();
        }

        if (!document.Components.Schemas.TryGetValue(refId, out var resolved))
        {
            warnings.Add(new ToolGenerationWarning(
                "schema_reference_missing",
                $"Schema reference '{refId}' could not be resolved."));
            return new OpenApiSchema();
        }

        return ResolveSchema(resolved, document, warnings, visitedRefs);
    }

    private static string BuildFallbackOperationId(OperationType method, string path)
    {
        var methodPart = method.ToString().ToLowerInvariant();
        var pathPart = NormalizeSymbol(path.Replace("/", "_", StringComparison.Ordinal));
        return $"{methodPart}_{pathPart}".Trim('_');
    }

    private static string BuildToolName(string prefix, string operationId)
    {
        var normalizedPrefix = NormalizeSymbol(prefix);
        var normalizedOperation = NormalizeSymbol(operationId);
        return $"{normalizedPrefix}_{normalizedOperation}".Trim('_');
    }

    private static string NormalizeSymbol(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withWordBoundaries = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1_$2");
        var normalized = Regex.Replace(withWordBoundaries, "[^a-zA-Z0-9]+", "_");
        return normalized.Trim('_').ToLowerInvariant();
    }
}
