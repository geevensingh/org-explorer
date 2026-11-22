namespace OrgExplorer.Workflow;

public sealed record PersonCategory
{
    internal PersonCategory(string id, string displayName, string pluralDisplayName, string? emailPrefix = null)
    {
        Id = id;
        DisplayName = displayName;
        PluralDisplayName = pluralDisplayName;
        EmailPrefix = emailPrefix;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string PluralDisplayName { get; }
    internal string? EmailPrefix { get; }
}

public static class PersonCategories
{
    public static PersonCategory Employee { get; } = new("employee", "Employee", "Employees");
    public static PersonCategory Vendor { get; } = new("vendor", "Vendor", "Vendors", "v-");
    public static IReadOnlyList<PersonCategory> All { get; } = Array.AsReadOnly(new[] { Employee, Vendor });

    internal static PersonCategory FromEmail(string email) =>
        All.FirstOrDefault(category => category.EmailPrefix is { } prefix &&
            email.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? Employee;
}
