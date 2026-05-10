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
                u.EmailConfirmed,
                u.LockoutEnd
            }));
        }

        [HttpPost("users/{userId}/role")]
        public async Task<IActionResult> ChangeRole(string userId, [FromBody] Role newRole)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound();
            user.Role = newRole;
            await _userManager.UpdateAsync(user);
            await LogAction("ChangeRole", user.Email, $"Changed role to {newRole}");
            return Ok();
        }

        [HttpPost("users/{userId}/ban")]
        public async Task<IActionResult> BanUser(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound();
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            await LogAction("BanUser", user.Email, "User banned indefinitely");
            return Ok();
        }

        [HttpDelete("users/{userId}")]
        public async Task<IActionResult> DeleteUser(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound();
            await _userManager.DeleteAsync(user);
            await LogAction("DeleteUser", user.Email, "User account deleted");
            return Ok();
        }

        // --- 3. TRIP MANAGEMENT ---
        [HttpGet("trips")]
        public async Task<IActionResult> GetAllTrips()
        {
            var trips = await _context.Trips.Include(t => t.Vehicle).Include(t => t.Route).OrderByDescending(t => t.StartTime).ToListAsync();
            return Ok(trips);
        }

        [HttpPost("trips")]
        public async Task<IActionResult> CreateTrip([FromBody] Trip trip)
        {
            _context.Trips.Add(trip);
            await _context.SaveChangesAsync();
            await LogAction("CreateTrip", $"Trip {trip.Id}", $"Route: {trip.RouteId}, Vehicle: {trip.VehicleId}");
            return Ok(trip);
        }

        [HttpDelete("trips/{id}")]
        public async Task<IActionResult> CancelTrip(int id)
        {
            var trip = await _context.Trips.FindAsync(id);
            if (trip == null) return NotFound();
            trip.Status = TripStatus.Cancelled;
            await _context.SaveChangesAsync();
            await LogAction("CancelTrip", $"Trip {id}", "Trip status set to Cancelled");
            return Ok();
        }

        // --- 4. VEHICLE MANAGEMENT ---
        [HttpGet("vehicles")]
        public async Task<IActionResult> GetVehicles() => Ok(await _context.Vehicles.ToListAsync());

        [HttpPost("vehicles")]
        public async Task<IActionResult> UpsertVehicle([FromBody] Vehicle vehicle)
        {
            if (vehicle.Id == 0) _context.Vehicles.Add(vehicle);
            else _context.Vehicles.Update(vehicle);
            await _context.SaveChangesAsync();
            await LogAction("UpsertVehicle", vehicle.PlateNumber, "Vehicle details updated");
            return Ok(vehicle);
        }

        [HttpDelete("vehicles/{id}")]
        public async Task<IActionResult> DeleteVehicle(int id)
        {
            var vehicle = await _context.Vehicles.FindAsync(id);
            if (vehicle == null) return NotFound();
            _context.Vehicles.Remove(vehicle);
            await _context.SaveChangesAsync();
            return Ok();
        }

        // --- 5. ROUTE MANAGEMENT ---
        [HttpGet("routes")]
        public async Task<IActionResult> GetRoutes() => Ok(await _context.Routes.ToListAsync());

        [HttpPost("routes")]
        public async Task<IActionResult> UpsertRoute([FromBody] MatatuRoute route)
        {
            if (route.Id == 0) _context.Routes.Add(route);
            else _context.Routes.Update(route);
            await _context.SaveChangesAsync();
            return Ok(route);
        }

        // --- 6. PAYMENT OVERSIGHT ---
        [HttpGet("payments")]
        public async Task<IActionResult> GetPayments(string? status)
        {
            var query = _context.Payments.Include(p => p.Ticket).AsQueryable();
            if (!string.IsNullOrEmpty(status)) {
                if (Enum.TryParse<PaymentStatus>(status, out var pStatus))
                    query = query.Where(p => p.Status == pStatus);
            }
            return Ok(await query.OrderByDescending(p => p.InitiatedAt).ToListAsync());
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
