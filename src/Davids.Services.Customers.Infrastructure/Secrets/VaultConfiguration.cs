using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Davids.Services.Customers.Infrastructure.Secrets;

public static class VaultConfiguration
{
    // Resource servers validate RS256 tokens via public OIDC JWKS discovery and never store signing keys in Vault.
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConnectionStrings:customersdb",
        "ConnectionStrings:messaging",
        "LoggingSinks:Seq:ApiKey",
        "LoggingSinks:Elasticsearch:ApiKey"
    };

    public static async Task LoadVaultAsync(this ConfigurationManager configuration, CancellationToken ct = default)
    {
        if (!configuration.GetValue<bool>("Vault:Enabled"))
        {
            return;
        }

        var address = configuration["Vault:Address"]
            ?? throw new InvalidOperationException("Vault:Address is required.");
        var token = configuration["Vault:Token"];
        var tokenFile = configuration["Vault:TokenFile"];
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            token = (await File.ReadAllTextAsync(tokenFile, ct)).Trim();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Vault token or Vault Agent token file is required.");
        }

        var path = configuration["Vault:KvPath"] ?? "secret/data/davids/customers";
        using var client = new HttpClient
        {
            BaseAddress = new Uri(address.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Add("X-Vault-Token", token);

        using var response = await client.GetAsync("v1/" + path.TrimStart('/'), ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var values = Flatten(document.RootElement.GetProperty("data").GetProperty("data"));
        var unknown = values.Keys.FirstOrDefault(key => !AllowedKeys.Contains(key));
        if (unknown is not null)
        {
            throw new InvalidOperationException($"Unsupported Vault setting: {unknown}");
        }

        configuration.AddInMemoryCollection(values);
    }

    public static Dictionary<string, string?> Flatten(JsonElement root)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonElement element, string prefix)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, string.IsNullOrEmpty(prefix) ? property.Name : prefix + ":" + property.Name);
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                result[prefix] = element.GetString();
            }
            else
            {
                throw new InvalidOperationException("Vault settings must contain string leaves.");
            }
        }

        Walk(root, string.Empty);
        return result;
    }
}
