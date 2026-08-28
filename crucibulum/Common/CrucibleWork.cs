// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

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
