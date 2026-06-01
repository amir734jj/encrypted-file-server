using Shared.Models;

namespace Shared.Contracts;

public record FrontendRequest(
    FrontendType Type,
    bool AllowAnonymous = false);