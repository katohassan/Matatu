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
    private readonly ISmsService _smsService;
    private readonly IConfiguration _config;

    public TicketingController(MatatuContext db, UserManager<User> userManager, IPesaPalService pesapalService, ISmsService smsService, IConfiguration config)
    {
        _db = db;
        _userManager = userManager;
        _pesapalService = pesapalService;
        _smsService = smsService;
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

            // Send SMS to Passenger
            await _smsService.SendSmsAsync(ticket.PassengerPhone, 
                $"MoveSafe Ticket Confirmed!\nCode: {ticket.TicketCode}\nRoute: {trip.Route?.Origin} -> {trip.Route?.Destination}\nFare: UGX {ticket.Amount}\nHave a safe journey!");
            
            // Send SMS to Conductor
            await _smsService.SendSmsAsync(conductor.PhoneNumber ?? "", 
                $"New Passenger Booked!\nPhone: {ticket.PassengerPhone}\nVehicle: {trip.Vehicle?.PlateNumber}");
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

            // Send SMS on successful mobile payment
            if (payment.Ticket != null && payment.Ticket.Trip != null)
            {
                var t = payment.Ticket;
                var tr = t.Trip;
                await _smsService.SendSmsAsync(t.PassengerPhone, 
                    $"MoveSafe Ticket Confirmed!\nCode: {t.TicketCode}\nRoute: {tr.Route?.Origin} -> {tr.Route?.Destination}\nFare: UGX {t.Amount}\nHave a safe journey!");
            }

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
            totalRevenue = tickets.Where(t => t.Status == TicketStatus.Paid || t.Status == TicketStatus.Boarded).Sum(t => t.Amount),
            lat = trip.CurrentLat,
            lng = trip.CurrentLng,
            tickets
        });
    }

    // ── POST /api/ticketing/trips/{id}/location ─────────────────────────────
    [HttpPost("trips/{id}/location")]
    public async Task<IActionResult> UpdateLocation(int id, [FromBody] LocationUpdateDto dto)
    {
        var trip = await _db.Trips.FindAsync(id);
        if (trip == null || trip.Status != TripStatus.Active) return NotFound();

        trip.CurrentLat = dto.Lat;
        trip.CurrentLng = dto.Lng;
        await _db.SaveChangesAsync();

        return Ok();
    }

    // ── GET /api/ticketing/history ───────────────────────────────────────────
    [HttpGet("history")]
    public async Task<IActionResult> GetPassengerHistory()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var history = await _db.Tickets
            .Include(t => t.Trip)
                .ThenInclude(tr => tr!.Route)
            .Include(t => t.Trip)
                .ThenInclude(tr => tr!.Vehicle)
            .Where(t => t.PassengerId == user.Id || t.PassengerPhone == user.PhoneNumber)
            .OrderByDescending(t => t.IssuedAt)
            .Select(t => new {
                t.Id,
                t.TicketCode,
                t.Amount,
                Status = t.Status.ToString(),
                t.IssuedAt,
                Route = t.Trip != null && t.Trip.Route != null ? $"{t.Trip.Route.Origin} → {t.Trip.Route.Destination}" : "Unknown",
                Vehicle = t.Trip != null && t.Trip.Vehicle != null ? t.Trip.Vehicle.PlateNumber : "Unknown"
            })
            .ToListAsync();

        return Ok(history);
    }

    // ── POST /api/ticketing/tickets/{id}/cancel ─────────────────────────────
    [HttpPost("tickets/{id}/cancel")]
    public async Task<IActionResult> CancelTicket(int id, [FromBody] CancelTicketDto dto)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var ticket = await _db.Tickets
            .Include(t => t.Trip)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null) return NotFound();
        if (ticket.Status == TicketStatus.Cancelled) return BadRequest(new { error = "Already cancelled." });
        if (ticket.Status == TicketStatus.Boarded) return BadRequest(new { error = "Cannot cancel a boarded ticket." });

        // Check 15 min rule
        if (ticket.Trip != null)
        {
            var diff = ticket.Trip.StartTime - DateTime.UtcNow;
            if (diff.TotalMinutes < 15 && ticket.Trip.Status == TripStatus.Active)
                return BadRequest(new { error = "Cancellations only allowed up to 15 mins before departure." });
        }

        // Refund Logic (90% refund for demo purposes)
        decimal refund = (ticket.Status == TicketStatus.Paid || ticket.Status == TicketStatus.Boarded) ? ticket.Amount * 0.9m : 0;

        ticket.Status = TicketStatus.Cancelled;
        if (ticket.Trip != null && ticket.Trip.PassengerCount > 0)
            ticket.Trip.PassengerCount--;

        var cancellation = new Cancellation
        {
            TicketId = ticket.Id,
            Reason = dto.Reason,
            RefundAmount = refund,
            CancelledByUserId = user.Id
        };

        _db.Cancellations.Add(cancellation);
        await _db.SaveChangesAsync();

        // Send SMS notification
        await _smsService.SendSmsAsync(ticket.PassengerPhone, 
            $"MoveSafe: Ticket {ticket.TicketCode} cancelled. Refund of UGX {refund} initiated.");

        return Ok(new { message = "Ticket cancelled successfully.", refund });
    }

    // ── POST /api/ticketing/verify ───────────────────────────────────────────
    [HttpPost("verify")]
    public async Task<IActionResult> VerifyTicket([FromBody] VerifyTicketDto dto)
    {
        var conductor = await _userManager.GetUserAsync(User);
        if (conductor == null) return Unauthorized();

        var ticket = await _db.Tickets
            .Include(t => t.Trip)
            .FirstOrDefaultAsync(t => t.TicketCode == dto.TicketCode);

        if (ticket == null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.Status == TicketStatus.Boarded)
            return BadRequest(new { error = "Ticket already used/boarded." });

        if (ticket.Status != TicketStatus.Paid)
            return BadRequest(new { error = "Ticket has not been paid yet." });

        // Mark as boarded
        ticket.Status = TicketStatus.Boarded;
        await _db.SaveChangesAsync();

        return Ok(new { 
            message = "Ticket verified! Passenger boarded.",
            ticketCode = ticket.TicketCode
        });
    }
}

// ─── DTOs ────────────────────────────────────────────────────────────────────
public record StartTripDto(int VehicleId, int RouteId, string? DriverId);
public record IssueTicketDto(int TripId, string PassengerPhone, string? PassengerId, decimal Amount, PaymentProvider PaymentProvider);
public record ConfirmPaymentDto(string CheckoutRequestId);
public record VerifyTicketDto(string TicketCode);
public record LocationUpdateDto(double Lat, double Lng);
public record CancelTicketDto(string Reason);
