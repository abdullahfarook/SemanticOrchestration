using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace SemanticOrchestration;

public static class Utils
{
    static string openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
                                 throw new Exception("OPENAI_API_KEY not found");

    public static Kernel CreateKernelWithChatCompletion()
    {
        return Kernel
            .CreateBuilder()
            .AddOpenAIChatCompletion("gpt-4.1-mini", openAiApiKey)
            .Build();
    }
}

/// <summary>
/// Agent wrapper that short-circuits invocation based on a stop predicate over the thread.
/// If stop predicate is true, it yields no response and returns immediately, allowing
/// sequential orchestration to effectively terminate early.
/// </summary>
public sealed class TerminatingGuardAgent : Agent
{
    private readonly Agent _inner;
    private readonly Func<ICollection<ChatMessageContent>, bool> _shouldStop;

    public TerminatingGuardAgent(Agent inner, Func<ICollection<ChatMessageContent>, bool> shouldStop)
    {
        _inner = inner;
        _shouldStop = shouldStop;
        Name = inner.Name;
        Description = inner.Description;
        Instructions = inner.Instructions;
        Kernel = inner.Kernel;
    }

    public override async IAsyncEnumerable<AgentResponseItem<ChatMessageContent>> InvokeAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (_shouldStop(messages))
        {
            Console.WriteLine($"⏹ [{Name}] Termination marker detected. Skipping.");
            yield break;
        }

        await foreach (var item in _inner.InvokeAsync(messages, thread, options, cancellationToken))
        {
            yield return item;
        }
    }

    public override async IAsyncEnumerable<AgentResponseItem<StreamingChatMessageContent>> InvokeStreamingAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (_shouldStop(messages))
        {
            Console.WriteLine($"⏹ [{Name}] Termination marker detected. Skipping (streaming).");
            yield break;
        }

        await foreach (var item in _inner.InvokeStreamingAsync(messages, thread, options, cancellationToken))
        {
            yield return item;
        }
    }

    protected override IEnumerable<string> GetChannelKeys()
    {
        return _inner.GetType().GetMethod("GetChannelKeys",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is { } m
            ? (IEnumerable<string>)m.Invoke(_inner, null)!
            : [typeof(TerminatingGuardAgent).FullName!];
    }

    protected override Task<AgentChannel> CreateChannelAsync(CancellationToken cancellationToken)
    {
        var m = _inner.GetType().GetMethod("CreateChannelAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, [typeof(CancellationToken)], null);
        return m != null
            ? (Task<AgentChannel>)m.Invoke(_inner, [cancellationToken])!
            : Task.FromException<AgentChannel>(new NotImplementedException());
    }

    protected override Task<AgentChannel> RestoreChannelAsync(string channelState, CancellationToken cancellationToken)
    {
        var m = _inner.GetType().GetMethod("RestoreChannelAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, [typeof(string), typeof(CancellationToken)], null);
        return m != null
            ? (Task<AgentChannel>)m.Invoke(_inner, [channelState, cancellationToken])!
            : Task.FromException<AgentChannel>(new NotImplementedException());
    }
}