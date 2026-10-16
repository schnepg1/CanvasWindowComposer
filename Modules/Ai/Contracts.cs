using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Ai;

/// <summary>A connection owns authentication; the canvas receives semantic data only.</summary>
public interface ICanvasComposerProvider
{
    Task<ComposerPlan> GenerateAsync(string model, string prompt, ComposerContext context,
        IProgress<string>? progress, CancellationToken cancellation);
    Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellation);
}

public sealed record AiModel(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record ChatGptAccount(string Id, string Label, bool SignedIn, bool PlanUsageEnabled, bool NeedsWelcome)
{
    public override string ToString() => Label + (SignedIn ? "" : " · signed out");
}

public interface IChatGptAccounts
{
    IReadOnlyList<ChatGptAccount> Accounts { get; }
    Task<ChatGptAccount> SignInAsync(string? accountId, CancellationToken cancellation);
    Task<string> SignOutAsync(string accountId, CancellationToken cancellation);
    void AcknowledgeWelcome(string accountId);
    ICanvasComposerProvider CreateProvider(string accountId);
}

public interface IComposerCredentials
{
    string? Read();
    void Write(string apiKey);
    void Delete();
}

public class AiConnectionException(string message, bool manageUsage = false, int? httpStatus = null,
    string? serviceCode = null, string? requestId = null) : Exception(message)
{
    public bool ManageUsage { get; } = manageUsage;
    public int? HttpStatus { get; } = httpStatus;
    public string? ServiceCode { get; } = serviceCode;
    public string? RequestId { get; } = requestId;
}
