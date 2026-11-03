using System.Security.Claims;
using Davids.ServiceDefaults;
using Davids.Services.Customers.Api;
using Davids.Services.Customers.Api.Exceptions;
using Davids.Services.Customers.Application.Commands;
using Davids.Services.Customers.Application.DTO;
using Davids.Services.Customers.Application.Services;
using Davids.Services.Customers.Infrastructure;
using Davids.Services.Customers.Infrastructure.Auth;
using Davids.Services.Customers.Infrastructure.Persistence;
using Davids.Services.Customers.Infrastructure.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
await builder.Configuration.LoadVaultAsync();
builder.AddServiceDefaults();
builder.Services.AddTrustedForwardedHeaders(builder.Configuration, builder.Environment);
builder.Services.AddCustomersInfrastructure(builder.Configuration);

var customerOidc = builder.Configuration.GetSection("CustomerOidc").Get<OidcAuthoritySettings>()
    ?? new OidcAuthoritySettings { Audience = "davids-services" };
customerOidc.Audience ??= "davids-services";
var customerStaticKey = customerOidc.ValidateAndResolveStaticPublicKey("CustomerOidc");

var adminOidc = builder.Configuration.GetSection("AdminOidc").Get<OidcAuthoritySettings>()
    ?? new OidcAuthoritySettings { Audience = "davids-admin" };
adminOidc.Audience ??= "davids-admin";
var adminStaticKey = adminOidc.ValidateAndResolveStaticPublicKey("AdminOidc");

var requireHttpsMetadata =
    builder.Environment.IsProduction() ||
    !(builder.Environment.IsDevelopment() ||
      builder.Environment.IsEnvironment("Testing") ||
      builder.Configuration.GetValue<bool>("Http:AllowInsecureDevelopment"));

builder.Services.AddAuthentication(CustomersInfrastructureExtensions.CustomerScheme)
    .AddJwtBearer(CustomersInfrastructureExtensions.CustomerScheme, options =>
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = requireHttpsMetadata;
        if (!string.IsNullOrWhiteSpace(customerOidc.MetadataAddress))
        {
            options.MetadataAddress = customerOidc.MetadataAddress;
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(customerOidc.EffectiveIssuer),
            ValidIssuer = customerOidc.EffectiveIssuer,
            ValidateAudience = true,
            ValidAudience = customerOidc.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = customerStaticKey,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var role = context.Principal?.FindFirstValue("role");
                if (!string.Equals(role, DavidsRoles.Customer, StringComparison.Ordinal))
                {
                    context.Fail("CustomerBearer accepts customer ('user') tokens only.");
                }

                return Task.CompletedTask;
            }
        };
    })
    .AddJwtBearer(CustomersInfrastructureExtensions.AdminScheme, options =>
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = requireHttpsMetadata;
        if (!string.IsNullOrWhiteSpace(adminOidc.MetadataAddress))
        {
            options.MetadataAddress = adminOidc.MetadataAddress;
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(adminOidc.EffectiveIssuer),
            ValidIssuer = adminOidc.EffectiveIssuer,
            ValidateAudience = true,
            ValidAudience = adminOidc.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = adminStaticKey,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var role = context.Principal?.FindFirstValue("role");
                if (!DavidsRoles.CanReadWorkforce(role))
                {
                    context.Fail("AdminBearer accepts workforce roles (admin, support, auditor) only.");
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CustomerOnly", policy =>
    {
        policy.AddAuthenticationSchemes(CustomersInfrastructureExtensions.CustomerScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("role", DavidsRoles.Customer);
    })
    .AddPolicy("WorkforceRead", policy =>
    {
        policy.AddAuthenticationSchemes(CustomersInfrastructureExtensions.AdminScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("role", DavidsRoles.Admin, DavidsRoles.Support, DavidsRoles.Auditor);
    })
    .AddPolicy("WorkforceMutate", policy =>
    {
        policy.AddAuthenticationSchemes(CustomersInfrastructureExtensions.AdminScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("role", DavidsRoles.Admin, DavidsRoles.Support);
    })
    .AddPolicy("AdminOnly", policy =>
    {
        policy.AddAuthenticationSchemes(CustomersInfrastructureExtensions.AdminScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("role", DavidsRoles.Admin);
    })
    .AddPolicy("CustomerOrAdmin", policy =>
    {
        policy.AddAuthenticationSchemes(
            CustomersInfrastructureExtensions.AdminScheme,
            CustomersInfrastructureExtensions.CustomerScheme);
        policy.RequireAuthenticatedUser();
    });

builder.Services.AddExceptionHandler<CustomerApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddCustomersOpenApi();

var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    if (!app.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("Automatic migration is development-only.");
    }

    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<CustomersDbContext>().Database.MigrateAsync();
}

app.UseTrustedForwardedHeaders();
app.UseExceptionHandler();
if (!(app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Http:AllowInsecureDevelopment")))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "docs";
        options.SwaggerEndpoint("../openapi/v1.json", "Davids Customers v1");
    });
}

static CustomerActorMetadata ResolveCustomerActor(ClaimsPrincipal principal, HttpContext httpContext)
{
    var actor = DavidsActorContext.FromPrincipal(principal, httpContext);
    return new CustomerActorMetadata(
        actor.ActorSub,
        actor.ActorEmail,
        actor.ActorRole,
        actor.ActorAuthority,
        actor.CorrelationId);
}

app.MapGet("/", () => Results.Text("Davids Customers Service"))
    .ExcludeFromDescription()
    .AllowAnonymous();

app.MapGet("/customers", async (
    bool? isVip,
    bool? vipOnly,
    bool? vip,
    ICustomersService service,
    CancellationToken ct,
    string? search = null,
    string? query = null,
    string? state = null) =>
{
    var effectiveSearch = search ?? query;
    var effectiveVip = isVip ?? vipOnly ?? vip;
    if (string.IsNullOrWhiteSpace(effectiveSearch) && string.IsNullOrWhiteSpace(state) && effectiveVip is null)
    {
        return Results.Ok(await service.GetAllAsync(ct));
    }

    return Results.Ok(await service.SearchAsync(new CustomerSearchQuery(effectiveSearch, state, effectiveVip), ct));
})
.Produces<IReadOnlyList<CustomerDto>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.RequireAuthorization("WorkforceRead");

app.MapGet("/customers/me", async (
    HttpContext httpContext,
    ClaimsPrincipal user,
    ICustomersService service,
    CancellationToken ct) =>
{
    if (httpContext.Request.QueryString.HasValue)
    {
        return Results.BadRequest(new { code = "unexpected_query_parameters", reason = "Self-service profile endpoint does not accept query parameters." });
    }

    if (!Guid.TryParse(user.FindFirstValue("sub"), out var callerId))
    {
        return Results.Unauthorized();
    }

    var dto = await service.GetAsync(callerId, ct);
    return Results.Ok(dto);
})
.Produces<CustomerDetailsDto>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.RequireAuthorization("CustomerOnly");

app.MapPut("/customers/me", async (
    HttpContext httpContext,
    UpdateMyCustomerProfile body,
    ClaimsPrincipal user,
    ICustomersService service,
    CancellationToken ct) =>
{
    if (httpContext.Request.QueryString.HasValue)
    {
        return Results.BadRequest(new { code = "unexpected_query_parameters", reason = "Self-service profile endpoint does not accept query parameters." });
    }

    if (body is null)
    {
        return Results.BadRequest(new { code = "invalid_request", reason = "Request body is required." });
    }

    if (!Guid.TryParse(user.FindFirstValue("sub"), out var callerId))
    {
        return Results.Unauthorized();
    }

    var actor = ResolveCustomerActor(user, httpContext);
    var updated = await service.UpdateSelfServiceAsync(callerId, body, actor, ct);
    return Results.Ok(updated);
})
.Produces<CustomerDetailsDto>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.RequireAuthorization("CustomerOnly");

app.MapGet("/customers/change-audits", async (
    int? limit,
    Guid? targetCustomerId,
    string? actorSub,
    ICustomersService service,
    CancellationToken ct) =>
{
    if (limit is < 1 or > 500)
    {
        return Results.BadRequest(new { code = "invalid_limit", reason = "Limit must be between 1 and 500." });
    }

    var entries = await service.BrowseChangeAuditsAsync(limit ?? 100, targetCustomerId, actorSub, ct);
    return Results.Ok(entries);
})
.Produces<IReadOnlyList<CustomerProfileChangeAuditDto>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.RequireAuthorization("WorkforceRead");

app.MapGet("/customers/{customerId:guid}", async (
    Guid customerId,
    HttpContext httpContext,
    ClaimsPrincipal user,
    ICustomersService service,
    CancellationToken ct) =>
{
    if (httpContext.Request.QueryString.HasValue)
    {
        return Results.BadRequest(new { code = "unexpected_query_parameters", reason = "Resource identifier endpoint does not accept query parameters." });
    }

    var isWorkforce = DavidsRoles.CanReadWorkforce(user.FindFirstValue("role"));
    var isOwner = Guid.TryParse(user.FindFirstValue("sub"), out var callerId) && callerId == customerId;
    if (!isWorkforce && !isOwner)
    {
        return Results.Forbid();
    }

    var dto = await service.GetAsync(customerId, ct);
    return dto is null ? Results.NotFound() : Results.Ok(dto);
})
.Produces<CustomerDetailsDto>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.RequireAuthorization("CustomerOrAdmin");

app.MapGet("/customers/{customerId:guid}/state", async (
    Guid customerId,
    HttpContext httpContext,
    ClaimsPrincipal user,
    ICustomersService service,
    CancellationToken ct) =>
{
    if (httpContext.Request.QueryString.HasValue)
    {
        return Results.BadRequest(new { code = "unexpected_query_parameters", reason = "Customer state endpoint does not accept query parameters." });
    }

    var isWorkforce = DavidsRoles.CanReadWorkforce(user.FindFirstValue("role"));
    var isOwner = Guid.TryParse(user.FindFirstValue("sub"), out var callerId) && callerId == customerId;
    if (!isWorkforce && !isOwner)
    {
        return Results.Forbid();
    }

    return Results.Ok(await service.GetStateAsync(customerId, ct));
})
.Produces<CustomerStateDto>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.RequireAuthorization("CustomerOrAdmin");

app.MapGet("/customers/{customerId:guid}/change-audits", async (
    Guid customerId,
    int? limit,
    ClaimsPrincipal user,
    ICustomersService service,
    CancellationToken ct) =>
{
    if (limit is < 1 or > 200)
    {
        return Results.BadRequest(new { code = "invalid_limit", reason = "Limit must be between 1 and 200." });
    }

    var isWorkforce = DavidsRoles.CanReadWorkforce(user.FindFirstValue("role"));
    var isOwner = Guid.TryParse(user.FindFirstValue("sub"), out var callerId) && callerId == customerId;
    if (!isWorkforce && !isOwner)
    {
        return Results.Forbid();
    }

    return Results.Ok(await service.BrowseChangeAuditsAsync(limit ?? 50, customerId, actorSub: null, ct));
})
.Produces<IReadOnlyList<CustomerProfileChangeAuditDto>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.RequireAuthorization("CustomerOrAdmin");

app.MapPost("/customers", async (
    CompleteCustomerRegistration command,
    HttpContext httpContext,
    ClaimsPrincipal user,
    ICustomersService service,
    CancellationToken ct) =>
{
    if (command is null)
    {
        return Results.BadRequest(new { code = "invalid_request", reason = "Request body is required." });
    }

    if (!Guid.TryParse(user.FindFirstValue("sub"), out var callerId))
    {
        return Results.Unauthorized();
    }

    var effective = command.CustomerId == Guid.Empty ? command with { CustomerId = callerId } : command;
    if (effective.CustomerId != callerId)
    {
        return Results.Forbid();
    }

    var actor = ResolveCustomerActor(user, httpContext);
    await service.CompleteRegistrationAsync(effective, ct, actor);
    return Results.Created($"customers/{effective.CustomerId}", (object?)null);
})
.Produces(StatusCodes.Status201Created)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict)
.RequireAuthorization("CustomerOnly");

app.MapPut("/customers/{customerId:guid}", async (
    Guid customerId,
    HttpContext httpContext,
    UpdateCustomer body,
    ClaimsPrincipal user,
    ICustomersService service,
    CancellationToken ct) =>
{
    if (httpContext.Request.QueryString.HasValue)
    {
        return Results.BadRequest(new { code = "unexpected_query_parameters", reason = "Customer update endpoint does not accept query parameters." });
    }

    if (body is null)
    {
        return Results.BadRequest(new { code = "invalid_request", reason = "Request body is required." });
    }

    if (body.CustomerId != Guid.Empty && body.CustomerId != customerId)
    {
        return Results.BadRequest(new { code = "customer_id_mismatch", reason = "Payload customerId must match route customerId when specified." });
    }

    var actor = ResolveCustomerActor(user, httpContext);
    var updated = await service.UpdateAsync(body with { CustomerId = customerId }, ct, actor);
    return Results.Ok(updated);
})
.Produces<CustomerDetailsDto>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict)
.RequireAuthorization("WorkforceMutate");

app.MapPut("/customers/{customerId:guid}/state/{state}", async (
    Guid customerId,
    string state,
    HttpContext httpContext,
    ClaimsPrincipal user,
    ICustomersService service,
    CancellationToken ct) =>
{
    if (httpContext.Request.QueryString.HasValue)
    {
        return Results.BadRequest(new { code = "unexpected_query_parameters", reason = "Customer state transition endpoint does not accept query parameters." });
    }

    var actor = ResolveCustomerActor(user, httpContext);
    await service.ChangeStateAsync(customerId, state, ct, actor);
    return Results.NoContent();
})
.Produces(StatusCodes.Status204NoContent)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.RequireAuthorization("WorkforceMutate");

app.Run();
public partial class Program;
