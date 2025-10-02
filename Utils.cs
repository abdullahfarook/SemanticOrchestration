using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

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
/// Service to manage shared chat history across all agents in the orchestration.
/// This ensures that all agents have access to the complete conversation context.
/// </summary>
public class SharedChatHistoryService
{
    private readonly List<ChatMessageContent> _sharedHistory = new();
    private readonly object _lock = new();

    /// <summary>
    /// Adds a message to the shared chat history.
    /// </summary>
    /// <param name="message">The message to add</param>
    public void AddMessage(ChatMessageContent message)
    {
        lock (_lock)
        {
            // Filter out tool messages and other problematic message types
            // that can cause issues when shared across agents
            if (ShouldIncludeMessage(message))
            {
                _sharedHistory.Add(message);
                Console.WriteLine($"📝 [SharedHistory] Added {message.Role}: {message.Content?.Substring(0, Math.Min(50, message.Content?.Length ?? 0))}...");
            }
            else
            {
                Console.WriteLine($"🚫 [SharedHistory] Filtered out {message.Role} message");
            }
        }
    }

    /// <summary>
    /// Determines if a message should be included in the shared history.
    /// Filters out tool messages and other problematic message types.
    /// </summary>
    private static bool ShouldIncludeMessage(ChatMessageContent message)
    {
        // Include user and assistant messages
        if (message.Role == AuthorRole.User || message.Role == AuthorRole.Assistant)
        {
            return true;
        }

        // Filter out tool messages and system messages that might cause issues
        if (message.Role == AuthorRole.Tool || message.Role == AuthorRole.System)
        {
            return false;
        }

        // Include other message types by default
        return true;
    }

    /// <summary>
    /// Gets all messages from the shared chat history.
    /// </summary>
    /// <returns>A copy of all messages in the shared history</returns>
    public ICollection<ChatMessageContent> GetAllMessages()
    {
        lock (_lock)
        {
            return new List<ChatMessageContent>(_sharedHistory);
        }
    }

    /// <summary>
    /// Gets all messages from the shared chat history, filtered for compatibility.
    /// </summary>
    /// <returns>A copy of all compatible messages in the shared history</returns>
    public ICollection<ChatMessageContent> GetCompatibleMessages()
    {
        lock (_lock)
        {
            return _sharedHistory.Where(ShouldIncludeMessage).ToList();
        }
    }

    /// <summary>
    /// Gets the last N messages from the shared chat history.
    /// </summary>
    /// <param name="count">Number of recent messages to retrieve</param>
    /// <returns>A copy of the last N messages</returns>
    public ICollection<ChatMessageContent> GetRecentMessages(int count = 10)
    {
        lock (_lock)
        {
            return _sharedHistory.TakeLast(count).ToList();
        }
    }

    /// <summary>
    /// Clears the shared chat history.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _sharedHistory.Clear();
            Console.WriteLine("🗑️ [SharedHistory] Cleared all messages");
        }
    }

    /// <summary>
    /// Removes problematic messages from the shared history to prevent API errors.
    /// </summary>
    public void CleanProblematicMessages()
    {
        lock (_lock)
        {
            var originalCount = _sharedHistory.Count;
            _sharedHistory.RemoveAll(m => !ShouldIncludeMessage(m));
            var removedCount = originalCount - _sharedHistory.Count;
            if (removedCount > 0)
            {
                Console.WriteLine($"🧹 [SharedHistory] Cleaned {removedCount} problematic messages");
            }
        }
    }

    /// <summary>
    /// Gets the count of messages in the shared history.
    /// </summary>
    public int MessageCount
    {
        get
        {
            lock (_lock)
            {
                return _sharedHistory.Count;
            }
        }
    }
}

/// <summary>
/// A shared chat history thread that maintains conversation context across all agents.
/// </summary>
public class SharedChatHistoryThread : AgentThread
{
    private readonly SharedChatHistoryService _sharedHistory;

    public SharedChatHistoryThread(SharedChatHistoryService sharedHistory)
    {
        _sharedHistory = sharedHistory;
    }

    /// <summary>
    /// Gets all messages from the shared chat history.
    /// </summary>
    public ICollection<ChatMessageContent> Messages => _sharedHistory.GetAllMessages();

    /// <summary>
    /// Adds a message to the shared chat history.
    /// </summary>
    public void Add(ChatMessageContent message)
    {
        _sharedHistory.AddMessage(message);
    }

    /// <summary>
    /// Clears the shared chat history.
    /// </summary>
    public void Clear()
    {
        _sharedHistory.Clear();
    }

    /// <inheritdoc />
    protected override Task<string?> CreateInternalAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(Guid.NewGuid().ToString("N"));
    }

    /// <inheritdoc />
    protected override Task DeleteInternalAsync(CancellationToken cancellationToken)
    {
        _sharedHistory.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task OnNewMessageInternalAsync(ChatMessageContent newMessage, CancellationToken cancellationToken = default)
    {
        _sharedHistory.AddMessage(newMessage);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Enhanced agent wrapper that uses shared chat history to maintain conversation context
/// across all agent handoffs in the orchestration.
/// </summary>
public class SharedHistoryAgent : Agent
{
    private readonly Agent _wrappedAgent;
    private readonly SharedChatHistoryService _sharedHistory;

    public SharedHistoryAgent(Agent wrappedAgent, SharedChatHistoryService sharedHistory)
    {
        _wrappedAgent = wrappedAgent;
        _sharedHistory = sharedHistory;
        Name = wrappedAgent.Name;
        Description = wrappedAgent.Description;
        Instructions = wrappedAgent.Instructions;
        Kernel = wrappedAgent.Kernel;
    }

    public override async IAsyncEnumerable<AgentResponseItem<ChatMessageContent>> InvokeAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Add the current messages to shared history
        foreach (var message in messages)
        {
            _sharedHistory.AddMessage(message);
        }

        // Create a ChatHistoryAgentThread with the shared history
        var chatHistoryThread = new ChatHistoryAgentThread();
        
        // Populate the thread with compatible shared history messages
        foreach (var historyMessage in _sharedHistory.GetCompatibleMessages())
        {
            chatHistoryThread.ChatHistory.Add(historyMessage);
        }

        string input = string.Join(" ", messages.Select(m => m.Content));
        Console.WriteLine($"\n🤖 [{this.Name}] Processing with shared history: \"{input}\"");
        Console.WriteLine($"📊 [SharedHistory] Total messages: {_sharedHistory.MessageCount}");

        await foreach (var response in _wrappedAgent.InvokeAsync(messages, chatHistoryThread, options, cancellationToken))
        {
            // Add the agent's response to shared history
            _sharedHistory.AddMessage(response.Message);
            
            if (!string.IsNullOrEmpty(response.Message.Content))
            {
                Console.WriteLine($"✅ [{this.Name}] Response: {response.Message.Content}");
            }

            yield return response;
        }

        Console.WriteLine($"🏁 [{this.Name}] Completed with shared context");
    }

    public override async IAsyncEnumerable<AgentResponseItem<StreamingChatMessageContent>> InvokeStreamingAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Add the current messages to shared history
        foreach (var message in messages)
        {
            _sharedHistory.AddMessage(message);
        }

        // Create a ChatHistoryAgentThread with the shared history
        var chatHistoryThread = new ChatHistoryAgentThread();
        
        // Populate the thread with compatible shared history messages
        foreach (var historyMessage in _sharedHistory.GetCompatibleMessages())
        {
            chatHistoryThread.ChatHistory.Add(historyMessage);
        }

        string input = string.Join(" ", messages.Select(m => m.Content));
        Console.WriteLine($"\n🤖 [{this.Name}] Processing (streaming) with shared history: \"{input}\"");
        Console.WriteLine($"📊 [SharedHistory] Total messages: {_sharedHistory.MessageCount}");

        await foreach (var response in _wrappedAgent.InvokeStreamingAsync(messages, chatHistoryThread, options, cancellationToken))
        {
            // Add the agent's response to shared history
            _sharedHistory.AddMessage(new ChatMessageContent(response.Message.Role ?? AuthorRole.Assistant, response.Message.Content));
            
            Console.WriteLine($"✅ [{this.Name}] Streaming: {response.Message.Content}");
            yield return response;
        }

        Console.WriteLine($"🏁 [{this.Name}] Streaming completed with shared context");
    }

    protected override IEnumerable<string> GetChannelKeys()
    {
        // Use reflection to access the protected method
        var method = _wrappedAgent.GetType().GetMethod("GetChannelKeys",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return method != null
            ? (IEnumerable<string>)method.Invoke(_wrappedAgent, null)!
            : [typeof(SharedHistoryAgent).FullName!];
    }

    protected override Task<AgentChannel> CreateChannelAsync(CancellationToken cancellationToken)
    {
        // Use reflection to access the protected method
        var method = _wrappedAgent.GetType().GetMethod("CreateChannelAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, [typeof(CancellationToken)], null);
        return method != null
            ? (Task<AgentChannel>)method.Invoke(_wrappedAgent, [cancellationToken])!
            : Task.FromException<AgentChannel>(new NotImplementedException());
    }

    protected override Task<AgentChannel> RestoreChannelAsync(string channelState, CancellationToken cancellationToken)
    {
        // Use reflection to access the protected method
        var method = _wrappedAgent.GetType().GetMethod("RestoreChannelAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, [typeof(string), typeof(CancellationToken)], null);
        return method != null
            ? (Task<AgentChannel>)method.Invoke(_wrappedAgent, [channelState, cancellationToken])!
            : Task.FromException<AgentChannel>(new NotImplementedException());
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