using Api.Interfaces;
using Shared.Interfaces;
using Shared.Models;

namespace Api.Services.Encryption;

public sealed class EncryptionProviderFactory : IEncryptionProviderFactory
{
    private readonly Dictionary<string, IEncryptionProvider> _providers;

    private static readonly Dictionary<EncryptionMethod, string> MethodToKey = new()
    {
        [EncryptionMethod.AesCtr256] = "aes-ctr-256",
        [EncryptionMethod.AesGcm256] = "aes-gcm-256",
        [EncryptionMethod.ChaCha20Poly1305] = "chacha20-poly1305",
    };

    public EncryptionProviderFactory(IEnumerable<IEncryptionProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderKey, StringComparer.OrdinalIgnoreCase);
    }

    public IEncryptionProvider GetProvider(EncryptionMethod method)
    {
        if (MethodToKey.TryGetValue(method, out var key) && _providers.TryGetValue(key, out var provider))
            return provider;
        throw new ArgumentException($"Unknown encryption method: {method}. Available: {string.Join(", ", MethodToKey.Keys)}");
    }

    public IReadOnlyList<EncryptionMethod> AvailableProviders => MethodToKey.Keys.ToList();
}
