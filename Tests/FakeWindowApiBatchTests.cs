using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class FakeWindowApiBatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchMove_ResizesOnlyWhenRequested(bool posOnly)
    {
        var api = new FakeWindowApi();
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        var result = api.BatchMove([new((IntPtr)1, new(100, 200, 400, 300), posOnly)], false, false);
        Assert.True(result.Summary.Accepted);
        Assert.Equal(posOnly ? (100, 200, 800, 600) : (100, 200, 400, 300), api.GetWindowRect((IntPtr)1));
    }
}
