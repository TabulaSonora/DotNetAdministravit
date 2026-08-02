using TabulaSonora.Patches;

namespace TabulaSonora.Tests;

/// <summary>
/// The drum coarse-pitch NRPN's plane law: offset added one-for-one, clamped to the plane's range.
/// </summary>
/// <remarks>
/// This is the engine's own plane write (<c>nrpn_apply</c> case <c>0x18</c>, confirmed by live
/// plane reads), and it regressed once already: an earlier revision doubled the offset and skipped
/// the clamp, which pushed 100%-follow keys twice as far as the engine does and drove
/// WATRWLD1.MID's NRPN-dropped crash five octaves into subsonics on the SC-55 map.
/// </remarks>
public class DrumKeyOverridesTests
{
    [Fact]
    public void PitchOffsetLandsOnThePlaneUnscaled()
    {
        // The spec's measured case: entry +12 moves a stored plane of 60 to 72 on every map.
        var key = new DrumKey(Tone: 0, Level: 0, Pitch: 60, Group: 0, Pan: 64);
        Assert.Equal(72, DrumKeyOverrides.Apply(key, 12, null).Pitch);

        // WATRWLD1.MID's crash: kit plane 69 on the SC-55 Standard kit, entry 24 = -40 steps.
        Assert.Equal(29, DrumKeyOverrides.Apply(key with { Pitch = 69 }, 24 - 0x40, null).Pitch);
    }

    [Fact]
    public void ThePlaneClampsAtItsOwnEnds()
    {
        // The clamp's bottom is the "absolute rate floor" of the earlier measurements: keys with
        // different kit planes floor at different offsets, which made it look like a rate limit.
        var key = new DrumKey(Tone: 0, Level: 0, Pitch: 60, Group: 0, Pan: 64);
        Assert.Equal(0, DrumKeyOverrides.Apply(key, -0x40, null).Pitch);
        Assert.Equal(123, DrumKeyOverrides.Apply(key, 0x3F, null).Pitch);
        Assert.Equal(0x7F, DrumKeyOverrides.Apply(key with { Pitch = 100 }, 0x3F, null).Pitch);
    }
}
