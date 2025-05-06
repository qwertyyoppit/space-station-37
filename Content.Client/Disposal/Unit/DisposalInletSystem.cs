using Content.Shared.Disposal.Components;
using Content.Shared.Disposal.Unit;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Audio.Systems;

namespace Content.Client.Disposal.Unit;

public sealed class DisposalInletSystem : SharedDisposalInletSystem
{
    [Dependency] private readonly AppearanceSystem _appearanceSystem = default!;
    [Dependency] private readonly AnimationPlayerSystem _animationSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;

    private const string AnimationKey = "disposal_inlet_animation";

    private const string DefaultFlushState = "intake-closing";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DisposalInletComponent, AppearanceChangeEvent>(OnAppearanceChange);
    }

    protected override void OnDisposalInit(EntityUid uid, DisposalInletComponent comp, ref ComponentInit args)
    {
        base.OnDisposalInit(uid, comp, ref args);

        if (!TryComp<SpriteComponent>(uid, out var sprite) || !TryComp<AppearanceComponent>(uid, out var appearance))
            return;

        UpdateState(uid, comp, sprite, appearance);
    }

    private void OnAppearanceChange(EntityUid uid, DisposalInletComponent comp, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        UpdateState(uid, comp, args.Sprite, args.Component);
    }

    /// <summary>
    /// Update visuals and tick animation
    /// </summary>
    private void UpdateState(EntityUid uid, DisposalInletComponent comp, SpriteComponent sprite, AppearanceComponent appearance)
    {
        if (!_appearanceSystem.TryGetData<DisposalInletComponent.VisualState>(uid, DisposalInletComponent.Visuals.VisualState, out var state, appearance))
            return;

        sprite.LayerSetVisible(DisposalInletVisualLayers.Base, state == DisposalInletComponent.VisualState.Anchored);
        sprite.LayerSetVisible(DisposalInletVisualLayers.OverlayFlush, state == DisposalInletComponent.VisualState.OverlayFlushing);

        if (state == DisposalInletComponent.VisualState.OverlayFlushing)
        {
            if (!_animationSystem.HasRunningAnimation(uid, AnimationKey))
            {
                var flushState = sprite.LayerMapTryGet(DisposalInletVisualLayers.OverlayFlush, out var flushLayer)
                    ? sprite.LayerGetState(flushLayer)
                    : new RSI.StateId(DefaultFlushState);

                // Setup the flush animation to play
                var anim = new Animation
                {
                    Length = comp.FlushDelay,
                    AnimationTracks =
                    {
                        new AnimationTrackSpriteFlick
                        {
                            LayerKey = DisposalInletVisualLayers.OverlayFlush,
                            KeyFrames =
                            {
                                // Play the flush animation
                                new AnimationTrackSpriteFlick.KeyFrame(flushState, 0),
                            }
                        },
                    }
                };

                if (comp.FlushSound != null)
                {
                    anim.AnimationTracks.Add(
                        new AnimationTrackPlaySound
                        {
                            KeyFrames =
                            {
                                new AnimationTrackPlaySound.KeyFrame(_audioSystem.ResolveSound(comp.FlushSound), 0)
                            }
                        });
                }

                _animationSystem.Play(uid, anim, AnimationKey);
            }
        }
        else
            _animationSystem.Stop(uid, AnimationKey);
    }
}

public enum DisposalInletVisualLayers : byte
{
    Base,
    OverlayFlush,
}
