using Api.Data.Entities;
using EfCoreRepository;

namespace Api.Data.Profiles;

public class EncryptedFileProfile : EntityProfile<EncryptedFile>
{
    public EncryptedFileProfile()
    {
        MapAll();
    }
}
