using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MatatuMVC.Data;
using MatatuMVC.Services;
using MatatuMVC.Models;

namespace MatatuMVC.Controllers;

[Route("api/pesapal")]
[ApiController]
public class PesaPalController : ControllerBase
{
    private readonly IPesaPalService _pesapalService;
    private readonly MatatuContext _context;
    private readonly ILogger<PesaPalController> _logger;

    public PesaPalController(IPesaPalService pesapalService, MatatuContext context, ILogger<PesaPalController> logger)
    {
        _pesapalService = pesapalService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// 4. Handle the Callback when the user is redirected back from PesaPal iframe.
    /// PesaPal sends: OrderTrackingId & OrderMerchantReference
    /// </summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string OrderTrackingId, [FromQuery] string OrderMerchantReference)
    {
        _logger.LogInformation("PesaPal Callback received. TrackingId: {TrackingId}, Ref: {Ref}", OrderTrackingId, OrderMerchantReference);

        if (string.IsNullOrEmpty(OrderTrackingId))
            return BadRequest("Invalid callback parameters");

        // We can immediately check the status using the API
        var status = await _pesapalService.GetTransactionStatusAsync(OrderTrackingId);

        if (status != null)
        {
            await ProcessPaymentStatusAsync(OrderTrackingId, OrderMerchantReference, status);
        }

        // Redirect back to our platform's dashboard UI. 
        // In a real SPA, this might redirect to a deep link or the dashboard with a success parameter.
        return Redirect("/?payment_status=processed");
    }

    /// <summary>
    /// 5. Handle the IPN Webhook from PesaPal.
    /// PesaPal POSTs to this endpoint asynchronously when a transaction succeeds or fails.
    /// Payload: { "OrderTrackingId": "...", "OrderNotificationType": "IPNCHANGE", "OrderMerchantReference": "..." }
    /// </summary>
    [HttpPost("ipn")]
    public async Task<IActionResult> IpnWebhook([FromBody] PesaPalIpnPayload payload)
    {
        _logger.LogInformation("PesaPal IPN Webhook received. TrackingId: {TrackingId}, Type: {Type}", payload.OrderTrackingId, payload.OrderNotificationType);

        if (payload == null || string.IsNullOrEmpty(payload.OrderTrackingId))
            return BadRequest("Invalid IPN payload");

        // The IPN just tells us something changed. We must GET the actual status securely from PesaPal.
        var status = await _pesapalService.GetTransactionStatusAsync(payload.OrderTrackingId);

        if (status == null)
            return StatusCode(500, "Could not retrieve transaction status from PesaPal");

        await ProcessPaymentStatusAsync(payload.OrderTrackingId, payload.OrderMerchantReference, status);

        // PesaPal expects a 200 OK response with specific JSON to acknowledge the IPN:
        return Ok(new
        {
            orderNotificationType = payload.OrderNotificationType,
            orderTrackingId = payload.OrderTrackingId,
            orderMerchantReference = payload.OrderMerchantReference,
            status = 200
        });
    }

    private async Task ProcessPaymentStatusAsync(string orderTrackingId, string merchantReference, PesaPalTransactionStatus status)
    {
        // MerchantReference is our Payment.Id in the database
        if (!int.TryParse(merchantReference, out int paymentId))
            return;

        var payment = await _context.Payments
            .Include(p => p.Ticket)
            .ThenInclude(t => t.Trip)
            .FirstOrDefaultAsync(p => p.Id == paymentId);

        if (payment == null) return;

        payment.TransactionId = status.ConfirmationCode;

        // PesaPal Status Codes: 0=INVALID, 1=COMPLETED, 2=FAILED, 3=REVERSED
        if (status.StatusCode == 1)
        {
            if (payment.Status != PaymentStatus.Success)
            {
                payment.Status = PaymentStatus.Success;
                payment.CompletedAt = DateTime.UtcNow;

                payment.Ticket.Status = TicketStatus.Paid;
                payment.Ticket.Trip.PassengerCount++;
                
                _logger.LogInformation("Payment {PaymentId} marked as COMPLETED via PesaPal.", payment.Id);
            }
        }
        else if (status.StatusCode == 2 || status.StatusCode == 0)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = status.PaymentStatusDescription;
            payment.Ticket.Status = TicketStatus.Cancelled;
            _logger.LogWarning("Payment {PaymentId} failed/invalid via PesaPal.", payment.Id);
        }
        else if (status.StatusCode == 3)
        {
            payment.Status = PaymentStatus.Refunded;
            payment.Ticket.Status = TicketStatus.Cancelled;
            // Should decrease passenger count if previously completed
        }

        await _context.SaveChangesAsync();
    }
}

public class PesaPalIpnPayload
{
    public string OrderTrackingId { get; set; } = string.Empty;
    public string OrderNotificationType { get; set; } = string.Empty;
    public string OrderMerchantReference { get; set; } = string.Empty;
}
