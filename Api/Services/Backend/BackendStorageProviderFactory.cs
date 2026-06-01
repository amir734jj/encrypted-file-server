using Shared.Contracts;
using Shared.Interfaces;

namespace Api.Services.Backend;

public interface IBackendStorageProviderFactory
{
    IBackendStorageProvider GetProvider(BackendStorageType type);
}

public sealed class BackendStorageProviderFactory(IEnumerable<IBackendStorageProvider> providers) : IBackendStorageProviderFactory
{
    private readonly Dictionary<BackendStorageType, IBackendStorageProvider> _providers =
        providers.ToDictionary(p => p.StorageType);

    public IBackendStorageProvider GetProvider(BackendStorageType type) =>
        _providers.TryGetValue(type, out var provider)
            ? provider
            : throw new ArgumentException($"No backend storage provider for type: {type}");
}
