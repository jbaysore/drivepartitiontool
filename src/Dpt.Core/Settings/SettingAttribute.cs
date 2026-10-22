namespace Dpt.Core.Settings;

/// <summary>
/// Marks an <see cref="AppSettings"/> property as user-visible. The Settings page is generated from these
/// attributes, so adding a behavior switch only needs a property with this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string section, string label, string description) : Attribute
{
    public string Section { get; } = section;
    public string Label { get; } = label;
    public string Description { get; } = description;

    /// <summary>Optional: lower numbers appear first within a section.</summary>
    public int Order { get; init; }

    /// <summary>Optional: shown in the "Advanced" area of its section.</summary>
    public bool Advanced { get; init; }
}
