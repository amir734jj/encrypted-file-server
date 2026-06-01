using Api.Data.Entities;
using Shared.Interfaces;

namespace Api.Extensions;

public static class DataSourceExtensions
{
    public static BackendConnectionInfo ToBackendConnectionInfo(this DataSource ds) =>
        new(ds.Backend.Host, ds.Backend.Port, ds.Backend.Username, ds.Backend.Password, ds.Backend.BasePath, ds.Backend.UseSsl, ds.Backend.Protocol);
}
