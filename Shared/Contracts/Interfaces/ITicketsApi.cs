using Refit;

namespace Shared.Contracts.Interfaces;

[Headers("Authorization: Bearer")]
public interface ITicketsApi
{
    [Get("/api/tickets")]
    Task<List<AccessTicketDto>> GetAllAsync();

    [Post("/api/tickets")]
    Task<AccessTicketCreatedDto> CreateAsync([Body] CreateAccessTicketRequest request);

    [Delete("/api/tickets/{id}")]
    Task DeleteAsync(Guid id);
}
