namespace Shared.Contracts;

public record CreateDataSourceRequest(
    string Name,
    BackendRequest Backend,
    List<FrontendRequest> Frontends);