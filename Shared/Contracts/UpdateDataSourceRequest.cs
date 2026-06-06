using Shared.Models;

namespace Shared.Contracts;

public record UpdateDataSourceRequest(
    string Name,
    BackendRequest Backend,
    List<FrontendRequest> Frontends,
    long? MaxSizeBytes = null);
