using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MatatuMVC.Models;

namespace MatatuMVC.Data;

public class MatatuContext : IdentityDbContext<User>
{
    public MatatuContext(DbContextOptions<MatatuContext> options) : base(options) { }

    public DbSet<Booking>     Bookings    { get; set; }
    public DbSet<Message>     Messages    { get; set; }
    public DbSet<Vehicle>     Vehicles    { get; set; }
    public DbSet<MatatuRoute> Routes      { get; set; }
    public DbSet<Trip>        Trips       { get; set; }
    public DbSet<Ticket>      Tickets     { get; set; }
    public DbSet<Payment>     Payments    { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Seed routes
        modelBuilder.Entity<MatatuRoute>().HasData(
            new MatatuRoute { Id = 1, Name = "Kampala - Kisaasi",    Origin = "Old Taxi Park", Destination = "Kisaasi",    Fare = 2000, IsActive = true },
            new MatatuRoute { Id = 2, Name = "Kampala - Ntinda",     Origin = "Old Taxi Park", Destination = "Ntinda",     Fare = 1500, IsActive = true },
            new MatatuRoute { Id = 3, Name = "Kampala - Wandegeya",  Origin = "New Taxi Park", Destination = "Wandegeya",  Fare = 1000, IsActive = true },
            new MatatuRoute { Id = 4, Name = "Kampala - Nakawa",     Origin = "Old Taxi Park", Destination = "Nakawa",     Fare = 2500, IsActive = true },
            new MatatuRoute { Id = 5, Name = "Kampala - Makerere",   Origin = "New Taxi Park", Destination = "Makerere",   Fare = 1000, IsActive = true }
        );

        // Seed a demo vehicle
        modelBuilder.Entity<Vehicle>().HasData(
            new Vehicle { Id = 1, PlateNumber = "UBK 342C", Capacity = 14, Sacco = "Kampala SACCO", Model = "Toyota HiAce", IsActive = true,
                InsuranceExpiry  = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                InspectionExpiry = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                PsvLicenseExpiry = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc) }
        );
    }
}
