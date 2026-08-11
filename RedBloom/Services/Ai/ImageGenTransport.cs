using System.Runtime.CompilerServices;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// An agent that is a diffusion model: each message is a prompt, and the reply is a picture.
/// </summary>
/// <remarks>
/// It speaks no wire protocol — there is nothing to reach over the network — so it does not stream
/// text back the way the endpoint transports do. It runs <see cref="ImageGen"/> once on the last
/// thing the user said and hands the finished picture to the chat through an
/// <see cref="AgentEventKind.Image"/> event, which the view already knows how to show at full size.
/// </remarks>
public sealed class ImageGenTransport : IAgentTransport
{
    private readonly AiAgent _agent;

    public ImageGenTransport(AiAgent agent) => _agent = agent;

    public async IAsyncEnumerable<AgentEvent> SendAsync(
        IReadOnlyList<AgentMessage> conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = LastPrompt(conversation);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return AgentEvent.Failure(LocalizationService.T("L_ImageAgentNoPrompt"));
            yield break;
        }

        yield return AgentEvent.Doing(AgentPhase.Drawing);

        var options = new ImageOptions { ModelPath = ImageGen.ResolveModel(_agent.Model) ?? string.Empty };
        var result = await ImageGen.GenerateAsync(prompt, options, cancellationToken).ConfigureAwait(false);

        if (!result.Ok || result.PngPath is null)
        {
            yield return AgentEvent.Failure(result.Message);
            yield break;
        }

        yield return AgentEvent.Image(result.PngPath);
        yield return new AgentEvent(AgentEventKind.Completed, string.Empty);
    }

    /// <summary>
    /// Confirms the engine is installed and the chosen model can be found, without drawing anything.
    /// </summary>
    public Task<string?> TestAsync(CancellationToken cancellationToken = default)
    {
        if (!ImageGen.Available)
        {
            return Task.FromResult<string?>(LocalizationService.T("L_ImageAgentNoEngine"));
        }

        return Task.FromResult(ImageGen.ResolveModel(_agent.Model) is null
            ? LocalizationService.T("L_ImageAgentNoModel")
            : null);
    }

    /// <summary>The most recent thing the user said, which is the prompt to draw.</summary>
    private static string LastPrompt(IReadOnlyList<AgentMessage> conversation)
    {
        for (var i = conversation.Count - 1; i >= 0; i--)
        {
            if (conversation[i].Role == AgentRole.User && !string.IsNullOrWhiteSpace(conversation[i].Text))
            {
                return conversation[i].Text.Trim();
            }
        }

        return string.Empty;
    }

    public void Dispose()
    {
        // Nothing is held open: each draw is a process that has already exited by the time the
        // picture is handed back.
    }
}
