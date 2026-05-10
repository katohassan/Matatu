using Microsoft.EntityFrameworkCore;
using MatatuMVC.Data;
using MatatuMVC.Models;
using MatatuMVC.Services;
using Microsoft.AspNetCore.Identity;

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
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequireUppercase = true;
    options.Password.RequiredLength = 6;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<MatatuContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

var app = builder.Build();

// Seed Default User & Apply Migrations
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<MatatuContext>();
    try {
        Console.WriteLine(">>> SYSTEM: BUILDING DATABASE SCHEMA...");
        // Use synchronous call to force the thread to wait until the file is physically written
        context.Database.EnsureCreated(); 
        Console.WriteLine(">>> SYSTEM: DATABASE READY. FLUSHING...");
        Thread.Sleep(2000); // 2-second safety buffer for SQLite file lock
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
        // FORCE PROMOTION: Ensure existing user is upgraded to Admin
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
            Name = "Mukasa John",
            Role = Role.Driver,
            EmailConfirmed = true,
            PhoneNumber = "0770000001"
        };
        await userManager.CreateAsync(driver, "Hassan@20");
    }

        // Seed some Demo Trips if none exist
        if (!context.Trips.Any())
        {
            var route = context.Routes.First();
            var vehicle = context.Vehicles.First();
            context.Trips.Add(new Trip { 
                VehicleId = vehicle.Id, 
                RouteId = route.Id, 
                Status = TripStatus.Active, 
                StartTime = DateTime.UtcNow,
                CurrentLat = 0.3476,
                CurrentLng = 32.5825
            });
            await context.SaveChangesAsync();
        }
}

// Configure the HTTP request pipeline.
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

app.MapStaticAssets();

app.MapControllers(); // for API controllers
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
