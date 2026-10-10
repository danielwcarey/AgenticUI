using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Data.Common;

namespace AgenticUI.AgentServer;

/// <summary>
/// Configuration for the <see href="https://learn.microsoft.com/azure/ai-foundry/">Microsoft Foundry</see>
/// service that backs every scenario. Local inference uses the OpenAI-compatible API;
/// Azure inference authenticates with Microsoft Entra ID.
/// </summary>
public sealed class FoundryOptions
{
    public string Provider { get; set; } = "Azure";
    public const string DefaultModel = "gpt-5-mini";
    public const string DefaultReasoningModel = "gpt-5-mini";

    /// <summary>The Foundry resource endpoint.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The deployment name used by most scenarios, e.g. <c>gpt-5-mini</c>.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>A reasoning-capable deployment used by the reasoning scenario, e.g. <c>gpt-5-mini</c>.</summary>
    public string ReasoningModel { get; set; } = DefaultReasoningModel;
}

/// <summary>Helpers for resolving Foundry configuration and building chat clients.</summary>
public static class Foundry
{
    /// <summary>
    /// Reads Foundry settings from configuration. Aspire provides the local <c>chat</c> connection
    /// string or the Azure <c>FOUNDRY_URI</c> endpoint. The <c>Foundry</c> configuration section remains
    /// available for running the agent server without the AppHost.
    /// </summary>
    public static FoundryOptions ReadOptions(IConfiguration configuration)
    {
        var options = new FoundryOptions();
        configuration.GetSection("Foundry").Bind(options);

        options.Provider = configuration["FOUNDRY_PROVIDER"] ?? options.Provider;
        if (options.Provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString("chat");
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                var connection = new DbConnectionStringBuilder { ConnectionString = connectionString };
                options.Endpoint = Convert.ToString(connection["Endpoint"]);
                options.Model = Convert.ToString(connection["Model"])!;
                options.ReasoningModel = options.Model;
            }
        }
        else if (!options.Provider.Equals("Azure", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Foundry:Provider must be Local or Azure.");
        }

        options.Endpoint = configuration["FOUNDRY_URI"] ?? options.Endpoint;
        options.Model = configuration["FOUNDRY_MODEL"] ?? options.Model;
        options.ReasoningModel = configuration["FOUNDRY_REASONING_MODEL"] ?? options.ReasoningModel;

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException(
                "No Microsoft Foundry endpoint configured. Reference the Foundry resource from the AppHost " +
                "or set the Foundry:Endpoint configuration value.");
        }

        return options;
    }

    private static AzureOpenAIClient CreateClient(FoundryOptions options) =>
        new(new Uri(options.Endpoint!, UriKind.Absolute), new DefaultAzureCredential());

    /// <summary>Creates a chat-completions <see cref="ChatClient"/> for a Foundry deployment.</summary>
    /// <param name="options">The Foundry configuration.</param>
    /// <param name="model">The deployment name; defaults to <see cref="FoundryOptions.Model"/>.</param>
    public static ChatClient CreateChatClient(FoundryOptions options, string? model = null) =>
        options.Provider.Equals("Local", StringComparison.OrdinalIgnoreCase)
            ? new OpenAIClient(new ApiKeyCredential("local"), new OpenAIClientOptions
            {
                Endpoint = new Uri(options.Endpoint!.TrimEnd('/') + "/v1")
            }).GetChatClient(model ?? options.Model)
            : CreateClient(options).GetChatClient(model ?? options.Model);

    /// <summary>
    /// Creates the reasoning client using local chat completions or Azure's OpenAI <em>Responses</em> API.
    /// Azure reasoning summaries arrive through the Responses API as <see cref="TextReasoningContent"/>,
    /// which the MAF AG-UI adapter emits as <c>REASONING_*</c> events. Local mode uses chat completions;
    /// the default local model returns answers without separate reasoning summaries.
    /// </summary>
    public static IChatClient CreateReasoningChatClient(FoundryOptions options) =>
        options.Provider.Equals("Local", StringComparison.OrdinalIgnoreCase)
            ? CreateChatClient(options, options.ReasoningModel).AsIChatClient()
            : CreateClient(options).GetResponsesClient().AsIChatClient(options.ReasoningModel);
}
