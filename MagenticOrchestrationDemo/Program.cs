// Program.cs

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.Handoff;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
namespace Caitalyst;

// TODO: Create process if mental agent is called
class Program
{
    static async Task Main(string[] args)
    {
        // Example: using OpenAIChatCompletion (you may need to install and configure accordingly)

        // 1. Setup kernel and chat service
        // Replace with your provider (OpenAI, AzureOpenAI, etc.)
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
        var mental = CreateLoggedAgent("MentalAgent", "Mental support", "Offer empathetic, safe responses.");
        var human = CreateLoggedAgent("HumanAgent", "Human in the loop", "Acts as a placeholder for human responses.");

        var innerHandoffs = OrchestrationHandoffs
            .StartWith(triage)
            .Add(triage, academic, career, mental)
            .Add(mental, human);

        var humanCallbackInner = () =>
        {
            Console.WriteLine("\n--- HUMAN REQUIRED (Inner) ---");
            Console.Write("Human: ");
            return ValueTask.FromResult(new ChatMessageContent(AuthorRole.User, Console.ReadLine() ?? ""));
        };

        var innerOrchestration = new HandoffOrchestration(
            innerHandoffs, triage, academic, career, mental, human)
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
            superTriage,       // entry point
            superTriage,       // start agent
            superTriage,       // fallback
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
    }
}

/// <summary>
/// Wraps an orchestration as if it were a single agent.
/// </summary>
public class OrchestrationAgent : Agent
{
    private readonly HandoffOrchestration _orchestration;

    public OrchestrationAgent(string name, string description,
        HandoffOrchestration orchestration) : base()
    {
        Name = name;
        Description = description;
        _orchestration = orchestration;
    }

    public override async IAsyncEnumerable<AgentResponseItem<ChatMessageContent>> InvokeAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
            null, new[] { typeof(CancellationToken) }, null);
        return (Task<AgentChannel>)method!.Invoke(_wrappedAgent, new object[] { cancellationToken })!;
    }

    protected override Task<AgentChannel> RestoreChannelAsync(string channelState, CancellationToken cancellationToken)
    {
        // Use reflection to access the protected method
        var method = typeof(ChatCompletionAgent).GetMethod("RestoreChannelAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, new[] { typeof(string), typeof(CancellationToken) }, null);
        return (Task<AgentChannel>)method!.Invoke(_wrappedAgent, new object[] { channelState, cancellationToken })!;
    }
}




//
//
//
// class Program
// {
//     static string openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
//     static async Task Main(string[] args)
//     {
//         // Example: using OpenAIChatCompletion (you may need to install and configure accordingly)
//         if (string.IsNullOrEmpty(openAiApiKey))
//         {
//             Console.WriteLine("Please set environment variable OPENAI_API_KEY");
//             return;
//         }
//         
//         // 1. Setup kernel and chat service
//         // Replace with your provider (OpenAI, AzureOpenAI, etc.)
//         var kernel = CreateKernelWithChatCompletion();
//
//
//         // 2. Define agents
//
//         // Triage agent: determines whether we need specialist or general
//         var triageAgent = CreateChatCompletionAgent(
//             kernel: kernel,
//             name: "TriageAgent",
//             description: "Determine whether the user's request needs general help or specialist help",
//             instructions: "You are a triage assistant. Decide if the user's issue is simple or needs a specialist."
//         );
//
//         // Specialist agent: more technical / detailed help
//         var specialistAgent = CreateChatCompletionAgent(
//             kernel: kernel,
//             name: "SpecialistAgent",
//             description: "Give detailed technical support",
//             instructions: "You are a specialist. Help in depth when triage says so."
//         );
//
//         // General agent: basic help
//         var generalAgent = CreateChatCompletionAgent(
//             kernel: kernel,
//             name: "GeneralAgent",
//             description: "Provides general/basic help",
//             instructions: "You are a general support assistant. Give simple, high-level responses."
//         );
//
//         // 3. Define handoff relationships
//
//         var handoffs = OrchestrationHandoffs
//             .StartWith(triageAgent)
//             .Add(triageAgent, specialistAgent, generalAgent) // from triage, can go to either specialist or general
//             .Add(specialistAgent, generalAgent, "If the user decides to not go technical or if specialist cannot help more")
//             .Add(generalAgent, specialistAgent, "If general help is not enough, escalate to specialist");
//
//         // 4. Callbacks
//
//         // Response callback: log what agent says
//         async ValueTask ResponseCallback(ChatMessageContent resp)
//         {
//             // Print agent name and content
//             Console.WriteLine($"[{resp.AuthorName}]: {resp.Content}");
//             await Task.CompletedTask;
//         }
//
//         // Interactive callback: get user input from console when needed
//         async ValueTask<ChatMessageContent> InteractiveCallback()
//         {
//             Console.Write("User: ");
//             string userInput = Console.ReadLine() ?? "";
//             return new ChatMessageContent(AuthorRole.User, userInput);
//         }
//
//         // 5. Build orchestration
//
//         var handoffOrchestration = new HandoffOrchestration(
//             handoffs,
//             triageAgent,
//             specialistAgent,
//             generalAgent
//         )
//         {
//             InteractiveCallback = InteractiveCallback,
//             ResponseCallback = ResponseCallback
//         };
//
//         // 6. Start runtime
//
//         var runtime = new InProcessRuntime();
//         await runtime.StartAsync();
//
//         // 7. Invoke
//
//         Console.WriteLine("=== Semantic Kernel: Sequential + Handoff + Human in the loop Example ===");
//         Console.WriteLine("Type your request, and follow the prompts. (Type 'exit' to quit.)");
//
//         while (true)
//         {
//             Console.Write("You: ");
//             string input = Console.ReadLine();
//             if (input == null || input.Trim().ToLower() == "exit")
//             {
//                 break;
//             }
//
//             // Invoke orchestration with the user input as the “task”
//             var result = await handoffOrchestration.InvokeAsync(input, runtime);
//
//             // Wait for the orchestration to finish and get final value
//             var final = await result.GetValueAsync(TimeSpan.FromMinutes(1));
//
//             Console.WriteLine($"=== Final Output ===\n{final}\n");
//             Console.WriteLine("-------------------------------");
//         }
//
//         // 8. Clean up if needed
//
//         Console.WriteLine("Exiting...");
//     }
//     public static ChatCompletionAgent CreateChatCompletionAgent(string instructions, string? description = null,
//         string? name = null, Kernel? kernel = null)
//     {
//         return new ChatCompletionAgent
//         {
//             Name = name,
//             Description = description,
//             Instructions = instructions,
//             Kernel = kernel ?? CreateKernelWithChatCompletion(),
//         };
//     }
//     public static Kernel CreateKernelWithChatCompletion()
//     {
//         return Kernel
//             .CreateBuilder()
//             .AddOpenAIChatCompletion("gpt-4.1-mini", openAiApiKey)
//             .Build();
//     }
// }

// using System.Diagnostics.CodeAnalysis;
// using System.Text.Json;
// using Microsoft.Extensions.DependencyInjection;
// using Microsoft.Extensions.Logging;
// using Microsoft.SemanticKernel;
// using Microsoft.SemanticKernel.Agents;
// using Microsoft.SemanticKernel.Agents.Magentic;
// using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
// using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
// using Microsoft.SemanticKernel.ChatCompletion;
// using Microsoft.SemanticKernel.Connectors.OpenAI;
// // Filters APIs are not available in this SK version; using events instead
// /// <summary>
// /// AI-driven group chat manager that requests user input when agents ask for clarification
// /// </summary>
// public class HybridAIWithHumanFallbackManager : GroupChatManager
// {
//     private readonly string _topic;
//     private readonly IChatCompletionService _chatCompletion;
//
//     public HybridAIWithHumanFallbackManager(string topic, IChatCompletionService chatCompletion)
//     {
//         _topic = topic;
//         _chatCompletion = chatCompletion;
//     }
//
//     public override ValueTask<GroupChatManagerResult<string>> FilterResults(ChatHistory history, CancellationToken cancellationToken = default)
//     {
//         var prompt = $"""
//             The discussion on '{_topic}' has concluded.
//             Provide a concise summary and final recommendation based on the conversation.
//             """;
//
//         return GetResponseAsync<string>(history, prompt, cancellationToken);
//     }
//
//     public override ValueTask<GroupChatManagerResult<string>> SelectNextAgent(ChatHistory history, GroupChatTeam team, CancellationToken cancellationToken = default)
//     {
//         var participants = string.Join("\n", team.Select(a => $"- {a.Key}: {a.Value.Description}"));
//         var prompt = $"""
//             You are a mediator guiding a discussion on '{_topic}'.
//             Select the next participant to speak based on the conversation flow.
//             Participants:
//             {participants}
//             
//             Reply with only the participant name.
//             """;
//
//         return GetResponseAsync<string>(history, prompt, cancellationToken);
//     }
//
//     public override ValueTask<GroupChatManagerResult<bool>> ShouldRequestUserInput(ChatHistory history, CancellationToken cancellationToken = default)
//     {
//         var lastMessage = history.LastOrDefault();
//         if (lastMessage == null)
//         {
//             return ValueTask.FromResult(new GroupChatManagerResult<bool>(false) { Reason = "No messages yet." });
//         }
//
//         // Only consider AI/agent messages, not user messages
//         if (lastMessage.Role == AuthorRole.Assistant || lastMessage.Role == AuthorRole.Tool)
//         {
//             var text = lastMessage.ToString();
//             if (IsClarificationRequest(text))
//             {
//                 return ValueTask.FromResult(new GroupChatManagerResult<bool>(true) { Reason = "Agent requested clarification." });
//             }
//         }
//
//         return ValueTask.FromResult(new GroupChatManagerResult<bool>(false) { Reason = "No clarification detected." });
//     }
//
//     public override async ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(ChatHistory history, CancellationToken cancellationToken = default)
//     {
//         var result = await base.ShouldTerminate(history, cancellationToken);
//         if (!result.Value)
//         {
//             var prompt = $"""
//                 You are a mediator guiding a discussion on '{_topic}'.
//                 Determine if the discussion has reached a clear, actionable conclusion.
//                 Reply with True to end, or False to continue.
//                 """;
//
//             result = await GetResponseAsync<bool>(history, prompt, cancellationToken);
//         }
//         return result;
//     }
//
//     private static bool IsClarificationRequest(string text)
//     {
//         if (string.IsNullOrWhiteSpace(text))
//             return false;
//
//         var normalized = text.ToLowerInvariant();
//
//         // Simple heuristic for clarity-seeking phrases
//         var cues = new[]
//         {
//             "clarify", "clarity", "could you specify", "can you specify",
//             "what do you mean", "could you provide more detail", "need more detail",
//             "unclear", "ambiguous", "please elaborate", "more information",
//             "requirements?", "what exactly", "which one", "which exactly",
//             "can you confirm", "do you want", "should it", "could you clarify"
//         };
//
//         if (normalized.Contains('?'))
//         {
//             foreach (var cue in cues)
//             {
//                 if (normalized.Contains(cue))
//                 {
//                     return true;
//                 }
//             }
//         }
//
//         return false;
//     }
//
//     private async ValueTask<GroupChatManagerResult<TValue>> GetResponseAsync<TValue>(ChatHistory history, string prompt, CancellationToken cancellationToken = default)
//     {
//         var executionSettings = new OpenAIPromptExecutionSettings { ResponseFormat = typeof(GroupChatManagerResult<TValue>) };
//         var request = new ChatHistory(history) { new ChatMessageContent(AuthorRole.System, prompt) };
//
//         // Use streaming for better user experience
//         var responseBuilder = new System.Text.StringBuilder();
//
//         await foreach (var chunk in _chatCompletion.GetStreamingChatMessageContentsAsync(request, executionSettings, kernel: null, cancellationToken))
//         {
//             var content = chunk.Content;
//             if (!string.IsNullOrEmpty(content))
//             {
//                 responseBuilder.Append(content);
//
//                 // Stream the content to console for real-time feedback
//                 Console.Write(content);
//                 await Task.Delay(10); // Small delay for streaming effect
//             }
//         }
//
//         Console.WriteLine(); // New line after streaming
//
//         var responseText = responseBuilder.ToString();
//         return JsonSerializer.Deserialize<GroupChatManagerResult<TValue>>(responseText) ??
//                throw new InvalidOperationException($"Failed to parse response: {responseText}");
//     }
// }
// public class AgentLoggingFilter : IAutoFunctionInvocationFilter
// {
//     private readonly ILogger<AgentLoggingFilter> _logger;
//
//     public AgentLoggingFilter(ILogger<AgentLoggingFilter> logger)
//     {
//         _logger = logger;
//     }
//
//     
//
//     public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
//     {
//         // You may need some way to map the function invocation back to agent.
//         // If your agent sets some context variable (e.g. “currentAgent”) this can be read here,
//         // or if the "context" includes metadata about agent, use that.
//         string pluginName = context.Function.PluginName;
//         string functionName = context.Function.Name;
//
//         // Log before invocation
//         Console.WriteLine($"Function invocation: {pluginName}.{functionName}");
//
//         await next(context);
//
//         // Log after invocation
//     }
// }
// internal class Program
// {
//     private static async Task Main(string[] args)
//     {
//         Console.WriteLine("Semantic Kernel - Magentic Orchestration Demo\n");
//
//         var openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
//         if (string.IsNullOrWhiteSpace(openAiApiKey))
//         {
//             Console.WriteLine("Please set the OPENAI_API_KEY environment variable.");
//             return;
//         }
//
//         var modelId = Environment.GetEnvironmentVariable("OPENAI_CHAT_MODEL");
//         if (string.IsNullOrWhiteSpace(modelId))
//         {
//             // Reasonable default for demo; override via OPENAI_CHAT_MODEL
//             modelId = "gpt-4o-mini";
//         }
//
//         var builder = Kernel.CreateBuilder();
//         builder.AddOpenAIChatCompletion(modelId, openAiApiKey);
//         builder.Services.AddSingleton<IAutoFunctionInvocationFilter, AgentLoggingFilter>();
//         var kernel = builder.Build();
//
//         #pragma warning disable SKEXP001
//         var researchAgent = new ChatCompletionAgent
//         {
//             Name = "ResearchAgent",
//             Description = "Finds and summarizes information succinctly.",
//             Instructions = "You are a focused researcher. Provide concise, sourced summaries when possible. Avoid code.",
//             Kernel = kernel,
//             
//         };
//
//         var coderAgent = new ChatCompletionAgent
//         {
//             Name = "CoderAgent",
//             Description = "Writes code and performs lightweight calculations.",
//             Instructions = "You are a precise software engineer. When helpful, provide short runnable code snippets and explain outputs briefly.",
//             Kernel = kernel,
//         };
//
//         var reviewerAgent = new ChatCompletionAgent
//         {
//             Name = "ReviewerAgent",
//             Description = "Reviews and synthesizes the final answer for clarity and actionability.",
//             Instructions = "You are an editor. Combine prior messages into a single clear, actionable answer. Keep it brief.",
//             Kernel = kernel,
//         };
//
//         var chatService = kernel.GetRequiredService<IChatCompletionService>();
//         var execution = new OpenAIPromptExecutionSettings
//         {
//             Temperature = 0.2,
//         };
//
//         var manager = new AgentGroupChat(researchAgent, coderAgent, reviewerAgent);
//         var manager2 = new GroupChatOrchestration(new StandardMagenticManager(manager, execution));
//
//         string? currentRole = null;
//         bool isNewMessage = true;
//         var orchestration = new MagenticOrchestration(manager, researchAgent, coderAgent, reviewerAgent)
//         {
//             
//             StreamingResponseCallback = (response, final) =>
//             {
//                 var roleLabel = response.AuthorName?.ToString() ?? "unknown";
//                 if (isNewMessage || !string.Equals(currentRole, roleLabel, StringComparison.OrdinalIgnoreCase))
//                 {
//                     if (!isNewMessage)
//                     {
//                         Console.WriteLine();
//                     }
//                     Console.Write($"[{roleLabel}] ");
//                     currentRole = roleLabel;
//                     isNewMessage = false;
//                 }
//
//                 Console.Write(response.Content);
//                 if (final)
//                 {
//                     Console.WriteLine();
//                     isNewMessage = true;
//                     currentRole = null;
//                 }
//                 return ValueTask.CompletedTask;
//             }
//         };
//         #pragma warning restore SKEXP001
//
//         await using var runtime = new InProcessRuntime();
//         await runtime.StartAsync();
//
//         var userTask = args.Length > 0
//             ? string.Join(" ", args)
//             : "Compare the pros/cons of Redis vs. PostgreSQL for caching in a .NET web API and provide a short code example for each integration.";
//
//         Console.WriteLine($"User task: {userTask}\n");
//
//         var result = await orchestration.InvokeAsync(userTask, runtime);
//         Console.WriteLine("=== Final Output ===\n");
//         var finalOutput = await result.GetValueAsync();
//         Console.WriteLine(finalOutput);
//
//         await runtime.RunUntilIdleAsync();
//     }
// }
//
//
// public class StandardMagenticManagerV2(StandardMagenticManager standardMagenticManager):GroupChatManager
// {
//     
// }

public static class Utils
{
    static string openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?? throw new Exception("OPENAI_API_KEY not found");
    public static Kernel CreateKernelWithChatCompletion()
    {
        return Kernel
            .CreateBuilder()
            .AddOpenAIChatCompletion("gpt-4.1-mini", openAiApiKey)
            .Build();
    }
}