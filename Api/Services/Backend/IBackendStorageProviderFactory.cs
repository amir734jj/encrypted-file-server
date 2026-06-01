using Shared.Contracts;
using Shared.Interfaces;

namespace Api.Services.Backend;

public interface IBackendStorageProviderFactory
{
    IBackendStorageProvider GetProvider(BackendStorageType type);
}