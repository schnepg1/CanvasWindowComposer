using CanvasDesktop.Ai;
using System.Text;

namespace CanvasDesktop.Tests;

public class ChatGptCredentialStorageTests
{
    [Fact]
    public void WindowsStorageEncryptsRoundTripsAndReplacesCredentialsAtomically()
    {
        string folder=Path.Combine(Path.GetTempPath(),"canvas-ai-credentials-"+Guid.NewGuid().ToString("N"));
        try
        {
            string path=Path.Combine(folder,"chatgpt.dat");var store=new WindowsChatGptStore(path);
            Assert.Null(store.Load());
            var tokens=new ChatGptTokens("test-access-secret","test-refresh-secret","test-id-secret",["chatgpt.tokens.use.direct"],DateTimeOffset.UtcNow.AddHours(1),DateTimeOffset.UtcNow);
            var state=new ChatGptState(1,"urn:uuid:"+Guid.NewGuid(),[new("id","client","subject","Account",true,tokens)]);
            store.Save(state);string bytes=Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("test-access-secret",bytes);Assert.DoesNotContain("test-refresh-secret",bytes);
            var loaded=Assert.Single(store.Load()!.Registrations).Tokens!;
            Assert.Equal(tokens.AccessToken,loaded.AccessToken);Assert.Equal(tokens.RefreshToken,loaded.RefreshToken);
            Assert.Equal(tokens.IdToken,loaded.IdToken);Assert.Equal(tokens.Scopes,loaded.Scopes);Assert.Equal(tokens.ExpiresAt,loaded.ExpiresAt);
            store.Save(state with { Registrations=[state.Registrations[0] with { Tokens=null }] });
            Assert.Null(Assert.Single(store.Load()!.Registrations).Tokens);Assert.Single(Directory.GetFiles(folder));
            File.WriteAllText(path,"corrupt");Assert.ThrowsAny<Exception>(()=>store.Load());Assert.Equal("corrupt",File.ReadAllText(path));
        }
        finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
    }
}
