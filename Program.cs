using Microsoft.EntityFrameworkCore;
using MatatuMVC.Data;
using MatatuMVC.Models;
using MatatuMVC.Services;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

Console.WriteLine(">>> MATATU SYSTEM BOOTING: " + DateTime.UtcNow);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddDbContext<MatatuContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddMemoryCache();

// Typed HttpClients for external services
builder.Services.AddHttpClient<ISmsService, AfricaTalkingSmsService>();
builder.Services.AddHttpClient<IPesaPalService, PesaPalService>();

builder.Services.AddScoped<IMobileMoneyService, MobileMoneyService>();
builder.Services.AddScoped<IPesaPalService, PesaPalService>();
builder.Services.AddScoped<ISmsService, AfricaTalkingSmsService>();

builder.Services.AddIdentity<User, IdentityRole>(options => {
    options.Password.RequireDigit = false;
    options.Password.RequiredLength = 4;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
})
.AddEntityFrameworkStores<MatatuContext>()
.AddDefaultTokenProviders();

// --- MAP ROLE ENUM TO IDENTITY CLAIMS ---
builder.Services.AddScoped<IUserClaimsPrincipalFactory<User>, CustomClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Home/Dashboard"; 
});

var app = builder.Build();

// Seed Default User & Apply Migrations
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<MatatuContext>();
    try {
        Console.WriteLine(">>> SYSTEM: BUILDING DATABASE SCHEMA...");
        context.Database.EnsureCreated(); 
        Console.WriteLine(">>> SYSTEM: DATABASE READY. FLUSHING...");
        Thread.Sleep(2000); 
    } catch (Exception ex) {
        Console.WriteLine(">>> DATABASE SCHEMA ERROR: " + ex.Message);
    }

    var userManager = services.GetRequiredService<UserManager<User>>();
    
    // 1. Seed Admin User
    var existingUser = await userManager.FindByEmailAsync("kh256712@students.cavendish.ug");
    if (existingUser == null)
    {
        var user = new User {
            UserName = "kh256712@students.cavendish.ug",
            Email = "kh256712@students.cavendish.ug",
            Name = "Kato Hassan",
            Role = Role.Admin,
            EmailConfirmed = true
        };
        await userManager.CreateAsync(user, "Hassan@20");
    }
    else
    {
        // FORCE PROMOTION
        if (existingUser.Role != Role.Admin)
        {
            existingUser.Role = Role.Admin;
            await userManager.UpdateAsync(existingUser);
            Console.WriteLine($">>> SYSTEM: FORCED ADMIN PROMOTION FOR {existingUser.Email}");
        }
    }

    // 2. Seed Driver User
    var existingDriver = await userManager.FindByEmailAsync("driver@matatu.ug");
    if (existingDriver == null)
    {
        var driver = new User {
            UserName = "driver@matatu.ug",
            Email = "driver@matatu.ug",
            Name = "Musa Driver",
            Role = Role.Driver,
            EmailConfirmed = true
        };
        await userManager.CreateAsync(driver, "Driver@123");
    }

    // 3. Seed Passenger User (Hassan Kato)
    var hassanUser = await userManager.FindByEmailAsync("hassankato272@gmail.com");
    if (hassanUser == null)
    {
        var user = new User {
            UserName = "hassankato272@gmail.com",
            Email = "hassankato272@gmail.com",
            Name = "Hassan Kato",
            Role = Role.Passenger,
            EmailConfirmed = true
        };
        await userManager.CreateAsync(user, "Hassan@20");
    }
    else
    {
        // FORCE PASSWORD & ROLE RESET FOR TESTING
        var token = await userManager.GeneratePasswordResetTokenAsync(hassanUser);
        await userManager.ResetPasswordAsync(hassanUser, token, "Hassan@20");
        hassanUser.Role = Role.Passenger;
        await userManager.UpdateAsync(hassanUser);
        Console.WriteLine(">>> SYSTEM: FORCED PASSWORD/ROLE RESET FOR: hassankato272@gmail.com");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

// --- CUSTOM CLAIMS FACTORY ---
public class CustomClaimsPrincipalFactory : UserClaimsPrincipalFactory<User, IdentityRole>
{
    public CustomClaimsPrincipalFactory(UserManager<User> userManager, RoleManager<IdentityRole> roleManager, Microsoft.Extensions.Options.IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, roleManager, optionsAccessor) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
        return identity;
    }
}
