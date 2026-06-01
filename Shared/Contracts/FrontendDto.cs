using Shared.Models;

namespace Shared.Contracts;

public record FrontendDto(
    FrontendType Type,
    bool AllowAnonymous);