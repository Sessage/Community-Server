using System.ComponentModel.DataAnnotations;

namespace Klassenbibliothek.Localization;

/// <summary>Resolves validation messages at validation time, including after a language change.</summary>
public abstract class LocalizedValidationAttribute(ValidationAttribute rule, string resourceKey, params object[] arguments) : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context)
        => rule.IsValid(value) ? ValidationResult.Success : new ValidationResult(SharedText.Get(resourceKey, arguments),
            context.MemberName is { } name ? [name] : null);
}

public sealed class LocalizedRequiredAttribute(string key) : LocalizedValidationAttribute(new RequiredAttribute(), key);
public sealed class LocalizedMaxLengthAttribute(int maximum, string key) : LocalizedValidationAttribute(new MaxLengthAttribute(maximum), key);
public sealed class LocalizedMinLengthAttribute(int minimum, string key) : LocalizedValidationAttribute(new MinLengthAttribute(minimum), key);
public sealed class LocalizedEmailAddressAttribute(string key) : LocalizedValidationAttribute(new EmailAddressAttribute(), key);

public sealed class LocalizedStringLengthAttribute(int maximum, int minimum, string key)
    : LocalizedValidationAttribute(new StringLengthAttribute(maximum) { MinimumLength = minimum }, key, minimum, maximum);

public sealed class LocalizedCompareAttribute(string otherProperty, string key) : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context)
        => Equals(value, context.ObjectType.GetProperty(otherProperty)?.GetValue(context.ObjectInstance))
            ? ValidationResult.Success : new ValidationResult(SharedText.Get(key),
                context.MemberName is { } name ? [name] : null);
}
