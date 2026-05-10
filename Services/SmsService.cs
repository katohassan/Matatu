namespace MatatuMVC.Services;

public interface ISmsService
{
    Task<bool> SendSmsAsync(string phoneNumber, string message);
}

public class AfricaTalkingSmsService : ISmsService
{
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;

    public AfricaTalkingSmsService(IConfiguration config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public async Task<bool> SendSmsAsync(string phoneNumber, string message)
    {
        var username = _config["AfricaTalking:Username"] ?? "sandbox";
        var apiKey = _config["AfricaTalking:ApiKey"];
        
        if (string.IsNullOrEmpty(apiKey) || apiKey == "YOUR_API_KEY_HERE")
        {
            Console.WriteLine($"[SMS DEBUG] To: {phoneNumber}, Msg: {message}");
            return true; // Simulate success in dev/sandbox
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.africastalking.com/version1/messaging");
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("apikey", apiKey);

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("to", phoneNumber),
            new KeyValuePair<string, string>("message", message)
        });

        request.Content = content;

        try
        {
            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
