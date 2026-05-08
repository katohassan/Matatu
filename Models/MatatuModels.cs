using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace MatatuMVC.Models;

public enum Role
{
    Passenger,
    Driver
}

public class User : IdentityUser
{
    [Required]
    public string Name { get; set; } = string.Empty;
    
    public Role Role { get; set; }
    
    public string? Nin { get; set; }
    
    public string? District { get; set; }
}

public class Booking
{
    public int Id { get; set; }
    public string PassengerId { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Status { get; set; } = "Pending";
}

public class Message
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string FromUserId { get; set; } = string.Empty;
    public string ToUserId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsRead { get; set; }
}
