using System.Security.Cryptography;
using Api.Data.Entities;
using Api.Extensions;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api/tickets")]
[Authorize]
public sealed class TicketsController(IEfRepository repository) : ControllerBase
{
    private IBasicCrud<AccessTicket> TicketDal => repository.For<AccessTicket>();
    private Guid CurrentUserId => User.GetUserId();

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var tickets = (await TicketDal.GetAll(
            filterExprs: [t => t.UserId == CurrentUserId && t.ExpiresAt > DateTimeOffset.UtcNow],
            orderBy: t => t.CreatedAt,
            project: t => new AccessTicketDto(t.Id, t.Username, t.CreatedAt, t.ExpiresAt))).ToList();

        return Ok(tickets);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAccessTicketRequest req)
    {
        if (req.ExpiresAt <= DateTimeOffset.UtcNow)
            return BadRequest("Expiration must be in the future.");

        var username = GenerateRandomString(12);
        var password = GenerateRandomString(24);

        var ticket = await TicketDal.Save(new AccessTicket
        {
            UserId = CurrentUserId,
            Username = username,
            Password = password,
            ExpiresAt = req.ExpiresAt
        });

        return Ok(new AccessTicketCreatedDto(ticket.Id, username, password, ticket.CreatedAt, ticket.ExpiresAt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var exists = await TicketDal.Any(
            filterExprs: [t => t.Id == id && t.UserId == CurrentUserId]);

        if (!exists)
            return NotFound();

        await TicketDal.Delete(id);
        return NoContent();
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(length, chars, static (span, c) =>
        {
            Span<byte> random = stackalloc byte[span.Length];
            RandomNumberGenerator.Fill(random);
            for (var i = 0; i < span.Length; i++)
                span[i] = c[random[i] % c.Length];
        });
    }
}
