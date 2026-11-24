using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// Shorthands for building model metadata, each giving exactly what the matching SDK constructor gave (same JSON), so the seed and
/// the stores write the same definitions as before (S.2b2).
/// </summary>
internal static class ModelFactory
{
    /// <summary>As <c>new Label(text, languageCode)</c>: one localized label, no user-localized label.</summary>
    public static LabelSet Label(string text, int languageCode) =>
        new() { LocalizedLabels = [new LocalizedText { Label = text, LanguageCode = languageCode }] };

    /// <summary>As <c>new BooleanManagedProperty(value)</c>.</summary>
    public static ManagedFlag Flag(bool value) => new() { Value = value };

    /// <summary>As <c>new AttributeRequiredLevelManagedProperty(level)</c>.</summary>
    public static RequiredLevelSetting Required(RequirementLevel level) => new() { Value = level };

    /// <summary>As <c>new OptionMetadata(label, value)</c>: no <c>ExternalValue</c>, unlike the parameterless constructor.</summary>
    public static ChoiceOption Option(LabelSet label, int value) => new() { Label = label, Value = value, ExternalValue = null };

    /// <summary>As the SDK's <c>DateTimeBehavior.UserLocal</c> and the like (<see cref="DateTimeBehaviors"/>).</summary>
    public static DateTimeBehaviorName Behavior(string value) => new() { Value = value };
}

/// <summary>The <c>DateTimeBehavior</c> values (the SDK's <c>DateTimeBehavior</c> instances).</summary>
internal static class DateTimeBehaviors
{
    public const string UserLocal = "UserLocal", DateOnly = "DateOnly", TimeZoneIndependent = "TimeZoneIndependent";
}
