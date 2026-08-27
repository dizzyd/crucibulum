namespace Crucibulum;

/// <summary>What the fire is doing for the crucible, which is what decides the fuel bill.</summary>
public enum CrucibleWork
{
    /// <summary>No crucible, or an empty one. The forge burns exactly as vanilla does.</summary>
    None,

    /// <summary>Bringing a charge up towards its melting point.</summary>
    Heating,

    /// <summary>At the melting point and changing state. The expensive part, and the slow one.</summary>
    Melting,

    /// <summary>Metal already liquid, only being kept from freezing.</summary>
    Holding,
}
