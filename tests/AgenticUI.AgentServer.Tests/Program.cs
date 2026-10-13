using AgenticUI.AgentServer;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;

// Dependency-free regression runner: dotnet run --project tests/AgenticUI.AgentServer.Tests
const string weatherXml = """
    <tool_call>
    <function=get_weather>
    <parameter=location>
    Chicago
    </parameter>
    </function>
    </tool_call>
    """;
var weather = AIFunctionFactory.Create((string location) => $"Sunny in {location}", "get_weather");
var options = new ChatOptions { Tools = [weather] };
var messages = new[] { new ChatMessage(ChatRole.User, "Weather in Chicago?") };

const string weatherJson = """
    <tool_call>{"name":"get_weather","arguments":{"location":"Chicago"}}</tool_call>
    """;
using (var client = new FoundryLocalToolCallChatClient(new StubClient(() => TextResponse(weatherJson))))
{
    var response = await client.GetStreamingResponseAsync(messages, options).ToChatResponseAsync();
    var call = response.Messages.Single().Contents.OfType<FunctionCallContent>().Single();
    Check(call.Name == "get_weather" && (string?)call.Arguments!["location"] == "Chicago", "JSON-in-tag weather arguments");
}

using (var client = new FoundryLocalToolCallChatClient(new StubClient(() => TextResponse("{\"name\":\"weather_report\",\"arguments\":{\"location\":\"Chicago\"}}"))))
{
    Check(!(await client.GetResponseAsync(messages, options)).Messages.Single().Contents.OfType<FunctionCallContent>().Any(),
        "ordinary JSON is not executed");
}

using (var client = new FoundryLocalToolCallChatClient(new StubClient(() =>
    TextResponse("{\"name\":\"get_weather\",\"arguments\":{\"location\":\"Chicago\"}}"))))
{
    var call = (await client.GetResponseAsync(messages, options)).Messages.Single().Contents.OfType<FunctionCallContent>().Single();
    Check(call.Name == "get_weather" && (string?)call.Arguments!["location"] == "Chicago", "bare JSON tool envelope");
}

using (var client = Foundry.CreateAgentChatClient(new FoundryOptions
{
    Provider = "Local", Endpoint = "http://localhost:1234", Model = "another-model",
    LocalToolCallCompatibility = false
}))
{
    Check(client is not FoundryLocalToolCallChatClient, "compatibility can be disabled for native streaming");
}
foreach (var model in new[] { "qwen3.5-2b", "phi-4-mini", "qwen2.5-7b", "another-model" })
{
    using var client = Foundry.CreateAgentChatClient(new FoundryOptions
    {
        Provider = "Local", Endpoint = "http://localhost:1234", Model = model
    });
    Check(client is FoundryLocalToolCallChatClient, $"model selection supports {model}");
}

using (var client = new FoundryLocalToolCallChatClient(new StubClient(() => TextResponse(weatherXml))))
{
    var response = await client.GetResponseAsync(messages, options);
    var call = response.Messages.Single().Contents.OfType<FunctionCallContent>().Single();
    Check(call.Name == "get_weather" && (string?)call.Arguments!["location"] == "Chicago", "Qwen weather arguments");
    Check(response.FinishReason == ChatFinishReason.ToolCalls && response.Text == "", "XML replaced by tool content");
    var streamed = await client.GetStreamingResponseAsync(messages, options).ToChatResponseAsync();
    Check(streamed.Messages.Single().Contents.OfType<FunctionCallContent>().Single().Name == "get_weather", "streaming tool content");
}

using (var client = new FoundryLocalToolCallChatClient(new StubClient(() => TextResponse(weatherXml))))
{
    Check((await client.GetStreamingResponseAsync(messages).ToChatResponseAsync()).Text == weatherXml,
        "no tools: preserve ordinary streaming");
}

var typed = AIFunctionFactory.Create((decimal amount, bool enabled, string[] items) => "ok", "typed");
const string typedXml = """
    <tool_call><function=typed>
    <parameter=amount>12.50</parameter>
    <parameter=enabled>true</parameter>
    <parameter=items>["a","b"]</parameter>
    </function></tool_call>
    """;
const string typedJson = """
    <tool_call>{"name":"typed","arguments":{"amount":12.50,"enabled":true,"items":["a","b"]}}</tool_call>
    """;
foreach (var typedText in new[] { typedXml, typedJson })
{
    using var client = new FoundryLocalToolCallChatClient(new StubClient(() => TextResponse(typedText)));
    var response = await client.GetResponseAsync(messages, new ChatOptions { Tools = [typed] });
    var call = response.Messages.Single().Contents.OfType<FunctionCallContent>().Single();
    Check(((JsonElement)call.Arguments!["amount"]!).GetDecimal() == 12.50m, "numeric argument");
    Check(((JsonElement)call.Arguments["enabled"]!).GetBoolean(), "boolean argument");
    Check((await typed.InvokeAsync(new AIFunctionArguments(call.Arguments)))?.ToString() == "ok", "typed function binding");
}

using (var client = new FoundryLocalToolCallChatClient(new StubClient(() => TextResponse(weatherXml + "\n" + weatherXml))))
{
    var calls = (await client.GetResponseAsync(messages, options)).Messages.Single().Contents.OfType<FunctionCallContent>().ToArray();
    Check(calls.Length == 2 && calls[0].CallId != calls[1].CallId, "multiple calls have unique IDs");
}

using (var client = new FoundryLocalToolCallChatClient(new StubClient(() =>
    new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("native", "get_weather", new Dictionary<string, object?> { ["location"] = "Chicago" })])))))
{
    var call = (await client.GetResponseAsync(messages, options)).Messages.Single().Contents.OfType<FunctionCallContent>().Single();
    Check(call.CallId == "native", "native tool calls preserved");
    var streamedCall = (await client.GetStreamingResponseAsync(messages, options).ToChatResponseAsync())
        .Messages.Single().Contents.OfType<FunctionCallContent>().Single();
    Check(streamedCall.CallId == "native", "native tool calls preserved through streaming interface");
}

using (var client = new FoundryLocalToolCallChatClient(new StubClient(() => TextResponse("Example: " + weatherXml))))
{
    Check((await client.GetResponseAsync(messages, options)).Text.StartsWith("Example:"), "XML examples are text");
}

foreach (var invalid in new[]
{
    weatherXml.Replace("get_weather", "unknown_tool"),
    weatherXml.Replace("location", "unknown_argument"),
    "<tool_call><function=get_weather></function></tool_call>",
    weatherXml.Replace("</tool_call>", ""),
    weatherXml + " trailing prose",
    weatherXml.Replace("</function>", "<parameter=location>Seattle</parameter></function>"),
    weatherJson.Replace("get_weather", "unknown_tool"),
    weatherJson.Replace("location", "unknown_argument"),
    "<tool_call>{\"name\":\"get_weather\",\"arguments\":{}}</tool_call>",
    "<tool_call>{\"name\":\"get_weather\",\"arguments\":{\"location\":123}}</tool_call>",
    "<tool_call>{invalid JSON}</tool_call>",
    "<tool_call>{\"name\":\"get_weather\",\"arguments\":{\"location\":\"Chicago\",\"location\":\"Seattle\"}}</tool_call>"
})
{
    using var client = new FoundryLocalToolCallChatClient(new StubClient(() => TextResponse(invalid)));
    try
    {
        await client.GetResponseAsync(messages, options);
        throw new Exception("Invalid tool call was accepted.");
    }
    catch (InvalidOperationException) { }
}
Console.WriteLine("PASS: malformed, unknown and duplicate calls rejected");

var invocationCount = 0;
var invoked = AIFunctionFactory.Create((string location) => { invocationCount++; return $"Sunny in {location}"; }, "get_weather");
var turn = 0;
using (var client = new ChatClientBuilder(new FoundryLocalToolCallChatClient(new StubClient(() =>
    TextResponse(turn++ == 0 ? weatherXml : "Sunny in Chicago")))).UseFunctionInvocation().Build())
{
    var response = await client.GetStreamingResponseAsync(messages, new ChatOptions { Tools = [invoked] }).ToChatResponseAsync();
    Check(invocationCount == 1 && response.Text.Contains("Sunny in Chicago"), "framework executes normalized tool and continues");
}

if (args.Length == 3 && args[0] == "--live")
{
    var liveInvocations = 0;
    var liveWeather = AIFunctionFactory.Create((string location) =>
    {
        liveInvocations++;
        return $"Sunny in {location}";
    }, "get_weather", "Get the current weather in a location.");
    using var client = new ChatClientBuilder(Foundry.CreateAgentChatClient(new FoundryOptions
    {
        Provider = "Local", Endpoint = args[1], Model = args[2]
    })).UseFunctionInvocation().Build();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var response = await client.GetStreamingResponseAsync(
        [new ChatMessage(ChatRole.System, "Use get_weather to answer weather questions. After the tool returns, answer using its result."),
         new ChatMessage(ChatRole.User, "What is the weather in Chicago?")],
        new ChatOptions { Tools = [liveWeather], MaxOutputTokens = 512 }, timeout.Token).ToChatResponseAsync(timeout.Token);
    Console.WriteLine($"Live callback count: {liveInvocations}; answer: {response.Text}");
    Check(liveInvocations == 1 && !string.IsNullOrWhiteSpace(response.Text) && !response.Text.Contains("<tool_call>"),
        $"live {args[2]} invokes the weather callback and returns an answer");
    Console.WriteLine(response.Text);
}

Console.WriteLine("All Foundry Local tool-call regression checks passed.");

static ChatResponse TextResponse(string text) => new(new ChatMessage(ChatRole.Assistant, text));
static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine($"PASS: {label}");
}

sealed class StubClient(Func<ChatResponse> responseFactory) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => Task.FromResult(responseFactory());

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var update in responseFactory().ToChatResponseUpdates()) yield return update;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
