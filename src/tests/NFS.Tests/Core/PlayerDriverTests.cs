using NFS.Core;

namespace NFS.Tests.Core;

/// <summary>(values from the original). [ref:3078]</summary>
public class PlayerDriverTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(255, 7, 127, 248, 0, 124)]
    [InlineData(9, 15, -1, 8, 8, 0)]          // -1/4 = 0 (C division), not -4
    [InlineData(100, 200, -127, 96, 200, -124)]
    [InlineData(1, 8, -128, 0, 8, -128)]
    [InlineData(1, 8, -5, 0, 8, -4)]
    public void Apply_QuantisesInputs(int gas, int brake, int steer, int eGas, int eBrake, int eSteer)
    {
        var c = new CarObj { Gear = 3 };
        bool reset = PlayerDriver.Apply(c, new PlayerInput((sbyte)steer, (byte)gas, (byte)brake, 0), true, 6);
        Assert.False(reset);
        Assert.Equal((eGas, eBrake, eSteer), (c.Throttle, c.Brake, c.SteerTarget));
        Assert.Equal(3, c.RequestedGear);
    }

    [Fact]
    public void Apply_ButtonsAndReset()
    {
        var c = new CarObj { Gear = 2 };
        byte b = PlayerInput.BtnHandbrake | PlayerInput.BtnHorn | PlayerInput.BtnReset | PlayerInput.BtnGearUp;
        Assert.True(PlayerDriver.Apply(c, new PlayerInput(0, 0, 0, b), false, 6));
        Assert.Equal((1, 1, 3), (c.Handbrake, c.Horn, c.RequestedGear));
        Assert.False(PlayerDriver.Apply(c, new PlayerInput(0, 0, 0, PlayerInput.BtnGearDown), true, 6));
        Assert.Equal((0, 0, 1), (c.Handbrake, c.Horn, c.RequestedGear));   // auto: D → N
    }

    [Theory]
    [InlineData(0, 2, 1)] [InlineData(1, 2, 2)] [InlineData(2, 2, 2)] [InlineData(5, 2, 5)]
    [InlineData(0, 1, 0)] [InlineData(1, 1, 0)] [InlineData(2, 1, 1)] [InlineData(5, 1, 1)]
    [InlineData(0, 3, 1)] [InlineData(4, 0, 4)]                       // up wins over down
    public void GearAuto(int gear, int buttons, int expected) =>
        Assert.Equal(expected, PlayerDriver.GearAuto((byte)gear, (byte)buttons));

    [Theory]
    [InlineData(0, 6, 2, 1)] [InlineData(4, 6, 2, 5)] [InlineData(5, 6, 2, 5)]
    [InlineData(0, 6, 1, 0)] [InlineData(3, 6, 1, 2)] [InlineData(3, 6, 3, 4)] [InlineData(3, 6, 0, 3)]
    public void GearManual(int gear, int n, int buttons, int expected) =>
        Assert.Equal(expected, PlayerDriver.GearManual((byte)gear, n, (byte)buttons));
}
