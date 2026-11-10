using NFS.Desktop;

namespace NFS.Desktop.Tests;

public class WindowTitleTests
{
    [Fact]
    public void MainWindowTitle_IsTheGameNameWithPortTag() =>
        Assert.Equal("Need For Speed 2 Special Edition (Indigo x64)", Game1.WindowTitle);
}
