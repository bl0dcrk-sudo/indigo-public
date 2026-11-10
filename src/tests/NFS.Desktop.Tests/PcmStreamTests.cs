using NFS.Core;
using NFS.Desktop;
using Xunit;

namespace NFS.Desktop.Tests;

public class PcmStreamTests
{
    [Fact]
    public void PcmQueue_PreservesSignedStereoLittleEndianOrder()
    {
        var q = new PcmBufferQueue();
        q.Enqueue([short.MinValue, short.MaxValue, -1, 0]);
        Assert.Equal(new byte[] { 0, 128, 255, 127, 255, 255, 0, 0 }, q.Dequeue());
        Assert.Null(q.Dequeue());
        Assert.Throws<ArgumentException>(() => q.Enqueue([1]));
    }

    [Fact]
    public void PcmQueue_DropsOldestDuringStallAndClearsOnStop()
    {
        var q = new PcmBufferQueue();
        for (short i = 0; i < 6; i++) q.Enqueue([i, i]);
        Assert.Equal(4, q.Count);
        Assert.Equal(new byte[] { 2, 0, 2, 0 }, q.Dequeue());
        q.Clear();
        Assert.Null(q.Dequeue());
    }

    [Fact]
    public void HornKey_IsHeldAcrossStepsAndReleased()
    {
        var keys = new KeyboardDriver();
        var horn = new DriveKeys(false, false, false, false, false, false, false, Horn: true);
        Assert.Equal(PlayerInput.BtnHorn, keys.Next(horn).Buttons);
        Assert.Equal(PlayerInput.BtnHorn, keys.Next(horn).Buttons);
        Assert.Equal(0, keys.Next(horn with { Horn = false }).Buttons);
    }
}
