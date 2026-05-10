using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MatatuMVC.Data;
using MatatuMVC.Models;
using System.Security.Claims;

namespace MatatuMVC.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = "Admin,Owner")]
    public class AdminApiController : ControllerBase
    {
        private readonly MatatuContext _context;
        private readonly UserManager<User> _userManager;

        public AdminApiController(MatatuContext context, UserManager<User> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // --- 1. OVERVIEW STATS ---
        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            var today = DateTime.UtcNow.Date;
            var totalTripsToday = await _context.Trips.CountAsync(t => t.StartTime >= today);
            var activeVehicles = await _context.Vehicles.CountAsync(v => v.IsActive);
            var totalRevenue = await _context.Payments
                .Where(p => p.Status == PaymentStatus.Success && p.CompletedAt >= today)
                .SumAsync(p => p.Amount);

            var occupancyRate = 0.0;
            var totalCapacity = await _context.Vehicles.Where(v => v.IsActive).SumAsync(v => v.Capacity);
            if (totalCapacity > 0)
            {
                var currentPassengers = await _context.Trips.Where(t => t.Status == TripStatus.Active).SumAsync(t => t.PassengerCount);
                occupancyRate = Math.Round((double)currentPassengers / totalCapacity * 100, 1);
            }

            return Ok(new {
                totalRevenueToday = totalRevenue,
                totalTripsToday,
                activeVehicles,
                occupancyRate,
                revenueTrend = new[] { 120000, 150000, 140000, 180000, 210000, 190000, totalRevenue } // Demo data + today
            });
        }

        // --- 2. USER MANAGEMENT ---
        [HttpGet("users")]
        public async Task<IActionResult> GetUsers()
        {
            var users = await _userManager.Users.ToListAsync();
            return Ok(users.Select(u => new {
                u.Id,
                u.Name,
                u.Email,
                u.Role,
                u.PhoneNumber,
                u.EmailConfirmed
            }));
        }

        [HttpPost("users/{userId}/role")]
        public async Task<IActionResult> ChangeRole(string userId, [FromBody] Role newRole)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound();

            user.Role = newRole;
            await _userManager.UpdateAsync(user);
            
            await LogAction("ChangeRole", userId, $"Changed role to {newRole}");
            return Ok();
        }

        // --- 3. TRIP MANAGEMENT ---
        [HttpGet("trips")]
        public async Task<IActionResult> GetAllTrips()
        {
            var trips = await _context.Trips
                .Include(t => t.Vehicle)
                .Include(t => t.Route)
                .OrderByDescending(t => t.StartTime)
                .Take(50)
                .ToListAsync();

            return Ok(trips.Select(t => new {
                t.Id,
                route = t.Route?.Name,
                vehicle = t.Vehicle?.PlateNumber,
                t.StartTime,
                t.Status,
                t.PassengerCount
            }));
        }

        // --- 4. VEHICLE & ROUTE MANAGEMENT ---
        [HttpGet("vehicles")]
        public async Task<IActionResult> GetVehicles() => Ok(await _context.Vehicles.ToListAsync());

        [HttpPost("vehicles")]
        public async Task<IActionResult> AddVehicle([FromBody] Vehicle vehicle)
        {
            _context.Vehicles.Add(vehicle);
            await _context.SaveChangesAsync();
            await LogAction("AddVehicle", vehicle.PlateNumber, "New vehicle registered");
            return Ok(vehicle);
        }

        [HttpGet("routes")]
        public async Task<IActionResult> GetRoutes() => Ok(await _context.Routes.ToListAsync());

        [HttpPost("routes")]
        public async Task<IActionResult> AddRoute([FromBody] MatatuRoute route)
        {
            _context.Routes.Add(route);
            await _context.SaveChangesAsync();
            await LogAction("AddRoute", route.Name, $"Fare set to {route.Fare}");
            return Ok(route);
        }

        // --- 5. PAYMENT OVERSIGHT ---
        [HttpGet("payments")]
        public async Task<IActionResult> GetPayments()
        {
            var payments = await _context.Payments
                .Include(p => p.Ticket)
                .OrderByDescending(p => p.InitiatedAt)
                .Take(100)
                .ToListAsync();

            return Ok(payments.Select(p => new {
                p.Id,
                p.TransactionId,
                p.Amount,
                p.Status,
                p.Provider,
                p.InitiatedAt,
                ticketCode = p.Ticket?.TicketCode
            }));
        }

        // --- HELPERS ---
        private async Task LogAction(string action, string target, string details)
        {
            var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";
            _context.AdminLogs.Add(new AdminLog {
                AdminId = adminId,
                Action = action,
                Target = target,
                Details = details
            });
            await _context.SaveChangesAsync();
        }
    }
}
