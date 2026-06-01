using Api.Data.Entities;
using EfCoreRepository;

namespace Api.Data.Profiles;

public class DataSourceProfile : EntityProfile<DataSource>
{
    public DataSourceProfile()
    {
        MapAll(d => d.Backend, d => d.Frontends, d => d.User!);
    }
}
