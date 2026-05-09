using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MatatuMVC.Data;
using MatatuMVC.Models;

namespace MatatuMVC.Controllers;

[Authorize]
[Route("api/[controller]")]
[ApiController]
public class MessagesController : ControllerBase
{
    private readonly MatatuContext _context;
    private readonly UserManager<User> _userManager;

    public MessagesController(MatatuContext context, UserManager<User> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    // GET api/messages/{otherUserId} - get conversation between current user and another
    [HttpGet("{otherUserId}")]
    public async Task<IActionResult> GetConversation(string otherUserId)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Unauthorized();

        var messages = await _context.Messages
            .Where(m =>
                (m.FromUserId == currentUser.Id && m.ToUserId == otherUserId) ||
                (m.FromUserId == otherUserId && m.ToUserId == currentUser.Id))
            .OrderBy(m => m.Timestamp)
            .Select(m => new {
                m.Id,
                m.Text,
                m.FromUserId,
                m.ToUserId,
                m.Timestamp,
                m.IsRead,
                IsMine = m.FromUserId == currentUser.Id
            })
            .ToListAsync();

        // Mark messages from other user as read
        var unread = await _context.Messages
            .Where(m => m.FromUserId == otherUserId && m.ToUserId == currentUser.Id && !m.IsRead)
            .ToListAsync();
        unread.ForEach(m => m.IsRead = true);
        await _context.SaveChangesAsync();

        return Ok(messages);
    }

    // POST api/messages - send a message
    [HttpPost]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageDto dto)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Unauthorized();

        var receiver = await _userManager.FindByIdAsync(dto.ToUserId);
        if (receiver == null) return NotFound("Recipient not found.");

        var message = new Message
        {
            FromUserId = currentUser.Id,
            ToUserId = dto.ToUserId,
            Text = dto.Text,
            Timestamp = DateTime.UtcNow,
            IsRead = false
        };

        _context.Messages.Add(message);
        await _context.SaveChangesAsync();

        return Ok(new {
            message.Id,
            message.Text,
            message.FromUserId,
            message.ToUserId,
            message.Timestamp,
            IsMine = true
        });
    }

    // GET api/messages/contacts - get all users you can chat with
    [HttpGet("contacts")]
    public async Task<IActionResult> GetContacts()
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Unauthorized();

        // Return all users that are not the current user
        var contacts = await _userManager.Users
            .Where(u => u.Id != currentUser.Id)
            .Select(u => new {
                u.Id,
                u.Name,
                u.Email,
                u.Role
            })
            .ToListAsync();

        return Ok(contacts);
    }

    // GET api/messages/unread-count - get total unread messages for current user
    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount()
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Unauthorized();

        var count = await _context.Messages
            .CountAsync(m => m.ToUserId == currentUser.Id && !m.IsRead);

        return Ok(new { count });
    }
}

public class SendMessageDto
{
    public string ToUserId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}
