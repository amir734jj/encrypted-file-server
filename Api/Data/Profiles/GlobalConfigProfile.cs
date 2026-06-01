using Api.Data.Entities;
using EfCoreRepository;

namespace Api.Data.Profiles;

public class GlobalConfigProfile : EntityProfile<GlobalConfig>
{
    public GlobalConfigProfile()
    {
        MapAll();
    }
}
