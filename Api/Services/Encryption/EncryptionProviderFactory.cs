using Api.Interfaces;
using Shared.Interfaces;

namespace Api.Services.Encryption;

public sealed class EncryptionProviderFactory : IEncryptionProviderFactory
{
    private readonly Dictionary<string, IEncryptionProvider> _providers;

    public EncryptionProviderFactory(IEnumerable<IEncryptionProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderKey, StringComparer.OrdinalIgnoreCase);
    }

    public IEncryptionProvider GetProvider(string providerKey)
    {
        if (_providers.TryGetValue(providerKey, out var provider))
            return provider;
        throw new ArgumentException($"Unknown encryption provider: {providerKey}. Available: {string.Join(", ", _providers.Keys)}");
    }

    public IReadOnlyList<string> AvailableProviders => _providers.Keys.ToList();
}
