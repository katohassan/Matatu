using Microsoft.EntityFrameworkCore;
using MatatuMVC.Data;
using MatatuMVC.Models;
using MatatuMVC.Services;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddDbContext<MatatuContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
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
    var context = scope.ServiceProvider.GetRequiredService<MatatuContext>();
    await context.Database.MigrateAsync();

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
    var existingUser = await userManager.FindByEmailAsync("hassankato272@gmail.com");
    if (existingUser == null)
    {
        var user = new User
        {
            UserName = "hassankato272@gmail.com",
            Email = "hassankato272@gmail.com",
            Name = "Kato Hassan",
            Role = Role.Passenger,
            EmailConfirmed = true
        };
        var seedPassword = builder.Configuration["SeedUser:Password"] ?? "Hassan@20";
        var result = await userManager.CreateAsync(user, seedPassword);
        if (!result.Succeeded)
        {
            // Log errors to console for debugging
            foreach (var error in result.Errors)
            {
                Console.WriteLine($"Seed Error: {error.Description}");
            }
        }
    }

    // Seed Driver User
    var existingDriver = await userManager.FindByEmailAsync("driver@matatu.ug");
    if (existingDriver == null)
    {
        var driver = new User
        {
            UserName = "driver@matatu.ug",
            Email = "driver@matatu.ug",
            Name = "Mukasa John",
            Role = Role.Driver,
            EmailConfirmed = true
        };
        var seedPassword = builder.Configuration["SeedUser:Password"] ?? "Hassan@20";
        await userManager.CreateAsync(driver, seedPassword);
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
