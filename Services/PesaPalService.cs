using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using MatatuMVC.Models;

namespace MatatuMVC.Services;

public interface IPesaPalService
{
    Task<string> GetAccessTokenAsync();
    Task<string> RegisterIpnAsync(string ipnUrl);
    Task<PesaPalOrderResponse?> SubmitOrderAsync(Payment payment, string redirectUrl, string ipnId);
    Task<PesaPalTransactionStatus?> GetTransactionStatusAsync(string orderTrackingId);
}

public class PesaPalOrderResponse
{
    [JsonPropertyName("order_tracking_id")]
    public string OrderTrackingId { get; set; } = string.Empty;

    [JsonPropertyName("merchant_reference")]
    public string MerchantReference { get; set; } = string.Empty;

    [JsonPropertyName("redirect_url")]
    public string RedirectUrl { get; set; } = string.Empty;
}

public class PesaPalTransactionStatus
{
    [JsonPropertyName("payment_method")]
    public string PaymentMethod { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("created_date")]
    public DateTime CreatedDate { get; set; }

    [JsonPropertyName("confirmation_code")]
    public string ConfirmationCode { get; set; } = string.Empty;

    [JsonPropertyName("payment_status_description")]
    public string PaymentStatusDescription { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("payment_account")]
    public string PaymentAccount { get; set; } = string.Empty;

    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; } // 0=INVALID, 1=COMPLETED, 2=FAILED, 3=REVERSED
}

public class PesaPalService : IPesaPalService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<PesaPalService> _logger;
    private readonly IMemoryCache _cache;
    private const string TokenCacheKey = "PesaPal_Auth_Token";

    public PesaPalService(HttpClient httpClient, IConfiguration config, ILogger<PesaPalService> logger, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _cache = cache;
        var baseUrl = _config["PesaPal:BaseUrl"] ?? "https://cybqa.pesapal.com/pesapalv3/api/";
        if (!baseUrl.EndsWith("/")) baseUrl += "/";
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// 1. Authenticate with PesaPal using Consumer Key & Secret.
    /// Handles token caching automatically.
    /// </summary>
    public async Task<string> GetAccessTokenAsync()
    {
        // PesaPal tokens expire in 5 minutes. Cache it for 4 minutes to be safe.
        if (_cache.TryGetValue(TokenCacheKey, out string? cachedToken) && !string.IsNullOrEmpty(cachedToken))
        {
            return cachedToken;
        }

        var key = _config["PesaPal:ConsumerKey"];
        var secret = _config["PesaPal:ConsumerSecret"];
        
        _logger.LogInformation("Attempting PesaPal Auth with Key starting with: {Key}", key?.Substring(0, Math.Min(4, key?.Length ?? 0)));

        var payload = new
        {
            consumer_key = key,
            consumer_secret = secret
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("Auth/RequestToken", content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to get PesaPal token: {Status} - {Error}", response.StatusCode, error);
            throw new Exception($"PesaPal Auth Failed: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) 
            throw new Exception($"Empty response from PesaPal at {response.RequestMessage?.RequestUri}");
            
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("token", out var tokenProp))
        {
            // If the key is missing, PesaPal returned a non-standard error structure but with a 200 OK status.
            throw new Exception($"PesaPal Auth Failed. PesaPal response: {json}");
        }
        
        var token = tokenProp.GetString();

        if (string.IsNullOrEmpty(token)) throw new Exception("Token was null in PesaPal response.");

        // Cache the token for 4 minutes
        _cache.Set(TokenCacheKey, token, TimeSpan.FromMinutes(4));
        return token;
    }

    /// <summary>
    /// 2. Register an IPN (Webhook) URL to receive async status updates.
    /// </summary>
    public async Task<string> RegisterIpnAsync(string ipnUrl)
    {
        var token = await GetAccessTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var payload = new
        {
            url = ipnUrl,
            ipn_notification_type = "POST"
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("URLSetup/RegisterIPN", content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to register IPN: {Error}", await response.Content.ReadAsStringAsync());
            throw new Exception("Failed to register IPN");
        }

        var json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) 
            throw new Exception($"Empty response from PesaPal RegisterIPN at {response.RequestMessage?.RequestUri}");

        var doc = JsonDocument.Parse(json);
        
        if (!doc.RootElement.TryGetProperty("ipn_id", out var ipnProp))
        {
            // The JSON didn't contain ipn_id, likely an error response from PesaPal
            throw new Exception($"PesaPal RegisterIPN Failed. PesaPal response: {json}");
        }
        
        return ipnProp.GetString() ?? "";
    }

    /// <summary>
    /// 3. Submit Order Request.
    /// Returns the OrderTrackingId and the RedirectUrl (iframe URL).
    /// </summary>
    public async Task<PesaPalOrderResponse?> SubmitOrderAsync(Payment payment, string redirectUrl, string ipnId)
    {
        var token = await GetAccessTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Map Matatu Payment Provider to PesaPal format
        // PesaPal handles multiple networks behind the scenes (M-Pesa, Airtel, MTN, Cards)
        string currency = payment.Provider == PaymentProvider.MPesa ? "KES" : "UGX";
        string country = payment.Provider == PaymentProvider.MPesa ? "KE" : "UG";

        // Format phone number to international format (e.g. 256...)
        string formattedPhone = payment.PhoneNumber ?? "";
        if (formattedPhone.StartsWith("0")) formattedPhone = "256" + formattedPhone.Substring(1);
        else if (formattedPhone.StartsWith("+")) formattedPhone = formattedPhone.Substring(1);
        else if (!formattedPhone.StartsWith("256") && !formattedPhone.StartsWith("254")) formattedPhone = "256" + formattedPhone;

        var payload = new
        {
            id = payment.Id.ToString(), 
            currency = currency,
            amount = payment.Amount,
            description = "Payment for Matatu Trip Fare",
            callback_url = redirectUrl,
            notification_id = ipnId,
            billing_address = new
            {
                email_address = "passenger@matatu.com",
                phone_number = formattedPhone,
                country_code = country,
                first_name = "Matatu",
                middle_name = "",
                last_name = "Passenger",
                line_1 = "",
                line_2 = "",
                city = "Kampala",
                state = "",
                postal_code = "",
                zip_code = ""
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("Transactions/SubmitOrderRequest", content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("SubmitOrder Failed: {Error}", await response.Content.ReadAsStringAsync());
            throw new Exception("Failed to submit order to PesaPal");
        }

        var json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) 
            throw new Exception($"Empty response from PesaPal SubmitOrder at {response.RequestMessage?.RequestUri}");

        var responseObj = JsonSerializer.Deserialize<PesaPalOrderResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (responseObj == null || string.IsNullOrEmpty(responseObj.OrderTrackingId))
        {
            throw new Exception($"PesaPal SubmitOrder Failed. PesaPal response: {json}");
        }
        
        return responseObj;
    }

    /// <summary>
    /// 6. Check transaction status manually using the OrderTrackingId
    /// </summary>
    public async Task<PesaPalTransactionStatus?> GetTransactionStatusAsync(string orderTrackingId)
    {
        var token = await GetAccessTokenAsync();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.GetAsync($"Transactions/GetTransactionStatus?orderTrackingId={orderTrackingId}");
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("GetTransactionStatus Failed: {Error}", await response.Content.ReadAsStringAsync());
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) 
            return null;

        return JsonSerializer.Deserialize<PesaPalTransactionStatus>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
