using CSnakes.Runtime;
using CSnakes.Runtime.Locators;
using CSnakes.Runtime.PackageManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.Handoff;
using Microsoft.SemanticKernel.Agents.Orchestration.Sequential;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using SemanticOrchestration;

var pythonHomePath = AppContext.BaseDirectory;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddFilter("CSnakes", LogLevel.Debug);
builder.Services
    .WithPython()
    .WithHome(pythonHomePath)
    .FromRedistributable(RedistributablePythonVersion.Python3_10)
    .WithVirtualEnvironment(Path.Combine(pythonHomePath, ".venv"))
    .WithPipInstaller();

var app = builder.Build();
var installer = app.Services.GetRequiredService<IPythonPackageInstaller>();
// await installer.InstallPackagesFromRequirements("requirements.txt");
// await installer.InstallPackage("llmsherpa");
var env = app.Services.GetRequiredService<IPythonEnvironment>();

var hello = env.Hello();
Console.WriteLine(hello.Greetings("World"));
Console.WriteLine(hello.ReadPdf("World"));


var kernel = Utils.CreateKernelWithChatCompletion();
var runtime = new InProcessRuntime();

ChatCompletionAgent CreateAgent(string name, string description, string instructions)
{
    return new ChatCompletionAgent
    {
        Name = name,
        Description = description,
        Instructions = instructions,
        Kernel = kernel
    };
}

// Create a wrapper that adds logging to each agent
LoggedChatCompletionAgent CreateLoggedAgent(string name, string description, string instructions)
{
    var agent = CreateAgent(name, description, instructions);

    // Override the InvokeAsync method to add logging
    return new LoggedChatCompletionAgent(agent);
}

// ---------------- INNER ORCHESTRATION ----------------
var triage = CreateLoggedAgent("InnerTriageAgent", "Inner triage",
    "Decide whether user needs academic, career, or mental support.");
var academic = CreateLoggedAgent("AcademicAgent", "Academic expert", "Answer academic questions.");
var career = CreateLoggedAgent("CareerAgent", "Career advisor", "Help with jobs and careers.");
// Build MentalAgent as a SequentialOrchestration of two evaluators: mental and physical health
var mentalEvaluator = CreateLoggedAgent(
    "MentalHealthEvaluator",
    "Evaluates mental health concerns",
    "Assess the user's mental health concerns. Provide supportive, empathetic guidance and identify if professional help may be needed. Keep it concise and safe.");

var physicalEvaluator = CreateLoggedAgent(
    "PhysicalHealthEvaluator",
    "Evaluates physical health concerns",
    "You must first ask the user to describe their symptoms in detail (onset, duration, severity, triggers, relevant medical history). Do not provide guidance until they've listed symptoms. After they respond, offer general wellness suggestions and clearly advise consulting a healthcare professional when appropriate. Keep it concise, supportive, and safe. End your first reply with a single clear question requesting their symptoms.");
// Wrap physical evaluator with a terminating guard that skips execution if <STOP/> is present in the thread
var guardedPhysical = new TerminatingGuardAgent(
    physicalEvaluator,
    messages =>
        messages.Any(m => m.Content != null && m.Content.Contains("<STOP/>", StringComparison.OrdinalIgnoreCase))
);
var mentalSequential = new SequentialOrchestration(
    mentalEvaluator,
    guardedPhysical)
{
    ResponseCallback = (resp) =>
    {
        Console.WriteLine($"\n🧠 [MentalSequential] → {resp.Content}");
        return ValueTask.CompletedTask;
    }
};

// Wrap the sequential orchestration as an agent so it can participate in handoffs
var mentalAgent = new OrchestrationAgent(
    "MentalAgent",
    "Handles mental and physical health evaluation",
    mentalSequential);

var innerHandoffs = OrchestrationHandoffs
    .StartWith(triage)
    .Add(triage, academic, career, mentalAgent);

var humanCallbackInner = () =>
{
    Console.WriteLine("\n--- HUMAN REQUIRED (Inner) ---");
    Console.Write("Human: ");
    return ValueTask.FromResult(new ChatMessageContent(AuthorRole.User, Console.ReadLine() ?? ""));
};

var innerOrchestration = new HandoffOrchestration(
    innerHandoffs, triage, academic, career, mentalAgent)
{
    InteractiveCallback = () => humanCallbackInner(),
    ResponseCallback = (resp) =>
    {
        Console.WriteLine($"\n🔄 [---InnerOrchestration---] → {resp.Content}");
        return ValueTask.CompletedTask;
    }
};

var studentSupportAgent = new OrchestrationAgent(
    "StudentSupportAgent",
    "Handles academic, career, and mental queries",
    innerOrchestration
);

// ---------------- OUTER ORCHESTRATION ----------------
var superTriage = CreateLoggedAgent("superTriage", "Outer triage",
    "Route user requests either to StudentSupportAgent (for academic/career/mental) or to BillingAgent (for invoices).");
var billingAgent = CreateLoggedAgent("billing", "Billing expert",
    "Answer questions about invoices, payments, and refunds.");

var superHandoffs = OrchestrationHandoffs
    .StartWith(superTriage)
    .Add(superTriage, studentSupportAgent, billingAgent);

var superOrchestration = new HandoffOrchestration(
    superHandoffs,
    superTriage, // entry point
    superTriage, // start agent
    superTriage, // fallback
    studentSupportAgent,
    billingAgent
)
{
    InteractiveCallback = () =>
    {
        Console.WriteLine("\n--- HUMAN REQUIRED (Outer) ---");
        Console.Write("Human: ");
        return ValueTask.FromResult(new ChatMessageContent(AuthorRole.User, Console.ReadLine() ?? ""));
    },
    ResponseCallback = (resp) =>
    {
        Console.WriteLine($"\n🎯 [---SuperOrchestration---] → {resp.Content}");
        return ValueTask.CompletedTask;
    }
};

// ---------------- RUN DEMO ----------------
Console.WriteLine("Enter a prompt (academic, career, mental, or billing topic):");
string prompt = Console.ReadLine() ?? "I need help with paying tuition invoice.";

Console.WriteLine($"\n🚀 [Main] Starting orchestration with: \"{prompt}\"");

// Start the runtime
await runtime.StartAsync();

// Run the orchestration
var result = await superOrchestration.InvokeAsync(prompt, runtime);
string text = await result.GetValueAsync(TimeSpan.FromSeconds(300));

Console.WriteLine("\n=== DONE ===");
Console.WriteLine(text);

await runtime.RunUntilIdleAsync();

/// <summary>
/// Wraps an orchestration as if it were a single agent.
/// </summary>
public class OrchestrationAgent : Agent
{
    private readonly AgentOrchestration<string, string> _orchestration;

    public OrchestrationAgent(string name, string description,
        AgentOrchestration<string, string> orchestration) : base()
    {
        Name = name;
        Description = description;
        _orchestration = orchestration;
    }

    public override async IAsyncEnumerable<AgentResponseItem<ChatMessageContent>> InvokeAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Convert messages to a single string for the orchestration
        string input = string.Join(" ", messages.Select(m => m.Content));

        Console.WriteLine($"\n🏢 [{this.Name}] Processing: \"{input}\"");

        // Create a runtime for the orchestration
        var runtime = new InProcessRuntime();
        await runtime.StartAsync();

        // Invoke the orchestration
        var result = await _orchestration.InvokeAsync(input, runtime);
        string text = await result.GetValueAsync(TimeSpan.FromSeconds(30));

        Console.WriteLine($"✅ [{this.Name}] Completed: {text}");

        // Return the result as a response item
        yield return new AgentResponseItem<ChatMessageContent>(
            new ChatMessageContent(AuthorRole.Assistant, text),
            thread ?? new ChatHistoryAgentThread());

        await runtime.RunUntilIdleAsync();
    }

    public override async IAsyncEnumerable<AgentResponseItem<StreamingChatMessageContent>> InvokeStreamingAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // For simplicity, convert streaming to non-streaming
        await foreach (var response in InvokeAsync(messages, thread, options, cancellationToken))
        {
            yield return new AgentResponseItem<StreamingChatMessageContent>(
                new StreamingChatMessageContent(AuthorRole.Assistant, response.Message.Content),
                response.Thread);
        }
    }

    protected override IEnumerable<string> GetChannelKeys()
    {
        yield return typeof(OrchestrationAgent).FullName!;
    }

    protected override Task<AgentChannel> CreateChannelAsync(CancellationToken cancellationToken)
    {
        // This is a simplified implementation - in practice you'd need to create a proper channel
        throw new NotImplementedException("OrchestrationAgent channel creation not implemented");
    }

    protected override Task<AgentChannel> RestoreChannelAsync(string channelState, CancellationToken cancellationToken)
    {
        // This is a simplified implementation - in practice you'd need to restore a proper channel
        throw new NotImplementedException("OrchestrationAgent channel restoration not implemented");
    }
}

/// <summary>
/// A wrapper around ChatCompletionAgent that adds logging to show when each agent is invoked.
/// </summary>
public class LoggedChatCompletionAgent : Agent
{
    private readonly ChatCompletionAgent _wrappedAgent;

    public LoggedChatCompletionAgent(ChatCompletionAgent agent)
    {
        _wrappedAgent = agent;
        // Copy properties from the wrapped agent
        Name = agent.Name;
        Description = agent.Description;
        Instructions = agent.Instructions;
        Kernel = agent.Kernel;
    }

    public override async IAsyncEnumerable<AgentResponseItem<ChatMessageContent>> InvokeAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        string input = string.Join(" ", messages.Select(m => m.Content));
        Console.WriteLine($"\n🤖 [{this.Name}] Processing: \"{input}\"");

        await foreach (var response in _wrappedAgent.InvokeAsync(messages, thread, options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(response.Message.Content))
            {
                Console.WriteLine($"✅ [{this.Name}] Response: {response.Message.Content}");
            }

            yield return response;
        }

        Console.WriteLine($"🏁 [{this.Name}] Completed");
    }

    public override async IAsyncEnumerable<AgentResponseItem<StreamingChatMessageContent>> InvokeStreamingAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        string input = string.Join(" ", messages.Select(m => m.Content));
        Console.WriteLine($"\n🤖 [{this.Name}] Processing (streaming): \"{input}\"");

        await foreach (var response in _wrappedAgent.InvokeStreamingAsync(messages, thread, options, cancellationToken))
        {
            Console.WriteLine($"✅ [{this.Name}] Streaming: {response.Message.Content}");
            yield return response;
        }

        Console.WriteLine($"🏁 [{this.Name}] Streaming completed");
    }

    protected override IEnumerable<string> GetChannelKeys()
    {
        // Use reflection to access the protected method
        var method = typeof(ChatCompletionAgent).GetMethod("GetChannelKeys",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (IEnumerable<string>)method!.Invoke(_wrappedAgent, null)!;
    }

    protected override Task<AgentChannel> CreateChannelAsync(CancellationToken cancellationToken)
    {
        // Use reflection to access the protected method
        var method = typeof(ChatCompletionAgent).GetMethod("CreateChannelAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, [typeof(CancellationToken)], null);
        return (Task<AgentChannel>)method!.Invoke(_wrappedAgent, [cancellationToken])!;
    }

    protected override Task<AgentChannel> RestoreChannelAsync(string channelState, CancellationToken cancellationToken)
    {
        // Use reflection to access the protected method
        var method = typeof(ChatCompletionAgent).GetMethod("RestoreChannelAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, [typeof(string), typeof(CancellationToken)], null);
        return (Task<AgentChannel>)method!.Invoke(_wrappedAgent, [channelState, cancellationToken])!;
    }
}
