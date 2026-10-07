using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Omega.Core.Models;
using Omega.Services;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Omega.Controls;

/// <summary>
/// The Now Playing panel's FX layer (NOW_PLAYING_FX): a Composition
/// stack behind the panel content — palette gradient, drifting
/// colour blobs, a particle pool, and (Pulse) a breathing artwork
/// glow — fed by <see cref="ArtworkPaletteService"/> and the mode
/// read once at construction from settings ("Aurora" default).
///
/// Everything here is panel-scoped: visuals are built on Loaded,
/// loops run only while the panel is alive and not idle, and
/// <see cref="TeardownFx"/> (from DisposePanel) stops and releases
/// the lot. Mode Off builds nothing at all: the XAML's static
/// HeroTintBrush treatment is the whole background, as before.
/// All motion lives on the Composition layer — the UI thread only
/// swaps brush colours on track change and eases the Pulse
/// amplitude on play/pause; nothing here touches layout, so the
/// resize grip's per-frame reflow stays free (sizes/offsets are
/// expression-bound, never resize handlers).
/// </summary>
public sealed partial class NowPlayingPanel
{
    private enum FxMode
    {
        Off,
        Aurora,
        Particles,
        Pulse,
    }

    // Reference space the FX geometry is authored in (the polish
    // spec's default panel). Blob/particle layers scale uniformly
    // from it by host width; the base gradient and scrim bind to the
    // host size exactly.
    private const float FxReferenceWidth = 344f;
    private const float FxReferenceHeight = 800f;

    private FxMode _fxMode = FxMode.Aurora;
    private bool _fxReady;
    private bool _fxCalm = true;
    private bool _motionEnabled = true;
    private bool? _fxLastPlaying;

    private Compositor? _compositor;
    private ContainerVisual? _fxRoot;
    private readonly List<(CompositionObject Target, string Property, CompositionAnimation Animation)> _loops = new();
    private readonly List<IDisposable> _fxDisposables = new();

    private CompositionColorGradientStop[]? _baseStops;
    private CompositionColorGradientStop[]? _blobAStops;
    private CompositionColorGradientStop[]? _blobBStops;
    private CompositionColorGradientStop[][]? _particleStops;
    private CompositionColorGradientStop[]? _glowStops;
    private ContainerVisual? _particleLayer;
    private ContainerVisual? _glowContainer;
    private CompositionPropertySet? _pulseProps;

    private ArtworkPalette? _fallbackPalette;
    private ArtworkPalette? _songPalette;
    private ArtworkPalette? _appliedPalette;
    private int _paletteGeneration;
    private int _colorSwapGeneration;

    private static FxMode ParseFxMode(string? value) => value switch
    {
        "Off" => FxMode.Off,
        "Particles" => FxMode.Particles,
        "Pulse" => FxMode.Pulse,
        _ => FxMode.Aurora,
    };

    /// <summary>The shell's AnimationsEnabled check, mirrored (the reduced-motion signal).</summary>
    private static bool FxAnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // ------------------------------------------------------------------
    // Palette pipeline (mirrors the favourite lookup's discipline:
    // fire-and-forget, generation-checked, failure = fallback)
    // ------------------------------------------------------------------

    private async Task RefreshPaletteAsync()
    {
        // The palette only feeds the FX layer; with the mode Off
        // there is nothing to feed, so the decode never runs.
        if (_fxMode == FxMode.Off)
        {
            return;
        }

        Song? song = ViewModel.CurrentSong;
        int generation = ++_paletteGeneration;
        ArtworkPalette? palette = null;
        if (song is not null)
        {
            try
            {
                palette = await _paletteService.GetPaletteAsync(song);
            }
            catch (Exception)
            {
                palette = null;
            }
        }

        if (_disposed || generation != _paletteGeneration)
        {
            return;
        }

        // A failed/absent extraction falls back to the static-tint
        // family rather than leaving the previous song's colours up.
        if (palette is not null)
        {
            _songPalette = palette;
        }
        else if (_fallbackPalette is not null)
        {
            _songPalette = _fallbackPalette;
        }
        else
        {
            return; // Pre-init with nothing to show yet; InitializeFx seeds the fallback.
        }

        UpdateFxColors(animate: true);
    }

    private ArtworkPalette FallbackPalette()
    {
        // The static HeroTintBrush family (NOW_PLAYING_FX §1
        // fallback): tint as Mid/Dominant, a darkened tint as the
        // anchor, the transport accent as the vibrant stand-in.
        Color tint = (GetThemeBrush("HeroTintBrush") as SolidColorBrush)?.Color
            ?? Color.FromArgb(255, 0x1E, 0x1B, 0x4B);
        Color accent = (GetThemeBrush("TransportActiveBrush") as SolidColorBrush)?.Color
            ?? Color.FromArgb(255, 0x3B, 0xE4, 0x77);
        return new ArtworkPalette(tint, accent, tint, ScaleColor(tint, 0.55f));
    }

    private static Color ScaleColor(Color color, float factor) => Color.FromArgb(
        color.A,
        (byte)Math.Min(255f, color.R * factor),
        (byte)Math.Min(255f, color.G * factor),
        (byte)Math.Min(255f, color.B * factor));

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    // ------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------

    private void InitializeFx()
    {
        if (_fxMode == FxMode.Off || _fxReady)
        {
            return;
        }

        try
        {
            _fallbackPalette = FallbackPalette();
            _songPalette ??= _fallbackPalette;
            _motionEnabled = FxAnimationsEnabled();
            _fxCalm = ViewModel.CurrentSong is null;

            Visual hostVisual = ElementCompositionPreview.GetElementVisual(FxHost);
            _compositor = hostVisual.Compositor;

            _fxRoot = _compositor.CreateContainerVisual();
            _fxDisposables.Add(_fxRoot);
            BindSize(_fxRoot, hostVisual);
            _fxRoot.Clip = _compositor.CreateInsetClip(0f, 0f, 0f, 0f);
            ElementCompositionPreview.SetElementChildVisual(FxHost, _fxRoot);

            BuildBaseLayer(hostVisual);
            BuildBlobs(hostVisual);
            if (_fxMode is FxMode.Particles or FxMode.Pulse)
            {
                BuildParticles(hostVisual, _fxMode == FxMode.Pulse ? 28 : 48);
            }

            if (_fxMode == FxMode.Pulse)
            {
                BuildGlow();
            }

            // The FX stack IS the background now: the tint borders
            // go transparent so it shows through the identity zone
            // and the header alike (the N4 seam dissolves into the
            // gradient). Mode Off never reaches here.
            HeaderTintBorder.Background = null;
            HeroTintBorder.Background = null;

            _fxReady = true;
            if (_particleLayer is not null)
            {
                _particleLayer.IsVisible = !_fxCalm;
            }

            if (_glowContainer is not null)
            {
                _glowContainer.IsVisible = !_fxCalm;
            }

            UpdateFxColors(animate: false);
            if (_motionEnabled && !_fxCalm)
            {
                StartLoops();
            }

            FxOnPlayStateChanged(ViewModel.IsPlaying, instant: true);
        }
        catch (Exception)
        {
            // Composition hosting is the wave's risk area: any
            // failure degrades to the pre-FX panel (static tint,
            // borders restored) rather than a broken surface.
            TeardownFx();
            _fxMode = FxMode.Off;
            HeaderTintBorder.Background = GetThemeBrush("HeroTintBrush");
            HeroTintBorder.Background = GetThemeBrush("HeroTintBrush");
        }
    }

    private void BindSize(Visual visual, Visual hostVisual)
    {
        ExpressionAnimation expression = _compositor!.CreateExpressionAnimation("host.Size");
        expression.SetReferenceParameter("host", hostVisual);
        visual.StartAnimation("Size", expression);
    }

    private void BuildBaseLayer(Visual hostVisual)
    {
        Compositor compositor = _compositor!;
        ArtworkPalette palette = _fallbackPalette!;

        var gradient = compositor.CreateLinearGradientBrush();
        gradient.StartPoint = new Vector2(0.15f, 0f);
        gradient.EndPoint = new Vector2(0.85f, 1f);
        var stop0 = compositor.CreateColorGradientStop(0f, palette.Dark);
        var stop1 = compositor.CreateColorGradientStop(0.55f, palette.Mid);
        var stop2 = compositor.CreateColorGradientStop(1f, palette.Dark);
        gradient.ColorStops.Add(stop0);
        gradient.ColorStops.Add(stop1);
        gradient.ColorStops.Add(stop2);
        _baseStops = new[] { stop0, stop1, stop2 };
        _fxDisposables.Add(gradient);

        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = gradient;
        BindSize(sprite, hostVisual);
        _fxDisposables.Add(sprite);
        _fxRoot!.Children.InsertAtTop(sprite);

        // Legibility scrim (the mock's rule: text sits on the
        // scrim, not on the colour). Theme-aware: black over the
        // dark gradient, white over the light one.
        Color scrimColor = ActualTheme == ElementTheme.Light
            ? Color.FromArgb(77, 255, 255, 255)
            : Color.FromArgb(46, 0, 0, 0);
        var scrim = compositor.CreateSpriteVisual();
        scrim.Brush = compositor.CreateColorBrush(scrimColor);
        BindSize(scrim, hostVisual);
        _fxDisposables.Add(scrim);
        _fxRoot.Children.InsertAtTop(scrim);
    }

    private void BuildBlobs(Visual hostVisual)
    {
        // Blobs live in a layer authored in reference space and
        // scaled uniformly by host width, so their composition
        // survives the 288–520 resize range unchanged.
        Compositor compositor = _compositor!;
        var layer = compositor.CreateContainerVisual();
        var scale = compositor.CreateExpressionAnimation(
            "Vector3(host.Size.X / refWidth, host.Size.X / refWidth, 1f)");
        scale.SetReferenceParameter("host", hostVisual);
        scale.SetScalarParameter("refWidth", FxReferenceWidth);
        layer.StartAnimation("Scale", scale);
        _fxDisposables.Add(layer);
        _fxRoot!.Children.InsertAtTop(layer);

        double blobOpacity = _fxMode switch
        {
            FxMode.Particles => 0.45,
            FxMode.Pulse => 0.7,
            _ => 0.85,
        };

        BuildBlob(layer, new Vector2(86, 205), 460f, _fallbackPalette!.Vibrant,
            periodSeconds: 23, drift: new Vector2(46, 30), opacity: (float)blobOpacity,
            stops: out _blobAStops, breathe: true);
        BuildBlob(layer, new Vector2(298, 478), 380f, _fallbackPalette.Mid,
            periodSeconds: 19, drift: new Vector2(-38, -26), opacity: (float)(blobOpacity * 0.85),
            stops: out _blobBStops, breathe: false);
    }

    private void BuildBlob(
        ContainerVisual layer,
        Vector2 anchor,
        float size,
        Color color,
        double periodSeconds,
        Vector2 drift,
        float opacity,
        out CompositionColorGradientStop[] stops,
        bool breathe)
    {
        Compositor compositor = _compositor!;
        CompositionRadialGradientBrush brush = MakeRadialBrush(
            WithAlpha(color, 190), WithAlpha(color, 0), out stops);

        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        sprite.Size = new Vector2(size, size);
        sprite.Opacity = opacity;
        Vector3 home = new(anchor.X - (size / 2), anchor.Y - (size / 2), 0);
        sprite.Offset = home;
        _fxDisposables.Add(sprite);
        layer.Children.InsertAtTop(sprite);

        // Drift: there-and-back across three keyframes reads as an
        // endless alternate loop (Composition has no alternate flag).
        var driftAnim = compositor.CreateVector3KeyFrameAnimation();
        driftAnim.InsertKeyFrame(0f, home);
        driftAnim.InsertKeyFrame(0.5f, home + new Vector3(drift.X, drift.Y, 0));
        driftAnim.InsertKeyFrame(1f, home);
        driftAnim.Duration = TimeSpan.FromSeconds(periodSeconds);
        driftAnim.IterationBehavior = AnimationIterationBehavior.Forever;
        _loops.Add((sprite, "Offset", driftAnim));

        if (breathe)
        {
            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
            scaleAnim.InsertKeyFrame(0.5f, new Vector3(1.12f, 1.12f, 1f));
            scaleAnim.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
            scaleAnim.Duration = TimeSpan.FromSeconds(27);
            scaleAnim.IterationBehavior = AnimationIterationBehavior.Forever;
            sprite.CenterPoint = new Vector3(size / 2, size / 2, 0);
            _loops.Add((sprite, "Scale", scaleAnim));
        }
    }

    private void BuildParticles(Visual hostVisual, int count)
    {
        Compositor compositor = _compositor!;
        _particleLayer = compositor.CreateContainerVisual();
        var scale = compositor.CreateExpressionAnimation(
            "Vector3(host.Size.X / refWidth, host.Size.X / refWidth, 1f)");
        scale.SetReferenceParameter("host", hostVisual);
        scale.SetScalarParameter("refWidth", FxReferenceWidth);
        _particleLayer.StartAnimation("Scale", scale);
        _fxDisposables.Add(_particleLayer);
        _fxRoot!.Children.InsertAtTop(_particleLayer);

        // Three shared brushes (one per palette role) recoloured in
        // place on track change — sprites persist, colours swap.
        ArtworkPalette palette = _fallbackPalette!;
        Color[] roleColors = { palette.Vibrant, palette.Mid, palette.Dominant };
        byte[] roleAlphas = { 160, 140, 120 };
        var brushes = new CompositionRadialGradientBrush[3];
        _particleStops = new CompositionColorGradientStop[3][];
        for (int i = 0; i < brushes.Length; i++)
        {
            brushes[i] = MakeRadialBrush(
                WithAlpha(roleColors[i], roleAlphas[i]), WithAlpha(roleColors[i], 0),
                out CompositionColorGradientStop[] stops);
            _particleStops[i] = stops;
        }

        var random = new Random(20261008);
        for (int i = 0; i < count; i++)
        {
            bool bokeh = i % 5 == 0;
            float diameter = bokeh ? 22f + ((float)random.NextDouble() * 20f) : 3f + ((float)random.NextDouble() * 3.5f);
            float peak = bokeh ? 0.16f : 0.75f;
            float x = (float)random.NextDouble() * FxReferenceWidth;
            float y = (float)random.NextDouble() * FxReferenceHeight;
            double drift = 560 + (random.NextDouble() * 220);
            double period = 24 + (random.NextDouble() * 22);
            double sway = 6 + (random.NextDouble() * 8);
            double phase = random.NextDouble();

            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = brushes[i % brushes.Length];
            sprite.Size = new Vector2(diameter, diameter);
            _fxDisposables.Add(sprite);
            _particleLayer.Children.InsertAtTop(sprite);

            // Still frame (what reduced motion and the pre-start
            // moment show): the sprite partway along its rise.
            sprite.Offset = new Vector3(
                x + SwayAt(phase, sway, phase),
                (float)(40 - (drift * phase)),
                0);
            sprite.Opacity = peak * FadeAt(phase);

            // Rise + sway as one Vector3 loop; opacity fades at both
            // ends of the same period so the wrap teleport (bottom
            // → top) happens while the sprite is invisible.
            var rise = compositor.CreateVector3KeyFrameAnimation();
            for (int step = 0; step <= 4; step++)
            {
                double t = step / 4.0;
                rise.InsertKeyFrame(
                    (float)t,
                    new Vector3(
                        x + SwayAt(t, sway, phase),
                        (float)(40 - (drift * t)),
                        0));
            }

            rise.Duration = TimeSpan.FromSeconds(period);
            rise.IterationBehavior = AnimationIterationBehavior.Forever;
            _loops.Add((sprite, "Offset", rise));

            var twinkle = compositor.CreateScalarKeyFrameAnimation();
            twinkle.InsertKeyFrame(0f, 0f);
            twinkle.InsertKeyFrame(0.12f, peak);
            twinkle.InsertKeyFrame(0.85f, peak);
            twinkle.InsertKeyFrame(1f, 0f);
            twinkle.Duration = TimeSpan.FromSeconds(period);
            twinkle.IterationBehavior = AnimationIterationBehavior.Forever;
            _loops.Add((sprite, "Opacity", twinkle));
        }
    }

    private static float SwayAt(double t, double sway, double phase) =>
        (float)(sway * Math.Sin((t + phase) * Math.PI * 4));

    private static float FadeAt(double t)
    {
        if (t < 0.12)
        {
            return (float)(t / 0.12);
        }

        return t > 0.85 ? (float)((1 - t) / 0.15) : 1f;
    }

    private void BuildGlow()
    {
        Compositor compositor = _compositor!;
        Visual glowHostVisual = ElementCompositionPreview.GetElementVisual(GlowHost);

        _glowContainer = compositor.CreateContainerVisual();
        _fxDisposables.Add(_glowContainer);
        ElementCompositionPreview.SetElementChildVisual(GlowHost, _glowContainer);

        CompositionRadialGradientBrush brush = MakeRadialBrush(
            WithAlpha(_fallbackPalette!.Vibrant, 150), WithAlpha(_fallbackPalette.Vibrant, 0),
            out _glowStops);
        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        BindSize(sprite, glowHostVisual);
        _fxDisposables.Add(sprite);
        _glowContainer.Children.InsertAtTop(sprite);

        // The breath: a phase scalar loops 0→1 at two beats of
        // 102 BPM (no tempo exists upstream — this is the honest
        // breathing approximation of NOW_PLAYING_FX §2), and the
        // amplitude scalar is eased from code on play/pause. The
        // glow's Scale/Opacity are expressions over the pair, so
        // the composition thread does all per-frame work.
        _pulseProps = compositor.CreatePropertySet();
        _pulseProps.InsertScalar("Amplitude", 0f);
        _pulseProps.InsertScalar("Phase", 0f);
        _fxDisposables.Add(_pulseProps);

        var phase = compositor.CreateScalarKeyFrameAnimation();
        phase.InsertKeyFrame(0f, 0f);
        phase.InsertKeyFrame(1f, 1f);
        phase.Duration = TimeSpan.FromSeconds(120.0 / 102.0);
        phase.IterationBehavior = AnimationIterationBehavior.Forever;
        _loops.Add((_pulseProps, "Phase", phase));

        if (_motionEnabled)
        {
            var center = compositor.CreateExpressionAnimation(
                "Vector3(host.Size.X * 0.5f, host.Size.Y * 0.5f, 0f)");
            center.SetReferenceParameter("host", glowHostVisual);
            sprite.StartAnimation("CenterPoint", center);

            var scaleExpr = compositor.CreateExpressionAnimation(
                "Vector3(1f + 0.07f * props.Amplitude * Sin(props.Phase * 6.2831853f), " +
                "1f + 0.07f * props.Amplitude * Sin(props.Phase * 6.2831853f), 1f)");
            scaleExpr.SetReferenceParameter("props", _pulseProps);
            sprite.StartAnimation("Scale", scaleExpr);

            var opacityExpr = compositor.CreateExpressionAnimation(
                "0.45f + 0.35f * props.Amplitude * (0.5f + 0.5f * Sin(props.Phase * 6.2831853f))");
            opacityExpr.SetReferenceParameter("props", _pulseProps);
            sprite.StartAnimation("Opacity", opacityExpr);
        }
        else
        {
            sprite.Opacity = 0.5f;
        }
    }

    private CompositionRadialGradientBrush MakeRadialBrush(
        Color center, Color edge, out CompositionColorGradientStop[] stops)
    {
        Compositor compositor = _compositor!;
        var brush = compositor.CreateRadialGradientBrush();
        brush.EllipseCenter = new Vector2(0.5f, 0.5f);
        brush.EllipseRadius = new Vector2(0.5f, 0.5f);
        var stop0 = compositor.CreateColorGradientStop(0f, center);
        var stop1 = compositor.CreateColorGradientStop(1f, edge);
        brush.ColorStops.Add(stop0);
        brush.ColorStops.Add(stop1);
        stops = new[] { stop0, stop1 };
        _fxDisposables.Add(brush);
        return brush;
    }

    // ------------------------------------------------------------------
    // Colour swaps (track change): a ~360 ms opacity dip on the FX
    // root while the brush stops change underneath it.
    // ------------------------------------------------------------------

    private void UpdateFxColors(bool animate)
    {
        if (!_fxReady)
        {
            return;
        }

        ArtworkPalette target = (_fxCalm ? _fallbackPalette : _songPalette) ?? _fallbackPalette!;
        if (target == _appliedPalette)
        {
            return;
        }

        if (animate && _motionEnabled && _fxRoot is not null && _compositor is not null)
        {
            _ = DipSwapAsync(target);
        }
        else
        {
            _colorSwapGeneration++;
            SwapColors(target);
            _appliedPalette = target;
        }
    }

    private async Task DipSwapAsync(ArtworkPalette target)
    {
        int generation = ++_colorSwapGeneration;
        try
        {
            var dip = _compositor!.CreateScalarKeyFrameAnimation();
            dip.InsertKeyFrame(0f, 1f);
            dip.InsertKeyFrame(0.45f, 0.15f);
            dip.InsertKeyFrame(1f, 1f);
            dip.Duration = TimeSpan.FromMilliseconds(360);
            _fxRoot!.StartAnimation("Opacity", dip);

            await Task.Delay(165);
            if (_disposed || generation != _colorSwapGeneration || !_fxReady)
            {
                return;
            }

            SwapColors(target);
            _appliedPalette = target;
        }
        catch (Exception)
        {
            // A failed swap leaves the previous palette up; the
            // next track change retries. Never an error surface.
        }
    }

    private void SwapColors(ArtworkPalette palette)
    {
        SetStops(_baseStops, palette.Dark, palette.Mid, palette.Dark);
        SetStops(_blobAStops, WithAlpha(palette.Vibrant, 190), WithAlpha(palette.Vibrant, 0));
        SetStops(_blobBStops, WithAlpha(palette.Mid, 170), WithAlpha(palette.Mid, 0));
        if (_particleStops is not null)
        {
            SetStops(_particleStops[0], WithAlpha(palette.Vibrant, 160), WithAlpha(palette.Vibrant, 0));
            SetStops(_particleStops[1], WithAlpha(palette.Mid, 140), WithAlpha(palette.Mid, 0));
            SetStops(_particleStops[2], WithAlpha(palette.Dominant, 120), WithAlpha(palette.Dominant, 0));
        }

        if (_glowStops is not null)
        {
            SetStops(_glowStops, WithAlpha(palette.Vibrant, 150), WithAlpha(palette.Vibrant, 0));
        }
    }

    private static void SetStops(CompositionColorGradientStop[]? stops, params Color[] colors)
    {
        if (stops is null)
        {
            return;
        }

        for (int i = 0; i < stops.Length && i < colors.Length; i++)
        {
            stops[i].Color = colors[i];
        }
    }

    // ------------------------------------------------------------------
    // State sync (called from RefreshFromViewModel; change-guarded)
    // ------------------------------------------------------------------

    private void FxOnIdleChanged(bool idle)
    {
        if (idle == _fxCalm && _fxReady)
        {
            return;
        }

        _fxCalm = idle;
        if (!_fxReady)
        {
            return;
        }

        if (_particleLayer is not null)
        {
            _particleLayer.IsVisible = !idle;
        }

        if (_glowContainer is not null)
        {
            _glowContainer.IsVisible = !idle;
        }

        if (idle)
        {
            StopLoops();
        }
        else if (_motionEnabled)
        {
            StartLoops();
        }

        // Idle shows the fallback palette (calm, brand-neutral);
        // leaving idle restores the song's. The swap itself is
        // immediate — the palette path cross-fades real changes.
        UpdateFxColors(animate: false);
    }

    private void FxOnPlayStateChanged(bool playing, bool instant = false)
    {
        if (_fxLastPlaying == playing && !instant)
        {
            return;
        }

        _fxLastPlaying = playing;
        if (_fxMode != FxMode.Pulse || _pulseProps is null || _compositor is null)
        {
            return;
        }

        float target = playing ? 1f : 0f;
        if (instant || !_motionEnabled)
        {
            // Reduced motion: a static amplitude difference between
            // playing and paused, never a moving one.
            float value = _motionEnabled ? target : (playing ? 0.6f : 0.25f);
            _pulseProps.StopAnimation("Amplitude");
            _pulseProps.InsertScalar("Amplitude", value);
            return;
        }

        var ease = _compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.42f, 0f), new Vector2(0.58f, 1f));
        var anim = _compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, playing ? 0f : 1f);
        anim.InsertKeyFrame(1f, target, ease);
        anim.Duration = TimeSpan.FromSeconds(1);
        _pulseProps.StartAnimation("Amplitude", anim);
    }

    // ------------------------------------------------------------------
    // Loop registry + teardown
    // ------------------------------------------------------------------

    private void StartLoops()
    {
        foreach ((CompositionObject target, string property, CompositionAnimation animation) in _loops)
        {
            try
            {
                target.StartAnimation(property, animation);
            }
            catch (Exception)
            {
                // A single failed loop must not take the layer down.
            }
        }
    }

    private void StopLoops()
    {
        foreach ((CompositionObject target, string property, _) in _loops)
        {
            try
            {
                target.StopAnimation(property);
            }
            catch (Exception)
            {
                // Already stopped/disposed — teardown is idempotent.
            }
        }
    }

    private void TeardownFx()
    {
        _paletteGeneration++;
        _colorSwapGeneration++;
        StopLoops();
        _loops.Clear();

        try
        {
            ElementCompositionPreview.SetElementChildVisual(FxHost, null);
        }
        catch (Exception)
        {
            // Host already out of the tree — nothing to detach.
        }

        try
        {
            ElementCompositionPreview.SetElementChildVisual(GlowHost, null);
        }
        catch (Exception)
        {
            // Same as above.
        }

        for (int i = _fxDisposables.Count - 1; i >= 0; i--)
        {
            try
            {
                _fxDisposables[i].Dispose();
            }
            catch (Exception)
            {
                // Double-dispose of a composition object is absorbed.
            }
        }

        _fxDisposables.Clear();
        _fxRoot = null;
        _compositor = null;
        _particleLayer = null;
        _glowContainer = null;
        _pulseProps = null;
        _baseStops = null;
        _blobAStops = null;
        _blobBStops = null;
        _particleStops = null;
        _glowStops = null;
        _appliedPalette = null;
        _fxReady = false;
    }
}
