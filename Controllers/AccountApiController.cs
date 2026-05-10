using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MatatuMVC.Models;
using System.Security.Claims;

namespace MatatuMVC.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AccountApiController : ControllerBase
    {
        private readonly UserManager<User> _userManager;

        public AccountApiController(UserManager<User> userManager)
        {
            _userManager = userManager;
        }

        [HttpGet("profile")]
        public async Task<IActionResult> GetProfile()
        {
            if (!User.Identity.IsAuthenticated)
            {
                return Unauthorized(new { message = "Guest User" });
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            return Ok(new
            {
                name = user.Name,
                email = user.Email,
                role = user.Role.ToString(),
                phoneNumber = user.PhoneNumber ?? "Not Set",
                joinedDate = "May 2026" // Placeholder for demo, ideally user.CreatedAt
            });
        }
    }
}
