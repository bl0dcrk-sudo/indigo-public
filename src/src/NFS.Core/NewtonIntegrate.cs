namespace NFS.Core;

/// <summary>
/// Integrators (frame queue 64 Hz / sim queue 32 Hz). "Helpers". [ref:0502]
/// All divisions by 64/32 are C divisions (truncation toward zero).
/// </summary>
public static class NewtonIntegrate
{
    /// <summary>Orthonormalisation period of <see cref="Rot64"/> (byte counter car+0x8F).</summary>
    public const byte OrthoPeriod64 = 0x10;
    /// <summary>Orthonormalisation period of <see cref="Rot32"/>.</summary>
    public const byte OrthoPeriod32 = 0x20;
    /// <summary>Accumulated |angle| threshold forcing orthonormalisation (arg). [ref:0503]</summary>
    public const int RotAccumThreshold = 0x1000;

    /// <summary><c>if (active) pos_k += vel_k / 64</c>. [ref:0504]</summary>
    public static void Pos(CarObj c)
    {
        if (c.Active == 0) return;
        c.Pos.X += Fx.DivPow2(c.Vel.X, 6);
        c.Pos.Y += Fx.DivPow2(c.Vel.Y, 6);
        c.Pos.Z += Fx.DivPow2(c.Vel.Z, 6);
    }

    /// <summary>(player with glue logic / net): [ref:0505]
    /// <c>pos_k += FX(vel_k, car+0x218) / 64; car+0x94 = car+0x218</c>. No <c>active</c> check in the original.</summary>
    public static void PosScaled(CarObj c)
    {
        int k = c.CatchUp;
        c.Pos.X += Fx.DivPow2(Fx.Mul(c.Vel.X, k), 6);
        c.Pos.Y += Fx.DivPow2(Fx.Mul(c.Vel.Y, k), 6);
        c.Pos.Z += Fx.DivPow2(Fx.Mul(c.Vel.Z, k), 6);
        c.TimeScale = k;
    }

    /// <summary>Frame-queue step for a player: ω/64 per step; orient = orient·dR; [ref:0506]
    /// <c>--orthoCnt == 0 || reortho</c> → orthonormalize, counter = 0x10, accumulator = 0.</summary>
    public static void Rot64(CarObj c)
    {
        if (c.Active == 0) return;
        if (!FxMath.RotateMatrix(out var m, Fx.DivPow2(c.AngVel.X, 6), Fx.DivPow2(c.AngVel.Y, 6),
                Fx.DivPow2(c.AngVel.Z, 6), out bool reortho, RotAccumThreshold, ref c.RotAccum))
            return;
        c.Orient = Mat3.Mul(c.Orient, m);
        c.OrthoCount--;                                  // byte: 0 wraps to 0xFF (DEC DL; TEST; JBE)
        if (c.OrthoCount != 0 && !reortho) return;
        FxMath.Orthonormalize(ref c.Orient);
        c.OrthoCount = OrthoPeriod64;
        c.RotAccum = 0;
    }

    /// <summary>Other cars (sim queue): ω/32; reortho checked first, so the [ref:0507]
    /// counter is not decremented when the accumulator forces orthonormalisation; period 0x20.</summary>
    public static void Rot32(CarObj c)
    {
        if (c.Active == 0) return;
        if (!FxMath.RotateMatrix(out var m, Fx.DivPow2(c.AngVel.X, 5), Fx.DivPow2(c.AngVel.Y, 5),
                Fx.DivPow2(c.AngVel.Z, 5), out bool reortho, RotAccumThreshold, ref c.RotAccum))
            return;
        c.Orient = Mat3.Mul(c.Orient, m);
        if (!reortho)
        {
            c.OrthoCount--;
            if (c.OrthoCount != 0) return;
        }
        FxMath.Orthonormalize(ref c.Orient);
        c.OrthoCount = OrthoPeriod32;
        c.RotAccum = 0;
    }
}
