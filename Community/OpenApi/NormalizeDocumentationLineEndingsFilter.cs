using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace TodoSuite.Server.OpenApi;

/// <summary>Canonicalizes documentation after XML comments have been processed on the current OS.</summary>
public sealed class NormalizeDocumentationLineEndingsFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
        => new OpenApiWalker(new DocumentationVisitor()).Walk(document);

    private sealed class DocumentationVisitor : OpenApiVisitorBase
    {
        private static string? Normalize(string? text) => text?.ReplaceLineEndings("\n");

        private static void Description(object element)
        {
            if (element is IOpenApiDescribedElement described)
                described.Description = Normalize(described.Description);
        }

        public override void Visit(OpenApiInfo info)
        {
            info.Description = Normalize(info.Description);
            info.Summary = Normalize(info.Summary);
        }

        public override void Visit(OpenApiOperation operation)
        {
            operation.Description = Normalize(operation.Description);
            operation.Summary = Normalize(operation.Summary);
        }

        public override void Visit(IOpenApiPathItem pathItem)
        {
            Description(pathItem);
            if (pathItem is OpenApiPathItem item) item.Summary = Normalize(item.Summary);
        }

        public override void Visit(IOpenApiExample example)
        {
            Description(example);
            if (example is OpenApiExample item) item.Summary = Normalize(item.Summary);
            // Example values, schema defaults and enum values are API data, not documentation.
        }

        public override void Visit(IOpenApiSchema schema) => Description(schema);
        public override void Visit(IOpenApiParameter parameter) => Description(parameter);
        public override void Visit(IOpenApiRequestBody requestBody) => Description(requestBody);
        public override void Visit(IOpenApiResponse response) => Description(response);
        public override void Visit(IOpenApiHeader header) => Description(header);
        public override void Visit(IOpenApiLink link) => Description(link);
        public override void Visit(IOpenApiSecurityScheme securityScheme) => Description(securityScheme);
        public override void Visit(OpenApiTag tag) => Description(tag);
        public override void Visit(OpenApiExternalDocs externalDocs) => externalDocs.Description = Normalize(externalDocs.Description);
        public override void Visit(OpenApiServer server) => server.Description = Normalize(server.Description);
        public override void Visit(OpenApiServerVariable variable) => variable.Description = Normalize(variable.Description);
    }
}
