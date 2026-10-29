using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Davids.Services.Customers.Api;

public static class CustomersOpenApiExtensions
{
    private static JsonNode[] AllowedStates =>
    [
        JsonValue.Create("valid"),
        JsonValue.Create("incomplete"),
        JsonValue.Create("suspicious"),
        JsonValue.Create("locked")
    ];

    public static IServiceCollection AddCustomersOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Davids Customers API";
                document.Info.Version = "1.0.0";
                document.Info.Description =
                    "Customer profile, registration completion, and administrator customer management endpoints protected by dual RS256 OIDC authorities.";

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["BearerAuth"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "RS256 OIDC access token (CustomerBearer aud: davids-services or AdminBearer aud: davids-admin)."
                };

                if (document.Components.Schemas is not null)
                {
                    if (document.Components.Schemas.TryGetValue("CompleteCustomerRegistration", out var completeRef) &&
                        completeRef is OpenApiSchema completeSchema &&
                        completeSchema.Properties is not null)
                    {
                        completeSchema.Required = new HashSet<string>(StringComparer.Ordinal) { "fullName", "address" };
                        ConfigureNonEmptyString(completeSchema.Properties, "fullName", 200);
                        ConfigureNonEmptyString(completeSchema.Properties, "address", 500);
                    }

                    if (document.Components.Schemas.TryGetValue("UpdateCustomer", out var updateRef) &&
                        updateRef is OpenApiSchema updateSchema &&
                        updateSchema.Properties is not null)
                    {
                        updateSchema.Required = new HashSet<string>(StringComparer.Ordinal) { "fullName", "address" };
                        ConfigureNonEmptyString(updateSchema.Properties, "fullName", 200);
                        ConfigureNonEmptyString(updateSchema.Properties, "address", 500);

                        if (updateSchema.Properties.TryGetValue("email", out var emailProp) && emailProp is OpenApiSchema emailSchema)
                        {
                            emailSchema.Type = JsonSchemaType.String | JsonSchemaType.Null;
                            emailSchema.MinLength = 6;
                            emailSchema.MaxLength = 254;
                            emailSchema.Pattern = @"^[A-Za-z0-9_%+\-]{1,20}(?:\.[A-Za-z0-9_%+\-]{1,20}){0,2}@(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,30}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$";
                        }

                        if (updateSchema.Properties.TryGetValue("state", out var stateProp) && stateProp is OpenApiSchema stateSchema)
                        {
                            stateSchema.Type = JsonSchemaType.String | JsonSchemaType.Null;
                            stateSchema.Enum = [(JsonNode?)null!, .. AllowedStates];
                        }
                    }
                }

                foreach (var (_, pathItem) in document.Paths ?? [])
                {
                    foreach (var (_, operation) in pathItem.Operations ?? [])
                    {
                        if (operation.RequestBody is OpenApiRequestBody reqBody)
                        {
                            reqBody.Required = true;
                        }

                        foreach (var parameter in operation.Parameters ?? [])
                        {
                            if (string.Equals(parameter.Name, "state", StringComparison.Ordinal) &&
                                parameter.Schema is OpenApiSchema paramSchema)
                            {
                                paramSchema.Enum = parameter.In == ParameterLocation.Query
                                    ? [JsonValue.Create(string.Empty), .. AllowedStates]
                                    : [.. AllowedStates];
                            }
                        }

                        if (operation.Security is { Count: > 0 })
                        {
                            operation.Security =
                            [
                                new OpenApiSecurityRequirement
                                {
                                    [new OpenApiSecuritySchemeReference("BearerAuth", document)] = []
                                }
                            ];
                        }
                    }
                }

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var requiresAuth = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();

                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("429", new OpenApiResponse { Description = "Rate limit exceeded." });

                if (requiresAuth)
                {
                    operation.Security = [new OpenApiSecurityRequirement()];
                    operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing or invalid RS256 Bearer access token." });
                    operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Caller lacks permission for this customer resource." });
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    private static void ConfigureNonEmptyString(IDictionary<string, IOpenApiSchema> properties, string name, int maxLength)
    {
        if (properties.TryGetValue(name, out var prop) && prop is OpenApiSchema schema)
        {
            schema.Type = JsonSchemaType.String;
            schema.MinLength = 1;
            schema.MaxLength = maxLength;
            schema.Pattern = @"^[^\u0000]*\S[^\u0000]*$";
        }
    }
}
