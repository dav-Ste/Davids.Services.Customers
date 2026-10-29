using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Davids.Services.Customers.Application.DTO;
using Davids.Services.Customers.Application.Events;
using Davids.Services.Customers.Application.Services;
using Davids.Services.Customers.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Davids.Services.Customers.Tests;

[Trait("Category", "Integration")]
public sealed class CustomersTests
{
    [Fact]
    public async Task SignedUpProvisioningAndCompletionPreserveCustomerContract()
    {
        using var factory = new CustomersFactory();
        using var client = factory.CreateCustomersClient();
        var customerId = Guid.NewGuid();
        await factory.SeedIncompleteCustomerAsync(customerId, "Jane.Doe@Example.com");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateCustomerToken(customerId));
        var initial = await client.GetFromJsonAsync<CustomerDetailsDto>($"/customers/{customerId}");
        Assert.Equal("Jane.Doe@Example.com", initial!.Email);
        Assert.Equal("incomplete", initial.State);

        var complete = await client.PostAsJsonAsync("/customers", new
        {
            customerId = Guid.Empty,
            fullName = "Jane Doe",
            address = "10 Downing Street"
        });
        Assert.Equal(HttpStatusCode.Created, complete.StatusCode);
        Assert.Equal($"customers/{customerId}", complete.Headers.Location?.ToString());

        var completed = await client.GetFromJsonAsync<CustomerDetailsDto>($"/customers/{customerId}");
        Assert.Equal("valid", completed!.State);
        Assert.Equal("Jane Doe", completed.FullName);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        Assert.True(await db.Outbox.CountAsync() >= 1);
    }

    [Fact]
    public async Task ResourceOwnerCannotReadOrCompleteAnotherCustomer()
    {
        using var factory = new CustomersFactory();
        using var client = factory.CreateCustomersClient();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await factory.SeedIncompleteCustomerAsync(alice, "alice@example.com");
        await factory.SeedIncompleteCustomerAsync(bob, "bob@example.com");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateCustomerToken(alice));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/customers/{bob}")).StatusCode);

        var completeBob = await client.PostAsJsonAsync("/customers", new
        {
            customerId = bob,
            fullName = "Mallory",
            address = "123 Fake Street"
        });
        Assert.Equal(HttpStatusCode.Forbidden, completeBob.StatusCode);
    }

    [Fact]
    public async Task AdminCanBrowseUpdateAndChangeStateIncludingVipUpgradeWhileCustomersAndSymmetricTokensAreRejected()
    {
        using var factory = new CustomersFactory();
        using var client = factory.CreateCustomersClient();
        var customerId = Guid.NewGuid();
        await factory.SeedIncompleteCustomerAsync(customerId, "vip@example.com");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateCustomerToken(customerId));
        await client.PostAsJsonAsync("/customers", new
        {
            customerId,
            fullName = "VIP Candidate",
            address = "221B Baker Street"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/customers")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PutAsync($"/customers/{customerId}/state/locked", content: null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PutAsJsonAsync($"/customers/{customerId}", new
            {
                fullName = "Tampered Name",
                address = "Tampered Street",
                email = "vip@example.com",
                state = "locked"
            })).StatusCode);

        // Even an HS256 symmetric token claiming admin must be rejected because only RS256 OIDC keys are accepted.
        var hs256Attempt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: CustomersFactory.AdminIssuer,
            audience: "davids-admin",
            claims:
            [
                new Claim("sub", Guid.NewGuid().ToString("N")),
                new Claim("role", "admin"),
                new Claim("jti", Guid.NewGuid().ToString("N"))
            ],
            notBefore: DateTime.UtcNow.AddSeconds(-5),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("legacy-symmetric-key-that-must-never-be-accepted-123")),
                SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hs256Attempt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/customers")).StatusCode);

        // A customer RSA key forging role=admin is also rejected on AdminOnly endpoints.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateCustomerToken(Guid.NewGuid(), "admin"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/customers")).StatusCode);

        // Dedicated Admin Identity RS256 token succeeds.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateAdminToken(Guid.NewGuid()));
        var all = await client.GetFromJsonAsync<CustomerDto[]>("/customers");
        Assert.Single(all!);

        var filtered = await client.GetFromJsonAsync<CustomerDto[]>("/customers?query=vip@example.com");
        Assert.Single(filtered!);
        var unmatched = await client.GetFromJsonAsync<CustomerDto[]>("/customers?query=nobody@example.com");
        Assert.Empty(unmatched!);

        var updateProfile = await client.PutAsJsonAsync($"/customers/{customerId}", new
        {
            fullName = "Updated VIP Candidate",
            address = "100 Oxford Street, London",
            email = "vip@example.com",
            state = "valid"
        });
        Assert.Equal(HttpStatusCode.OK, updateProfile.StatusCode);

        var updatedDetails = await client.GetFromJsonAsync<CustomerDetailsDto>($"/customers/{customerId}");
        Assert.Equal("Updated VIP Candidate", updatedDetails!.FullName);
        Assert.Equal("100 Oxford Street, London", updatedDetails.Address);

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ICustomersService>();
            for (var i = 0; i < 20; i++)
            {
                await svc.RecordCompletedOrderAsync(new OrderCompleted(Guid.NewGuid(), customerId));
            }
        }

        var details = await client.GetFromJsonAsync<CustomerDetailsDto>($"/customers/{customerId}");
        Assert.Equal("valid", details!.State);
        Assert.True(details.IsVip);
    }

    [Fact]
    public async Task DuplicateCompletionAndInvalidAddressesReturnDomainErrors()
    {
        using var factory = new CustomersFactory();
        using var client = factory.CreateCustomersClient();
        var customerId = Guid.NewGuid();
        await factory.SeedIncompleteCustomerAsync(customerId);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateCustomerToken(customerId));

        var badName = await client.PostAsJsonAsync("/customers", new
        {
            customerId,
            fullName = "   ",
            address = "Valid Street 1"
        });
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/customers", new
            {
                customerId,
                fullName = "John Smith",
                address = "Valid Street 1"
            })).StatusCode);

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/customers", new
            {
                customerId,
                fullName = "John Smith",
                address = "Valid Street 1"
            })).StatusCode);
    }

    [Fact]
    public async Task OpenApiContractAndQueryVsPathStateEnumsEnforceStagingDastInvariants()
    {
        using var baseFactory = new CustomersFactory();
        using var nonDevClient = baseFactory.CreateCustomersClient();
        Assert.Equal(HttpStatusCode.NotFound, (await nonDevClient.GetAsync("/openapi/v1.json")).StatusCode);

        using var devFactory = baseFactory.WithWebHostBuilder(wb => wb.UseEnvironment("Development"));
        using var client = devFactory.CreateClient();

        var openApiResp = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApiResp.StatusCode);

        using var doc = System.Text.Json.JsonDocument.Parse(await openApiResp.Content.ReadAsStringAsync());
        Assert.Equal("3.1.1", doc.RootElement.GetProperty("openapi").GetString());
        Assert.True(doc.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("BearerAuth", out _));

        // GET /customers query 'state' allows empty string ("") for unfiltered Admin SPA requests, while PUT path 'state' does not.
        var getParams = doc.RootElement.GetProperty("paths").GetProperty("/customers").GetProperty("get").GetProperty("parameters");
        var queryStateEnum = new List<string>();
        foreach (var param in getParams.EnumerateArray())
        {
            if (param.GetProperty("name").GetString() == "state")
            {
                foreach (var val in param.GetProperty("schema").GetProperty("enum").EnumerateArray())
                {
                    queryStateEnum.Add(val.GetString()!);
                }
            }
        }
        Assert.Contains("", queryStateEnum);
        Assert.Contains("valid", queryStateEnum);

        var putParams = doc.RootElement.GetProperty("paths").GetProperty("/customers/{customerId}/state/{state}")
            .GetProperty("put").GetProperty("parameters");
        var pathStateEnum = new List<string>();
        foreach (var param in putParams.EnumerateArray())
        {
            if (param.GetProperty("name").GetString() == "state")
            {
                foreach (var val in param.GetProperty("schema").GetProperty("enum").EnumerateArray())
                {
                    pathStateEnum.Add(val.GetString()!);
                }
            }
        }
        Assert.DoesNotContain("", pathStateEnum);
        Assert.Equal(4, pathStateEnum.Count);

        // Request bodies on mutating operations explicitly require non-null payloads in OpenAPI 3.1.
        Assert.True(doc.RootElement.GetProperty("paths").GetProperty("/customers").GetProperty("post")
            .GetProperty("requestBody").GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task RestlerPathTruncationQueryInjectionAndPutBodyIdMismatchAreRejected()
    {
        using var factory = new CustomersFactory();
        using var client = factory.CreateCustomersClient();
        var customerId = Guid.NewGuid();
        var otherCustomerId = Guid.NewGuid();
        await factory.SeedIncompleteCustomerAsync(customerId, "release.customer@example.com");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateCustomerToken(customerId));

        // Baseline: valid path-only requests succeed.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/customers/{customerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/customers/{customerId}/state")).StatusCode);

        // RESTler InvalidDynamicObjectChecker regression: injected query strings on path-only endpoints must return 400.
        var truncatedState = await client.GetAsync($"/customers/{customerId}?injected_query_string=123/state");
        Assert.Equal(HttpStatusCode.BadRequest, truncatedState.StatusCode);
        Assert.Contains("unexpected_query_parameters", await truncatedState.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/customers/{customerId}??")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/customers/{customerId}/state?injected_query_string=123")).StatusCode);

        // Admin mutation endpoints also reject unexpected query parameters and mismatched body CustomerId.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateAdminToken(Guid.NewGuid()));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PutAsync($"/customers/{customerId}/state/suspended?injected_query_string=123", content: null)).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"/customers/{customerId}?injected_query_string=123", new
            {
                customerId,
                fullName = "Release Customer",
                address = "42 Security Gate Way"
            })).StatusCode);

        var mismatch = await client.PutAsJsonAsync($"/customers/{customerId}", new
        {
            customerId = otherCustomerId,
            fullName = "Release Customer",
            address = "42 Security Gate Way"
        });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("customer_id_mismatch", await mismatch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/customers/{customerId}", new
            {
                customerId,
                fullName = "Release Customer",
                address = "42 Security Gate Way"
            })).StatusCode);
    }
}
