using CanvasDesktop.Ai;
using CanvasDesktop.CanvasView;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CanvasDesktop.Tests;

public class CanvasComposerProviderTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Respond = (_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Respond(request,token);
    }
    private static ComposerContext Context()=>new(Guid.NewGuid(),0,new(0,0),[],[]);
    private static string PlanJson=>"""{"summary":"Plan","nodes":[{"id":"a","kind":"note","text":"Hello","color":"#FFFFFF"}],"edges":[],"edits":[]}""";
    private static HttpResponseMessage Response(string status="completed",string? text=null) => new(HttpStatusCode.OK)
    {
        Content=new StringContent(JsonSerializer.Serialize(new { status,output=new[]{new {type="message",content=new[]{new {type="output_text",text=text??PlanJson}}}} }),Encoding.UTF8,"application/json")
    };
    [Fact]
    public async Task RequestUsesOnlyExplicitContextAndStrictSchemaWithoutStoredResponses()
    {
        var handler=new Handler(); string? body=null; string? key=null; Uri? uri=null;
        handler.Respond=async (request,token)=>
        { body=await request.Content!.ReadAsStringAsync(token);key=request.Headers.Authorization?.Parameter;uri=request.RequestUri;return Response(); };
        using var http=new HttpClient(handler);var provider=new OpenAiCanvasComposerProvider(http,"test-key");
        var result=await provider.GenerateAsync("test-model","Make a roadmap",Context(),null,CancellationToken.None);
        Assert.Equal("Plan",result.Summary);Assert.Equal("test-key",key);Assert.Equal("https://api.openai.com/v1/responses",uri!.ToString());
        using var json=JsonDocument.Parse(body!);var root=json.RootElement;
        Assert.False(root.GetProperty("store").GetBoolean());Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        using var input=JsonDocument.Parse(root.GetProperty("input")[0].GetProperty("content").GetString()!);
        Assert.Empty(input.RootElement.GetProperty("sharedNotes").EnumerateArray());
        Assert.DoesNotContain("test-key",body);Assert.DoesNotContain("Desktop",body);Assert.DoesNotContain("Obstacles",body);
    }
    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    public async Task IncompleteResultsNeverBecomePlans(string status)
    {
        var handler=new Handler { Respond=(_,_)=>Task.FromResult(Response(status)) };using var http=new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>new OpenAiCanvasComposerProvider(http,"key").GenerateAsync("model","prompt",Context(),null,CancellationToken.None));
    }
    [Fact]
    public async Task RefusalAndMalformedPlanAreRejected()
    {
        var handler=new Handler { Respond=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("""{"status":"completed","output":[{"content":[{"type":"refusal","refusal":"No"}]}]}""") }) };
        using var http=new HttpClient(handler);var provider=new OpenAiCanvasComposerProvider(http,"key");
        await Assert.ThrowsAsync<InvalidDataException>(()=>provider.GenerateAsync("model","prompt",Context(),null,CancellationToken.None));
        handler.Respond=(_,_)=>Task.FromResult(Response(text:"not json"));
        await Assert.ThrowsAsync<JsonException>(()=>provider.GenerateAsync("model","prompt",Context(),null,CancellationToken.None));
    }
    [Fact]
    public async Task HttpFailureDoesNotExposeResponseBodyOrApiKey()
    {
        var handler=new Handler { Respond=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content=new StringContent("secret body") }) };
        using var http=new HttpClient(handler);
        var error=await Assert.ThrowsAsync<AiConnectionException>(()=>new OpenAiCanvasComposerProvider(http,"test-secret").GenerateAsync("model","prompt",Context(),null,CancellationToken.None));
        Assert.Contains("401",error.Message);Assert.DoesNotContain("secret",error.Message);
    }
    [Fact]
    public async Task CancellationReachesHttpAndOversizedInputNeverMakesARequest()
    {
        int requests=0;var handler=new Handler { Respond=async (_,token)=>{requests++;await Task.Delay(Timeout.Infinite,token);return Response();} };
        using var http=new HttpClient(handler);var provider=new OpenAiCanvasComposerProvider(http,"key");
        using var cts=new CancellationTokenSource();var task=provider.GenerateAsync("model","prompt",Context(),null,cts.Token);cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);
        await Assert.ThrowsAsync<ArgumentException>(()=>provider.GenerateAsync("model",new string('x',8001),Context(),null,CancellationToken.None));
        Assert.Equal(1,requests);
    }
    [Fact]
    public async Task OversizedResponseIsRejectedBeforeParsing()
    {
        var handler=new Handler { Respond=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(new string('x',1024*1024+1)) }) };
        using var http=new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>new OpenAiCanvasComposerProvider(http,"key").GenerateAsync("model","prompt",Context(),null,CancellationToken.None));
    }
}
