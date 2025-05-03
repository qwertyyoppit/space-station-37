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
public abstract class DisposalInletSystem : EntitySystem
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
        SubscribeLocalEvent<DisposalInletComponent, ContainerRelayMovementEntityEvent>(OnMovement);
    }

    private void AddInsertVerb(EntityUid uid, DisposalInletComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands == null || args.Using == null)
            return;

        if (!ActionBlockerSystem.CanDrop(args.User))
            return;

        if (!CanInsert(uid, component, args.Using.Value))
            return;

        InteractionVerb insertVerb = new()
        {
            Text = Name(args.Using.Value),
            Category = VerbCategory.Insert,
            Act = () =>
            {
                _handsSystem.TryDropIntoContainer(args.User,
                    args.Using.Value,
                    component.Container,
                    checkActionBlocker: false,
                    args.Hands);
                _adminLog.Add(LogType.Action,
                    LogImpact.Medium,
                    $"{ToPrettyString(args.User):player} inserted {ToPrettyString(args.Using.Value)} into {ToPrettyString(uid)}");
                AfterInsert(uid, component, args.Using.Value, args.User);
            }
        };

        args.Verbs.Add(insertVerb);
    }

    private void OnDoAfter(EntityUid uid, DisposalInletComponent component, DoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Args.Target == null || args.Args.Used == null)
            return;

        AfterInsert(uid, component, args.Args.Target.Value, args.Args.User, doInsert: true);

        args.Handled = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<DisposalInletComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var unit, out var metadata))
        {
            Update(uid, unit, metadata);
        }
    }

    private void OnAfterInteractUsing(EntityUid uid, DisposalInletComponent component, AfterInteractUsingEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;

        if (!HasComp<HandsComponent>(args.User))
        {
            return;
        }

        if (!CanInsert(uid, component, args.Used) ||
            !_handsSystem.TryDropIntoContainer(args.User, args.Used, component.Container))
        {
            return;
        }

        _adminLog.Add(LogType.Action,
            LogImpact.Medium,
            $"{ToPrettyString(args.User):player} inserted {ToPrettyString(args.Used)} into {ToPrettyString(uid)}");
        AfterInsert(uid, component, args.Used, args.User);
        args.Handled = true;
    }

    protected virtual void OnDisposalInit(Entity<DisposalInletComponent> ent, ref ComponentInit args)
    {
        ent.Comp.Container = Containers.EnsureContainer<Container>(ent, DisposalInletComponent.ContainerId);
    }

    private void OnPowerChange(EntityUid uid, DisposalInletComponent component, ref PowerChangedEvent args)
    {
        if (!component.Running)
            return;

        UpdateVisualState(uid, component);

        if (!args.Powered)
        {
            //flush!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
        }
    }

    private void OnAnchorChanged(EntityUid uid, DisposalInletComponent component, ref AnchorStateChangedEvent args)
    {
        if (Terminating(uid))
            return;

        UpdateVisualState(uid, component);
        if (!args.Anchored)
            TryEjectContents(uid, component);
    }

    private void OnDragDropOn(EntityUid uid, DisposalInletComponent component, ref DragDropTargetEvent args)
    {
        args.Handled = TryInsert(uid, args.Dragged, args.User);
    }

    public bool CanFlush(EntityUid unit, DisposalInletComponent component)
    {
        return GetState(unit, component) == DisposalsInletState.Ready
               && Comp<TransformComponent>(unit).Anchored;
    }

    public void Remove(EntityUid uid, DisposalInletComponent component, EntityUid toRemove)
    {
        if (GameTiming.ApplyingState)
            return; //RETURN AFTER THIS IF POWER IS YES!!!!!!!!!!!! UNLESS THIS IS FOR FLUSHING

        if (!Containers.Remove(toRemove, component.Container))
            return;

        if (component.Container.ContainedEntities.Count == 0)
        {
            Dirty(uid, component);
        }

        UpdateVisualState(uid, component);
    }

    public void UpdateVisualState(EntityUid uid, DisposalInletComponent component, bool flush = false)
    {
        if (!TryComp(uid, out AppearanceComponent? appearance))
            return;

        if (!Transform(uid).Anchored)
        {
            _appearance.SetData(uid,
                DisposalInletComponent.Visuals.VisualState,
                DisposalInletComponent.VisualState.UnAnchored,
                appearance);
        }
    }

    /// <summary>
    /// Gets the current pressure state of a disposals inlet.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="component"></param>
    /// <param name="metadata"></param>
    /// <returns></returns>
    public DisposalsInletState GetState(EntityUid uid, DisposalInletComponent component, MetaDataComponent? metadata = null)
    {
        var nextFlush = Metadata.GetPauseTime(uid, metadata) + component.NextFlush - GameTiming.CurTime;

        if (nextFlush > TimeSpan.Zero)
        {
            return DisposalsInletState.Flushed;
        }

        return DisposalsInletState.Ready;
    }

    protected void OnPreventCollide(EntityUid uid,
        DisposalInletComponent component,
        ref PreventCollideEvent args)
    {
        if (!CanEnterDirection(uid, args.OtherEntity))
            return;

        if (!CanInsert(uid, component, args.OtherEntity))
            return;

        args.Cancelled = true;
        Dirty(uid, component);
    }

    private void OnStartCollide(EntityUid uid,
        DisposalInletComponent component,
        ref StartCollideEvent args)
    {
        //jkill the victim here!!!!!!!!!!!
    }

    private bool CanEnterDirection(EntityUid uid,
        EntityUid other)
    {
        var xForm = Transform(uid);
        var otherXForm = Transform(other);

        var (pos, rot) = TransformSystem.GetWorldPositionRotation(xForm);
        var otherPos = TransformSystem.GetWorldPosition(otherXForm);

        var approachingAngle = (pos - otherPos).ToAngle();
        var rotateAngle = rot.ToWorldVec().ToAngle();

        var diff = Math.Abs(approachingAngle - rotateAngle);
        diff %= MathHelper.TwoPi;
        if (diff > Math.PI)
            diff = MathHelper.TwoPi - diff;

        return diff < Math.PI / 5;
    }

    protected void OnCanDragDropOn(EntityUid uid, DisposalInletComponent component, ref CanDropTargetEvent args)
    {
        if (args.Handled)
            return;

        args.CanDrop = CanInsert(uid, component, args.Dragged);
        args.Handled = true;
    }

    private void OnEmagged(EntityUid uid, DisposalInletComponent component, ref GotEmaggedEvent args)
    {

        args.Handled = true;
    }

    public virtual bool CanInsert(EntityUid uid, DisposalInletComponent component, EntityUid entity)
    {
        if (!Containers.CanInsert(entity, component.Container))
            return false;

        if (!Transform(uid).Anchored)
            return false;

        if (_whitelistSystem.IsBlacklistPass(component.Blacklist, entity) ||
            _whitelistSystem.IsWhitelistFail(component.Whitelist, entity))
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

        if (!Containers.Insert(toInsert, disposal.Container))
            return;

        _adminLog.Add(LogType.Action,
            LogImpact.Medium,
            $"{ToPrettyString(user):player} inserted {ToPrettyString(toInsert)} into {ToPrettyString(uid)}");
        AfterInsert(uid, disposal, toInsert, user);
    }

    public virtual void AfterInsert(EntityUid uid,
        DisposalInletComponent component,
        EntityUid inserted,
        EntityUid? user = null,
        bool doInsert = false)
    {
        Audio.PlayPredicted(component.InsertSound, uid, user: user);
        if (doInsert && !Containers.Insert(inserted, component.Container))
            return;

        if (user != inserted && user != null)
            _adminLog.Add(LogType.Action,
                LogImpact.Medium,
                $"{ToPrettyString(user.Value):player} inserted {ToPrettyString(inserted)} into {ToPrettyString(uid)}");

        QueueAutomaticEngage(uid, component);

        Joints.RecursiveClearJoints(inserted);
        UpdateVisualState(uid, component);
    }

    public bool TryInsert(EntityUid unitId,
        EntityUid toInsertId,
        EntityUid? userId,
        DisposalInletComponent? component = null)
    {
        if (!Resolve(unitId, ref component))
            return false;

        if (userId.HasValue && !HasComp<HandsComponent>(userId) && toInsertId != userId)
        {
            _popupSystem.PopupEntity(Loc.GetString("disposal-unit-no-hands"),
                userId.Value,
                userId.Value,
                PopupType.SmallCaution);
            return false;
        }

        if (!CanInsert(unitId, component, toInsertId))
            return false;

        bool insertingSelf = userId == toInsertId;

        var delay = insertingSelf ? component.EntryDelay : component.DraggedEntryDelay;

        if (userId != null && !insertingSelf)
            _popupSystem.PopupEntity(
                Loc.GetString("disposal-unit-being-inserted",
                    ("user", Identity.Entity((EntityUid)userId, EntityManager))),
                toInsertId,
                toInsertId,
                PopupType.Large);

        if (delay <= 0 || userId == null)
        {
            AfterInsert(unitId, component, toInsertId, userId, doInsert: true);
            return true;
        }

        // Can't check if our target AND disposals moves currently so we'll just check target.
        // if you really want to check if disposals moves then add a predicate.
        var doAfterArgs = new DoAfterArgs(EntityManager,
            userId.Value,
            delay,
            new DisposalDoAfterEvent(),
            unitId,
            target: toInsertId,
            used: unitId)
        {
            BreakOnDamage = true,
            BreakOnMove = true,
            NeedHand = false,
        };

        _doAfterSystem.TryStartDoAfter(doAfterArgs);
        return true;
    }

    private void UpdateState(EntityUid uid,
        DisposalsInletState state,
        DisposalInletComponent component,
        MetaDataComponent metadata)
    {
        if (component.State == state)
            return;

        component.State = state;
        UpdateVisualState(uid, component);
        Dirty(uid, component, metadata);

        if (state == DisposalsInletState.Ready)
        {
            component.NextPressurized = TimeSpan.Zero;

            if (component.Container.ContainedEntities.Count > 0)
            {
                component.NextFlush = GameTiming.CurTime + component.AutomaticEngageTime;
            }
            else
            {
                component.NextFlush = null;
            }
        }
    }

    /// <summary>
    /// Work out if we can stop updating this disposals component i.e. full pressure and nothing colliding.
    /// </summary>
    private void Update(EntityUid uid, DisposalInletComponent component, MetaDataComponent metadata)
    {
        var state = GetState(uid, component, metadata);

        // Pressurizing, just check if we need a state update.
        if (component.NextPressurized > GameTiming.CurTime)
        {
            UpdateState(uid, state, component, metadata);
            return;
        }

        if (component.NextFlush != null)
        {
            if (component.NextFlush.Value < GameTiming.CurTime)
            {
                TryFlush(uid, component);
            }
        }

        UpdateState(uid, state, component, metadata);
    }
}
