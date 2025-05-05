using System.Linq;
using Content.Shared.ActionBlocker;
using Content.Shared.Administration.Logs;
using Content.Shared.Body.Components;
using Content.Shared.Climbing.Systems;
using Content.Shared.Containers;
using Content.Shared.Database;
using Content.Shared.Disposal.Components;
using Content.Shared.Disposal.Unit.Events;
using Content.Shared.DoAfter;
using Content.Shared.DragDrop;
using Content.Shared.Emag.Systems;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Movement.Events;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Throwing;
using Content.Shared.Verbs;
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared.Disposal.Unit;

[Serializable, NetSerializable]
public sealed partial class DisposalDoAfterEvent : SimpleDoAfterEvent
{
}

public abstract class SharedDisposalUnitSystem : EntitySystem
{
    [Dependency] protected readonly ActionBlockerSystem ActionBlockerSystem = default!;
    [Dependency] private   readonly EntityWhitelistSystem _whitelistSystem = default!;
    [Dependency] protected readonly MetaDataSystem Metadata = default!;
    [Dependency] private   readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] protected readonly SharedAudioSystem Audio = default!;
    [Dependency] protected readonly IGameTiming GameTiming = default!;
    [Dependency] private   readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private   readonly ClimbSystem _climb = default!;
    [Dependency] protected readonly SharedContainerSystem Containers = default!;
    [Dependency] protected readonly SharedJointSystem Joints = default!;
    [Dependency] private   readonly SharedPowerReceiverSystem _power = default!;
    [Dependency] private   readonly SharedDisposalTubeSystem _disposalTubeSystem = default!;
    [Dependency] private   readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private   readonly SharedDoAfterSystem _doAfterSystem = default!;
    [Dependency] private   readonly SharedHandsSystem _handsSystem = default!;
    [Dependency] protected readonly SharedTransformSystem TransformSystem = default!;
    [Dependency] private   readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private   readonly SharedMapSystem _map = default!;

    protected static TimeSpan ExitAttemptDelay = TimeSpan.FromSeconds(0.5);

    // Percentage
    public const float PressurePerSecond = 0.05f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DisposalUnitComponent, PreventCollideEvent>(OnPreventCollide);
        SubscribeLocalEvent<DisposalUnitComponent, CanDropTargetEvent>(OnCanDragDropOn);
        SubscribeLocalEvent<DisposalUnitComponent, GetVerbsEvent<InteractionVerb>>(AddInsertVerb);
        SubscribeLocalEvent<DisposalUnitComponent, GetVerbsEvent<AlternativeVerb>>(AddDisposalAltVerbs);
        SubscribeLocalEvent<DisposalUnitComponent, GetVerbsEvent<Verb>>(AddClimbInsideVerb);

        SubscribeLocalEvent<DisposalUnitComponent, DisposalDoAfterEvent>(OnDoAfter);

        SubscribeLocalEvent<DisposalUnitComponent, BeforeThrowInsertEvent>(OnThrowInsert);

        SubscribeLocalEvent<DisposalUnitComponent, DisposalUnitComponent.UiButtonPressedMessage>(OnUiButtonPressed);

        SubscribeLocalEvent<DisposalUnitComponent, GotEmaggedEvent>(OnEmagged);
        SubscribeLocalEvent<DisposalUnitComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        SubscribeLocalEvent<DisposalUnitComponent, PowerChangedEvent>(OnPowerChange);
        SubscribeLocalEvent<DisposalUnitComponent, ComponentInit>(OnDisposalInit);

        SubscribeLocalEvent<DisposalUnitComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<DisposalUnitComponent, AfterInteractUsingEvent>(OnAfterInteractUsing);
        SubscribeLocalEvent<DisposalUnitComponent, DragDropTargetEvent>(OnDragDropOn);
        SubscribeLocalEvent<DisposalUnitComponent, ContainerRelayMovementEntityEvent>(OnMovement);
    }

    private void AddDisposalAltVerbs(Entity<DisposalUnitComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        var uid = ent.Owner;
        var component = ent.Comp;
        var containerComp = component.ContainerComponent;

        // Behavior for if the disposals bin has items in it
        if (containerComp.Container.ContainedEntities.Count > 0)
        {
            // Verbs to flush the unit
            AlternativeVerb flushVerb = new()
            {
                Act = () => ManualEngage(ent),
                Text = Loc.GetString("disposal-flush-verb-get-data-text"),
                Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/delete_transparent.svg.192dpi.png")),
                Priority = 1,
            };
            args.Verbs.Add(flushVerb);

            // Verb to eject the contents
            AlternativeVerb ejectVerb = new()
            {
                Act = () => TryEjectContents(ent),
                Category = VerbCategory.Eject,
                Text = Loc.GetString("disposal-eject-verb-get-data-text")
            };
            args.Verbs.Add(ejectVerb);
        }
    }

    private void AddInsertVerb(EntityUid uid, DisposalUnitComponent comp, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands == null || args.Using == null)
            return;

        if (!ActionBlockerSystem.CanDrop(args.User))
            return;

        var ent = new Entity<DisposalUnitComponent>(uid, comp);
        var containerComp = ent.Comp.ContainerComponent;

        if (!CanInsert(ent, args.Using.Value))
            return;

        InteractionVerb insertVerb = new()
        {
            Text = Name(args.Using.Value),
            Category = VerbCategory.Insert,
            Act = () =>
            {
                _handsSystem.TryDropIntoContainer(args.User, args.Using.Value, containerComp.Container, checkActionBlocker: false, args.Hands);
                _adminLog.Add(LogType.Action, LogImpact.Medium, $"{ToPrettyString(args.User):player} inserted {ToPrettyString(args.Using.Value)} into {ToPrettyString(ent)}");
                AfterInsert(ent, args.Using.Value, args.User);
            }
        };

        args.Verbs.Add(insertVerb);
    }

    private void OnDoAfter(EntityUid uid, DisposalUnitComponent comp, DoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Args.Target == null || args.Args.Used == null)
            return;

        var ent = new Entity<DisposalUnitComponent>(uid, comp);

        AfterInsert(ent, args.Args.Target.Value, args.Args.User, doInsert: true);

        args.Handled = true;
    }

    private void OnThrowInsert(Entity<DisposalUnitComponent> ent, ref BeforeThrowInsertEvent args)
    {
        if (!CanInsert(ent, args.ThrownEntity))
            args.Cancelled = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<DisposalUnitComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var comp, out var metadata))
        {
            Update(new Entity<DisposalUnitComponent>(uid, comp), metadata);
        }
    }

    // TODO: This should just use the same thing as entity storage?
    private void OnMovement(Entity<DisposalUnitComponent> ent, ref ContainerRelayMovementEntityEvent args)
    {
        var currentTime = GameTiming.CurTime;

        if (!ActionBlockerSystem.CanMove(args.Entity))
            return;

        var comp = ent.Comp;

        if (!TryComp(args.Entity, out HandsComponent? hands) ||
            hands.Count == 0 ||
            currentTime < comp.LastExitAttempt + ExitAttemptDelay)
            return;

        Dirty(ent);
        comp.LastExitAttempt = currentTime;
        Remove(ent, args.Entity);
        UpdateUI(ent);
    }

    private void OnActivate(EntityUid uid, DisposalUnitComponent component, ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        _ui.TryToggleUi(uid, DisposalUnitComponent.DisposalUnitUiKey.Key, args.User);
    }

    private void OnAfterInteractUsing(EntityUid uid, DisposalUnitComponent comp, AfterInteractUsingEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;

        if (!HasComp<HandsComponent>(args.User))
        {
            return;
        }

        var ent = new Entity<DisposalUnitComponent>(uid, comp);
        var containerComp = ent.Comp.ContainerComponent;

        if (!CanInsert(ent, args.Used) || !_handsSystem.TryDropIntoContainer(args.User, args.Used, containerComp.Container))
        {
            return;
        }

        _adminLog.Add(LogType.Action, LogImpact.Medium, $"{ToPrettyString(args.User):player} inserted {ToPrettyString(args.Used)} into {ToPrettyString(ent)}");
        AfterInsert(ent, args.Used, args.User);
        args.Handled = true;
    }

    protected virtual void OnDisposalInit(Entity<DisposalUnitComponent> ent, ref ComponentInit args)
    {
        ent.Comp.ContainerComponent = EnsureComp<DisposalContainerComponent>(ent);
        ent.Comp.ContainerComponent.Container = Containers.EnsureContainer<Container>(ent, DisposalContainerComponent.ContainerId);
    }

    private void OnPowerChange(Entity<DisposalUnitComponent> ent, ref PowerChangedEvent args)
    {
        var comp = ent.Comp;

        if (!comp.Running)
            return;

        UpdateUI(ent);
        UpdateVisualState(ent);

        if (!args.Powered)
        {
            comp.NextFlush = null;
            Dirty(ent);
            return;
        }

        if (comp.Engaged)
        {
            // Run ManualEngage to recalculate a new flush time
            ManualEngage(ent);
        }
    }

    private void OnAnchorChanged(Entity<DisposalUnitComponent> ent, ref AnchorStateChangedEvent args)
    {
        if (Terminating(ent))
            return;

        UpdateVisualState(ent);
        if (!args.Anchored)
            TryEjectContents(ent);
    }

    private void OnDragDropOn(Entity<DisposalUnitComponent> ent, ref DragDropTargetEvent args)
    {
        args.Handled = TryInsert(ent, args.Dragged, args.User);
    }

    protected virtual void UpdateUI(Entity<DisposalUnitComponent> entity)
    {

    }

    /// <summary>
    /// Returns the estimated time when the disposal unit will be back to full pressure.
    /// </summary>
    public TimeSpan EstimatedFullPressure(Entity<DisposalUnitComponent> ent)
    {
        var comp = ent.Comp;

        if (comp.NextPressurized < GameTiming.CurTime)
            return TimeSpan.Zero;

        return comp.NextPressurized;
    }

    public bool CanFlush(Entity<DisposalUnitComponent> ent)
    {
        return GetState(ent) == DisposalsPressureState.Ready
               && _power.IsPowered(ent.Owner)
               && Comp<TransformComponent>(ent).Anchored;
    }

    public void Remove(Entity<DisposalUnitComponent> ent, EntityUid toRemove)
    {
        if (GameTiming.ApplyingState)
            return;

        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        if (!Containers.Remove(toRemove, containerComp.Container))
            return;

        if (containerComp.Container.ContainedEntities.Count == 0)
        {
            // If not manually engaged then reset the flushing entirely.
            if (!comp.Engaged)
            {
                comp.NextFlush = null;
                Dirty(ent);
                UpdateUI(ent);
            }
        }

        _climb.Climb(toRemove, toRemove, ent, silent: true);

        UpdateVisualState(ent);
    }

    public void UpdateVisualState(Entity<DisposalUnitComponent> ent, bool flush = false)
    {
        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        if (!TryComp(ent, out AppearanceComponent? appearance))
        {
            return;
        }

        if (!Transform(ent).Anchored)
        {
            _appearance.SetData(ent, DisposalUnitComponent.Visuals.VisualState, DisposalUnitComponent.VisualState.UnAnchored, appearance);
            _appearance.SetData(ent, DisposalUnitComponent.Visuals.Handle, DisposalUnitComponent.HandleState.Normal, appearance);
            _appearance.SetData(ent, DisposalUnitComponent.Visuals.Light, DisposalUnitComponent.LightStates.Off, appearance);
            return;
        }

        var state = GetState(ent);

        switch (state)
        {
            case DisposalsPressureState.Flushed:
                _appearance.SetData(ent, DisposalUnitComponent.Visuals.VisualState, DisposalUnitComponent.VisualState.OverlayFlushing, appearance);
                break;
            case DisposalsPressureState.Pressurizing:
                _appearance.SetData(ent, DisposalUnitComponent.Visuals.VisualState, DisposalUnitComponent.VisualState.OverlayCharging, appearance);
                break;
            case DisposalsPressureState.Ready:
                _appearance.SetData(ent, DisposalUnitComponent.Visuals.VisualState, DisposalUnitComponent.VisualState.Anchored, appearance);
                break;
        }

        _appearance.SetData(ent, DisposalUnitComponent.Visuals.Handle, comp.Engaged
            ? DisposalUnitComponent.HandleState.Engaged
            : DisposalUnitComponent.HandleState.Normal, appearance);

        if (!_power.IsPowered(ent.Owner))
        {
            _appearance.SetData(ent, DisposalUnitComponent.Visuals.Light, DisposalUnitComponent.LightStates.Off, appearance);
            return;
        }

        var lightState = DisposalUnitComponent.LightStates.Off;

        if (containerComp.Container.ContainedEntities.Count > 0)
        {
            lightState |= DisposalUnitComponent.LightStates.Full;
        }

        if (state is DisposalsPressureState.Pressurizing or DisposalsPressureState.Flushed)
        {
            lightState |= DisposalUnitComponent.LightStates.Charging;
        }
        else
        {
            lightState |= DisposalUnitComponent.LightStates.Ready;
        }

        _appearance.SetData(ent, DisposalUnitComponent.Visuals.Light, lightState, appearance);
    }

    /// <summary>
    /// Gets the current pressure state of a disposals unit.
    /// </summary>
    /// <param name="ent"></param>
    /// <param name="metadata"></param>
    /// <returns></returns>
    public DisposalsPressureState GetState(Entity<DisposalUnitComponent> ent, MetaDataComponent? metadata = null)
    {
        var comp = ent.Comp;

        var nextPressure = Metadata.GetPauseTime(ent, metadata) + comp.NextPressurized - GameTiming.CurTime;
        var pressurizeTime = 1f / PressurePerSecond;
        var pressurizeDuration = pressurizeTime - comp.FlushDelay.TotalSeconds;

        if (nextPressure.TotalSeconds > pressurizeDuration)
        {
            return DisposalsPressureState.Flushed;
        }

        if (nextPressure > TimeSpan.Zero)
        {
            return DisposalsPressureState.Pressurizing;
        }

        return DisposalsPressureState.Ready;
    }

    public float GetPressure(Entity<DisposalUnitComponent> ent, MetaDataComponent? metadata = null)
    {
        if (!Resolve(ent, ref metadata))
            return 0f;

        var comp = ent.Comp;

        var pauseTime = Metadata.GetPauseTime(ent, metadata);
        return MathF.Min(1f,
            (float)(GameTiming.CurTime - pauseTime - comp.NextPressurized).TotalSeconds / PressurePerSecond);
    }

    protected void OnPreventCollide(Entity<DisposalUnitComponent> ent,
        ref PreventCollideEvent args)
    {
        var otherBody = args.OtherEntity;

        // Items dropped shouldn't collide but items thrown should
        if (HasComp<ItemComponent>(otherBody) && !HasComp<ThrownItemComponent>(otherBody))
        {
            args.Cancelled = true;
        }
    }

    protected void OnCanDragDropOn(Entity<DisposalUnitComponent> ent, ref CanDropTargetEvent args)
    {
        if (args.Handled)
            return;

        args.CanDrop = CanInsert(ent, args.Dragged);
        args.Handled = true;
    }

    protected void OnEmagged(Entity<DisposalUnitComponent> ent, ref GotEmaggedEvent args)
    {
        var comp = ent.Comp;
        comp.DisablePressure = true;
        args.Handled = true;
    }

    public virtual bool CanInsert(Entity<DisposalUnitComponent> ent, EntityUid entity)
    {
        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        // TODO: All of the below should be using the EXISTING EVENT
        if (!Containers.CanInsert(entity, containerComp.Container))
            return false;

        if (!Transform(ent).Anchored)
            return false;

        var storable = HasComp<ItemComponent>(entity);
        if (!storable && !HasComp<BodyComponent>(entity))
            return false;

        if (_whitelistSystem.IsBlacklistPass(comp.Blacklist, entity) ||
            _whitelistSystem.IsWhitelistFail(comp.Whitelist, entity))
            return false;

        if (TryComp<PhysicsComponent>(entity, out var physics) && (physics.CanCollide) || storable)
            return true;
        else
            return false;
    }

    public void DoInsertDisposalUnit(Entity<DisposalUnitComponent> ent,
        EntityUid toInsert,
        EntityUid user,
        DisposalUnitComponent? disposal = null)
    {

        if (!Resolve(ent, ref disposal))
            return;

        var containerComp = disposal.ContainerComponent;

        if (!Containers.Insert(toInsert, containerComp.Container))
            return;

        _adminLog.Add(LogType.Action, LogImpact.Medium, $"{ToPrettyString(user):player} inserted {ToPrettyString(toInsert)} into {ToPrettyString(ent)}");
        AfterInsert(ent, toInsert, user);
    }

    public virtual void AfterInsert(Entity<DisposalUnitComponent> ent,
        EntityUid inserted,
        EntityUid? user = null,
        bool doInsert = false)
    {
        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        Audio.PlayPredicted(comp.InsertSound, ent, user: user);
        if (doInsert && !Containers.Insert(inserted, containerComp.Container))
            return;

        if (user != inserted && user != null)
            _adminLog.Add(LogType.Action, LogImpact.Medium, $"{ToPrettyString(user.Value):player} inserted {ToPrettyString(inserted)} into {ToPrettyString(ent)}");

        QueueAutomaticEngage(ent);

        _ui.CloseUi(ent.Owner, DisposalUnitComponent.DisposalUnitUiKey.Key, inserted);

        // Maybe do pullable instead? Eh still fine.
        Joints.RecursiveClearJoints(inserted);
        UpdateVisualState(ent);
    }

    public bool TryInsert(Entity<DisposalUnitComponent> ent, EntityUid toInsertId, EntityUid? userId, DisposalUnitComponent? unit = null)
    {
        if (!Resolve(ent, ref unit))
            return false;

        if (userId.HasValue && !HasComp<HandsComponent>(userId) && toInsertId != userId) // Mobs like mouse can Jump inside even with no hands
        {
            _popupSystem.PopupEntity(Loc.GetString("disposal-unit-no-hands"), userId.Value, userId.Value, PopupType.SmallCaution);
            return false;
        }

        if (!CanInsert(ent, toInsertId))
            return false;

        var insertingSelf = userId == toInsertId;

        var delay = insertingSelf ? unit.EntryDelay : unit.DraggedEntryDelay;

        if (userId != null && !insertingSelf)
            _popupSystem.PopupEntity(Loc.GetString("disposal-unit-being-inserted", ("user", Identity.Entity((EntityUid)userId, EntityManager))), toInsertId, toInsertId, PopupType.Large);

        if (delay <= 0 || userId == null)
        {
            AfterInsert(ent, toInsertId, userId, doInsert: true);
            return true;
        }

        // Can't check if our target AND disposals moves currently so we'll just check target.
        // if you really want to check if disposals moves then add a predicate.
        var doAfterArgs = new DoAfterArgs(EntityManager, userId.Value, delay, new DisposalDoAfterEvent(), ent, target: toInsertId, used: ent)
        {
            BreakOnDamage = true,
            BreakOnMove = true,
            NeedHand = false,
        };

        _doAfterSystem.TryStartDoAfter(doAfterArgs);
        return true;
    }

    private void UpdateState(Entity<DisposalUnitComponent> ent, DisposalsPressureState state, MetaDataComponent metadata)
    {
        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        if (comp.State == state)
            return;

        comp.State = state;
        UpdateVisualState(ent);
        Dirty(ent, metadata);

        if (state == DisposalsPressureState.Ready)
        {
            comp.NextPressurized = TimeSpan.Zero;

            // Manually engaged
            if (comp.Engaged)
            {
                comp.NextFlush = GameTiming.CurTime + comp.ManualFlushTime;
            }
            else if (containerComp.Container.ContainedEntities.Count > 0)
            {
                comp.NextFlush = GameTiming.CurTime + comp.AutomaticEngageTime;
            }
            else
            {
                comp.NextFlush = null;
            }
        }
    }

    /// <summary>
    /// Work out if we can stop updating this disposals component i.e. full pressure and nothing colliding.
    /// </summary>
    private void Update(Entity<DisposalUnitComponent> ent, MetaDataComponent metadata)
    {
        var state = GetState(ent, metadata);
        var comp = ent.Comp;

        // Pressurizing, just check if we need a state update.
        if (comp.NextPressurized > GameTiming.CurTime)
        {
            UpdateState(ent, state, metadata);
            return;
        }

        if (comp.NextFlush != null)
        {
            if (comp.NextFlush.Value < GameTiming.CurTime)
            {
                TryFlush(ent);
            }
        }

        UpdateState(ent, state, metadata);
    }

    public bool TryFlush(Entity<DisposalUnitComponent> ent)
    {
        if (!CanFlush(ent))
        {
            return false;
        }

        var uid = ent.Owner;
        var comp = ent.Comp;

        if (comp.NextFlush != null)
            comp.NextFlush = comp.NextFlush.Value + comp.AutomaticEngageTime;

        var beforeFlushArgs = new BeforeDisposalFlushEvent();
        RaiseLocalEvent(ent, beforeFlushArgs);

        if (beforeFlushArgs.Cancelled)
        {
            Disengage(ent);
            return false;
        }

        var xform = Transform(ent);
        if (!TryComp(xform.GridUid, out MapGridComponent? grid))
            return false;

        var coords = xform.Coordinates;
        var entry = _map.GetLocal(xform.GridUid.Value, grid, coords)
            .FirstOrDefault(HasComp<Tube.DisposalEntryComponent>);

        if (entry == default || comp is not DisposalUnitComponent sDisposals)
        {
            comp.Engaged = false;
            UpdateUI(ent);
            Dirty(ent);
            return false;
        }

        HandleAir(uid, comp, xform);

        _disposalTubeSystem.TryInsert(entry, sDisposals.ContainerComponent, beforeFlushArgs.Tags);

        comp.NextPressurized = GameTiming.CurTime;
        if (!comp.DisablePressure)
            comp.NextPressurized += TimeSpan.FromSeconds(1f / PressurePerSecond);

        comp.Engaged = false;
        // stop queuing NOW
        comp.NextFlush = null;

        UpdateVisualState(ent, true);
        Dirty(ent);
        UpdateUI(ent);

        return true;
    }

    protected virtual void HandleAir(EntityUid uid, DisposalUnitComponent comp, TransformComponent xform)
    {

    }

    public void ManualEngage(Entity<DisposalUnitComponent> ent, MetaDataComponent? metadata = null)
    {
        var comp = ent.Comp;

        comp.Engaged = true;
        UpdateVisualState(ent);
        Dirty(ent);
        UpdateUI(ent);

        if (!CanFlush(ent))
            return;

        if (!Resolve(ent, ref metadata))
            return;

        var pauseTime = Metadata.GetPauseTime(ent, metadata);
        var nextEngage = GameTiming.CurTime - pauseTime + comp.ManualFlushTime;
        comp.NextFlush = TimeSpan.FromSeconds(Math.Min((comp.NextFlush ?? TimeSpan.MaxValue).TotalSeconds, nextEngage.TotalSeconds));
    }

    public void Disengage(Entity<DisposalUnitComponent> ent)
    {
        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        comp.Engaged = false;

        if (containerComp.Container.ContainedEntities.Count == 0)
        {
            comp.NextFlush = null;
        }

        UpdateVisualState(ent);
        Dirty(ent);
        UpdateUI(ent);
    }

    /// <summary>
    /// Remove all entities currently in the disposal unit.
    /// </summary>
    public void TryEjectContents(Entity<DisposalUnitComponent> ent)
    {
        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        foreach (var entity in containerComp.Container.ContainedEntities.ToArray())
        {
            Remove(ent, entity);
        }

        if (!comp.Engaged)
        {
            comp.NextFlush = null;
            Dirty(ent);
            UpdateUI(ent);
        }
    }

    /// <summary>
    /// If something is inserted (or the likes) then we'll queue up an automatic flush in the future.
    /// </summary>
    public void QueueAutomaticEngage(Entity<DisposalUnitComponent> ent, MetaDataComponent? metadata = null)
    {
        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        if (comp.Deleted || !comp.AutomaticEngage || !_power.IsPowered(ent.Owner) && containerComp.Container.ContainedEntities.Count == 0)
        {
            return;
        }

        var pauseTime = Metadata.GetPauseTime(ent, metadata);
        var automaticTime = GameTiming.CurTime + comp.AutomaticEngageTime - pauseTime;
        var flushTime = TimeSpan.FromSeconds(Math.Min((comp.NextFlush ?? TimeSpan.MaxValue).TotalSeconds, automaticTime.TotalSeconds));

        comp.NextFlush = flushTime;
        Dirty(ent);
        UpdateUI(ent);
    }

    private void OnUiButtonPressed(EntityUid uid, DisposalUnitComponent comp, DisposalUnitComponent.UiButtonPressedMessage args)
    {
        var ent = new Entity<DisposalUnitComponent>(uid, comp);

        if (args.Actor is not { Valid: true } player)
        {
            return;
        }

        switch (args.Button)
        {
            case DisposalUnitComponent.UiButton.Eject:
                TryEjectContents(ent);
                _adminLog.Add(LogType.Action, LogImpact.Low, $"{ToPrettyString(player):player} hit eject button on {ToPrettyString(ent)}");
                break;
            case DisposalUnitComponent.UiButton.Engage:
                ToggleEngage(ent);
                _adminLog.Add(LogType.Action, LogImpact.Low, $"{ToPrettyString(player):player} hit flush button on {ToPrettyString(ent)}, it's now {(comp.Engaged ? "on" : "off")}");
                break;
            case DisposalUnitComponent.UiButton.Power:
                _power.TogglePower(ent, user: args.Actor);
                break;
            default:
                throw new ArgumentOutOfRangeException($"{ToPrettyString(player):player} attempted to hit a nonexistant button on {ToPrettyString(ent)}");
        }
    }

    public void ToggleEngage(Entity<DisposalUnitComponent> ent)
    {
        var comp = ent.Comp;

        comp.Engaged ^= true;

        if (comp.Engaged)
        {
            ManualEngage(ent);
        }
        else
        {
            Disengage(ent);
        }
    }

    private void AddClimbInsideVerb(EntityUid uid, DisposalUnitComponent comp, GetVerbsEvent<Verb> args)
    {
        var ent = new Entity<DisposalUnitComponent>(uid, comp);
        var containerComp = comp.ContainerComponent;

        // This is not an interaction, activation, or alternative verb type because unfortunately most users are
        // unwilling to accept that this is where they belong and don't want to accidentally climb inside.
        if (!args.CanAccess ||
            !args.CanInteract ||
            containerComp.Container.ContainedEntities.Contains(args.User) ||
            !ActionBlockerSystem.CanMove(args.User))
        {
            return;
        }

        if (!CanInsert(ent, args.User))
            return;

        // Add verb to climb inside of the unit,
        Verb verb = new()
        {
            Act = () => TryInsert(ent, args.User, args.User),
            DoContactInteraction = true,
            Text = Loc.GetString("disposal-self-insert-verb-get-data-text")
        };
        // TODO VERB ICON
        // TODO VERB CATEGORY
        // create a verb category for "enter"?
        // See also, medical scanner. Also maybe add verbs for entering lockers/body bags?
        args.Verbs.Add(verb);
    }
}
