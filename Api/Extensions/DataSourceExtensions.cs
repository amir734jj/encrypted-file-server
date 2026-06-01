using Api.Data.Entities;
using Shared.Interfaces;

namespace Api.Extensions;

public static class DataSourceExtensions
{
    public static BackendConnectionInfo ToBackendConnectionInfo(this DataSource ds) =>
        new(ds.BackendFtpHost, ds.BackendFtpPort, ds.BackendFtpUsername, ds.BackendFtpPassword, ds.BackendFtpBasePath, ds.BackendFtpUseSsl);
}
