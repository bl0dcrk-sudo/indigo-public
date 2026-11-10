namespace NFS.Desktop.Tests;

/// <summary>Change network-menu-look, task 1.4: the <c>netMenuLook</c> key of <c>port.json</c>.</summary>
public sealed class NetMenuLookSettingsTests
{
    [Fact]
    public void WithoutTheKey_TheLookIsOriginal()
    {
        Assert.Equal(NetMenuLook.Original, new PortSettings().NetMenuLook);
        Assert.Null(new PortSettings().NetMenuLookText);
    }

    [Theory]
    [InlineData("original", NetMenuLook.Original)]
    [InlineData("classic", NetMenuLook.Classic)]
    [InlineData("Classic", NetMenuLook.Classic)]
    [InlineData(" CLASSIC ", NetMenuLook.Classic)]
    [InlineData("", NetMenuLook.Original)]
    [InlineData("fancy", NetMenuLook.Original)]
    public void ReadFromTheFile_UnknownNamesFallBackToOriginal(string value, NetMenuLook expected)
    {
        var dir = Directory.CreateTempSubdirectory("nfs-port-look-").FullName;
        var path = Path.Combine(dir, PortSettings.FileName);
        try
        {
            File.WriteAllText(path, $$"""{ "netMenuLook": "{{value}}", "future": 5 }""");
            var p = PortSettings.Load(path);
            Assert.Null(p.Error);
            Assert.Equal(expected, p.NetMenuLook);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ANonStringValue_IsIgnoredWithAMessageAndKeepsTheDefault()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-port-look-").FullName;
        var path = Path.Combine(dir, PortSettings.FileName);
        try
        {
            File.WriteAllText(path, """{ "netMenuLook": 3 }""");
            var p = PortSettings.Load(path);
            Assert.NotNull(p.Error);
            Assert.Equal(NetMenuLook.Original, p.NetMenuLook);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SavedAndReadBack_KeepsTheLookAndTheOtherKeys()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-port-look-").FullName;
        var path = Path.Combine(dir, PortSettings.FileName);
        try
        {
            File.WriteAllText(path, """{ "fullScreen": false, "future": 5 }""");
            var p = PortSettings.Load(path);
            p.NetMenuLook = NetMenuLook.Classic;
            Assert.Null(p.Save(path));
            Assert.Contains("\"netMenuLook\": \"classic\"", File.ReadAllText(path));
            var back = PortSettings.Load(path);
            Assert.Equal(NetMenuLook.Classic, back.NetMenuLook);
            Assert.False(back.FullScreen);
            Assert.True(back.Extra.ContainsKey("future"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ANeverSetLook_IsNotWrittenToTheFile()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-port-look-").FullName;
        var path = Path.Combine(dir, PortSettings.FileName);
        try
        {
            Assert.Null(new PortSettings { FullScreen = true }.Save(path));
            Assert.DoesNotContain("netMenuLook", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
