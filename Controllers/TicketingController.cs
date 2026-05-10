using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MatatuMVC.Data;
using MatatuMVC.Models;
using MatatuMVC.Services;

namespace MatatuMVC.Controllers;

[Authorize]
[Route("api/[controller]")]
[ApiController]
public class TicketingController : ControllerBase
{
    private readonly MatatuContext _db;
    private readonly UserManager<User> _userManager;
    private readonly IPesaPalService _pesapalService;
    private readonly IConfiguration _config;

    public TicketingController(MatatuContext db, UserManager<User> userManager, IPesaPalService pesapalService, IConfiguration config)
    {
        _db = db;
        _userManager = userManager;
        _pesapalService = pesapalService;
        _config = config;
    }

    // ── GET /api/ticketing/routes ────────────────────────────────────────────
    [HttpGet("routes")]
    [AllowAnonymous]
    public async Task<IActionResult> GetRoutes()
    {
        var routes = await _db.Routes
            .Where(r => r.IsActive)
            .Select(r => new { r.Id, r.Name, r.Origin, r.Destination, r.Fare })
            .ToListAsync();
        return Ok(routes);
    }

    // ── GET /api/ticketing/trips/active ─────────────────────────────────────
    [HttpGet("trips/active")]
    public async Task<IActionResult> GetActiveTrips()
    {
        var trips = await _db.Trips
            .Include(t => t.Vehicle)
            .Include(t => t.Route)
            .Where(t => t.Status == TripStatus.Active)
            .Select(t => new {
                t.Id,
                t.PassengerCount,
                Capacity = t.Vehicle != null ? t.Vehicle.Capacity : 14,
                IsFull = t.Vehicle != null && t.PassengerCount >= t.Vehicle.Capacity,
                Route = t.Route != null ? $"{t.Route.Origin} → {t.Route.Destination}" : "Unknown",
                Fare = t.Route != null ? t.Route.Fare : 0,
                Vehicle = t.Vehicle != null ? t.Vehicle.PlateNumber : "Unknown",
                t.StartTime
            })
            .ToListAsync();
        return Ok(trips);
    }

    // ── POST /api/ticketing/trips/start ─────────────────────────────────────
    /// <summary>Conductor starts a new trip. Validates vehicle capacity exists.</summary>
    [HttpPost("trips/start")]
    public async Task<IActionResult> StartTrip([FromBody] StartTripDto dto)
    {
        var conductor = await _userManager.GetUserAsync(User);
        if (conductor == null) return Unauthorized();

        var vehicle = await _db.Vehicles.FindAsync(dto.VehicleId);
        if (vehicle == null || !vehicle.IsActive)
            return BadRequest(new { error = "Vehicle not found or inactive." });

        var route = await _db.Routes.FindAsync(dto.RouteId);
        if (route == null || !route.IsActive)
            return BadRequest(new { error = "Route not found or inactive." });

        // Check compliance — block if insurance/inspection expired
        if (vehicle.InsuranceExpiry.HasValue && vehicle.InsuranceExpiry < DateTime.UtcNow)
            return BadRequest(new { error = "Vehicle insurance has expired. Cannot dispatch." });

        if (vehicle.PsvLicenseExpiry.HasValue && vehicle.PsvLicenseExpiry < DateTime.UtcNow)
            return BadRequest(new { error = "PSV license has expired. Cannot dispatch." });

        var trip = new Trip
        {
            VehicleId    = dto.VehicleId,
            RouteId      = dto.RouteId,
            ConductorId  = conductor.Id,
            DriverId     = dto.DriverId ?? conductor.Id,
            Status       = TripStatus.Active,
            PassengerCount = 0
        };

        _db.Trips.Add(trip);
        await _db.SaveChangesAsync();

        return Ok(new {
            trip.Id,
            message = $"Trip started on {route.Origin} → {route.Destination}. Vehicle capacity: {vehicle.Capacity}.",
            capacity = vehicle.Capacity,
            fare = route.Fare
        });
    }

    // ── POST /api/ticketing/issue ────────────────────────────────────────────
    /// <summary>Conductor issues a ticket. Enforces capacity. Initiates mobile money payment.</summary>
    [HttpPost("issue")]
    public async Task<IActionResult> IssueTicket([FromBody] IssueTicketDto dto)
    {
        var conductor = await _userManager.GetUserAsync(User);
        if (conductor == null) return Unauthorized();

        // Load trip with vehicle for capacity check
        var trip = await _db.Trips
            .Include(t => t.Vehicle)
            .Include(t => t.Route)
            .FirstOrDefaultAsync(t => t.Id == dto.TripId && t.Status == TripStatus.Active);

        if (trip == null)
            return BadRequest(new { error = "Active trip not found." });

        // ── CAPACITY ENFORCEMENT ─────────────────────────────────────────────
        var capacity = trip.Vehicle?.Capacity ?? 14;
        if (trip.PassengerCount >= capacity)
            return BadRequest(new
            {
                error = $"Vehicle is FULL ({trip.PassengerCount}/{capacity} passengers). Cannot issue more tickets.",
                isFull = true,
                passengerCount = trip.PassengerCount,
                capacity
            });

        // Create pending ticket
        var ticket = new Ticket
        {
            TripId               = trip.Id,
            PassengerPhone       = dto.PassengerPhone,
            PassengerId          = dto.PassengerId ?? string.Empty,
            Amount               = trip.Route?.Fare ?? dto.Amount,
            Status               = TicketStatus.Pending,
            IssuedByConductorId  = conductor.Id
        };

        _db.Tickets.Add(ticket);
        await _db.SaveChangesAsync();

        // ── INITIATE PESAPAL PAYMENT ─────────────────────────────────────────
        PesaPalOrderResponse? paymentResult = null;
        Payment? payment = null;

        if (dto.PaymentProvider != PaymentProvider.Cash)
        {
            // 1. Save Pending Payment first to generate Payment.Id (MerchantReference)
            payment = new Payment
            {
                TicketId          = ticket.Id,
                PhoneNumber       = dto.PassengerPhone,
                Amount            = ticket.Amount,
                Provider          = dto.PaymentProvider,
                Status            = PaymentStatus.Pending,
                CheckoutRequestId = string.Empty // Updated below
            };
            _db.Payments.Add(payment);
            await _db.SaveChangesAsync();

            // 2. Register IPN & Submit Order
            try
            {
                string ipnUrl = _config["PesaPal:IpnUrl"] ?? "https://yourdomain.com/api/pesapal/ipn";
                string ipnId = await _pesapalService.RegisterIpnAsync(ipnUrl);

                string redirectUrl = "https://matatu-q3wb.onrender.com/api/pesapal/callback";
                paymentResult = await _pesapalService.SubmitOrderAsync(payment, redirectUrl, ipnId);

                if (paymentResult != null)
                {
                    payment.CheckoutRequestId = paymentResult.OrderTrackingId;
                    await _db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                // Delete the pending ticket/payment if initiation failed
                _db.Payments.Remove(payment);
                _db.Tickets.Remove(ticket);
                await _db.SaveChangesAsync();
                return BadRequest(new { error = $"PesaPal Error: {ex.Message}" });
            }
        }
        else
        {
            // Cash payment — mark immediately as paid
            ticket.Status = TicketStatus.Paid;
            trip.PassengerCount++;
            await _db.SaveChangesAsync();
        }

        return Ok(new
        {
            ticketId   = ticket.Id,
            ticketCode = ticket.TicketCode,
            amount     = ticket.Amount,
            passengerPhone = ticket.PassengerPhone,
            paymentPrompt  = paymentResult != null ? "PesaPal checkout initiated. Please complete payment." : "Cash payment recorded.",
            checkoutRequestId = payment?.CheckoutRequestId,
            redirectUrl = paymentResult?.RedirectUrl,
            passengerCount = trip.PassengerCount,
            capacity,
            isFull = trip.PassengerCount >= capacity
        });
    }

    // ── POST /api/ticketing/payment/confirm ──────────────────────────────────
    /// <summary>Poll payment status. On success, marks ticket as paid and increments passenger count.</summary>
    [HttpPost("payment/confirm")]
    public async Task<IActionResult> ConfirmPayment([FromBody] ConfirmPaymentDto dto)
    {
        var payment = await _db.Payments
            .Include(p => p.Ticket)
                .ThenInclude(t => t!.Trip)
                    .ThenInclude(t => t!.Vehicle)
            .FirstOrDefaultAsync(p => p.CheckoutRequestId == dto.CheckoutRequestId);

        if (payment == null)
            return NotFound(new { error = "Payment not found." });

        if (payment.Status == PaymentStatus.Success)
            return Ok(new { confirmed = true, message = "Already confirmed.", ticketCode = payment.Ticket?.TicketCode });

        var status = await _pesapalService.GetTransactionStatusAsync(dto.CheckoutRequestId);

        if (status != null && status.StatusCode == 1) // 1 = COMPLETED
        {
            payment.Status = PaymentStatus.Success;
            payment.TransactionId = status.ConfirmationCode;
            payment.CompletedAt = DateTime.UtcNow;

            if (payment.Ticket != null)
            {
                payment.Ticket.Status = TicketStatus.Paid;
                var trip = payment.Ticket.Trip;
                if (trip != null)
                {
                    var capacity = trip.Vehicle?.Capacity ?? 14;
                    if (trip.PassengerCount < capacity)
                        trip.PassengerCount++;
                }
            }

            await _db.SaveChangesAsync();
            return Ok(new { confirmed = true, transactionId = status.ConfirmationCode, ticketCode = payment.Ticket?.TicketCode, message = "Payment confirmed via PesaPal!" });
        }

        return Ok(new { confirmed = false, message = "Payment pending or failed." });
    }

    // ── POST /api/ticketing/trips/{id}/end ──────────────────────────────────
    [HttpPost("trips/{id}/end")]
    public async Task<IActionResult> EndTrip(int id)
    {
        var conductor = await _userManager.GetUserAsync(User);
        if (conductor == null) return Unauthorized();

        var trip = await _db.Trips
            .Include(t => t.Route)
            .FirstOrDefaultAsync(t => t.Id == id && t.ConductorId == conductor.Id && t.Status == TripStatus.Active);

        if (trip == null) return NotFound(new { error = "Active trip not found." });

        trip.Status  = TripStatus.Completed;
        trip.EndTime = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var totalRevenue = await _db.Tickets
            .Where(t => t.TripId == id && t.Status == TicketStatus.Paid)
            .SumAsync(t => t.Amount);

        return Ok(new {
            message = "Trip completed.",
            passengerCount = trip.PassengerCount,
            totalRevenue,
            route = trip.Route != null ? $"{trip.Route.Origin} → {trip.Route.Destination}" : "Unknown"
        });
    }

    // ── GET /api/ticketing/trips/{id}/summary ───────────────────────────────
    [HttpGet("trips/{id}/summary")]
    public async Task<IActionResult> TripSummary(int id)
    {
        var trip = await _db.Trips
            .Include(t => t.Vehicle)
            .Include(t => t.Route)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (trip == null) return NotFound();

        var tickets = await _db.Tickets
            .Where(t => t.TripId == id)
            .Select(t => new { t.TicketCode, t.PassengerPhone, t.Amount, t.Status, t.IssuedAt })
            .ToListAsync();

        var capacity = trip.Vehicle?.Capacity ?? 14;

        return Ok(new
        {
            tripId = trip.Id,
            route  = trip.Route != null ? $"{trip.Route.Origin} → {trip.Route.Destination}" : "Unknown",
            vehicle = trip.Vehicle?.PlateNumber,
            status  = trip.Status.ToString(),
            passengerCount = trip.PassengerCount,
            capacity,
            percentFull = (int)Math.Round((double)trip.PassengerCount / capacity * 100),
            totalRevenue = tickets.Where(t => t.Status == TicketStatus.Paid).Sum(t => t.Amount),
            tickets
        });
    }
}

// ─── DTOs ────────────────────────────────────────────────────────────────────
public record StartTripDto(int VehicleId, int RouteId, string? DriverId);
public record IssueTicketDto(int TripId, string PassengerPhone, string? PassengerId, decimal Amount, PaymentProvider PaymentProvider);
public record ConfirmPaymentDto(string CheckoutRequestId);
