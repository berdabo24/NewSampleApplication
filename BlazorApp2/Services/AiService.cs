using OpenAI;
using Microsoft.Extensions.Configuration;
using System.ClientModel;
using OpenAI.Chat;
using System.Collections.Concurrent;

namespace BlazorApp2.Services;

public class AiService
{
    private readonly List<AiProviderConfig> _providers;

    public AiService(IConfiguration config)
    {
        _providers = new List<AiProviderConfig>();

        // --- METHOD: CHECK FOR INDIVIDUAL ENV VARIABLES (Render) ---
        // We assume 5 slots. You can add more if needed.
        for (int i = 1; i <= 5; i++)
        {
            var key = Environment.GetEnvironmentVariable($"GROQ_KEY_{i}");
            if (!string.IsNullOrEmpty(key))
            {
                _providers.Add(new AiProviderConfig
                {
                    Name = $"Groq-Env-{i}",
                    BaseUrl = "https://api.groq.com/openai/v1/",
                    ApiKey = key,
                    Model = "llama-3.1-8b-instant"
                });
            }
        }

        // --- FALLBACK: IF NO ENV VARS FOUND, TRY APPSETTINGS (Local Dev) ---
        if (_providers.Count == 0)
        {
            _providers = config.GetSection("AiProviders").Get<List<AiProviderConfig>>()
                         ?? new List<AiProviderConfig>();
        }

        // Final Safety Check
        if (_providers.Count == 0)
        {
            throw new Exception("No AI Providers found! Check .env variables or appsettings.json.");
        }
    }

    public async Task<string> AskAsync(string prompt)
    {
        // 2. Shuffle the list to load-balance (Round-Robin)
        var shuffledProviders = _providers.OrderBy(x => Guid.NewGuid()).ToList();

        List<string> errorLogs = new();

        // 3. Try each provider until one works
        foreach (var provider in shuffledProviders)
        {
            try
            {
                // Create the client for this specific provider
                // Note: We create it fresh each time because BaseUrl/Key changes
                var client = new ChatClient(
                    model: provider.Model,
                    credential: new ApiKeyCredential(provider.ApiKey),
                    options: new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl) }
                );

                // Ask the AI
                ChatCompletion completion = await client.CompleteChatAsync(
                    [new UserChatMessage(prompt)]
                );

                // If we get here, it worked! Return the text.
                return completion.Content[0].Text;
                //return $"[DEBUG: {provider.Name}] \n\n{completion.Content[0].Text}";
            }
            catch (Exception ex)
            {
                // --- ADD THIS DEBUG LINE ---
                System.Diagnostics.Debug.WriteLine($"[FAIL] Provider '{provider.Name}' crashed: {ex.Message}");

                // If it fails (429, 500, timeout), log it and loop to the next one
                errorLogs.Add($"Provider '{provider.Name}' failed: {ex.Message}");
                continue;
            }
        }

        // 4. If ALL providers fail, throw an error or return a safe message
        return $"System Overloaded. Errors: {string.Join("; ", errorLogs)}";
    }

    // --- NEW: Health Check Logic ---

    // Thread-safe storage for status (Key: "Groq-1", Value: true/false)
    public static ConcurrentDictionary<string, bool> ApiStatus = new();

    public async Task CheckHealthAsync()
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(3); // Fast timeout (3s)

        foreach (var provider in _providers)
        {
            try
            {
                // We verify the API is alive by asking for its "Model List" (Metadata).
                // This is much cheaper than generating text and usually doesn't count 
                // toward your strict Generation RPM limits.

                // Groq/OpenAI/OpenRouter standard endpoint: .../v1/models
                var url = provider.BaseUrl.TrimEnd('/') + "/models";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Authorization", $"Bearer {provider.ApiKey}");

                var response = await client.SendAsync(request);

                // 200 OK means the Key is valid and API is up.
                ApiStatus[provider.Name] = response.IsSuccessStatusCode;
            }
            catch
            {
                // Timeout or DNS error
                ApiStatus[provider.Name] = false;
            }
        }
    }
}


