using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MatatuMVC.Data;
using MatatuMVC.Models;

namespace MatatuMVC.Controllers;

[Authorize(Roles = "Admin,Owner")]
[Route("api/[controller]")]
[ApiController]
public class ManagementController : ControllerBase
{
    private readonly MatatuContext _db;
    private readonly UserManager<User> _userManager;

    public ManagementController(MatatuContext db, UserManager<User> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    // ── VEHICLES ─────────────────────────────────────────────────────────────
    [HttpGet("vehicles")]
    public async Task<IActionResult> GetVehicles()
    {
        var vehicles = await _db.Vehicles.ToListAsync();
        return Ok(vehicles);
    }

    [HttpPost("vehicles/{id}/maintenance")]
    public async Task<IActionResult> UpdateMaintenance(int id, [FromBody] MaintenanceUpdateDto dto)
    {
        var vehicle = await _db.Vehicles.FindAsync(id);
        if (vehicle == null) return NotFound();

        vehicle.LastServiceDate = dto.ServiceDate;
        vehicle.NextServiceDue = dto.NextDue;
        vehicle.CurrentOdometer = dto.Odometer;
        
        await _db.SaveChangesAsync();
        return Ok(new { message = "Maintenance log updated." });
    }

    // ── USERS / ASSIGNMENTS ──────────────────────────────────────────────────
    [HttpGet("staff")]
    public async Task<IActionResult> GetStaff()
    {
        var staff = await _userManager.Users
            .Where(u => u.Role == Role.Driver || u.Role == Role.Conductor)
            .Select(u => new { u.Id, u.Name, u.Role, u.PhoneNumber })
            .ToListAsync();
        return Ok(staff);
    }
}

public record MaintenanceUpdateDto(DateTime ServiceDate, DateTime NextDue, int Odometer);
