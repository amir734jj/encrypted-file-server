using System.Security.Cryptography;
using Api.Data;
using Api.Data.Entities;
using Api.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;

namespace Api.Controllers;

[ApiController]
[Route("api/tickets")]
[Authorize]
public sealed class TicketsController(AppDbContext db) : ControllerBase
{
    private Guid CurrentUserId => User.GetUserId();

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var tickets = await db.AccessTickets
            .Where(t => t.UserId == CurrentUserId && t.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new AccessTicketDto(t.Id, t.Username, t.CreatedAt, t.ExpiresAt))
            .ToListAsync();

        return Ok(tickets);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAccessTicketRequest req)
    {
        if (req.ExpiresAt <= DateTimeOffset.UtcNow)
            return BadRequest("Expiration must be in the future.");

        var username = GenerateRandomString(12);
        var password = GenerateRandomString(24);

        var ticket = new AccessTicket
        {
            UserId = CurrentUserId,
            Username = username,
            Password = password,
            ExpiresAt = req.ExpiresAt
        };

        db.AccessTickets.Add(ticket);
        await db.SaveChangesAsync();

        return Ok(new AccessTicketCreatedDto(ticket.Id, username, password, ticket.CreatedAt, ticket.ExpiresAt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var ticket = await db.AccessTickets
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == CurrentUserId);

        if (ticket is null)
            return NotFound();

        db.AccessTickets.Remove(ticket);
        await db.SaveChangesAsync();
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
