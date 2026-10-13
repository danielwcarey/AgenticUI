using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgenticUI.AgentServer;

/// <summary>
/// Foundry Local can return XML-like or JSON-in-tag tool syntax as text rather than OpenAI tool_calls.
/// Normalize it below the agent's function-invocation middleware so execution, approvals and
/// AG-UI events all use the usual framework pipeline.
/// </summary>
internal sealed partial class FoundryLocalToolCallChatClient(IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        NormalizeToolCalls(response, options);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options?.Tools is not { Count: > 0 })
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }
            yield break;
        }

        // Local streaming can also omit structured tool_calls. Request a complete response so
        // native calls or complete Qwen arguments can be normalized before any XML reaches the UI.
        // AG-UI still streams the resulting tool call, result and subsequent agent turns.
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    private static void NormalizeToolCalls(ChatResponse response, ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 })
        {
            return;
        }

        foreach (var message in response.Messages)
        {
            if (message.Role != ChatRole.Assistant || message.Contents.Any(c => c is FunctionCallContent))
            {
                continue;
            }

            var text = message.Text.Trim();
            if (TryParseBareJsonCall(text, options.Tools, out var jsonCall))
            {
                ReplaceTextWithCalls(response, message, [jsonCall!]);
                continue;
            }
            if (!text.StartsWith("<tool_call>", StringComparison.Ordinal))
            {
                continue;
            }

            // Only accept an entire response consisting of calls. Never interpret an XML example
            // embedded in prose as an instruction to execute a function.
            var calls = new List<AIContent>();
            var offset = 0;
            foreach (Match match in ToolCallPattern().Matches(text))
            {
                if (!string.IsNullOrWhiteSpace(text[offset..match.Index]))
                {
                    throw InvalidToolCall();
                }
                calls.Add(ParseCall(match.Groups[1].Value, options.Tools));
                offset = match.Index + match.Length;
            }
            if (calls.Count == 0 || !string.IsNullOrWhiteSpace(text[offset..]))
            {
                throw InvalidToolCall();
            }

            ReplaceTextWithCalls(response, message, calls);
        }
    }

    private static void ReplaceTextWithCalls(ChatResponse response, ChatMessage message, List<AIContent> calls)
    {
        message.Contents = message.Contents.Where(c => c is not TextContent).Concat(calls).ToList();
        // The provider's raw completion still contains text and finish_reason=stop.
        message.RawRepresentation = null;
        response.RawRepresentation = null;
        response.FinishReason = ChatFinishReason.ToolCalls;
    }

    private static bool TryParseBareJsonCall(string text, IList<AITool> tools, out FunctionCallContent? call)
    {
        call = null;
        if (!text.StartsWith('{')) return false;
        // Some local templates strip the tool_call delimiters. Recognize only the exact
        // name/arguments envelope for an available function; ordinary JSON remains text.
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { return false; }
        using (document)
        {
            var root = document.RootElement;
            if (root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("arguments", out _) ||
                !tools.OfType<AIFunctionDeclaration>().Any(t => t.Name == name.GetString()))
            {
                return false;
            }
            call = ParseJsonCall(text, tools);
            return true;
        }
    }

    private static FunctionCallContent ParseCall(string body, IList<AITool> tools)
    {
        if (body.TrimStart().StartsWith('{'))
        {
            return ParseJsonCall(body, tools);
        }

        var function = FunctionPattern().Match(body);
        if (!function.Success)
        {
            throw InvalidToolCall();
        }
        var name = function.Groups[1].Value;
        var tool = tools.OfType<AIFunctionDeclaration>().FirstOrDefault(t => t.Name == name)
            ?? throw InvalidToolCall();
        var schema = tool.JsonSchema;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        var parameters = function.Groups[2].Value;
        var offset = 0;
        foreach (Match parameter in ParameterPattern().Matches(parameters))
        {
            var parameterName = parameter.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(parameters[offset..parameter.Index]) ||
                arguments.ContainsKey(parameterName) ||
                !schema.TryGetProperty("properties", out var properties) ||
                !properties.TryGetProperty(parameterName, out var property))
            {
                throw InvalidToolCall();
            }
            arguments.Add(parameterName, ParseArgument(parameter.Groups[2].Value.Trim(), property));
            offset = parameter.Index + parameter.Length;
        }
        if (!string.IsNullOrWhiteSpace(parameters[offset..]) ||
            (schema.TryGetProperty("required", out var required) &&
             required.EnumerateArray().Any(p => !arguments.ContainsKey(p.GetString()!))))
        {
            throw InvalidToolCall();
        }

        return new FunctionCallContent($"call_{Guid.NewGuid():N}", name, arguments);
    }

    private static FunctionCallContent ParseJsonCall(string body, IList<AITool> tools)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var call = document.RootElement;
            if (call.ValueKind != JsonValueKind.Object ||
                !call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !call.TryGetProperty("arguments", out var values) || values.ValueKind != JsonValueKind.Object)
            {
                throw InvalidToolCall();
            }
            var tool = tools.OfType<AIFunctionDeclaration>().FirstOrDefault(t => t.Name == name.GetString())
                ?? throw InvalidToolCall();
            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var argument in values.EnumerateObject())
            {
                if (arguments.ContainsKey(argument.Name) ||
                    !tool.JsonSchema.TryGetProperty("properties", out var properties) ||
                    !properties.TryGetProperty(argument.Name, out var property))
                {
                    throw InvalidToolCall();
                }
                // Strings in the JSON format are JSON-encoded; XML parameter strings are raw.
                if (property.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "string")
                {
                    if (argument.Value.ValueKind != JsonValueKind.String) throw InvalidToolCall();
                    arguments.Add(argument.Name, argument.Value.GetString());
                }
                else
                {
                    arguments.Add(argument.Name, ParseArgument(argument.Value.GetRawText(), property));
                }
            }
            if (tool.JsonSchema.TryGetProperty("required", out var required) &&
                required.EnumerateArray().Any(p => !arguments.ContainsKey(p.GetString()!)))
            {
                throw InvalidToolCall();
            }
            return new FunctionCallContent($"call_{Guid.NewGuid():N}", tool.Name, arguments);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Foundry Local returned invalid JSON in a tool call.", exception);
        }
    }

    private static object? ParseArgument(string value, JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            throw InvalidToolCall();
        }
        if (type.GetString() == "string")
        {
            return value;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(value);
            var valid = type.GetString() switch
            {
                "object" => parsed.ValueKind == JsonValueKind.Object,
                "array" => parsed.ValueKind == JsonValueKind.Array,
                "boolean" => parsed.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => parsed.ValueKind == JsonValueKind.Number,
                "integer" => parsed.ValueKind == JsonValueKind.Number && parsed.TryGetInt64(out _),
                "null" => parsed.ValueKind == JsonValueKind.Null,
                _ => false
            };
            return valid ? parsed : throw InvalidToolCall();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Foundry Local returned invalid JSON in a tool argument.", exception);
        }
    }

    private static InvalidOperationException InvalidToolCall() =>
        new("Foundry Local returned an invalid or unsupported textual tool call. " +
            "The function and arguments must match the tools supplied to the agent.");

    [GeneratedRegex(@"<tool_call>\s*(.*?)\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallPattern();

    [GeneratedRegex(@"\A\s*<function=([^<>\s]+)>\s*(.*?)\s*</function>\s*\z", RegexOptions.Singleline)]
    private static partial Regex FunctionPattern();

    [GeneratedRegex(@"<parameter=([^<>\s]+)>\s*(.*?)\s*</parameter>", RegexOptions.Singleline)]
    private static partial Regex ParameterPattern();
}
