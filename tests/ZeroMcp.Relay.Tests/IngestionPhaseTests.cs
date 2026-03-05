using Microsoft.OpenApi.Models;
using ZeroMcp.Relay.Config;
using ZeroMcp.Relay.Ingestion;

namespace ZeroMcp.Relay.Tests;

public sealed class IngestionPhaseTests
{
    [Fact]
    public void ToolGenerator_CreatesFallbackOperationId_AndFlattensSchema()
    {
        var document = new OpenApiDocument
        {
            Components = new OpenApiComponents(),
            Paths = new OpenApiPaths()
        };

        var schema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["amount"] = new() { Type = "integer", Description = "Charge amount" },
                ["currency"] = new() { Type = "string" }
            },
            Required = new HashSet<string> { "amount" }
        };

        var op = new OpenApiOperation
        {
            Summary = "Create a charge",
            Parameters =
            [
                new OpenApiParameter { Name = "customerId", In = ParameterLocation.Path, Required = true, Schema = new OpenApiSchema { Type = "string" } }
            ],
            RequestBody = new OpenApiRequestBody
            {
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = schema }
                }
            }
        };

        document.Paths.Add("/charges/{customerId}", new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Post] = op
            }
        });

        var api = new ApiConfig
        {
            Name = "stripe",
            Prefix = "stripe",
            Include = [],
            Exclude = []
        };

        var generator = new OpenApiToolGenerator();
        var result = generator.Generate(api, document);

        Assert.Single(result.Tools);
        var tool = result.Tools[0];
        Assert.Equal("stripe_post_charges_customer_id", tool.Name);
        Assert.Contains(result.Warnings, warning => warning.Code == "missing_operation_id");

        var properties = Assert.IsType<Dictionary<string, object?>>(tool.InputSchema["properties"]);
        Assert.Contains("customerId", properties.Keys);
        Assert.Contains("amount", properties.Keys);
        Assert.Contains("currency", properties.Keys);
    }
}
