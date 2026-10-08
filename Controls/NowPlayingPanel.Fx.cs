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
/// the lot. Mode Off builds no visuals: the panel background is a
/// flat brush instead — the current track's theme-adjusted Base
/// colour when a palette exists, the XAML's static HeroTintBrush
/// treatment otherwise.
///
/// The panel's background colour rule (Manish, 2026-10-08): the
/// background IS the current track's main colour — the palette's
/// Mid role, the mean tempered by the dominant (see
/// <see cref="ComputeFamily"/> for why Mid, not Dominant) —
/// adjusted for the app theme: a shade family under the dark
/// theme (Deep ×0.20 / Base ×0.30 / Lift ×0.48 of Mid), a tint
/// family under the light theme (Mid lerped toward white:
/// Deep 0.60 / Base 0.72 / Lift 0.86). Every background surface —
/// the FX gradient, blobs, particles, glow, the Off-mode flat
/// brush, the artwork placeholder — consumes the one family
/// (<see cref="ComputeFamily"/>), so the whole card reads as a
/// single colour: the track's. The family is also the legibility
/// mechanism (light tint + theme-dark text, dark shade +
/// theme-light text), which is why the original build's separate
/// scrim sprite is gone.
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
    // from it by host width; the base gradient binds to the host
    // size exactly.
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
    private ContainerVisual? _blobLayer;
    private ContainerVisual? _particleLayer;
    private ContainerVisual? _glowContainer;
    private CompositionPropertySet? _pulseProps;

    private ArtworkPalette? _fallbackPalette;
    private ArtworkPalette? _songPalette;
    private ArtworkPalette? _appliedPalette;
    private int _paletteGeneration;
    private int _colorSwapGeneration;

    // Off-mode flat background bookkeeping: which palette the tint
    // borders currently show (null = the static HeroTintBrush
    // treatment) under which theme — the change guard that keeps
    // the 500 ms sync ticks from repainting an unchanged flat
    // background.
    private ArtworkPalette? _flatPalette;
    private ElementTheme _flatTheme;

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
        // The palette feeds every mode now: the FX layer's family
        // in Aurora/Particles/Pulse, the flat Base brush in Off —
        // so the decode runs regardless of mode (it is cached per
        // song by the service, and never blocks the panel opening).
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

        // Null on a failed/absent extraction: the apply paths
        // substitute the static fallback family rather than
        // leaving the previous song's colours up.
        _songPalette = palette;

        if (_fxMode == FxMode.Off)
        {
            ApplyFlatBackground();
        }
        else
        {
            UpdateFxColors(animate: true);
        }
    }

    /// <summary>
    /// Off mode's background: a flat brush of the current track's
    /// theme-adjusted Base colour on the two tint borders (the
    /// same elements the FX path un-tints), with the artwork
    /// placeholder lifted one step (family Lift) so the tile still
    /// reads as a tile on the coloured card. Idle, or no palette
    /// for the current song, restores the static HeroTintBrush
    /// treatment exactly as before this feature. Change-guarded:
    /// the sync ticks call this constantly.
    /// </summary>
    private void ApplyFlatBackground(bool force = false)
    {
        if (_fxMode != FxMode.Off || _disposed)
        {
            return;
        }

        ArtworkPalette? target = _fxCalm ? null : _songPalette;
        if (!force && target == _flatPalette && ActualTheme == _flatTheme)
        {
            return;
        }

        _flatPalette = target;
        _flatTheme = ActualTheme;
        if (target is null)
        {
            Brush? tint = GetThemeBrush("HeroTintBrush");
            HeaderTintBorder.Background = tint;
            HeroTintBorder.Background = tint;
            if (GetThemeBrush("LayerFillColorAltBrush") is Brush placeholder)
            {
                ArtworkPlaceholder.Background = placeholder;
            }
        }
        else
        {
            FxColorFamily family = ComputeFamily(target);
            var brush = new SolidColorBrush(family.Base);
            HeaderTintBorder.Background = brush;
            HeroTintBorder.Background = brush;
            ArtworkPlaceholder.Background = new SolidColorBrush(family.Lift);
        }
    }

    /// <summary>
    /// A theme flip while the panel is open: theme resources
    /// resolve to the new theme from here on, so the fallback
    /// family (synthesised from the theme's HeroTintBrush) is
    /// rebuilt and whatever is showing is repainted under the new
    /// theme — instantly. The dip/cross-fade belongs to track
    /// changes; a pure theme flip re-aims the same palette's
    /// family with no animation.
    /// </summary>
    private void FxOnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_disposed)
        {
            return;
        }

        if (_fallbackPalette is not null)
        {
            _fallbackPalette = FallbackPalette();
        }

        if (_fxMode == FxMode.Off)
        {
            ApplyFlatBackground(force: true);
        }
        else if (_fxReady)
        {
            UpdateFxColors(animate: false, force: true);
        }
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

    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    private static Color LerpColor(Color from, Color to, float amount) => Color.FromArgb(
        (byte)(from.A + ((to.A - from.A) * amount)),
        (byte)(from.R + ((to.R - from.R) * amount)),
        (byte)(from.G + ((to.G - from.G) * amount)),
        (byte)(from.B + ((to.B - from.B) * amount)));

    // ------------------------------------------------------------------
    // The colour family: ONE theme-adjusted derivation of a
    // palette that every background surface consumes, so the card
    // reads as the track's colour and nothing else. Deep/Base/Lift
    // are the background ramp (gradient stops Deep → Base → Deep,
    // the Off-mode flat brush is Base, the artwork placeholder is
    // Lift); the blobs ARE that ramp — BlobA is Lift, BlobB is
    // Deep, with zero admixture of their old palette roles —
    // the particles keep their roles tempered 40% toward Base,
    // and the glow is the Vibrant lightened toward white, then
    // tempered toward Base (see ComputeFamily).
    // ------------------------------------------------------------------

    private sealed record FxColorFamily(
        Color Deep,
        Color Base,
        Color Lift,
        Color BlobA,
        Color BlobB,
        Color ParticleA,
        Color ParticleB,
        Color ParticleC,
        Color Glow);

    /// <summary>
    /// The family for the palette currently targeted: the fallback
    /// palette keeps its authored roles (<see cref="FamilyFromRoles"/>);
    /// a real track palette gets the theme-adjusted derivation
    /// (<see cref="ComputeFamily"/>).
    /// </summary>
    private FxColorFamily FamilyFor(ArtworkPalette palette) =>
        ReferenceEquals(palette, _fallbackPalette)
            ? FamilyFromRoles(palette)
            : ComputeFamily(palette);

    /// <summary>
    /// The background rule (Manish, 2026-10-08): the panel
    /// background wears the artwork's MAIN colour — the colour
    /// a viewer would name for the cover — LIGHTER under the
    /// light theme (lerped toward white — Base 0.72, Deep
    /// 0.60, Lift 0.86) and DARKER under the dark theme
    /// (scaled — Base ×0.30, Deep ×0.20, Lift ×0.48). Particle
    /// colours keep their Vibrant/Mid/Dominant roles blended
    /// 40% toward Base, so a cover whose vibrant accent is a
    /// contrasting hue cannot turn the card into a rainbow.
    ///
    /// "Main colour" is the palette's Mid role — the pixel
    /// mean tempered halfway toward Dominant — NOT Dominant
    /// itself. Dominant is an area mode: on a busy poster the
    /// fullest single bin is the background wash, while the
    /// subject a human names fragments across bins. The hue
    /// diagnosis (winmega-qa/hue-diag/DIAGNOSIS.md,
    /// 2026-10-08) settled it on both proof covers. Phata
    /// Patakha: Dominant (38,24,41), hue 289°, is the dark
    /// violet stage — one flat colour, 8.9% of pixels — while
    /// Mid (63,42,53), hue 328.6°, is the plum-red subject
    /// everyone calls the cover's colour. Choozay: Dominant
    /// is near-black (6,5,5 — hue undefined), while Mid
    /// (43,34,23), hue 33–36°, matches the panel reading
    /// measured from the original build (36.2°). Do not
    /// "fix" the anchor back to Dominant: the extractor and
    /// the panel were faithful all along — the retune rounds
    /// aimed at the plumbing changed nothing because the
    /// disagreement was definitional.
    ///
    /// The blobs are the ramp itself — BlobA IS Lift, BlobB
    /// IS Deep, zero admixture of the palette roles — so
    /// every background pixel is a blend of same-hue colours
    /// and the field cannot leave the anchor's hue; the
    /// roles survive only where they belong: the particles
    /// and the glow. The light mixes carry the white they do
    /// because the tint doubles as the legibility mechanism:
    /// at Base 0.80 the tint read as neutral grey; at 0.72
    /// it keeps its chroma while staying light enough for
    /// theme-dark text. The GLOW is the Vibrant lightened
    /// toward white (0.35) BEFORE the 40% Base blend: the
    /// unlightened tempered Vibrant lands at nearly the
    /// background's own value on dark covers — a halo no
    /// lighter than its surround is invisible (ring Δ 0.008
    /// measured) — so the lightening is what makes the halo
    /// lighter than the card it rings by construction.
    /// </summary>
    private FxColorFamily ComputeFamily(ArtworkPalette palette)
    {
        Color anchor = palette.Mid;
        Color baseColor;
        Color deep;
        Color lift;
        if (ActualTheme == ElementTheme.Light)
        {
            baseColor = LerpColor(anchor, White, 0.72f);
            deep = LerpColor(anchor, White, 0.60f);
            lift = LerpColor(anchor, White, 0.86f);
        }
        else
        {
            baseColor = ScaleColor(anchor, 0.30f);
            deep = ScaleColor(anchor, 0.20f);
            lift = ScaleColor(anchor, 0.48f);
        }

        return new FxColorFamily(
            deep,
            baseColor,
            lift,
            lift,
            deep,
            LerpColor(palette.Vibrant, baseColor, 0.40f),
            LerpColor(palette.Mid, baseColor, 0.40f),
            LerpColor(palette.Dominant, baseColor, 0.40f),
            LerpColor(LerpColor(palette.Vibrant, White, 0.35f), baseColor, 0.40f));
    }

    /// <summary>
    /// The fallback palette is synthesised from the theme's own
    /// HeroTintBrush — already theme-adjusted by construction —
    /// so its family is its roles verbatim: gradient Deep/Base
    /// = Dark/Mid, Lift = Dominant, accents unblended. Running it
    /// through <see cref="ComputeFamily"/> would double-adjust
    /// it; this keeps the idle and extraction-failure surfaces
    /// exactly as they rendered before the colour rule.
    /// </summary>
    private static FxColorFamily FamilyFromRoles(ArtworkPalette palette) => new(
        palette.Dark,
        palette.Mid,
        palette.Dominant,
        palette.Vibrant,
        palette.Mid,
        palette.Vibrant,
        palette.Mid,
        palette.Dominant,
        palette.Vibrant);

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
            if (_blobLayer is not null)
            {
                _blobLayer.IsVisible = !_fxCalm;
            }

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
            // failure degrades to the Off behaviour (flat Base
            // brush when a palette exists, the static tint
            // otherwise) rather than a broken surface.
            TeardownFx();
            _fxMode = FxMode.Off;
            ApplyFlatBackground(force: true);
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

        // No legibility scrim anymore: the original build painted
        // one (black 18% dark / white 30% light) because the
        // gradient ran on the raw palette roles. The stops now
        // come from the theme-adjusted family, which IS the
        // legibility mechanism — a light tint under theme-dark
        // text, a dark shade under theme-light text — so a second
        // wash would only mute the track's colour.
    }

    private void BuildBlobs(Visual hostVisual)
    {
        // Blobs live in a layer authored in reference space and
        // scaled uniformly by host width, so their composition
        // survives the 288–520 resize range unchanged.
        Compositor compositor = _compositor!;
        var layer = compositor.CreateContainerVisual();
        _blobLayer = layer;
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
        // Strength tuning (rendered proof out29/out30, 2026-10-08):
        // the first tuning rendered but was imperceptible at normal
        // contrast — peak brush alpha 160/255 and 0.75 sprite
        // opacity compounded to ~0.47 over the background. The
        // ladder below (217/190/163) with 0.95 sprite opacity
        // compounds to ~0.81 for the brightest role; bokeh grows
        // and doubles its presence (0.16 → 0.30). Density is NOT
        // part of the fix — the field stays 48 sprites.
        ArtworkPalette palette = _fallbackPalette!;
        Color[] roleColors = { palette.Vibrant, palette.Mid, palette.Dominant };
        byte[] roleAlphas = { 217, 190, 163 };
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
            float diameter = bokeh ? 26f + ((float)random.NextDouble() * 26f) : 4f + ((float)random.NextDouble() * 5f);
            float peak = bokeh ? 0.30f : 0.95f;
            float x = (float)random.NextDouble() * FxReferenceWidth;
            double drift = 560 + (random.NextDouble() * 220);
            double period = 24 + (random.NextDouble() * 22);
            double sway = 6 + (random.NextDouble() * 8);
            double phase = random.NextDouble();

            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = brushes[i % brushes.Length];
            sprite.Size = new Vector2(diameter, diameter);
            _fxDisposables.Add(sprite);
            _particleLayer.Children.InsertAtTop(sprite);

            // The rise runs UP the panel: a sprite starts at the
            // rise base (40 above the reference height's floor)
            // and travels `drift` px toward the top over its
            // period, distributed across the whole field by its
            // phase. The original build rose from y=40 — the top
            // edge — so the entire field lived at negative Y
            // (above the panel, clipped) for the whole of its
            // full-opacity window and Particles rendered nothing
            // (rendered proof out28, 2026-10-08).
            double riseBase = FxReferenceHeight - 40.0;

            // Still frame (what reduced motion and the pre-start
            // moment show): the sprite partway along its rise.
            sprite.Offset = new Vector3(
                x + SwayAt(phase, sway, phase),
                (float)(riseBase - (drift * phase)),
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
                        (float)(riseBase - (drift * t)),
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

        // The halo is a RING, not a disc: the gradient peaks at
        // offset 0.70 — where the artwork's edge falls inside
        // this oversized sprite across the resize range — then
        // fades to nothing at the sprite boundary, easing down
        // toward the centre. The original two-stop brush peaked
        // at the centre (alpha 150), which the opaque artwork
        // covers completely; the only visible part, the narrow
        // spill ring, sampled the gradient's weakest tail
        // (alpha ≤ 26/255) and the breath modulated that by only
        // ~Δ0.02 — a halo that measured as nothing (rendered
        // proof out28, 2026-10-08).
        var brush = compositor.CreateRadialGradientBrush();
        brush.EllipseCenter = new Vector2(0.5f, 0.5f);
        brush.EllipseRadius = new Vector2(0.5f, 0.5f);
        Color glowColor = _fallbackPalette!.Vibrant;
        var glowStop0 = compositor.CreateColorGradientStop(0f, WithAlpha(glowColor, 120));
        var glowStop1 = compositor.CreateColorGradientStop(0.70f, WithAlpha(glowColor, 240));
        var glowStop2 = compositor.CreateColorGradientStop(1f, WithAlpha(glowColor, 0));
        brush.ColorStops.Add(glowStop0);
        brush.ColorStops.Add(glowStop1);
        brush.ColorStops.Add(glowStop2);
        _glowStops = new[] { glowStop0, glowStop1, glowStop2 };
        _fxDisposables.Add(brush);
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

            // Breath swing 0.35 → 0.95 at full amplitude (was
            // 0.45 → 0.80): with the ring's peak stop now at alpha
            // 240 and the glow colour lightened (ComputeFamily),
            // the wider swing is what makes the breath read at
            // normal contrast on dark covers.
            var opacityExpr = compositor.CreateExpressionAnimation(
                "0.35f + 0.60f * props.Amplitude * (0.5f + 0.5f * Sin(props.Phase * 6.2831853f))");
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

    private void UpdateFxColors(bool animate, bool force = false)
    {
        if (!_fxReady)
        {
            return;
        }

        ArtworkPalette target = (_fxCalm ? _fallbackPalette : _songPalette) ?? _fallbackPalette!;
        if (!force && target == _appliedPalette)
        {
            return;
        }

        // The stops are painted from the target's theme-adjusted
        // family, computed NOW: a theme flip re-aims the same
        // palette under the new theme via the force path.
        FxColorFamily family = FamilyFor(target);
        if (animate && _motionEnabled && _fxRoot is not null && _compositor is not null)
        {
            _ = DipSwapAsync(target, family);
        }
        else
        {
            _colorSwapGeneration++;
            SwapColors(family);
            ApplyPlaceholderColor(family);
            _appliedPalette = target;
        }
    }

    private async Task DipSwapAsync(ArtworkPalette target, FxColorFamily family)
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

            SwapColors(family);
            ApplyPlaceholderColor(family);
            _appliedPalette = target;
        }
        catch (Exception)
        {
            // A failed swap leaves the previous palette up; the
            // next track change retries. Never an error surface.
        }
    }

    private void SwapColors(FxColorFamily family)
    {
        // Gradient geometry unchanged (offsets 0 / 0.55 / 1); the
        // stops are the family's ramp now: Deep → Base → Deep.
        SetStops(_baseStops, family.Deep, family.Base, family.Deep);
        SetStops(_blobAStops, WithAlpha(family.BlobA, 190), WithAlpha(family.BlobA, 0));
        SetStops(_blobBStops, WithAlpha(family.BlobB, 170), WithAlpha(family.BlobB, 0));
        if (_particleStops is not null)
        {
            // Alphas match BuildParticles' role ladder — a swap
            // must not silently revert the strength tuning.
            SetStops(_particleStops[0], WithAlpha(family.ParticleA, 217), WithAlpha(family.ParticleA, 0));
            SetStops(_particleStops[1], WithAlpha(family.ParticleB, 190), WithAlpha(family.ParticleB, 0));
            SetStops(_particleStops[2], WithAlpha(family.ParticleC, 163), WithAlpha(family.ParticleC, 0));
        }

        if (_glowStops is not null)
        {
            // Peak stop alpha matches BuildGlow (240) — a swap
            // must not revert the ring-strength tuning.
            SetStops(_glowStops,
                WithAlpha(family.Glow, 120), WithAlpha(family.Glow, 240), WithAlpha(family.Glow, 0));
        }
    }

    /// <summary>
    /// The artwork placeholder tile takes the family's Lift: it
    /// sits ON the Base background, so one step up the ramp keeps
    /// it reading as a tile instead of a hole in the card. (In
    /// idle the whole player body is collapsed, so this is only
    /// ever seen while a song is current but its cover is missing
    /// or still loading.)
    /// </summary>
    private void ApplyPlaceholderColor(FxColorFamily family)
    {
        ArtworkPlaceholder.Background = new SolidColorBrush(family.Lift);
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
        // Off mode has no layer to park: idle flips the flat
        // background between the static tint (idle) and the
        // track's Base (a song is current). ApplyFlatBackground
        // is change-guarded, so the per-tick calls are free.
        if (_fxMode == FxMode.Off)
        {
            _fxCalm = idle;
            ApplyFlatBackground();
            return;
        }

        if (idle == _fxCalm && _fxReady)
        {
            return;
        }

        _fxCalm = idle;
        if (!_fxReady)
        {
            return;
        }

        // Idle is the quiet surface (NOW_PLAYING_FX §3): gradient
        // at most. The blob layer hides wholesale — parked
        // visible, its fallback-family colours are the theme's
        // saturated accent at near-full alpha, which painted a
        // loud green-teal wash over the idle card (rendered
        // proof out28 s06, 2026-10-08).
        if (_blobLayer is not null)
        {
            _blobLayer.IsVisible = !idle;
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

        // Idle shows the fallback palette's gradient with the
        // blob/particle/glow layers parked (above); leaving idle
        // restores the song's colours and layers. The swap itself
        // is immediate — the palette path cross-fades real changes.
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
        _blobLayer = null;
        _particleLayer = null;
        _glowContainer = null;
        _pulseProps = null;
        _baseStops = null;
        _blobAStops = null;
        _blobBStops = null;
        _particleStops = null;
        _glowStops = null;
        _appliedPalette = null;
        _flatPalette = null;
        _fxReady = false;
    }
}
