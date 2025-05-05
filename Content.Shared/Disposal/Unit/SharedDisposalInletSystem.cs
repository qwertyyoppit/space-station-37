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
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared.Disposal.Unit;

[Serializable, NetSerializable]
public abstract class SharedDisposalInletSystem : EntitySystem
{
    [Dependency] protected readonly ActionBlockerSystem ActionBlockerSystem = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelistSystem = default!;
    [Dependency] protected readonly MetaDataSystem Metadata = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] protected readonly SharedAudioSystem Audio = default!;
    [Dependency] protected readonly IGameTiming GameTiming = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly ClimbSystem _climb = default!;
    [Dependency] protected readonly SharedContainerSystem Containers = default!;
    [Dependency] protected readonly SharedJointSystem Joints = default!;
    [Dependency] private readonly SharedPowerReceiverSystem _power = default!;
    [Dependency] private readonly SharedDisposalTubeSystem _disposalTubeSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfterSystem = default!;
    [Dependency] private readonly SharedHandsSystem _handsSystem = default!;
    [Dependency] protected readonly SharedTransformSystem TransformSystem = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DisposalInletComponent, PreventCollideEvent>(OnPreventCollide);
        SubscribeLocalEvent<DisposalInletComponent, StartCollideEvent>(OnStartCollide);
        SubscribeLocalEvent<DisposalInletComponent, CanDropTargetEvent>(OnCanDragDropOn);
        SubscribeLocalEvent<DisposalInletComponent, GetVerbsEvent<InteractionVerb>>(AddInsertVerb);

        SubscribeLocalEvent<DisposalInletComponent, DisposalDoAfterEvent>(OnDoAfter);

        SubscribeLocalEvent<DisposalInletComponent, GotEmaggedEvent>(OnEmagged);
        SubscribeLocalEvent<DisposalInletComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        SubscribeLocalEvent<DisposalInletComponent, PowerChangedEvent>(OnPowerChange);
        SubscribeLocalEvent<DisposalInletComponent, ComponentInit>(OnDisposalInit);

        SubscribeLocalEvent<DisposalInletComponent, AfterInteractUsingEvent>(OnAfterInteractUsing);
        SubscribeLocalEvent<DisposalInletComponent, DragDropTargetEvent>(OnDragDropOn);
    }

    private void AddInsertVerb(EntityUid uid, DisposalInletComponent comp, GetVerbsEvent<InteractionVerb> args)
    {
        var ent = new Entity<DisposalInletComponent>(uid, comp);

        if (!args.CanAccess || !args.CanInteract || args.Hands == null || args.Using == null)
            return;

        if (!ActionBlockerSystem.CanDrop(args.User))
            return;

        if (!CanInsert(ent, args.Using.Value))
            return;

        InteractionVerb insertVerb = new()
        {
            Text = Name(args.Using.Value),
            Category = VerbCategory.Insert,
            Act = () =>
            {
                _handsSystem.TryDropIntoContainer(args.User,
                    args.Using.Value,
                    comp.ContainerComponent.Container,
                    checkActionBlocker: false,
                    args.Hands);
                _adminLog.Add(LogType.Action,
                    LogImpact.Medium,
                    $"{ToPrettyString(args.User):player} inserted {ToPrettyString(args.Using.Value)} into {ToPrettyString(ent)}");
                AfterInsert(ent, args.Using.Value, args.User);
            }
        };

        args.Verbs.Add(insertVerb);
    }

    private void OnDoAfter(EntityUid uid, DisposalInletComponent comp, DoAfterEvent args)
    {
        var ent = new Entity<DisposalInletComponent>(uid, comp);

        if (args.Handled || args.Cancelled || args.Args.Target == null || args.Args.Used == null)
            return;

        AfterInsert(ent, args.Args.Target.Value, args.Args.User, doInsert: true);

        args.Handled = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<DisposalInletComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var comp, out var metadata))
        {
            var ent = new Entity<DisposalInletComponent>(uid, comp);
            Update(ent, metadata);
        }
    }

    private void OnAfterInteractUsing(EntityUid uid, DisposalInletComponent comp, AfterInteractUsingEvent args)
    {
        var ent = new Entity<DisposalInletComponent>(uid, comp);
        var containerComp = comp.ContainerComponent;

        if (args.Handled || !args.CanReach)
            return;

        if (!HasComp<HandsComponent>(args.User))
        {
            return;
        }

        if (!CanInsert(ent, args.Used) ||
            !_handsSystem.TryDropIntoContainer(args.User, args.Used, containerComp.Container))
        {
            return;
        }

        _adminLog.Add(LogType.Action,
            LogImpact.Medium,
            $"{ToPrettyString(args.User):player} inserted {ToPrettyString(args.Used)} into {ToPrettyString(ent)}");
        AfterInsert(ent, args.Used, args.User);
        args.Handled = true;
    }

    protected virtual void OnDisposalInit(Entity<DisposalInletComponent> ent, ref ComponentInit args)
    {
        ent.Comp.ContainerComponent = EnsureComp<DisposalContainerComponent>(ent);
        ent.Comp.ContainerComponent.Container = Containers.EnsureContainer<Container>(ent, DisposalContainerComponent.ContainerId);
    }

    private void OnPowerChange(Entity<DisposalInletComponent> ent, ref PowerChangedEvent args)
    {
        if (!TryComp<DisposalInletComponent>(ent, out var comp))
            return;

        if (!comp.Running)
            return;

        UpdateVisualState(ent);
    }

    private void OnAnchorChanged(Entity<DisposalInletComponent> ent, ref AnchorStateChangedEvent args)
    {
        if (Terminating(ent))
            return;

        UpdateVisualState(ent);
        if (!args.Anchored)
            TryEjectContents(ent);
    }

    private void OnDragDropOn(Entity<DisposalInletComponent> ent, ref DragDropTargetEvent args)
    {
        args.Handled = args.User != args.Dragged;
        args.Handled = TryInsert(ent, args.Dragged, args.User);
    }

    public bool CanFlush(Entity<DisposalInletComponent> ent)
    {
        return GetState(ent) == DisposalsInletState.Ready
               && Comp<TransformComponent>(ent).Anchored;
    }

    public void Remove(Entity<DisposalInletComponent> ent, EntityUid toRemove)
    {
        if (GameTiming.ApplyingState)
            return;

        var containerComp = ent.Comp.ContainerComponent;

        if (!Containers.Remove(toRemove, containerComp.Container))
            return;

        if (containerComp.Container.ContainedEntities.Count == 0)
        {
            Dirty(ent);
        }
    }

    public void UpdateVisualState(Entity<DisposalInletComponent> ent, bool flush = false)
    {
        if (!TryComp(ent, out AppearanceComponent? appearance))
            return;

        if (!Transform(ent).Anchored)
        {
            _appearance.SetData(ent,
                DisposalInletComponent.Visuals.VisualState,
                DisposalInletComponent.VisualState.UnAnchored,
                appearance);
        }
    }

    /// <summary>
    /// Gets the current flushing state of a disposals inlet.
    /// </summary>
    /// <param name="ent"></param>
    /// <param name="metadata"></param>
    /// <returns></returns>
    public DisposalsInletState GetState(Entity<DisposalInletComponent> ent, MetaDataComponent? metadata = null)
    {
        var comp = ent.Comp;

        var nextFlushed = Metadata.GetPauseTime(ent, metadata) + comp.NextFlush - GameTiming.CurTime;

        if (nextFlushed > TimeSpan.Zero)
        {
            return DisposalsInletState.Flushed;
        }

        return DisposalsInletState.Ready;
    }

    protected void OnPreventCollide(Entity<DisposalInletComponent> ent,
        ref PreventCollideEvent args)
    {
        if (!CanEnterDirection(ent, args.OtherEntity))
            return;

        if (!CanInsert(ent, args.OtherEntity))
            return;

        args.Cancelled = true;
        Dirty(ent);
    }

    private void OnStartCollide(Entity<DisposalInletComponent> ent,
        ref StartCollideEvent args)
    {
        //jkill the victim here!!!!!!!!!!!
    }

    private bool CanEnterDirection(Entity<DisposalInletComponent> ent,
        EntityUid other)
    {
        var xform = Transform(ent);
        var otherXform = Transform(other);

        var (pos, rot) = TransformSystem.GetWorldPositionRotation(xform);
        var otherPos = TransformSystem.GetWorldPosition(otherXform);

        var approachingAngle = (pos - otherPos).ToAngle();
        var rotateAngle = rot.ToWorldVec().ToAngle();

        var diff = Math.Abs(approachingAngle - rotateAngle);
        diff %= MathHelper.TwoPi;
        if (diff > Math.PI)
            diff = MathHelper.TwoPi - diff;

        return diff < Math.PI / 5;
    }

    protected void OnCanDragDropOn(Entity<DisposalInletComponent> ent, ref CanDropTargetEvent args)
    {
        if (args.Handled)
            return;

        args.CanDrop = args.User == args.Dragged;
        if (args.CanDrop)
            args.CanDrop = CanInsert(ent, args.Dragged);
        args.Handled = true;
    }

    private void OnEmagged(Entity<DisposalInletComponent> ent, ref GotEmaggedEvent args)
    {
        var comp = ent.Comp;
        comp.DisableFlushDelay = true;
        args.Handled = true;
    }

    public virtual bool CanInsert(Entity<DisposalInletComponent> ent, EntityUid entity)
    {
        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        if (!Containers.CanInsert(entity, containerComp.Container))
            return false;

        if (!Transform(ent).Anchored)
            return false;

        if (_whitelistSystem.IsBlacklistPass(comp.Blacklist, entity) ||
            _whitelistSystem.IsWhitelistFail(comp.Whitelist, entity))
            return false;

        var storable = HasComp<ItemComponent>(entity);
        return TryComp<PhysicsComponent>(entity, out var physics)
               && (physics.CanCollide) && physics.BodyType != BodyType.Static
               || storable;
    }

    public void DoInsertDisposalInlet(Entity<DisposalInletComponent> ent,
        EntityUid toInsert,
        EntityUid user,
        DisposalInletComponent? disposal = null)
    {
        if (!Resolve(ent, ref disposal))
            return;

        var containerComp = disposal.ContainerComponent;

        if (!Containers.Insert(toInsert, containerComp.Container))
            return;

        _adminLog.Add(LogType.Action,
            LogImpact.Medium,
            $"{ToPrettyString(user):player} inserted {ToPrettyString(toInsert)} into {ToPrettyString(ent)}");
        AfterInsert(ent, toInsert, user);
    }

    public virtual void AfterInsert(Entity<DisposalInletComponent> ent,
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
            _adminLog.Add(LogType.Action,
                LogImpact.Medium,
                $"{ToPrettyString(user.Value):player} inserted {ToPrettyString(inserted)} into {ToPrettyString(ent)}");

        if (user != inserted) // Don't queue if player dragged themself in
            QueueAutomaticFlush(ent);

        Joints.RecursiveClearJoints(inserted);
        UpdateVisualState(ent);
    }

    public bool TryInsert(Entity<DisposalInletComponent> ent,
        EntityUid toInsertId,
        EntityUid? userId,
        DisposalInletComponent? comp = null)
    {
        if (!Resolve(ent, ref comp))
            return false;

        if (userId.HasValue && !HasComp<HandsComponent>(userId) && toInsertId != userId)
        {
            _popupSystem.PopupEntity(Loc.GetString("disposal-unit-no-hands"),
                userId.Value,
                userId.Value,
                PopupType.SmallCaution);
            return false;
        }

        if (!CanInsert(ent, toInsertId))
            return false;

        var delay = userId != null ? comp.EntryDelay : 0;

        if (delay <= 0 || userId == null)
        {
            AfterInsert(ent, toInsertId, userId, doInsert: true);
            return true;
        }

        var doAfterArgs = new DoAfterArgs(EntityManager,
            userId.Value,
            delay,
            new DisposalDoAfterEvent(),
            ent,
            target: toInsertId,
            used: ent)
        {
            BreakOnDamage = true,
            BreakOnMove = true,
            NeedHand = false,
        };

        _doAfterSystem.TryStartDoAfter(doAfterArgs);
        return true;
    }

    private void UpdateState(Entity<DisposalInletComponent> ent,
        DisposalsInletState state,
        MetaDataComponent metadata)
    {
        var comp = ent.Comp;

        if (comp.State == state)
            return;

        comp.State = state;
        UpdateVisualState(ent);
        Dirty(ent, metadata);
    }

    /// <summary>
    /// Work out if we can stop updating this disposal inlet component.
    /// </summary>
    private void Update(Entity<DisposalInletComponent> ent, MetaDataComponent metadata)
    {
        var comp = ent.Comp;
        if (comp.NextFlush < GameTiming.CurTime)
        {
            TryFlush(ent);
        }

        var state = GetState(ent, metadata);

        UpdateState(ent, state, metadata);
    }

    public bool TryFlush(Entity<DisposalInletComponent> ent)
    {
        if (!CanFlush(ent))
        {
            return false;
        }

        var comp = ent.Comp;

        var beforeFlushArgs = new BeforeDisposalFlushEvent();
        RaiseLocalEvent(ent, beforeFlushArgs);

        if (beforeFlushArgs.Cancelled)
        {
            return false;
        }

        var xform = Transform(ent);
        if (!TryComp(xform.GridUid, out MapGridComponent? grid))
            return false;

        var coords = xform.Coordinates;
        var entry = _map.GetLocal(xform.GridUid.Value, grid, coords)
            .FirstOrDefault(HasComp<Tube.DisposalEntryComponent>);

        if (entry == default || comp is not DisposalInletComponent sDisposals)
        {
            Dirty(ent);
            return false;
        }

        HandleAir(ent, xform);

        _disposalTubeSystem.TryInsert(entry, sDisposals.ContainerComponent, beforeFlushArgs.Tags);

        comp.NextFlush = null;

        UpdateVisualState(ent, true);
        Dirty(ent);

        return true;
    }

    protected virtual void HandleAir(Entity<DisposalInletComponent> ent, TransformComponent xform)
    {

    }

    /// <summary>
    /// Remove all entities currently in the disposal inlet.
    /// </summary>
    public void TryEjectContents(Entity<DisposalInletComponent> ent)
    {
        var comp = ent.Comp.ContainerComponent;

        foreach (var entity in comp.Container.ContainedEntities.ToArray())
        {
            Remove(ent, entity);
        }
    }

    /// <summary>
    /// When an entity is inserted, queue an automatic flush in the future.
    /// </summary>
    public void QueueAutomaticFlush(Entity<DisposalInletComponent> ent, MetaDataComponent? metadata = null)
    {
        var uid = ent.Owner;
        var comp = ent.Comp;
        var containerComp = comp.ContainerComponent;

        if (comp.Deleted || !_power.IsPowered(uid) && containerComp.Container.ContainedEntities.Count == 0)
        {
            return;
        }

        var pauseTime = Metadata.GetPauseTime(uid, metadata);
        var automaticTime = GameTiming.CurTime + comp.FlushDelay - pauseTime;
        var flushTime = TimeSpan.FromSeconds(Math.Min((comp.NextFlush ?? TimeSpan.MaxValue).TotalSeconds, automaticTime.TotalSeconds));

        comp.NextFlush = comp.DisableFlushDelay ? flushTime : GameTiming.CurTime;
        Dirty(ent);
    }
}
