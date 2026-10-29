using System.Text.Json;

namespace Bold.UpgradeCenter.Services;

internal static class UpgradeCenterConfigurationParser
{
    internal static UpgradeCenterRuntimeConfiguration Parse(
        JsonElement root,
        UpgradeCenterRuntimeConfiguration? fallback = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Product configuration must be a JSON object.");
        }

        fallback ??= new UpgradeCenterRuntimeConfiguration(null, null, null);
        return new UpgradeCenterRuntimeConfiguration(
            FirstConfiguredValue(GetProperty(root, "InternalAppUrls", "Idp"), fallback.IdpBaseUrl)?.TrimEnd('/'),
            FirstConfiguredValue(GetProperty(root, "InternalAppUrls", "Bi"), fallback.BiBaseUrl)?.TrimEnd('/'),
            FirstConfiguredValue(GetBoldProductVersion(root, "BoldBI"), fallback.BoldBiVersion),
            GetProperty(root, "SqlConfiguration", "ConnectionString"),
            GetIntegerProperty(root, "SqlConfiguration", "ServerType"),
            GetProperty(root, "MachineKey", "DecryptionKey"),
            GetRootProperty(root, "InternalAppClientId"),
            GetRootProperty(root, "InternalAppClientSecret"),
            GetBooleanRootProperty(root, "UseSingleTenantDb") ?? GetBooleanProperty(root, "SystemSettings", "UseSingleTenantDb"),
            FirstConfiguredValue(GetProperty(root, "SqlConfiguration", "SchemaName"), fallback.MasterDatabaseSchemaName),
            FirstConfiguredValue(GetProperty(root, "SqlConfiguration", "Prefix"), fallback.MasterDatabaseTablePrefix),
            GetBooleanRootProperty(root, "ShowDataHub"),
            FirstConfiguredValue(
                GetProperty(root, "InternalAppUrls", "Reports"),
                GetProperty(root, "InternalAppUrls", "Report"),
                GetProperty(root, "InternalAppUrls", "Reporting"),
                fallback.ReportsBaseUrl)?.TrimEnd('/'),
            FirstConfiguredValue(GetBoldProductVersion(root, "BoldReports"), fallback.BoldReportsVersion));
    }

    private static string? GetProperty(JsonElement root, string parentName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, parentName, out var parent) ||
            parent.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(parent, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static string? GetRootProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static int? GetIntegerProperty(JsonElement root, string parentName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, parentName, out var parent) ||
            parent.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(parent, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String ? MapDatabaseTypeName(value.GetString()) : null;
    }

    private static bool? GetBooleanRootProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, propertyName, out var value))
        {
            return null;
        }

        return ReadBooleanValue(value);
    }

    private static bool? GetBooleanProperty(JsonElement root, string parentName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, parentName, out var parent) ||
            parent.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(parent, propertyName, out var value))
        {
            return null;
        }

        return ReadBooleanValue(value);
    }

    private static bool? ReadBooleanValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.String &&
               bool.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static int? MapDatabaseTypeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

        return normalized switch
        {
            "mssql" or "sqlserver" or "microsoftsqlserver" => 0,
            "mysql" or "mariadb" => 1,
            "mssqlce" => 2,
            "oracle" or "ora" => 3,
            "postgresql" or "postgres" or "pgsql" => 4,
            _ => null
        };
    }

    private static string? GetBoldProductVersion(JsonElement root, string productName)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, "BoldProducts", out var boldProducts))
        {
            return null;
        }

        if (TryReadBoldProductVersion(boldProducts, productName, out var version))
        {
            return version;
        }

        if (boldProducts.ValueKind == JsonValueKind.Object &&
            TryGetProperty(boldProducts, "BoldProduct", out var boldProduct) &&
            TryReadBoldProductVersion(boldProduct, productName, out version))
        {
            return version;
        }

        return null;
    }

    private static bool TryReadBoldProductVersion(JsonElement element, string productName, out string? version)
    {
        version = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            version = IsBoldProduct(element, productName) && TryGetProperty(element, "Version", out var versionElement)
                ? versionElement.GetString()
                : null;
            return !string.IsNullOrWhiteSpace(version);
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (IsBoldProduct(item, productName) && TryGetProperty(item, "Version", out var versionElement))
                {
                    version = versionElement.GetString();
                    return !string.IsNullOrWhiteSpace(version);
                }
            }
        }

        return false;
    }

    private static bool IsBoldProduct(JsonElement element, string productName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               TryGetProperty(element, "Name", out var nameElement) &&
               string.Equals(nameElement.GetString(), productName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? FirstConfiguredValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
