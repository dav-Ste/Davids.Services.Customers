using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Davids.Services.Customers.Application.Events;
using Davids.Services.Customers.Application.Services;
using Davids.Services.Customers.Infrastructure;
using Davids.Services.Customers.Infrastructure.Auth;
using Davids.Services.Customers.Infrastructure.Persistence;
using Davids.Services.Customers.Infrastructure.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Davids.Services.Customers.Tests;

public sealed class CustomersInfrastructureTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SignedUpProvisioningRejectsDuplicateCreationOrNonCustomerRole()
    {
        using var factory = new CustomersFactory();
        using var _ = factory.CreateCustomersClient();
        var customerId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICustomersService>();
        await service.CreateFromSignedUpAsync(new SignedUp(customerId, "repeat@example.com", "user"));
        await Assert.ThrowsAsync<Davids.Services.Customers.Core.Exceptions.CustomerAlreadyCreatedException>(() =>
            service.CreateFromSignedUpAsync(new SignedUp(customerId, "repeat@example.com", "user")));
        await Assert.ThrowsAsync<Davids.Services.Customers.Core.Exceptions.InvalidCustomerRoleException>(() =>
            service.CreateFromSignedUpAsync(new SignedUp(Guid.NewGuid(), "admin@example.com", "admin")));

        var db = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        Assert.Equal(1, await db.Customers.CountAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApiIsServedInDevelopmentAndHiddenOutsideDevelopment()
    {
        using var devFactory = new CustomersFactory().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var devClient = devFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        Assert.True((await devClient.GetAsync("/alive")).IsSuccessStatusCode);
        Assert.Contains("openapi", await (await devClient.GetAsync("/openapi/v1.json")).Content.ReadAsStringAsync());

        using var testFactory = new CustomersFactory();
        using var testClient = testFactory.CreateCustomersClient();
        Assert.Equal(HttpStatusCode.NotFound, (await testClient.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OidcSettingsAndVaultRejectPrivateOrSymmetricSigningKeys()
    {
        using var rsa = RSA.Create(2048);
        var privateKeySettings = new OidcAuthoritySettings
        {
            Audience = "davids-services",
            PublicKeyPem = rsa.ExportPkcs8PrivateKeyPem()
        };
        Assert.Throws<InvalidOperationException>(() =>
            privateKeySettings.ValidateAndResolveStaticPublicKey("CustomerOidc"));

        using var doc = JsonDocument.Parse(
            """{"ConnectionStrings":{"customersdb":"Server=sql"},"LoggingSinks":{"Seq":{"ApiKey":"seq-key"}}}""");
        var values = VaultConfiguration.Flatten(doc.RootElement);
        Assert.Equal("Server=sql", values["ConnectionStrings:customersdb"]);
        Assert.Equal("seq-key", values["LoggingSinks:Seq:ApiKey"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveOidcJwksDiscoveryValidatesRs256TokensWithoutSharedSecrets()
    {
        using var jwksRsa = RSA.Create(2048);
        var parameters = jwksRsa.ExportParameters(includePrivateParameters: true);
        var kid = "jwks-test-kid-01";
        var signingKey = new RsaSecurityKey(parameters) { KeyId = kid };

        var discoveryJson = """
            {
              "issuer": "https://oidc-authority.test/",
              "jwks_uri": "https://oidc-authority.test/.well-known/jwks",
              "id_token_signing_alg_values_supported": ["RS256"]
            }
            """;
        var jwksJson = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent)
                }
            }
        });

        using var factory = new CustomersFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("CustomerOidc:Authority", "https://oidc-authority.test");
            b.UseSetting("CustomerOidc:Issuer", "https://oidc-authority.test/");
            b.UseSetting("CustomerOidc:PublicKeyPem", "");
            b.ConfigureServices(services =>
            {
                services.Configure<JwtBearerOptions>(CustomersInfrastructureExtensions.CustomerScheme, o =>
                {
                    o.BackchannelHttpHandler = new StubJwksHandler(discoveryJson, jwksJson);
                    o.TokenValidationParameters.IssuerSigningKey = null;
                });
            });
        });

        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var customerId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            db.Database.EnsureCreated();
            await scope.ServiceProvider.GetRequiredService<ICustomersService>()
                .CreateFromSignedUpAsync(new SignedUp(customerId, "jwks@example.com", "user"));
        }

        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://oidc-authority.test/",
            audience: "davids-services",
            claims:
            [
                new Claim("sub", customerId.ToString("N")),
                new Claim("role", "user"),
                new Claim("jti", Guid.NewGuid().ToString("N"))
            ],
            notBefore: DateTime.UtcNow.AddSeconds(-5),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync($"/customers/{customerId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class StubJwksHandler(string discoveryJson, string jwksJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath switch
            {
                "/.well-known/openid-configuration" => discoveryJson,
                "/.well-known/jwks" => jwksJson,
                _ => null
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
        }
    }
}
