namespace HeadlessClient.Infrastructure.Config;

/// <summary>
/// Operator toons that must never appear as "online" on the public site or member lists.
/// </summary>
public static class HiddenOperators
{
    public const string Wooz = "Wooz";

    public static bool IsHiddenName(string? name, string? configured = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Equals(Wooz, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(configured)
            && name.Equals(configured.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
