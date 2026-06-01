using Shared.Interfaces;
using Shared.Models;

namespace Api.Interfaces;

public interface IEncryptionProviderFactory
{
    IEncryptionProvider GetProvider(EncryptionMethod method);
    IReadOnlyList<EncryptionMethod> AvailableProviders { get; }
}
