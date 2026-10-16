using System.Text.Json;

namespace CanvasDesktop.Ai;

internal static class ChatGptErrors
{
    public static AiConnectionException Failure(JsonElement? data, HttpResponseMessage response, bool incomplete = false)
    {
        string? code = data is { } root ? Code(root) : null;
        bool usage = code == "subscription_sharing_usage_limit_exceeded";
        string message = code switch
        {
            "subscription_sharing_usage_limit_exceeded" => "ChatGPT plan or app usage limit reached. Choose Manage usage to review your limits.",
            "subscription_sharing_unsupported_capability" => "ChatGPT does not support this request or model capability. Choose a compatible model or adjust the request.",
            "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => "ChatGPT is temporarily unavailable. Your connection was preserved; try again later.",
            "subscription_sharing_user_not_eligible" => "ChatGPT plan usage is unavailable for this account, workspace or policy. Check eligibility or explicitly choose another connection.",
            "subscription_sharing_route_not_supported" => "This ChatGPT request route is not supported. Check the integration configuration.",
            "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" => "ChatGPT permission does not authorize this request. Check the account and granted permissions.",
            "subscription_sharing_invalid_user" => "ChatGPT could not validate this account. Check the connection and reconnect after confirmed session revocation.",
            _ when incomplete => "ChatGPT generation did not finish. Try a shorter request or retry later; nothing was applied.",
            _ when (int)response.StatusCode == 401 => "ChatGPT authorization was not accepted. Check the selected account and plan permission, or reconnect.",
            _ when (int)response.StatusCode == 403 => "ChatGPT access is restricted for this account, workspace or serving policy. Check eligibility and permissions.",
            _ when (int)response.StatusCode is 429 or >= 500 => "ChatGPT could not accept this request right now. Your connection was preserved; try again later.",
            _ => "ChatGPT could not complete this request. Check your connection, model and request; nothing was applied."
        };
        string? requestId = null;
        foreach (string header in new[] { "x-request-id", "openai-request-id" })
            if (response.Headers.TryGetValues(header, out var values)) { requestId = SafeIdentifier(values.FirstOrDefault()); break; }
        return new(message, usage, (int)response.StatusCode, code, requestId);
    }

    private static string? Code(JsonElement root, int depth = 0)
    {
        if (root.ValueKind != JsonValueKind.Object || depth > 2) return null;
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String) return SafeIdentifier(code.GetString());
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String) return SafeIdentifier(error.GetString());
            if (Code(error, depth + 1) is { } nested) return nested;
        }
        return root.TryGetProperty("response", out var response) ? Code(response, depth + 1) : null;
    }
    private static string? SafeIdentifier(string? value) => value is { Length: > 0 and <= 200 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? value : null;
}
