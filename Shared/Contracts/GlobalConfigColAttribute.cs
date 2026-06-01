namespace Shared.Contracts;

[AttributeUsage(AttributeTargets.Property)]
public sealed class GlobalConfigColAttribute : Attribute
{
    public required string Name { get; set; }
    public Type? OnChangeHandler { get; set; }
}
