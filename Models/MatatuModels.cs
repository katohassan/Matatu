using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace MatatuMVC.Models;

public enum Role { Passenger, Driver, Conductor, Owner, Admin }
public enum TripStatus { Active, Completed, Cancelled }
public enum TicketStatus { Pending, Paid, Boarded, Cancelled }
public enum PaymentStatus { Pending, Success, Failed, Timeout, Refunded }
public enum PaymentProvider { MPesa, AirtelMoney, MTNMoMo, TigoPesa, Cash }

// ─── User ────────────────────────────────────────────────────────────────────
public class User : IdentityUser
{
    [Required] public string Name { get; set; } = string.Empty;
    public Role Role { get; set; }
    public string? Nin { get; set; }
    public string? District { get; set; }
}

// ─── Vehicle ─────────────────────────────────────────────────────────────────
public class Vehicle
{
    public int Id { get; set; }
    [Required] public string PlateNumber { get; set; } = string.Empty;
    public int Capacity { get; set; } = 14;
    public string OwnerId { get; set; } = string.Empty;
    public string Sacco { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? InsuranceExpiry { get; set; }
    public DateTime? InspectionExpiry { get; set; }
    public DateTime? PsvLicenseExpiry { get; set; }
}

// ─── Route ───────────────────────────────────────────────────────────────────
public class MatatuRoute
{
    public int Id { get; set; }
    [Required] public string Name { get; set; } = string.Empty;
    [Required] public string Origin { get; set; } = string.Empty;
    [Required] public string Destination { get; set; } = string.Empty;
    public decimal Fare { get; set; }
    public string Stops { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

// ─── Trip ────────────────────────────────────────────────────────────────────
public class Trip
{
    public int Id { get; set; }
    public int VehicleId { get; set; }
    public string DriverId { get; set; } = string.Empty;
    public string ConductorId { get; set; } = string.Empty;
    public int RouteId { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    public TripStatus Status { get; set; } = TripStatus.Active;
    public int PassengerCount { get; set; } = 0;

    [ForeignKey(nameof(VehicleId))] public Vehicle? Vehicle { get; set; }
    [ForeignKey(nameof(RouteId))]   public MatatuRoute? Route { get; set; }
}

// ─── Ticket ──────────────────────────────────────────────────────────────────
public class Ticket
{
    public int Id { get; set; }
    public int TripId { get; set; }
    public string PassengerPhone { get; set; } = string.Empty;
    public string PassengerId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Pending;
    public string TicketCode { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public string IssuedByConductorId { get; set; } = string.Empty;

    [ForeignKey(nameof(TripId))] public Trip? Trip { get; set; }
}

// ─── Payment ─────────────────────────────────────────────────────────────────
public class Payment
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public PaymentProvider Provider { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? TransactionId { get; set; }
    public string? CheckoutRequestId { get; set; }
    public DateTime InitiatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }

    [ForeignKey(nameof(TicketId))] public Ticket? Ticket { get; set; }
}

// ─── Legacy / Preserved ──────────────────────────────────────────────────────
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
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
}
