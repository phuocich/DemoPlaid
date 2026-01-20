using System.Net.Http.Json;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();

// Configure HttpClient for Plaid API calls
builder.Services.AddHttpClient("Plaid", client =>
{
    var baseUrl = builder.Configuration["Plaid:BaseUrl"] ?? "https://sandbox.plaid.com";
    client.BaseAddress = new Uri(baseUrl);
});

// Configure CORS to allow frontend to call API
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    //app.UseSwagger();
    //app.UseSwaggerUI();
}

app.UseCors();
app.UseDefaultFiles(); // Enable default file serving (index.html)
app.UseStaticFiles(); // Serve static files from wwwroot
app.UseRouting();

// API endpoints
app.MapPost("/api/link-token", async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    var http = httpClientFactory.CreateClient("Plaid");
    var clientId = configuration["Plaid:ClientId"];
    var secret = configuration["Plaid:Secret"];

    var request = new
    {
        client_id = clientId,
        secret = secret,
        client_name = "Plaid Demo App",
        user = new { client_user_id = "user-123" },
        products = new[] { "transactions" },
        country_codes = new[] { "US" },
        language = "en",
        update = new { account_selection_enabled = true },
        // existing access token
        //access_token = existingAccessToken
    };

    try
    {
        var response = await http.PostAsJsonAsync("/link/token/create", request);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        var json = await JsonDocument.ParseAsync(stream);
        var linkToken = json.RootElement.GetProperty("link_token").GetString();

        return Results.Ok(new { link_token = linkToken });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error creating link token: {ex.Message}");
    }
});

app.MapPost("/api/exchange-token", async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    var http = httpClientFactory.CreateClient("Plaid");
    var clientId = configuration["Plaid:ClientId"];
    var secret = configuration["Plaid:Secret"];

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var requestBody = JsonDocument.Parse(body);
    
    if (!requestBody.RootElement.TryGetProperty("public_token", out var publicTokenElement))
    {
        return Results.BadRequest(new { error = "public_token is required" });
    }

    var publicToken = publicTokenElement.GetString();

    var request = new
    {
        client_id = clientId,
        secret = secret,
        public_token = publicToken
    };

    try
    {
        var response = await http.PostAsJsonAsync("/item/public_token/exchange", request);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        var json = await JsonDocument.ParseAsync(stream);
        var accessToken = json.RootElement.GetProperty("access_token").GetString();

        return Results.Ok(new { access_token = accessToken });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error exchanging token: {ex.Message}");
    }
});

// Create link_token for update mode (re-authentication)
app.MapPost("/api/link-token-update", async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    var http = httpClientFactory.CreateClient("Plaid");
    var clientId = configuration["Plaid:ClientId"];
    var secret = configuration["Plaid:Secret"];

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var requestBody = JsonDocument.Parse(body);
    
    if (!requestBody.RootElement.TryGetProperty("access_token", out var accessTokenElement))
    {
        return Results.BadRequest(new { error = "access_token is required" });
    }

    var accessToken = accessTokenElement.GetString();

    // Create link_token with access_token for update mode
    // This will open Plaid Link directly to the credential screen, not bank selection
    // Note: user object is required even in update mode
    var request = new
    {
        client_id = clientId,
        secret = secret,
        client_name = "Plaid Demo App",
        user = new { client_user_id = "user-123" }, // Required field for update mode
        access_token = accessToken
    };

    try
    {
        var response = await http.PostAsJsonAsync("/link/token/create", request);
        
        if (!response.IsSuccessStatusCode)
        {
            // Get error details for better debugging
            var errorContent = await response.Content.ReadAsStringAsync();
            return Results.Problem(
                detail: $"Plaid API error: {errorContent}",
                statusCode: (int)response.StatusCode
            );
        }
        
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        var json = await JsonDocument.ParseAsync(stream);
        var linkToken = json.RootElement.GetProperty("link_token").GetString();

        return Results.Ok(new { link_token = linkToken });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error creating update link token: {ex.Message}");
    }
});

app.MapPost("/api/transactions", async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    var http = httpClientFactory.CreateClient("Plaid");
    var clientId = configuration["Plaid:ClientId"];
    var secret = configuration["Plaid:Secret"];

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var requestBody = JsonDocument.Parse(body);
    
    if (!requestBody.RootElement.TryGetProperty("access_token", out var accessTokenElement))
    {
        return Results.BadRequest(new { error = "access_token is required" });
    }

    var accessToken = accessTokenElement.GetString();
    var startDate = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd");
    var endDate = DateTime.Now.ToString("yyyy-MM-dd");

    var request = new
    {
        client_id = clientId,
        secret = secret,
        access_token = accessToken,
        start_date = startDate,
        end_date = endDate
    };

    try
    {
        var response = await http.PostAsJsonAsync("/transactions/get", request);
        
        if (!response.IsSuccessStatusCode)
        {
            // Parse error response to check for ITEM_LOGIN_REQUIRED
            var errorContent = await response.Content.ReadAsStringAsync();
            var errorJson = JsonDocument.Parse(errorContent);
            
            if (errorJson.RootElement.TryGetProperty("error_code", out var errorCodeElement))
            {
                var errorCode = errorCodeElement.GetString();
                
                if (errorCode == "ITEM_LOGIN_REQUIRED")
                {
                    // Return specific error for frontend to handle
                    return Results.Json(
                        new { 
                            error = "ITEM_LOGIN_REQUIRED",
                            message = "Your bank account requires re-authentication. Please log in again.",
                            access_token = accessToken
                        },
                        statusCode: 401
                    );
                }
            }
            
            // Other errors
            return Results.Problem(
                detail: errorContent,
                statusCode: (int)response.StatusCode
            );
        }

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        return Results.Ok(json.RootElement);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error fetching transactions: {ex.Message}");
    }
});

app.Run();
