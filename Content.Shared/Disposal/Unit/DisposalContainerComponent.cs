using Content.Shared.Atmos;
using Robust.Shared.Containers;

namespace Content.Shared.Disposal.Components;

/// <summary>
/// Container and air storage common among disposal units.
/// </summary>
public sealed partial class DisposalContainerComponent : Component
{
    public const string ContainerId = "disposals";

    /// <summary>
    /// Air contained in the disposal unit.
    /// </summary>
    [DataField]
    public GasMixture Air = new(Atmospherics.CellVolume);

    [ViewVariables] public Container Container = default!;
}
