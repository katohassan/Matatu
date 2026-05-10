using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatatuMVC.Controllers
{
    [Authorize(Roles = "Admin,Owner")]
    public class AdminController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Users() => View();
        public IActionResult Trips() => View();
        public IActionResult Fleet() => View();
        public IActionResult Payments() => View();
    }
}
