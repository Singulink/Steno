using System.Reflection;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Steno.Tests;

/// <summary>
/// The division by 248 of the vectorized wide encode blocks, in every form that this machine can run, against plain division for every 16-bit
/// value (offsets in a range of size 2 are below 254 * 248 = 62,992, so every value the blocks can see is covered).
/// </summary>
/// <remarks>
/// The forms are internal to the library, which is strong-named, so they are reached by reflection.
/// </remarks>
[PrefixTestClass]
public sealed class WideDivideTests
{
    [TestMethod]
    public void Shifts_EveryValue() => CheckEveryValue("DivideBy248Shifts");

    [TestMethod]
    public void Sse2_EveryValue()
    {
        if (!Sse2.IsSupported)
            return;

        CheckEveryValue("DivideBy248Sse2");
    }

    [TestMethod]
    public void AdvSimd_EveryValue()
    {
        if (!AdvSimd.Arm64.IsSupported)
            return;

        CheckEveryValue("DivideBy248AdvSimd");
    }

    [TestMethod]
    public void Dispatch_EveryValue() => CheckEveryValue("DivideBy248");

    private static void CheckEveryValue(string name)
    {
        var codec = typeof(StenoEncoding).Assembly.GetType("Singulink.Steno.StenoCodec", throwOnError: true)!;
        var method = codec.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).ShouldNotBeNull(name);
        var divide = method.CreateDelegate<Func<Vector128<ushort>, Vector128<ushort>>>();

        Span<ushort> lanes = stackalloc ushort[8];

        for (int start = 0; start <= ushort.MaxValue; start += 8)
        {
            for (int i = 0; i < 8; i++)
                lanes[i] = (ushort)(start + i);

            var quotients = divide(Vector128.Create<ushort>(lanes));

            for (int i = 0; i < 8; i++)
            {
                if (quotients[i] != lanes[i] / 248)
                    quotients[i].ShouldBe((ushort)(lanes[i] / 248), $"{name}: {lanes[i]} / 248");
            }
        }
    }
}
