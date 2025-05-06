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
        if (!args.CanAccess || !args.CanInteract || args.Hands == null || args.Using == null)
            return;

        if (!ActionBlockerSystem.CanDrop(args.User))
            return;

        if (!CanInsert(uid, comp, args.Using.Value))
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
                    $"{ToPrettyString(args.User):player} inserted {ToPrettyString(args.Using.Value)} into {ToPrettyString(uid)}");
                AfterInsert(uid, comp, args.Using.Value, args.User);
            }
        };

        args.Verbs.Add(insertVerb);
    }

    private void OnDoAfter(EntityUid uid, DisposalInletComponent comp, DoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Args.Target == null || args.Args.Used == null)
            return;

        AfterInsert(uid, comp, args.Args.Target.Value, args.Args.User, doInsert: true);

        args.Handled = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<DisposalInletComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var comp, out var metadata))
        {
            Update(uid, comp, metadata);
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

        if (!CanInsert(uid, comp, args.Used) ||
            !_handsSystem.TryDropIntoContainer(args.User, args.Used, containerComp.Container))
        {
            return;
        }

        _adminLog.Add(LogType.Action,
            LogImpact.Medium,
            $"{ToPrettyString(args.User):player} inserted {ToPrettyString(args.Used)} into {ToPrettyString(ent)}");
        AfterInsert(uid, comp, args.Used, args.User);
        args.Handled = true;
    }

    protected virtual void OnDisposalInit(EntityUid uid, DisposalInletComponent comp, ref ComponentInit args)
    {
        comp.ContainerComponent = EnsureComp<DisposalContainerComponent>(uid);
        comp.ContainerComponent.Container = Containers.EnsureContainer<Container>(uid, DisposalContainerComponent.ContainerId);
    }

    private void OnPowerChange(EntityUid uid, DisposalInletComponent comp, ref PowerChangedEvent args)
    {
        if (!comp.Running)
            return;

        UpdateVisualState(uid, comp);
    }

    private void OnAnchorChanged(EntityUid uid, DisposalInletComponent comp, ref AnchorStateChangedEvent args)
    {
        if (Terminating(uid))
            return;

        UpdateVisualState(uid, comp);
        if (!args.Anchored)
            TryEjectContents(uid, comp);
    }

    private void OnDragDropOn(EntityUid uid, DisposalInletComponent comp, ref DragDropTargetEvent args)
    {
        args.Handled = args.User != args.Dragged;
        args.Handled = TryInsert(uid, args.Dragged, args.User);
    }

    public bool CanFlush(EntityUid uid, DisposalInletComponent comp)
    {
        return GetState(uid, comp) == DisposalsInletState.Ready
               && Comp<TransformComponent>(uid).Anchored;
    }

    public void Remove(EntityUid uid, DisposalInletComponent comp, EntityUid toRemove)
    {
        if (GameTiming.ApplyingState)
            return;

        var containerComp = comp.ContainerComponent;

        if (!Containers.Remove(toRemove, containerComp.Container))
            return;

        if (containerComp.Container.ContainedEntities.Count == 0)
        {
            Dirty(uid, comp);
        }
    }

    public void UpdateVisualState(EntityUid uid, DisposalInletComponent comp, bool flush = false)
    {
        if (!TryComp(uid, out AppearanceComponent? appearance))
            return;

        var state = GetState(uid, comp);

        switch (state)
        {
            case DisposalsInletState.Flushed:
                _appearance.SetData(uid, DisposalInletComponent.Visuals.VisualState, DisposalInletComponent.VisualState.OverlayFlushing, appearance);
                break;
            case DisposalsInletState.Ready:
                _appearance.SetData(uid, DisposalInletComponent.Visuals.VisualState, DisposalInletComponent.VisualState.Anchored, appearance);
                break;
        }
    }

    /// <summary>
    /// Gets the current flushing state of a disposals inlet.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="metadata"></param>
    /// <returns></returns>
    public DisposalsInletState GetState(EntityUid uid, DisposalInletComponent comp, MetaDataComponent? metadata = null)
    {
        var nextFlushed = Metadata.GetPauseTime(uid, metadata) + comp.NextFlush - GameTiming.CurTime;

        if (nextFlushed > TimeSpan.Zero)
        {
            return DisposalsInletState.Flushed;
        }

        return DisposalsInletState.Ready;
    }

    protected void OnPreventCollide(EntityUid uid, DisposalInletComponent comp,
        ref PreventCollideEvent args)
    {
        if (!CanEnterDirection((uid, comp), args.OtherEntity))
            return;

        if (!CanInsert(uid, comp, args.OtherEntity))
            return;

        args.Cancelled = true;
        Dirty(uid, comp);
    }

    private void OnStartCollide(Entity<DisposalInletComponent> ent,
        ref StartCollideEvent args)
    {
        TryInsert(ent, args.OtherEntity, default!);
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

    protected void OnCanDragDropOn(EntityUid uid, DisposalInletComponent comp, ref CanDropTargetEvent args)
    {
        if (args.Handled)
            return;

        args.CanDrop = args.User == args.Dragged;
        if (args.CanDrop)
            args.CanDrop = CanInsert(uid, comp, args.Dragged);
        args.Handled = true;
    }

    private void OnEmagged(Entity<DisposalInletComponent> ent, ref GotEmaggedEvent args)
    {
        var comp = ent.Comp;
        comp.DisableFlushDelay = true;
        args.Handled = true;
    }

    public virtual bool CanInsert(EntityUid uid, DisposalInletComponent comp, EntityUid entity)
    {
        var containerComp = comp.ContainerComponent;

        if (!Containers.CanInsert(entity, containerComp.Container))
            return false;

        if (!Transform(uid).Anchored)
            return false;

        if (_whitelistSystem.IsBlacklistPass(comp.Blacklist, entity) ||
            _whitelistSystem.IsWhitelistFail(comp.Whitelist, entity))
            return false;

        var storable = HasComp<ItemComponent>(entity);
        return TryComp<PhysicsComponent>(entity, out var physics)
               && (physics.CanCollide) && physics.BodyType != BodyType.Static
               || storable;
    }

    public void DoInsertDisposalInlet(EntityUid uid,
        EntityUid toInsert,
        EntityUid user,
        DisposalInletComponent? disposal = null)
    {
        if (!Resolve(uid, ref disposal))
            return;

        var ent = new Entity<DisposalInletComponent>(uid, disposal);
        var containerComp = disposal.ContainerComponent;

        if (!Containers.Insert(toInsert, containerComp.Container))
            return;

        _adminLog.Add(LogType.Action,
            LogImpact.Medium,
            $"{ToPrettyString(user):player} inserted {ToPrettyString(toInsert)} into {ToPrettyString(ent)}");
        AfterInsert(uid, disposal, toInsert, user);
    }

    public virtual void AfterInsert(EntityUid uid, DisposalInletComponent comp,
        EntityUid inserted,
        EntityUid? user = null,
        bool doInsert = false)
    {
        var containerComp = comp.ContainerComponent;

        Audio.PlayPredicted(comp.InsertSound, uid, user: user);
        if (doInsert && !Containers.Insert(inserted, containerComp.Container))
            return;

        if (user != inserted && user != null)
            _adminLog.Add(LogType.Action,
                LogImpact.Medium,
                $"{ToPrettyString(user.Value):player} inserted {ToPrettyString(inserted)} into {ToPrettyString(uid)}");

        if (user != inserted) // Don't queue if player dragged themself in
            QueueAutomaticFlush(uid, comp);

        Joints.RecursiveClearJoints(inserted);
        UpdateVisualState(uid, comp);
    }

    public bool TryInsert(EntityUid uid,
        EntityUid toInsertId,
        EntityUid? userId,
        DisposalInletComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return false;

        if (userId.HasValue && !HasComp<HandsComponent>(userId) && toInsertId != userId)
        {
            _popupSystem.PopupEntity(Loc.GetString("disposal-unit-no-hands"),
                userId.Value,
                userId.Value,
                PopupType.SmallCaution);
            return false;
        }

        if (!CanInsert(uid, comp, toInsertId))
            return false;

        var delay = userId != null ? comp.EntryDelay : 0;

        if (delay <= 0 || userId == null)
        {
            AfterInsert(uid, comp, toInsertId, userId, doInsert: true);
            return true;
        }

        var doAfterArgs = new DoAfterArgs(EntityManager,
            userId.Value,
            delay,
            new DisposalDoAfterEvent(),
            uid,
            target: toInsertId,
            used: uid)
        {
            BreakOnDamage = true,
            BreakOnMove = true,
            NeedHand = false,
        };

        _doAfterSystem.TryStartDoAfter(doAfterArgs);
        return true;
    }

    private void UpdateState(EntityUid uid, DisposalInletComponent comp,
        DisposalsInletState state,
        MetaDataComponent metadata)
    {
        if (comp.State == state)
            return;

        comp.State = state;
        UpdateVisualState(uid, comp);
        Dirty(uid, comp, metadata);
    }

    /// <summary>
    /// Work out if we can stop updating this disposal inlet component.
    /// </summary>
    private void Update(EntityUid uid, DisposalInletComponent comp, MetaDataComponent metadata)
    {
        if (comp.NextFlush < GameTiming.CurTime)
        {
            TryFlush(uid, comp);
        }

        var state = GetState(uid, comp, metadata);

        UpdateState(uid, comp, state, metadata);
    }

    public bool TryFlush(EntityUid uid, DisposalInletComponent comp)
    {
        if (!CanFlush(uid, comp))
        {
            return false;
        }

        var beforeFlushArgs = new BeforeDisposalFlushEvent();
        RaiseLocalEvent(uid, beforeFlushArgs);

        if (beforeFlushArgs.Cancelled)
        {
            return false;
        }

        var xform = Transform(uid);
        if (!TryComp(xform.GridUid, out MapGridComponent? grid))
            return false;

        var coords = xform.Coordinates;
        var entry = _map.GetLocal(xform.GridUid.Value, grid, coords)
            .FirstOrDefault(HasComp<Tube.DisposalEntryComponent>);

        if (entry == default || comp is not DisposalInletComponent sDisposals)
        {
            Dirty(uid, comp);
            return false;
        }

        HandleAir(uid, comp, xform);

        _disposalTubeSystem.TryInsert(entry, sDisposals.ContainerComponent, beforeFlushArgs.Tags);

        comp.NextFlush = null;

        UpdateVisualState(uid, comp, true);
        Dirty(uid, comp);

        return true;
    }

    protected virtual void HandleAir(EntityUid uid, DisposalInletComponent comp, TransformComponent xform)
    {

    }

    /// <summary>
    /// Remove all entities currently in the disposal inlet.
    /// </summary>
    public void TryEjectContents(EntityUid uid, DisposalInletComponent comp)
    {
        var containerComp = comp.ContainerComponent;

        foreach (var entity in containerComp.Container.ContainedEntities.ToArray())
        {
            Remove(uid, comp, entity);
        }
    }

    /// <summary>
    /// When an entity is inserted, queue an automatic flush in the future.
    /// </summary>
    public void QueueAutomaticFlush(EntityUid uid, DisposalInletComponent comp, MetaDataComponent? metadata = null)
    {
        var containerComp = comp.ContainerComponent;

        if (comp.Deleted || !_power.IsPowered(uid) && containerComp.Container.ContainedEntities.Count == 0)
        {
            return;
        }

        var pauseTime = Metadata.GetPauseTime(uid, metadata);
        var automaticTime = GameTiming.CurTime + comp.FlushDelay - pauseTime;
        var flushTime = TimeSpan.FromSeconds(Math.Min((comp.NextFlush ?? TimeSpan.MaxValue).TotalSeconds, automaticTime.TotalSeconds));

        comp.NextFlush = comp.DisableFlushDelay ? flushTime : GameTiming.CurTime;
        Dirty(uid, comp);
    }
}
