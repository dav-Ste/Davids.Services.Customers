using Davids.Services.Customers.Application.Services;
using Davids.Services.Customers.Core.Repositories;
using Davids.Services.Customers.Infrastructure.Auth;
using Davids.Services.Customers.Infrastructure.Messaging;
using Davids.Services.Customers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Davids.Services.Customers.Infrastructure;

public static class CustomersInfrastructureExtensions
{
    public const string CustomerScheme = "CustomerBearer";
    public const string AdminScheme = "AdminBearer";

    public static IServiceCollection AddCustomersInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var customerOidc = configuration.GetSection("CustomerOidc").Get<OidcAuthoritySettings>()
            ?? new OidcAuthoritySettings { Audience = "davids-services" };
        customerOidc.Audience ??= "davids-services";
        customerOidc.ValidateAndResolveStaticPublicKey("CustomerOidc");
        services.AddSingleton(customerOidc);

        var adminOidc = configuration.GetSection("AdminOidc").Get<OidcAuthoritySettings>()
            ?? new OidcAuthoritySettings { Audience = "davids-admin" };
        adminOidc.Audience ??= "davids-admin";
        adminOidc.ValidateAndResolveStaticPublicKey("AdminOidc");

        var connectionString = configuration.GetConnectionString("customersdb")
            ?? throw new InvalidOperationException("Missing customersdb connection.");
        services.AddDbContext<CustomersDbContext>(options => options.UseSqlServer(connectionString));

        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<ICustomersService, CustomersService>();

        if (configuration.GetValue("Messaging:Enabled", true))
        {
            services.AddHostedService<CustomersMessageWorker>();
        }

        services.AddHealthChecks().AddCheck<CustomersDatabaseHealthCheck>("customers-database");
        return services;
    }
}
