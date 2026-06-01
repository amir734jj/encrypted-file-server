using Api.Data.Entities;
using EfCoreRepository;

namespace Api.Data.Profiles;

public class AccessTicketProfile : EntityProfile<AccessTicket>
{
    public AccessTicketProfile()
    {
        MapAll(t => t.User!);
    }
}
