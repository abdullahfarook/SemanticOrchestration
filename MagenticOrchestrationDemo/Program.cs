using System.Diagnostics.CodeAnalysis;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Magentic;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

internal class Program
{
    [Experimental("SKEXP0110")]
    private static async Task Main(string[] args)
    {
        Console.WriteLine("Semantic Kernel - Magentic Orchestration Demo\n");

        var openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(openAiApiKey))
        {
            Console.WriteLine("Please set the OPENAI_API_KEY environment variable.");
            return;
        }

        var modelId = Environment.GetEnvironmentVariable("OPENAI_CHAT_MODEL");
        if (string.IsNullOrWhiteSpace(modelId))
        {
            // Reasonable default for demo; override via OPENAI_CHAT_MODEL
            modelId = "gpt-4o-mini";
        }

        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(modelId, openAiApiKey);
        var kernel = builder.Build();

        #pragma warning disable SKEXP001
        var researchAgent = new ChatCompletionAgent
        {
            Name = "ResearchAgent",
            Description = "Finds and summarizes information succinctly.",
            Instructions = "You are a focused researcher. Provide concise, sourced summaries when possible. Avoid code.",
            Kernel = kernel,
        };

        var coderAgent = new ChatCompletionAgent
        {
            Name = "CoderAgent",
            Description = "Writes code and performs lightweight calculations.",
            Instructions = "You are a precise software engineer. When helpful, provide short runnable code snippets and explain outputs briefly.",
            Kernel = kernel,
        };

        var reviewerAgent = new ChatCompletionAgent
        {
            Name = "ReviewerAgent",
            Description = "Reviews and synthesizes the final answer for clarity and actionability.",
            Instructions = "You are an editor. Combine prior messages into a single clear, actionable answer. Keep it brief.",
            Kernel = kernel,
        };

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var execution = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.2,
        };

        var manager = new StandardMagenticManager(chatService, execution)
        {
            MaximumInvocationCount = 8,
        };

        string? currentRole = null;
        bool isNewMessage = true;
        var orchestration = new MagenticOrchestration(manager, researchAgent, coderAgent, reviewerAgent)
        {
            StreamingResponseCallback = (response, final) =>
            {
                var roleLabel = response.AuthorName?.ToString() ?? "unknown";
                if (isNewMessage || !string.Equals(currentRole, roleLabel, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isNewMessage)
                    {
                        Console.WriteLine();
                    }
                    Console.Write($"[{roleLabel}] ");
                    currentRole = roleLabel;
                    isNewMessage = false;
                }

                Console.Write(response.Content);
                if (final)
                {
                    Console.WriteLine();
                    isNewMessage = true;
                    currentRole = null;
                }
                return ValueTask.CompletedTask;
            }
        };
        #pragma warning restore SKEXP001

        await using var runtime = new InProcessRuntime();
        await runtime.StartAsync();

        var userTask = args.Length > 0
            ? string.Join(" ", args)
            : "Compare the pros/cons of Redis vs. PostgreSQL for caching in a .NET web API and provide a short code example for each integration.";

        Console.WriteLine($"User task: {userTask}\n");

        var result = await orchestration.InvokeAsync(userTask, runtime);
        Console.WriteLine("=== Final Output ===\n");
        var finalOutput = await result.GetValueAsync();
        Console.WriteLine(finalOutput);

        await runtime.RunUntilIdleAsync();
    }
}
