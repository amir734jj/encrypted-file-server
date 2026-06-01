using Shared.Models;

namespace Api.Data.Entities;

public sealed class FrontendConfig
{
    public FrontendType Type { get; set; }
    public bool AllowAnonymous { get; set; }
}
