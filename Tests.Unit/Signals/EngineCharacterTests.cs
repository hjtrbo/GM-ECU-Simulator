using Common.Signals;
using Common.Signals.Engines;
using Xunit;

namespace EcuSimulator.Tests.Signals;

// Covers the pluggable engine-character layer: the NA vs boosted induction curves, the manifold-referenced fuel-
// pressure regulator riding on top of each, the registry's id resolution + default fallback, and the end-to-end swap
// through EngineModel. The character math is exercised directly via OperatingPoint (no easing/dither) so the curves
// are pinned exactly; one EngineModel test proves the swap is live.
public sealed class EngineCharacterTests
{
    private const double Baro = 101.0;

    // A running operating point at a given load (throttle tracks load; flags derived the same way EngineModel does).
    private static OperatingPoint Running(double load) => new(
        Rpm: 3000, Load: load, Throttle: load, Speed: 60,
        Running: true, Wot: load > 85, OverrunCut: false, ClosedLoop: load <= 85,
        EngineOff: false, BaroKpa: Baro, TimeSeconds: 0);

    private static OperatingPoint Off() => new(
        Rpm: 0, Load: 0, Throttle: 0, Speed: 0,
        Running: false, Wot: false, OverrunCut: false, ClosedLoop: false,
        EngineOff: true, BaroKpa: Baro, TimeSeconds: 0);

    [Fact]
    public void Na_MapCapsAtBarometric_AndFuelPressureTopsOutAtBase()
    {
        var na = new NaGasV8();

        // Vacuum-only: even at wide-open throttle MAP only reaches barometric, never above.
        Assert.Equal(Baro, na.Derive(SignalId.ManifoldAbsolutePressure, Running(100)), 3);
        Assert.True(na.Derive(SignalId.ManifoldAbsolutePressure, Running(80)) < Baro);

        // Fuel pressure (base + (MAP - baro)) therefore tops out at exactly the 4 bar base, never above.
        Assert.Equal(400.0, na.Derive(SignalId.FuelPressure, Running(100)), 3);
        Assert.True(na.Derive(SignalId.FuelPressure, Running(40)) < 400.0);   // sags under idle/cruise vacuum
    }

    [Fact]
    public void Boosted_DrivesMapAboveBarometric_AndFuelPressureAboveBase()
    {
        var boost = new BoostedGasV8();   // default PeakBoostKpa = 109

        double mapWot = boost.Derive(SignalId.ManifoldAbsolutePressure, Running(100));
        Assert.True(mapWot > Baro, $"expected boost above {Baro} kPa, got {mapWot}");
        Assert.Equal(210.0, mapWot, 3);                                        // baro 101 + 109 peak boost at WOT

        // The manifold-referenced regulator follows MAP above base: at WOT it sits exactly PeakBoostKpa over the base.
        Assert.Equal(400.0 + 109.0, boost.Derive(SignalId.FuelPressure, Running(100)), 3);
    }

    [Fact]
    public void Boosted_BelowOnsetLoad_MatchesNaVacuum()
    {
        var na = new NaGasV8();
        var boost = new BoostedGasV8();   // onset = 70% load

        // Part-throttle cruise: boosted engine is still in vacuum, identical to the NA curve.
        foreach (var load in new[] { 20.0, 40.0, 60.0 })
        {
            Assert.Equal(
                na.Derive(SignalId.ManifoldAbsolutePressure, Running(load)),
                boost.Derive(SignalId.ManifoldAbsolutePressure, Running(load)), 6);
        }
    }

    [Fact]
    public void Boosted_MapIsContinuousAcrossBoostOnset()
    {
        var boost = new BoostedGasV8();   // onset = 70

        // No jump at the threshold: just-below and just-above onset read essentially the same MAP.
        double below = boost.Derive(SignalId.ManifoldAbsolutePressure, Running(69.9));
        double above = boost.Derive(SignalId.ManifoldAbsolutePressure, Running(70.1));
        Assert.True(System.Math.Abs(above - below) < 0.5, $"discontinuity at onset: {below} -> {above}");
    }

    [Fact]
    public void EngineOff_BothCharacters_ReadBarometricAndPrimedBasePressure()
    {
        foreach (IEngineCharacter c in new IEngineCharacter[] { new NaGasV8(), new BoostedGasV8() })
        {
            Assert.Equal(Baro, c.Derive(SignalId.ManifoldAbsolutePressure, Off()), 3);  // manifold at atmosphere
            Assert.Equal(400.0, c.Derive(SignalId.FuelPressure, Off()), 3);             // pump primed to base, no vacuum
        }
    }

    [Theory]
    [InlineData("na-gas-v8", typeof(NaGasV8))]
    [InlineData("boosted-gas-v8", typeof(BoostedGasV8))]
    public void Registry_ResolvesKnownIds(string id, System.Type expected)
    {
        Assert.IsType(expected, EngineCharacterRegistry.Create(id));
        Assert.True(EngineCharacterRegistry.IsKnown(id));
        Assert.Contains(id, EngineCharacterRegistry.KnownIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("does-not-exist")]
    public void Registry_FallsBackToNaDefault_ForNullOrUnknownId(string? id)
    {
        Assert.IsType<NaGasV8>(EngineCharacterRegistry.Create(id));
        Assert.False(EngineCharacterRegistry.IsKnown(id));
    }

    [Fact]
    public void EngineModel_DefaultsToNa_AndSwappingCharacterIsLive()
    {
        var m = new EngineModel(ScenarioId.Cruise);
        Assert.Equal(EngineCharacterRegistry.DefaultId, m.Character.Id);

        // Pin wide-open load so MAP is driven to its ceiling; sample at t=0 so dither is zero (exact reads).
        m.SetOverride(SignalId.EngineLoad, 100);
        Assert.Equal(400.0, m.Sample(SignalId.FuelPressure, 0), 1);   // NA: tops out at base

        m.Character = EngineCharacterRegistry.Create("boosted-gas-v8");
        Assert.True(m.Sample(SignalId.FuelPressure, 0) > 400.0);      // boosted: rises above base under boost
    }
}
