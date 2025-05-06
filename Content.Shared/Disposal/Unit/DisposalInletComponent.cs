using Robust.Shared.Audio;
using Content.Shared.Whitelist;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared.Disposal.Components;

/// <summary>
/// Collects entities that fall into it and quickly flushes them out connected disposal tubes.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class DisposalInletComponent : Component
{
    [ViewVariables] public DisposalContainerComponent ContainerComponent = default!;

    /// <summary>
    /// Sounds played upon the inlet flushing.
    /// </summary>
    [DataField("soundFlush"), AutoNetworkedField]
    public SoundSpecifier? FlushSound = new SoundPathSpecifier("/Audio/Machines/disposalflush.ogg");

    /// <summary>
    /// Blacklists (prevents) entities listed from falling inside.
    /// </summary>
    [DataField]
    public EntityWhitelist? Blacklist;

    /// <summary>
    /// Whitelists (allows) entities listed from falling inside.
    /// </summary>
    [DataField]
    public EntityWhitelist? Whitelist;

    /// <summary>
    /// Sound played when an object falls into the disposal inlet.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("soundInsert")]
    public SoundSpecifier? InsertSound = new SoundPathSpecifier("/Audio/Effects/trashbag1.ogg");

    /// <summary>
    /// State for this disposals unit.
    /// </summary>
    [DataField, AutoNetworkedField]
    public DisposalsInletState State;

    /// <summary>
    /// Removes delay of flushing.
    /// </summary>
    [DataField]
    public bool DisableFlushDelay;

    /// <summary>
    /// How long it takes for items to be flushed after it is queued.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan FlushDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Delay from trying to enter disposals ourselves.
    /// </summary>
    [DataField]
    public float EntryDelay = 0.5f;

    /// <summary>
    /// Next time this inlet will flush.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField]
    public TimeSpan? NextFlush;

    [Serializable, NetSerializable]
    public enum Visuals : byte
    {
        VisualState
    }

    [Serializable, NetSerializable]
    public enum VisualState : byte
    {
        Anchored,
        OverlayFlushing
    }
}

[Serializable, NetSerializable]
public enum DisposalsInletState : byte
{
    Ready,

    /// <summary>
    /// Has been flushed recently within FlushDelay.
    /// </summary>
    Flushed
}
