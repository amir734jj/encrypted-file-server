using Api.Interfaces;

namespace Api.Data.Entities;

public sealed class GlobalConfig : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
