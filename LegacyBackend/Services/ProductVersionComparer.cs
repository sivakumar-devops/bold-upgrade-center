namespace Bold.UpgradeCenter.Services;

public static class ProductVersionComparer
{
    public static bool TryParse(string? value, out ProductVersionParts parts)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parts = new ProductVersionParts(Array.Empty<int>());
            return false;
        }

        var parsedParts = value
            .Trim()
            .TrimStart('v', 'V')
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var number) ? number : (int?)null)
            .ToList();

        if (parsedParts.Count == 0 || parsedParts.Any(part => part is null))
        {
            parts = new ProductVersionParts(Array.Empty<int>());
            return false;
        }

        parts = new ProductVersionParts(parsedParts.Select(part => part!.Value).ToArray());
        return true;
    }

    public static ProductVersionParts Parse(string value)
    {
        return TryParse(value, out var parts) ? parts : new ProductVersionParts(Array.Empty<int>());
    }

    public static int Compare(ProductVersionParts? leftParts, ProductVersionParts? rightParts)
    {
        var left = leftParts?.Parts ?? Array.Empty<int>();
        var right = rightParts?.Parts ?? Array.Empty<int>();
        var length = Math.Max(left.Count, right.Count);

        for (var index = 0; index < length; index++)
        {
            var leftPart = index < left.Count ? left[index] : 0;
            var rightPart = index < right.Count ? right[index] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public static int Compare(string left, string right)
    {
        return Compare(Parse(left), Parse(right));
    }
}

public sealed record ProductVersionParts(IReadOnlyList<int> Parts);

public sealed class ProductVersionPartsComparer : IComparer<ProductVersionParts>
{
    public static readonly ProductVersionPartsComparer Instance = new();

    public int Compare(ProductVersionParts? x, ProductVersionParts? y)
    {
        return ProductVersionComparer.Compare(x, y);
    }
}
