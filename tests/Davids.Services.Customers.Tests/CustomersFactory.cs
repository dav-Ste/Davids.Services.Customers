using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Davids.Services.Customers.Application.Events;
using Davids.Services.Customers.Application.Services;
using Davids.Services.Customers.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Davids.Services.Customers.Tests;

public sealed class CustomersFactory : WebApplicationFactory<Program>
{
    public const string CustomerIssuer = "https://identity.davids.test/";
    public const string AdminIssuer = "https://admin-identity.davids.test/";

    private static readonly RSA CustomerRsa = RSA.Create(2048);
    private static readonly RSA AdminRsa = RSA.Create(2048);

    public static readonly string CustomerPublicKeyPem = CustomerRsa.ExportSubjectPublicKeyInfoPem();
    public static readonly string AdminPublicKeyPem = AdminRsa.ExportSubjectPublicKeyInfoPem();

    private static readonly RsaSecurityKey CustomerPrivateKey = CreateSigningKey(CustomerRsa);
    private static readonly RsaSecurityKey AdminPrivateKey = CreateSigningKey(AdminRsa);

    private readonly SqliteConnection connection = new("Data Source=:memory:");

    public CustomersFactory() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("CustomerOidc:Issuer", CustomerIssuer);
        builder.UseSetting("CustomerOidc:Audience", "davids-services");
        builder.UseSetting("CustomerOidc:PublicKeyPem", CustomerPublicKeyPem);
        builder.UseSetting("AdminOidc:Issuer", AdminIssuer);
        builder.UseSetting("AdminOidc:Audience", "davids-admin");
        builder.UseSetting("AdminOidc:PublicKeyPem", AdminPublicKeyPem);
        builder.UseSetting("Vault:Enabled", "false");
        builder.UseSetting("Messaging:Enabled", "false");
        builder.UseSetting("Database:ApplyMigrations", "false");
        builder.UseSetting("ConnectionStrings:customersdb", "unused");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<CustomersDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<CustomersDbContext>>();
            services.RemoveAll<CustomersDbContext>();
            services.AddDbContext<CustomersDbContext>(options => options.UseSqlite(connection));
        });
    }

    public HttpClient CreateCustomersClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CustomersDbContext>().Database.EnsureCreated();
        return client;
    }

    public async Task SeedIncompleteCustomerAsync(Guid id, string email = "customer@example.com")
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICustomersService>()
            .CreateFromSignedUpAsync(new SignedUp(id, email, "user"));
    }

    public static string CreateCustomerToken(Guid subject, string role = "user")
        => WriteRs256Token(subject, role, CustomerIssuer, "davids-services", CustomerPrivateKey);

    public static string CreateAdminToken(Guid subject, string role = "admin")
        => WriteRs256Token(subject, role, AdminIssuer, "davids-admin", AdminPrivateKey);

    private static string WriteRs256Token(
        Guid subject,
        string role,
        string issuer,
        string audience,
        RsaSecurityKey signingKey)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims:
            [
                new Claim("sub", subject.ToString("N")),
                new Claim("role", role),
                new Claim("jti", Guid.NewGuid().ToString("N"))
            ],
            notBefore: now.AddSeconds(-5),
            expires: now.AddMinutes(30),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static RsaSecurityKey CreateSigningKey(RSA rsa)
    {
        var kid = Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16];
        return new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: true)) { KeyId = kid };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            connection.Dispose();
        }
    }
}
