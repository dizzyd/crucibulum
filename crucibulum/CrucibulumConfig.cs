// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

namespace Crucibulum;

/// <summary>
/// Written to ModConfig/crucibulum.json on first run.
/// </summary>
public class CrucibulumConfig
{
    /// <summary>
    /// Degrees added to the forge's own fuel-derived ceiling (700 + the fuel's inForge.tempGainDeg)
    /// when a crucible is sitting in it. 400 puts coke at 1200 C, charcoal at 1150, bituminous coal
    /// at 1100, lignite at 1000 and contaminated coal at 900 -- so fuel choice still decides which
    /// metals you can melt, but copper no longer needs a bellows.
    /// </summary>
    public int CrucibleTempBonus = 400;

    /// <summary>
    /// Hard ceiling on crucible temperature. 0 means "use the crucible's own maxHeatableTemp
    /// attribute", which vanilla ships as 1200 C. This is what keeps iron (1500 C) out of reach
    /// even with a bellows running.
    /// </summary>
    public int MaxCrucibleTemperature = 0;

    /// <summary>
    /// Multiplier on how fast the charge melts once it is up to temperature. 1 matches the firepit.
    /// </summary>
    public float MeltSpeedMultiplier = 1f;

    /// <summary>
    /// How fast the forge burns while working a crucible, against the firepit's rate per second.
    ///
    /// The forge burns on the calendar and the firepit on the real clock, which left melting over
    /// a forge six times cheaper for the same charge. This closes that gap.
    ///
    /// Note it is a rate, not a bill: what a job actually costs also depends on how long it takes,
    /// and the forge spends longer bringing a charge up to temperature than a firepit does. Over a
    /// whole job - heating and melting together - the default 0.6 lands a forge melt at roughly
    /// 0.65 of a firepit's fuel for a small charge and 0.8 for a brim-full crucible, so the forge
    /// stays the better place to melt without making fuel meaningless.
    ///
    /// Set it to 0 to go back to the vanilla forge burn rate and the six-fold discount.
    /// </summary>
    public float CrucibleFuelUseVsFirepit = 0.6f;

    /// <summary>
    /// What a melt costs to keep liquid, as a fraction of what it cost to make.
    ///
    /// Once the metal is molten the fire is only replacing what the crucible loses to the air, so
    /// holding is cheap: the default 0.35 leaves a molten crucible costing about a third of what
    /// the melt did - one lump of coke lasts 190s against melting's 67s - and barely more than an
    /// empty forge's 240s.
    /// </summary>
    public float MoltenHoldFuelShare = 0.35f;

    /// <summary>
    /// The thermal mass of the crucible itself, in ingot-equivalents.
    ///
    /// A charge heats the crucible in proportion to how much metal is in it against how much the
    /// clay itself soaks up, so this sets how much the charge size matters. At the default 3, an
    /// empty crucible climbs at full speed, a single ingot's worth of ore takes about a third
    /// longer, and a brim-full seven-ingot crucible takes over three times as long to reach
    /// temperature. Raise it to flatten the difference out.
    /// </summary>
    public float CrucibleThermalMass = 3f;

    /// <summary>
    /// How briskly the crucible climbs towards the temperature the fuel can hold it at.
    ///
    /// The climb is proportional to how far there is left to go, so it is quick while the crucible
    /// is cold and eases in over the last stretch. 0.5 brings a cold crucible up in a coke forge in
    /// about two minutes; raise it to heat faster.
    /// </summary>
    public float HeatRate = 0.5f;

    /// <summary>
    /// Whether a forge can be fitted with a blast gate at all. Turning this off leaves every forge
    /// on full draught, which is vanilla's behaviour; gates already fitted stop throttling and can
    /// still be taken back out.
    /// </summary>
    public bool EnableBlastGate = true;

    /// <summary>
    /// What each notch of the blast gate does to the fire, as a share of full draught. These are
    /// the same lever vanilla's bellows works from the other side, where the ceiling is
    /// <c>MaxTemperature * (1 + extraOxygenRate)</c> and a bellows takes the multiplier up to about
    /// 1.64 - so one is an open gate and anything less is a throttled one.
    ///
    /// A gate only ever restricts, so these are clamped to at most one however they are set here;
    /// a plate over the air inlet cannot make a fire burn hotter than an open one. They are also
    /// held above zero, since shut is a banked fire rather than an airtight one.
    ///
    /// Shut at 0.55 puts a crucible on coke at about 660 degC, which sits inside the workable band
    /// of most metals - vanilla counts metal workable at half its melting point. Moving these moves
    /// which metals can be held workable without melting.
    /// </summary>
    public float GateAirOpen = 1.00f;
    public float GateAirHalf = 0.85f;
    public float GateAirQuarter = 0.70f;
    public float GateAirShut = 0.55f;
}
