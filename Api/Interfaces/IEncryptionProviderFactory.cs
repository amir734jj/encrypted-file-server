using Shared.Interfaces;

namespace Api.Interfaces;

public interface IEncryptionProviderFactory
{
    IEncryptionProvider GetProvider(string providerKey);
    IReadOnlyList<string> AvailableProviders { get; }
}
