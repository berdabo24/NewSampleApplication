using OpenAI;
using Microsoft.Extensions.Configuration;
using System.ClientModel;
using OpenAI.Chat;

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
}
