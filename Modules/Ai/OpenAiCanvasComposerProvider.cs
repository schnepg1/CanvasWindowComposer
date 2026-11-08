using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Ai;

/// <summary>Explicit API-key adapter. The provider returns data and has no canvas mutation capability.</summary>
public sealed class OpenAiCanvasComposerProvider : ICanvasComposerProvider
{
    private readonly HttpClient http;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly bool _planUsage;
    public OpenAiCanvasComposerProvider(HttpClient http, string apiKey)
        : this(http, _ => Task.FromResult(apiKey), false) { }
    internal OpenAiCanvasComposerProvider(HttpClient http, string apiKey, bool planUsage)
        : this(http, _ => Task.FromResult(apiKey), planUsage) { }
    internal OpenAiCanvasComposerProvider(HttpClient http, Func<CancellationToken, Task<string>> token, bool planUsage)
    { this.http = http; _token = token; _planUsage = planUsage; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const string Instructions = """
        You are the Canvas Composer, generating visual diagrams, system architectures, workflows, pipelines, and concept maps for an infinite canvas desktop.
        Return the supplied JSON schema only.

        STRUCTURE:
        - summary: Concise description of the proposed visual system.
        - direction: Layout orientation: "LR" (left-to-right flow, default) or "TD" (top-to-bottom hierarchy).
        - zones: Optional project areas / swimlanes grouping related nodes (e.g., "Frontend", "Backend", "Funnel", "Data Pipeline").
        - nodes: Visual entities. Kind can be:
            * "web": A web app or website (specify url, optional chromeProfile, app="chrome.exe").
            * "window": A desktop application (specify app, e.g. "code.exe", "terminal.exe").
            * "service": A microservice, backend API, or cloud service (e.g. "Auth Service :5000", "Stripe API").
            * "database": A database or data store (e.g. "PostgreSQL", "Redis", "Snowflake", "S3 Bucket").
            * "step": A process milestone or workflow action.
            * "note": A markdown note or documentation card.
            * "rectangle" / "ellipse": Conceptual shapes.
          Each node must provide: id, kind, title, subtitle, text, color, app, url, chromeProfile, zone (use empty strings for unused string fields).
        - edges: Directed connections between node IDs. Provide: from, to, label (flow description or payload), color, stroke (8-32, default 16), routing ("curved", "straight", "orthogonal").
        - edits: Rewrites for existing shared notes. Leave empty for new diagrams.

        GUIDELINES:
        - Be versatile: Support software architecture, DevOps pipelines, developer environments, data engineering, marketing funnels, customer journeys, and concept maps.
        - Use descriptive arrow labels explaining what flows between nodes (e.g. "REST POST /v1/charge", "Story Swipe", "Docker Build", "Kafka Event").
        - Use harmonious hex colors (e.g., #38BDF8 for web/client, #10B981 for services/APIs, #3B82F6 for databases, #F59E0B for cues/warnings, #8B5CF6 for auth/analytics).
        - IDs are short local strings (e.g. "client", "api", "db"). Do not invent pixel coordinates; the client computes topological layout.
        - Prefer 3-14 nodes and clear, intuitive edge labels. Match the user's language.
        """;
    internal const string PlanSchema = """
        {
          "type":"object","additionalProperties":false,
          "properties":{
            "summary":{"type":"string"},
            "direction":{"type":"string","enum":["LR","TD"]},
            "zones":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string"},"name":{"type":"string"},"color":{"type":"string"}},"required":["id","name","color"]}},
            "nodes":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string"},"kind":{"type":"string","enum":["note","rectangle","ellipse","web","window","service","database","step"]},
              "title":{"type":"string"},"subtitle":{"type":"string"},"text":{"type":"string"},"color":{"type":"string"},
              "app":{"type":"string"},"url":{"type":"string"},"chromeProfile":{"type":"string"},"zone":{"type":"string"}},
              "required":["id","kind","title","subtitle","text","color","app","url","chromeProfile","zone"]}},
            "edges":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
              "from":{"type":"string"},"to":{"type":"string"},"label":{"type":"string"},"color":{"type":"string"},"stroke":{"type":"number"},"routing":{"type":"string","enum":["curved","straight","orthogonal"]}},
              "required":["from","to","label","color","stroke","routing"]}},
            "edits":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string"},"text":{"type":"string"}},"required":["id","text"]}}
          },"required":["summary","direction","zones","nodes","edges","edits"]
        }
        """;

    public async Task<ComposerPlan> GenerateAsync(string model, string prompt, ComposerContext context, IProgress<string>? progress, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(char.IsControl)) throw new ArgumentException("Enter a model ID that supports Structured Outputs.");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 8000) throw new ArgumentException("Enter a request of up to 8,000 characters.");
        if (context.SharedAnnotations.Count+context.SharedReferences.Count > CanvasComposer.MaximumContextItems
            || context.SharedAnnotations.Sum(a => a.Text.Length)+context.SharedReferences.Sum(a=>a.Title.Length) > CanvasComposer.MaximumContextCharacters)
            throw new ArgumentException("Share up to 20 context items and 32,000 characters per request.");
        string apiKey = await _token(cancellation).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 65536 || apiKey.Any(char.IsControl)) throw new AiConnectionException("Connect your account or enter a valid API key.");
        using var schema = JsonDocument.Parse(PlanSchema);
        var input = JsonSerializer.Serialize(new
        {
            request = prompt,
            layoutOnly=context.PreserveWording,
            sharedNotes = context.SharedAnnotations.Select(a => new { id = a.Id.ToString(), kind=a.Kind.ToString().ToLowerInvariant(), text = a.Text, bounds=a.Bounds }),
            sharedPlaces = context.SharedReferences.Select(a=>new { id=a.Id.ToString(),kind=a.Kind,title=a.Title,bounds=a.Bounds })
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        if (_planUsage)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        else
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        var body = new System.Collections.Generic.Dictionary<string, object>
        {
            ["model"] = model.Trim(), ["store"] = false, ["stream"] = _planUsage,
            ["instructions"] = Instructions,
            ["input"] = new[] { new { role = "user", content = input } },
            ["text"] = new { format = new { type = "json_schema", name = "canvas_proposal", strict = true, schema = schema.RootElement } }
        };
        if (!_planUsage) body["max_output_tokens"] = 12000;
        request.Content = JsonContent.Create(body);
        progress?.Report("Request sent. Waiting for the model…");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        await CheckStatusAsync(response, cancellation).ConfigureAwait(false);
        using var data = _planUsage
            ? await ComposerEventStream.ReadAsync(response, progress, cancellation).ConfigureAwait(false)
            : await BoundedHttp.ReadJsonAsync(response, cancellation).ConfigureAwait(false);
        var root = data.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed")
            throw new InvalidDataException("Generation did not finish. No canvas changes were made.");
        var result = new StringBuilder();
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content)) continue;
            foreach (var part in content.EnumerateArray())
            {
                string? type = part.GetProperty("type").GetString();
                if (type == "refusal") throw new InvalidDataException("The model declined this request. Try rephrasing it.");
                if (type == "output_text") result.Append(part.GetProperty("text").GetString());
            }
        }
        return JsonSerializer.Deserialize<ComposerPlan>(result.ToString(), Json)
            ?? throw new InvalidDataException("The model did not return a canvas proposal.");
    }
    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellation)
    {
        string token = await _token(cancellation).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new AiConnectionException("Connect your account or enter a valid API key.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        await CheckStatusAsync(response, cancellation).ConfigureAwait(false);
        using var json = await BoundedHttp.ReadJsonAsync(response, cancellation).ConfigureAwait(false);
        var models = new System.Collections.Generic.List<AiModel>();
        foreach (var item in json.RootElement.GetProperty(_planUsage ? "models" : "data").EnumerateArray())
        {
            if (_planUsage && (!item.TryGetProperty("visibility", out var visibility) || visibility.GetString() != "list")) continue;
            string? id = item.GetProperty(_planUsage ? "slug" : "id").GetString();
            string? label = _planUsage && item.TryGetProperty("display_name", out var name) ? name.GetString() : id;
            if (!string.IsNullOrWhiteSpace(id) && id.Length <= 200 && !id.Any(char.IsControl)) models.Add(new(id, label ?? id));
            if (models.Count > 2000) throw new InvalidDataException("The model catalog was too large.");
        }
        return models.AsReadOnly();
    }

    private async Task CheckStatusAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode) return;
        int status = (int)response.StatusCode;
        if (_planUsage)
        {
            JsonDocument? error = null;
            try { error = await BoundedHttp.ReadJsonAsync(response, cancellation).ConfigureAwait(false); }
            catch (JsonException) { }
            using (error) { throw ChatGptErrors.Failure(error?.RootElement, response); }
        }
        throw new AiConnectionException($"OpenAI returned HTTP {status}. Check your API key, model access and API billing.", httpStatus: status);
    }

}
