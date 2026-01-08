/*
    RarityBackgroundSpriteGeneratorWindow.cs
    Unity 6.3 LTS (Editor-only)

    WHAT THIS TOOL DOES
    -------------------
    Generates "rarity background" sprites (rounded rectangle, muted fill, dark outline, subtle highlight/lift),
    intended to sit BEHIND item icons (transparent-background item sprites) in an ARPG inventory UI.

    KEY ENHANCEMENTS IN THIS VERSION
    --------------------------------
    1) Lift Mode Enum (instead of only "Centre Lift"):
       - CentreLift (default)
       - EdgesLift (bright at edges, fades inward)
       - MidTopLift, TopRightLift, MidRightLift, BottomRightLift, BottomMidLift, BottomLeftLift, MidLeftLift, TopLeftLift
       This lets you create "light from a corner" looks, or edge-emphasis looks.

    2) Optional Gradient Fill:
       - Default remains Single Colour.
       - When enabled, you can choose 2 colours and how to interpolate them:
           * RGB     (simple, can look “muddy” between some colours)
           * HSV     (nice hue sweeps, can shift brightness/saturation unexpectedly)
           * OKLCH   (perceptual-ish interpolation; typically pleasing for rarity ramps)
       - Choose direction: vertical/horizontal/diagonals/radial.

    3) Full tooltips for all fields (including Batch list).

    4) Live Preview already existed in your previous script; this version ensures preview reflects:
       - lift mode
       - gradient settings
       - outline derived from the local fill (so outline follows gradient nicely)

    OUTPUT
    ------
    - Writes PNG files into a user-selected Assets folder.
    - Automatically imports them as Sprite assets (TextureImporter configured for UI usage).

    HOW TO USE
    ----------
    1) Put this script in: Assets/Editor/
    2) Menu: Tools -> RPG -> Rarity Background Sprite Generator
    3) Choose:
         - Output folder
         - Single Colour or Gradient
         - Shape + outline settings
         - Muting settings
         - Lift mode + intensity/falloff
    4) Generate single or batch.

    NOTE ON "LIFT"
    --------------
    In this tool "Lift" means: "the fill is nudged brighter in a pattern".
    That pattern is controlled by:
      - LiftMode (where the highlight originates)
      - LiftAmount (how strong)
      - LiftFalloff (how quickly it fades)

    TECH NOTES
    ----------
    - The rounded rectangle is produced via a Signed Distance Function (SDF).
    - Anti-aliasing is achieved by mapping distance to alpha with Smoothstep.
    - Outline is built as a band just inside the edge.
    - Fill colour is muted toward black for the “rarity plate” look.
    - Outline colour is derived from (darkened) local fill, so it looks consistent for gradients too.
*/

#if UNITY_EDITOR
#if UNITY_INCLUDE_TESTS
using NUnit.Framework;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public sealed class RarityBackgroundSpriteGeneratorWindow : EditorWindow
{
    // ----------------------------
    // Menu entry
    // ----------------------------

    [MenuItem("Tools/RPG/Rarity Background Sprite Generator")]
    private static void Open()
    {
        var window = GetWindow<RarityBackgroundSpriteGeneratorWindow>("Rarity BG Sprites");
        window.minSize = new Vector2(560, 560);
        window.Show();
    }

    // ----------------------------
    // Enums (UI-friendly choices)
    // ----------------------------

    /// <summary>
    /// Where the highlight/lift originates from.
    /// Think of these as "hotspots" or "light directions".
    /// </summary>
    private enum LiftMode
    {
        CentreLift,
        EdgesLift,

        MidTopLift,
        TopRightLift,
        MidRightLift,
        BottomRightLift,
        BottomMidLift,
        BottomLeftLift,
        MidLeftLift,
        TopLeftLift
    }

    /// <summary>
    /// Whether the fill colour is a single colour or a gradient between two colours.
    /// </summary>
    private enum FillMode
    {
        SingleColour,
        Gradient
    }

    /// <summary>
    /// How to interpolate between two colours when FillMode is Gradient.
    /// </summary>
    private enum GradientSpace
    {
        RGB,
        HSV,
        OKLCH
    }

    /// <summary>
    /// The spatial direction of the gradient.
    /// (Radial uses centre outwards.)
    /// </summary>
    private enum GradientDirection
    {
        Vertical_TopToBottom,
        Vertical_BottomToTop,

        Horizontal_LeftToRight,
        Horizontal_RightToLeft,

        Diagonal_TopLeftToBottomRight,
        Diagonal_BottomRightToTopLeft,

        Diagonal_TopRightToBottomLeft,
        Diagonal_BottomLeftToTopRight,

        Radial_CentreOut
    }

    /// <summary>
    /// Controls how the outline colour is chosen.
    /// - DerivedFromFill: current behaviour (outline is a darker version of the local fill colour).
    /// - ForcedBlack: outline is forced to a near-black colour (matches your original sample sprites).
    /// - CustomColour: outline uses a user-selected colour.
    /// </summary>
    private enum OutlineColourMode
    {
        DerivedFromFill,
        ForcedBlack,
        CustomColour
    }


    /// <summary>
    /// Controls whether the generated sprite is a rounded-rect "plate" with transparent corners (current default),
    /// or a full-bleed square that fills the entire texture (no transparent corners) while still having an inner rounded outline.
    /// </summary>
    private enum ShapeMode
    {
        /// <summary>
        /// Current behaviour: a rounded rectangle with transparent corners outside the shape.
        /// This matches your existing rarity plate style.
        /// </summary>
        RoundedPlate_TransparentCorners,

        /// <summary>
        /// New behaviour: fills the entire sprite to the edges (no transparency at the corners),
        /// but still draws a rounded, darker outline just INSIDE the edges to give a similar "card" feel.
        /// </summary>
        FullBleedSquare_InnerRoundedOutline
    }


    // ----------------------------
    // Settings
    // ----------------------------

    [Serializable]
    private sealed class Settings
    {
        // OUTPUT ---------------------------------------------------------

        [Tooltip("Select a folder under your project 'Assets/' where the generated PNG sprite(s) will be written.")]
        public DefaultAsset OutputFolder;

        [Tooltip("Filename (without extension) for single-generate output. Batch generation appends '_EntryName'.")]
        public string FileName = "Rarity_Background";

        [Tooltip("Imported Sprite 'Pixels Per Unit'. For pure UI usage this often doesn’t matter, but is set for completeness.")]
        public float PixelsPerUnit = 100f;

        [Tooltip("If enabled (recommended for UI), mipmaps are disabled on the imported sprite.")]
        public bool DisableMipMaps = true;

        [Tooltip("If enabled (recommended for UI), wrap mode is set to Clamp to avoid edge sampling repetition.")]
        public bool ClampWrapMode = true;

        // FILL -----------------------------------------------------------

        [Tooltip("Choose whether to use a Single Colour (default) or a Gradient between two colours.")]
        public FillMode FillMode = FillMode.SingleColour;

        [Tooltip("Base rarity colour (used when FillMode = Single Colour). This will be muted toward black.")]
        public Color BaseColor = new Color(0.25f, 0.45f, 0.55f, 1f);

        [Tooltip("Gradient start colour (used when FillMode = Gradient).")]
        public Color GradientColorA = new Color(0.25f, 0.45f, 0.55f, 1f);

        [Tooltip("Gradient end colour (used when FillMode = Gradient).")]
        public Color GradientColorB = new Color(0.20f, 0.20f, 0.20f, 1f);

        [Tooltip("How to interpolate between GradientColorA and GradientColorB. OKLCH is often the nicest for rarity ramps.")]
        public GradientSpace GradientSpace = GradientSpace.OKLCH;

        [Tooltip("Direction of the gradient when FillMode = Gradient.")]
        public GradientDirection GradientDirection = GradientDirection.Vertical_TopToBottom;

        [Tooltip("When using Radial gradient, controls how quickly the gradient reaches the end colour. 1 = normal.")]
        public float RadialGradientPower = 1.0f;

        // SHAPE ----------------------------------------------------------

        [Tooltip("Square output sprite size in pixels. Your sample images are 210x210.")]
        public int Size = 210;

        [Tooltip("Rounded corner radius in pixels. Larger values produce more rounded corners.")]
        public float CornerRadiusPx = 18f;

        [Tooltip("Outline thickness in pixels (dark border band around the rounded rectangle).")]
        public float OutlineThicknessPx = 3f;

        [Tooltip("Anti-aliasing softness width (in pixels). ~1–2px typically matches your samples well.")]
        public float EdgeSoftnessPx = 1.5f;

        [Tooltip("Controls the overall shape behaviour.\n" +
         "• RoundedPlate_TransparentCorners = current default (transparent corners outside a rounded rect).\n" +
         "• FullBleedSquare_InnerRoundedOutline = fills to all four edges (no transparent corners) with a rounded inner outline.")]
        public ShapeMode ShapeMode = ShapeMode.RoundedPlate_TransparentCorners;

        [Tooltip("Only used when ShapeMode = FullBleedSquare_InnerRoundedOutline.\n" +
                 "This is the radius (in pixels) for the INNER outline corners.\n" +
                 "Think of this as a 'rounded border' drawn inside the square.")]
        public float InnerOutlineCornerRadiusPx = 18f;

        [Tooltip("Only used when ShapeMode = FullBleedSquare_InnerRoundedOutline.\n" +
                 "Controls how soft the inner outline edge looks (anti-alias).")]
        public float InnerOutlineEdgeSoftnessPx = 1.5f;


        // COLOUR STYLING -------------------------------------------------

        [Tooltip("How strongly to mute the chosen colour toward black. 0 = unchanged colour. 1 = full black.\n" +
                 "Values ~0.6–0.75 typically match dark 'rarity plate' styles.")]
        public float MuteToBlack = 0.62f;

        [Tooltip("How much darker the outline is compared to the local fill colour.\n" +
                 "0 = outline same as fill (not useful). 0.2–0.35 is typical.")]
        public float OutlineDarken = 0.22f;

        [Tooltip("How the outline colour is chosen.\n" +
         "• DerivedFromFill = current default behaviour.\n" +
         "• ForcedBlack = uses a near-black outline colour (like your original sample sprites).\n" +
         "• CustomColour = uses the custom outline colour below.")]
        public OutlineColourMode OutlineColourMode = OutlineColourMode.DerivedFromFill;

        [Tooltip("Used when OutlineColourMode = ForcedBlack.\n" +
                 "A near-black outline colour. Default is very dark, but not pure #000 to keep it slightly softer.")]
        public Color ForcedBlackOutlineColor = new Color(0.04f, 0.04f, 0.04f, 1f);

        [Tooltip("Used when OutlineColourMode = CustomColour.\n" +
                 "The outline colour to use.")]
        public Color CustomOutlineColor = Color.black;

        [Tooltip("Extra multiplier applied to the outline mask only (NOT thickness).\n" +
                 "Use this if you want the outline to 'read' stronger without changing its thickness.\n" +
                 "1 = normal, 2 = twice as strong.")]
        public float OutlineStrength = 1.0f;


        [Tooltip("Optional extra darkening near edges (a subtle vignette). 0 = off.")]
        public float EdgeVignette = 0.08f;

        // LIFT / HIGHLIGHT ----------------------------------------------

        [Tooltip("Where the highlight 'lift' originates from (Centre, Edges, corners, etc). Default = CentreLift.")]
        public LiftMode LiftMode = LiftMode.CentreLift;

        [Tooltip("How much brighter the lifted area becomes.\n" +
                 "0 = no lift. ~0.12–0.22 usually feels close to your sample sprites.")]
        public float LiftAmount = 0.18f;

        [Tooltip("How quickly the lift fades away from its origin.\n" +
                 "Higher values concentrate the highlight more tightly.\n" +
                 "Typical values: 1.2–2.0.")]
        public float LiftFalloff = 1.65f;

        [Tooltip("For some lift modes (especially corner lifts), you may prefer a slightly wider influence.\n" +
                 "This scales the effective distance used by lift weighting. 1 = default.")]
        public float LiftDistanceScale = 1.0f;
    }

    private readonly Settings _settings = new Settings();

    // ----------------------------
    // Batch entries
    // ----------------------------

    [Serializable]
    private sealed class BatchEntry
    {
        [Tooltip("Name used to form the output filename: FileName + '_' + Name.\nExample: Rarity_Background_Epic.png")]
        public string Name = "Rare";

        [Tooltip("When UseGradient is OFF, this is the single fill colour for this entry.")]
        public Color ColorA = new Color(0.25f, 0.45f, 0.55f, 1f);

        [Tooltip("When UseGradient is ON, this is the second gradient colour for this entry.")]
        public Color ColorB = new Color(0.20f, 0.20f, 0.20f, 1f);

        [Tooltip("If enabled, this batch entry uses a gradient (ColorA -> ColorB). If disabled, it uses single colour (ColorA).")]
        public bool UseGradient = false;
    }

    // ------------------------------------------------------------------------------------------
    // Default Batch Rarity Tiers
    // ------------------------------------------------------------------------------------------
    // These are the "starter" rarity backgrounds that appear in the Batch section when you open
    // the window. Users can still edit, reorder, add, or remove entries at will.
    //
    // Notes on colour choices:
    // - These are intended to be "good starting points" that still look decent after muting.
    // - Lower rarities are more grey/green/blue-leaning and subtle.
    // - Higher rarities move into saturated blues/purples, then into warm golds.
    // - Mythic defaults to a GOLD gradient so it reads as "special" even at small UI sizes.
    // ------------------------------------------------------------------------------------------
    private List<BatchEntry> _batch = new List<BatchEntry>
{
    // Very Common: near-neutral, slightly cool grey (very subtle)
    new BatchEntry
    {
        Name = "Very Common",
        UseGradient = false,
        ColorA = new Color(0.38f, 0.40f, 0.42f, 1f),
        ColorB = Color.black
    },

    // Common: a touch brighter, still neutral-cool
    new BatchEntry
    {
        Name = "Common",
        UseGradient = false,
        ColorA = new Color(0.45f, 0.47f, 0.50f, 1f),
        ColorB = Color.black
    },

    // Uncommon: gentle green tint
    new BatchEntry
    {
        Name = "Uncommon",
        UseGradient = false,
        ColorA = new Color(0.22f, 0.46f, 0.30f, 1f),
        ColorB = Color.black
    },

    // Scarce: teal/sea-green hint (slightly “rarer” than uncommon)
    new BatchEntry
    {
        Name = "Scarce",
        UseGradient = false,
        ColorA = new Color(0.18f, 0.44f, 0.42f, 1f),
        ColorB = Color.black
    },

    // Very Scarce: desaturated cyan/blue (subtle but distinct from Scarce)
    new BatchEntry
    {
        Name = "Very Scarce",
        UseGradient = false,
        ColorA = new Color(0.18f, 0.38f, 0.50f, 1f),
        ColorB = Color.black
    },

    // Rare: “classic rare” blue
    new BatchEntry
    {
        Name = "Rare",
        UseGradient = false,
        ColorA = new Color(0.18f, 0.34f, 0.56f, 1f),
        ColorB = Color.black
    },

    // Very Rare: deeper/stronger blue leaning slightly toward violet
    new BatchEntry
    {
        Name = "Very Rare",
        UseGradient = false,
        ColorA = new Color(0.22f, 0.28f, 0.62f, 1f),
        ColorB = Color.black
    },

    // Epic: purple
    new BatchEntry
    {
        Name = "Epic",
        UseGradient = false,
        ColorA = new Color(0.42f, 0.24f, 0.62f, 1f),
        ColorB = Color.black
    },

    // Fabled: magenta-leaning purple (reads “higher than epic”)
    new BatchEntry
    {
        Name = "Fabled",
        UseGradient = false,
        ColorA = new Color(0.62f, 0.22f, 0.52f, 1f),
        ColorB = Color.black
    },

    // Legendary: warm amber/orange-gold (single colour)
    new BatchEntry
    {
        Name = "Legendary",
        UseGradient = false,
        ColorA = new Color(0.72f, 0.44f, 0.18f, 1f),
        ColorB = Color.black
    },

    // Mythic: GOLD gradient by default so it reads "special" even after muting.
    // ColourA: bright gold highlight
    // ColourB: deeper bronze / umber shadow
    new BatchEntry
    {
        Name = "Mythic",
        UseGradient = true,
        ColorA = new Color(0.95f, 0.78f, 0.22f, 1f),  // bright gold
        ColorB = new Color(0.40f, 0.22f, 0.06f, 1f)   // deep bronze
    },
};


    // ----------------------------
    // Preview
    // ----------------------------

    private Texture2D _previewTex;
    private Vector2 _scroll;

    // ----------------------------
    // Editor lifecycle
    // ----------------------------

    private void OnEnable()
    {
        // Build an initial preview so the window feels responsive immediately.
        RebuildPreview();
    }

    private void OnDisable()
    {
        // Prevent editor texture leaks.
        if (_previewTex != null)
        {
            DestroyImmediate(_previewTex);
            _previewTex = null;
        }
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawHeader();

        EditorGUILayout.Space(10);

        DrawOutputSection();

        EditorGUILayout.Space(10);

        DrawFillSection();

        EditorGUILayout.Space(10);

        DrawShapeSection();

        EditorGUILayout.Space(10);

        DrawStyleSection();

        EditorGUILayout.Space(10);

        DrawLiftSection();

        EditorGUILayout.Space(10);

        DrawPreviewSection();

        EditorGUILayout.Space(10);

        DrawGenerateSection();

        EditorGUILayout.Space(10);

        DrawBatchSection();

        EditorGUILayout.EndScrollView();
    }

    // ----------------------------
    // UI Sections
    // ----------------------------

    private void DrawHeader()
    {
        EditorGUILayout.LabelField("Rarity Background Sprite Generator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Generates rounded-rectangle rarity background sprites (PNG) with transparent corners, dark outline, and a configurable highlight/lift.\n" +
            "Use these as a UI plate behind transparent item icons to denote rarity/renown.",
            MessageType.Info);
    }

    private void DrawOutputSection()
    {
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();

        _settings.OutputFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("Output Folder (Assets/...)", "Select a folder under Assets/ where PNG sprites will be saved."),
            _settings.OutputFolder,
            typeof(DefaultAsset),
            false
        );

        _settings.FileName = EditorGUILayout.TextField(
            new GUIContent("File Name", "Base filename for the generated PNG (without extension)."),
            _settings.FileName
        );

        _settings.PixelsPerUnit = EditorGUILayout.FloatField(
            new GUIContent("Pixels Per Unit", "Sprite Pixels Per Unit used on import."),
            _settings.PixelsPerUnit
        );

        _settings.DisableMipMaps = EditorGUILayout.Toggle(
            new GUIContent("Disable MipMaps", "Recommended for UI sprites."),
            _settings.DisableMipMaps
        );

        _settings.ClampWrapMode = EditorGUILayout.Toggle(
            new GUIContent("Clamp Wrap Mode", "Recommended for UI sprites (prevents repeat sampling)."),
            _settings.ClampWrapMode
        );

        if (EditorGUI.EndChangeCheck())
        {
            RebuildPreview();
        }
    }

    private void DrawFillSection()
    {
        EditorGUILayout.LabelField("Fill", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();

        _settings.FillMode = (FillMode)EditorGUILayout.EnumPopup(
            new GUIContent("Fill Mode", "Single Colour (default) or Gradient between two colours."),
            _settings.FillMode
        );

        if (_settings.FillMode == FillMode.SingleColour)
        {
            _settings.BaseColor = EditorGUILayout.ColorField(
                new GUIContent("Rarity Colour", "The base rarity colour (muted toward black)."),
                _settings.BaseColor
            );
        }
        else
        {
            _settings.GradientColorA = EditorGUILayout.ColorField(
                new GUIContent("Gradient Colour A", "Gradient start colour."),
                _settings.GradientColorA
            );

            _settings.GradientColorB = EditorGUILayout.ColorField(
                new GUIContent("Gradient Colour B", "Gradient end colour."),
                _settings.GradientColorB
            );

            _settings.GradientSpace = (GradientSpace)EditorGUILayout.EnumPopup(
                new GUIContent("Gradient Space", "How the two colours are interpolated.\nOKLCH is often most pleasing for rarity ramps."),
                _settings.GradientSpace
            );

            _settings.GradientDirection = (GradientDirection)EditorGUILayout.EnumPopup(
                new GUIContent("Gradient Direction", "Spatial direction of the gradient across the sprite."),
                _settings.GradientDirection
            );

            if (_settings.GradientDirection == GradientDirection.Radial_CentreOut)
            {
                _settings.RadialGradientPower = EditorGUILayout.Slider(
                    new GUIContent("Radial Power", "1 = normal. Higher concentrates more of colour A near the centre."),
                    _settings.RadialGradientPower,
                    0.25f,
                    4.0f
                );
            }
        }

        if (EditorGUI.EndChangeCheck())
        {
            RebuildPreview();
        }
    }

    private void DrawShapeSection()
    {
        EditorGUILayout.LabelField("Shape", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();

        _settings.Size = EditorGUILayout.IntSlider(
            new GUIContent("Sprite Size (px)", "Output texture size in pixels (square). Sample sprites were 210x210."),
            _settings.Size,
            64,
            512
        );

        _settings.ShapeMode = (ShapeMode)EditorGUILayout.EnumPopup(
            new GUIContent("Shape Mode", "Choose whether corners are transparent (current) or the sprite fills to all edges (full-bleed)."),
            _settings.ShapeMode
            );

        if (_settings.ShapeMode == ShapeMode.FullBleedSquare_InnerRoundedOutline)
        {
            _settings.InnerOutlineCornerRadiusPx = EditorGUILayout.Slider(
                new GUIContent("Inner Outline Radius (px)", "Rounded corner radius for the INNER outline in full-bleed mode."),
                _settings.InnerOutlineCornerRadiusPx,
                0f,
                Mathf.Min(128f, _settings.Size * 0.45f)
            );

            _settings.InnerOutlineEdgeSoftnessPx = EditorGUILayout.Slider(
                new GUIContent("Inner Outline Softness (px)", "Anti-alias softness for the inner outline in full-bleed mode."),
                _settings.InnerOutlineEdgeSoftnessPx,
                0.5f,
                4f
            );
        }


        // In Full-Bleed mode the OUTER silhouette is not used (the sprite is opaque to the edges),
        // so CornerRadiusPx + EdgeSoftnessPx won’t affect the result.
        // We keep the values (so switching back preserves them), but grey the controls to reduce confusion.
        using (new EditorGUI.DisabledScope(_settings.ShapeMode == ShapeMode.FullBleedSquare_InnerRoundedOutline))
        {
            _settings.CornerRadiusPx = EditorGUILayout.Slider(
                new GUIContent(
                    "Corner Radius (px)",
                    "Rounded corner radius in pixels for the outer rounded plate.\n" +
                    "Used when Shape Mode = RoundedPlate_TransparentCorners."
                ),
                _settings.CornerRadiusPx,
                0f,
                Mathf.Min(128f, _settings.Size * 0.45f)
            );

            _settings.EdgeSoftnessPx = EditorGUILayout.Slider(
                new GUIContent(
                    "Edge Softness (px)",
                    "Anti-aliasing softness for the outer rounded plate edge (in pixels).\n" +
                    "Used when Shape Mode = RoundedPlate_TransparentCorners."
                ),
                _settings.EdgeSoftnessPx,
                0.5f,
                4f
            );
        }


        _settings.OutlineThicknessPx = EditorGUILayout.Slider(
            new GUIContent("Outline Thickness (px)", "Thickness of the dark outline band (in pixels)."),
            _settings.OutlineThicknessPx,
            0f,
            12f
        );


        if (EditorGUI.EndChangeCheck())
        {
            RebuildPreview();
        }
    }

    private void DrawStyleSection()
    {
        EditorGUILayout.LabelField("Colour Styling", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();

        _settings.MuteToBlack = EditorGUILayout.Slider(
            new GUIContent("Mute Toward Black", "Blend the fill toward black for a muted rarity plate look."),
            _settings.MuteToBlack,
            0f,
            0.9f
        );

        _settings.OutlineDarken = EditorGUILayout.Slider(
            new GUIContent("Outline Darken", "How much darker the outline is compared to the local fill."),
            _settings.OutlineDarken,
            0f,
            0.6f
        );

        _settings.OutlineColourMode = (OutlineColourMode)EditorGUILayout.EnumPopup(
            new GUIContent("Outline Colour Mode", "Choose how the outline colour is selected."),
            _settings.OutlineColourMode
);

        if (_settings.OutlineColourMode == OutlineColourMode.ForcedBlack)
        {
            _settings.ForcedBlackOutlineColor = EditorGUILayout.ColorField(
                new GUIContent("Forced Black Outline", "Near-black outline colour used when forcing a black outline."),
                _settings.ForcedBlackOutlineColor
            );
        }
        else if (_settings.OutlineColourMode == OutlineColourMode.CustomColour)
        {
            _settings.CustomOutlineColor = EditorGUILayout.ColorField(
                new GUIContent("Custom Outline Colour", "Outline colour used when Outline Colour Mode is CustomColour."),
                _settings.CustomOutlineColor
            );
        }

        _settings.OutlineStrength = EditorGUILayout.Slider(
            new GUIContent("Outline Strength", "Boosts outline visibility without changing thickness."),
            _settings.OutlineStrength,
            0f,
            3f
        );


        _settings.EdgeVignette = EditorGUILayout.Slider(
            new GUIContent("Edge Vignette", "Extra subtle darkening near edges (0 = none)."),
            _settings.EdgeVignette,
            0f,
            0.3f
        );

        if (EditorGUI.EndChangeCheck())
        {
            RebuildPreview();
        }
    }

    private void DrawLiftSection()
    {
        EditorGUILayout.LabelField("Lift / Highlight", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();

        _settings.LiftMode = (LiftMode)EditorGUILayout.EnumPopup(
            new GUIContent("Lift Mode", "Where the highlight originates from (Centre, Edges, corners, etc)."),
            _settings.LiftMode
        );

        _settings.LiftAmount = EditorGUILayout.Slider(
            new GUIContent("Lift Amount", "How much brighter the lifted area becomes."),
            _settings.LiftAmount,
            0f,
            0.6f
        );

        _settings.LiftFalloff = EditorGUILayout.Slider(
            new GUIContent("Lift Falloff", "How quickly the lift fades away from its origin (higher = tighter)."),
            _settings.LiftFalloff,
            0.5f,
            4f
        );

        _settings.LiftDistanceScale = EditorGUILayout.Slider(
            new GUIContent("Lift Distance Scale", "Scales the effective distance used by lift weighting (1 = default)."),
            _settings.LiftDistanceScale,
            0.5f,
            2.0f
        );

        if (EditorGUI.EndChangeCheck())
        {
            RebuildPreview();
        }
    }

    private void DrawPreviewSection()
    {
        EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

        if (_previewTex == null)
        {
            EditorGUILayout.HelpBox("Preview not available (texture not built).", MessageType.Warning);
            if (GUILayout.Button("Rebuild Preview")) RebuildPreview();
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();

            // Fixed on-screen size for stable UX even when output resolution changes.
            float previewSize = 260f;
            Rect r = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.ExpandWidth(false));

            // Draw a subtle checker background to make transparency obvious.
            DrawCheckerBackground(r, 12f);

            // Draw the preview sprite texture over it.
            EditorGUI.DrawPreviewTexture(r, _previewTex, null, ScaleMode.ScaleToFit);

            GUILayout.FlexibleSpace();
        }

        EditorGUILayout.Space(6);

        if (GUILayout.Button(new GUIContent("Rebuild Preview", "Forces the preview texture to rebuild.")))
        {
            RebuildPreview();
        }
    }

    private void DrawGenerateSection()
    {
        EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Single Sprite", EditorStyles.boldLabel);

            if (GUILayout.Button(new GUIContent("Generate Sprite PNG + Import as Sprite", "Writes a PNG into the output folder and imports it as a Sprite.")))
            {
                GenerateSingle();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Writes one PNG using the current settings and File Name, then imports it as a Sprite.",
                EditorStyles.wordWrappedMiniLabel);
        }
    }

    private void DrawBatchSection()
    {
        EditorGUILayout.LabelField("Batch Generate (optional)", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Create multiple rarity backgrounds quickly", EditorStyles.boldLabel);

            int removeIndex = -1;

            for (int i = 0; i < _batch.Count; i++)
            {
                var entry = _batch[i];
                if (entry == null) continue;

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        entry.Name = EditorGUILayout.TextField(
                            new GUIContent("Name", "Used in the filename suffix, e.g. _Epic, _Legendary."),
                            entry.Name);

                        if (GUILayout.Button(new GUIContent("Remove", "Remove this batch entry."), GUILayout.Width(80)))
                            removeIndex = i;
                    }

                    entry.UseGradient = EditorGUILayout.Toggle(
                        new GUIContent("Use Gradient", "If enabled, this entry uses ColorA -> ColorB gradient. If disabled, it uses ColorA only."),
                        entry.UseGradient);

                    entry.ColorA = EditorGUILayout.ColorField(
                        new GUIContent(entry.UseGradient ? "Colour A" : "Colour", entry.UseGradient ? "Gradient start colour." : "Single colour fill for this entry."),
                        entry.ColorA);

                    if (entry.UseGradient)
                    {
                        entry.ColorB = EditorGUILayout.ColorField(
                            new GUIContent("Colour B", "Gradient end colour."),
                            entry.ColorB);
                    }
                }
            }

            if (removeIndex >= 0 && removeIndex < _batch.Count)
            {
                _batch.RemoveAt(removeIndex);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Add Entry", "Adds a new batch entry.")))
                {
                    _batch.Add(new BatchEntry { Name = "NewRarity", ColorA = Color.white, ColorB = Color.black, UseGradient = false });
                }

                if (GUILayout.Button(new GUIContent("Generate Batch", "Generates all batch entries into the output folder.")))
                {
                    GenerateBatch();
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Each entry generates: FileName + '_' + EntryName (e.g. Rarity_Background_Epic.png)",
                EditorStyles.wordWrappedMiniLabel);
        }
    }

    // ----------------------------
    // Generation: Single + Batch
    // ----------------------------

    private void GenerateSingle()
    {
        string outFolder = GetOutputFolderPathOrNull();
        if (string.IsNullOrEmpty(outFolder))
        {
            EditorUtility.DisplayDialog("Output Folder Missing",
                "Please select an Output Folder under Assets/ before generating.",
                "OK");
            return;
        }

        // Build a texture using current settings
        Texture2D tex = BuildRarityBackgroundTexture(_settings);

        // Write PNG
        string filePath = Path.Combine(outFolder, $"{SanitizeFileName(_settings.FileName)}.png");
        WriteTextureToPng(tex, filePath);

        // Import and configure sprite
        ImportAsSprite(filePath, _settings);

        DestroyImmediate(tex);

        AssetDatabase.Refresh();
        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ToUnityAssetPath(filePath)));
    }

    private void GenerateBatch()
    {
        string outFolder = GetOutputFolderPathOrNull();
        if (string.IsNullOrEmpty(outFolder))
        {
            EditorUtility.DisplayDialog("Output Folder Missing",
                "Please select an Output Folder under Assets/ before generating.",
                "OK");
            return;
        }

        // We will generate ALL PNG files first, then configure/import them as Sprites afterwards.
        // Why? Because calling SaveAndReimport while AssetDatabase.StartAssetEditing() is active
        // can result in Unity deferring/skipping importer settings in a way that leaves assets
        // as plain textures ("images") rather than properly-imported Sprites.
        // Store Unity asset paths ("Assets/...") instead of absolute OS paths.
        // This makes the later import step more robust and avoids any path separator/casing issues.
        List<(string unityAssetPath, Settings settings)> generated = new List<(string, Settings)>();


        // Reduce import overhead by batching asset edits.
        AssetDatabase.StartAssetEditing();
        try
        {
            for (int i = 0; i < _batch.Count; i++)
            {
                BatchEntry entry = _batch[i];
                if (entry == null) continue;

                // For each entry we override just the fill parameters (single vs gradient + colours).
                Settings s = CloneSettings(_settings);

                if (!entry.UseGradient)
                {
                    s.FillMode = FillMode.SingleColour;
                    s.BaseColor = entry.ColorA;
                }
                else
                {
                    s.FillMode = FillMode.Gradient;
                    s.GradientColorA = entry.ColorA;
                    s.GradientColorB = entry.ColorB;
                }

                string fileName = $"{SanitizeFileName(_settings.FileName)}_{SanitizeFileName(entry.Name)}";
                string filePath = Path.Combine(outFolder, $"{fileName}.png");

                Texture2D tex = BuildRarityBackgroundTexture(s);
                WriteTextureToPng(tex, filePath);
                DestroyImmediate(tex);

                // Don't import/configure as Sprite yet. Just record what we generated.
                // We'll do the actual importer configuration AFTER we exit StartAssetEditing/StopAssetEditing.
                // Convert the absolute disk path to a Unity project-relative asset path ("Assets/..."),
                // and store that instead of the OS file path.
                string unityPath = ToUnityAssetPath(filePath);
                generated.Add((unityPath, s));

            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();

            // Important: Refresh once after writing files so Unity creates/imports the new assets.
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }


        // Now that we're OUTSIDE the AssetEditing batch, we can safely:
        // 1) ensure Unity sees the new files (Refresh already done in finally)
        // 2) configure each asset's TextureImporter as a Sprite
        //
        // Doing it here ensures the importer settings actually "stick" for batch output.
        AssetDatabase.StartAssetEditing();
        try
        {
            for (int i = 0; i < generated.Count; i++)
            {
                // Phase 2: configure each newly-written PNG asset as a Sprite using its Unity asset path.
                ImportAsSpriteUnityPath(generated[i].unityAssetPath, generated[i].settings);
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();

            // Optional: Refresh again (safe). You can omit if ImportAsSprite does SaveAndReimport reliably.
            AssetDatabase.Refresh();
        }

    }

    // ----------------------------
    // Preview rebuild
    // ----------------------------

    private void RebuildPreview()
    {
        if (_previewTex != null)
        {
            DestroyImmediate(_previewTex);
            _previewTex = null;
        }

        _previewTex = BuildRarityBackgroundTexture(_settings);
        Repaint();
    }

    // ----------------------------
    // Core image generation
    // ----------------------------

    private static Texture2D BuildRarityBackgroundTexture(Settings s)
    {
        int size = Mathf.Clamp(s.Size, 16, 2048);

        // RGBA32 for full colour + alpha.
        // We keep it readable so we can EncodeToPNG.
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        float radius = Mathf.Max(0f, s.CornerRadiusPx);
        float outline = Mathf.Max(0f, s.OutlineThicknessPx);
        float aa = Mathf.Max(0.5f, s.EdgeSoftnessPx);

        // Pre-allocate pixel buffer (faster than SetPixel per-pixel).
        Color[] pixels = new Color[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size; // 0..1
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size; // 0..1
                int idx = y * size + x;

                // Signed distance to rounded rect; negative inside, positive outside.
                // Compute shape alpha + outline mask differently depending on ShapeMode.
                float shapeAlpha;
                float outlineMask = 0f;

                if (s.ShapeMode == ShapeMode.RoundedPlate_TransparentCorners)
                {
                    // -------------------------
                    // Rounded plate with transparent outside corners (default)
                    // -------------------------

                    // dist: negative inside, 0 on the edge, positive outside
                    float dist = SignedDistanceRoundedRect(x + 0.5f, y + 0.5f, size, size, radius);

                    // Shape alpha from SDF (anti-aliased)
                    shapeAlpha = Smoothstep(aa, -aa, dist);

                    // Outside the rounded plate is transparent
                    if (shapeAlpha <= 0f)
                    {
                        pixels[idx] = new Color(0, 0, 0, 0);
                        continue;
                    }

                    // Outline band just inside the edge (correct mask)
                    // -----------------------------------------------
                    // We want outlineMask to be:
                    //   - 1 near the outer edge inside the shape (dist close to 0 but negative)
                    //   - 0 deeper inside past the inner boundary (dist <= -outline)
                    if (outline > 0f)
                    {
                        // inside: ~0 outside, ~1 inside
                        float inside = Smoothstep(aa, -aa, dist);

                        // withinOutlineBand: ~1 near edge, ~0 past inner boundary
                        float withinOutlineBand = Smoothstep(-outline - aa, -outline + aa, dist);

                        outlineMask = Mathf.Clamp01(inside * withinOutlineBand);
                    }
                }
                else
                {
                    // ---------------------------------------------
                    // Full-bleed square (opaque to edges) + inner rounded outline
                    // ---------------------------------------------

                    // Full-bleed: the entire sprite is opaque.
                    shapeAlpha = 1f;

                    // Inner outline band (optional)
                    if (outline > 0f)
                    {
                        float innerRadius = Mathf.Max(0f, s.InnerOutlineCornerRadiusPx);
                        float innerAA = Mathf.Max(0.5f, s.InnerOutlineEdgeSoftnessPx);

                        float distInner = SignedDistanceRoundedRectInset(
                            x + 0.5f,
                            y + 0.5f,
                            size,
                            size,
                            inset: outline * 0.5f,
                            radius: innerRadius
                        );

                        float half = outline * 0.5f;

                        float outerEdge = Smoothstep(+half + innerAA, +half - innerAA, distInner);
                        float innerEdge = Smoothstep(-half - innerAA, -half + innerAA, distInner);

                        outlineMask = Mathf.Clamp01(outerEdge * innerEdge);
                    }
                }


                // Outline mask is a band inside the edge.
#if false
// NOTE: This block is superseded by the ShapeMode-aware outline computation earlier.
// Keeping it disabled to preserve history without causing variable redefinition / scope issues.
float outlineMask = 0f;
if (outline > 0f)
{
    float bandOuter = 1f - Smoothstep(aa, -aa, dist);
    float bandInner = Smoothstep(-outline - aa, -outline + aa, dist);
    outlineMask = Mathf.Clamp01(bandInner * bandOuter);
}
#endif

                // 1) Determine base fill colour (single or gradient) at this pixel.
                Color rawFill = EvaluateFillColourAtUV(s, u, v);

                // 2) Mute fill toward black (your "rarity plate" look).
                Color mutedFill = Color.Lerp(rawFill, Color.black, Mathf.Clamp01(s.MuteToBlack));
                mutedFill.a = 1f;

                // 3) Apply lift pattern (centre/corner/edges etc).
                float liftWeight = EvaluateLiftWeightAtUV(s, u, v);      // 0..1
                float lift = Mathf.Clamp01(s.LiftAmount) * liftWeight;   // scaled by user amount

                // Brightened version of fill for lifting.
                // We “brighten” by scaling RGB upward a touch; clamp to avoid overflow.
                Color liftedFill = MultiplyRgb(mutedFill, 1f + lift);

                // 4) Optional edge vignette (darken near edges slightly).
                if (s.EdgeVignette > 0f)
                {
                    float vignette = EvaluateEdgeVignette(u, v); // 0 centre..1 edges
                    float darken = Mathf.Lerp(1f, 1f - s.EdgeVignette, vignette);
                    liftedFill = MultiplyRgb(liftedFill, darken);
                }

                // 5) Outline colour: derived from local fill (so gradients look coherent).
                // 5) Outline colour selection:
                // By default (DerivedFromFill) we keep your existing behaviour.
                // If ForcedBlack or CustomColour is selected, we override to a near-black / custom colour.
                Color outlineCol;

                switch (s.OutlineColourMode)
                {
                    default:
                    case OutlineColourMode.DerivedFromFill:
                        outlineCol = MultiplyRgb(liftedFill, 1f - Mathf.Clamp01(s.OutlineDarken));
                        outlineCol.a = 1f;
                        break;

                    case OutlineColourMode.ForcedBlack:
                        outlineCol = s.ForcedBlackOutlineColor;
                        outlineCol.a = 1f;
                        break;

                    case OutlineColourMode.CustomColour:
                        outlineCol = s.CustomOutlineColor;
                        outlineCol.a = 1f;
                        break;
                }

                // Optional: strengthen outline visibility without altering thickness.
                // We apply this to the mask (not the colour), so it remains intuitive.
                float outlineMaskStrong = Mathf.Clamp01(outlineMask * Mathf.Max(0f, s.OutlineStrength));

                outlineCol.a = 1f;

                // Blend fill vs outline based on outlineMask.
                Color final = Color.Lerp(liftedFill, outlineCol, outlineMaskStrong);

                // Apply shape alpha (rounded-corner transparency).
                final.a = shapeAlpha;

                pixels[idx] = final;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        return tex;
    }

    /// <summary>
    /// Returns a colour at UV based on FillMode:
    /// - SingleColour: returns BaseColor
    /// - Gradient: interpolates A->B using chosen space + direction
    /// </summary>
    private static Color EvaluateFillColourAtUV(Settings s, float u, float v)
    {
        if (s.FillMode == FillMode.SingleColour)
        {
            Color c = s.BaseColor;
            c.a = 1f;
            return c;
        }

        float t = EvaluateGradientT(s, u, v);

        // Clamp for sanity.
        t = Mathf.Clamp01(t);

        Color a = s.GradientColorA; a.a = 1f;
        Color b = s.GradientColorB; b.a = 1f;

        switch (s.GradientSpace)
        {
            case GradientSpace.RGB:
                return Color.Lerp(a, b, t);

            case GradientSpace.HSV:
                return LerpHSV(a, b, t);

            case GradientSpace.OKLCH:
                return LerpOKLCH(a, b, t);

            default:
                return Color.Lerp(a, b, t);
        }
    }

    /// <summary>
    /// Evaluates "t" (0..1) for the gradient at UV based on direction.
    /// </summary>
    private static float EvaluateGradientT(Settings s, float u, float v)
    {
        switch (s.GradientDirection)
        {
            case GradientDirection.Vertical_TopToBottom:
                return v;

            case GradientDirection.Vertical_BottomToTop:
                return 1f - v;

            case GradientDirection.Horizontal_LeftToRight:
                return u;

            case GradientDirection.Horizontal_RightToLeft:
                return 1f - u;

            case GradientDirection.Diagonal_TopLeftToBottomRight:
                return (u + v) * 0.5f;

            case GradientDirection.Diagonal_BottomRightToTopLeft:
                return 1f - ((u + v) * 0.5f);

            case GradientDirection.Diagonal_TopRightToBottomLeft:
                return ((1f - u) + v) * 0.5f;

            case GradientDirection.Diagonal_BottomLeftToTopRight:
                return 1f - (((1f - u) + v) * 0.5f);

            case GradientDirection.Radial_CentreOut:
                {
                    float dx = u - 0.5f;
                    float dy = v - 0.5f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / 0.7071f; // 0 at centre, ~1 at corners
                    r = Mathf.Clamp01(r);
                    float p = Mathf.Max(0.01f, s.RadialGradientPower);
                    return Mathf.Pow(r, p);
                }

            default:
                return v;
        }
    }

    /// <summary>
    /// Computes a lift weight (0..1) for the selected LiftMode.
    /// 1 = fully lifted area, 0 = no lift.
    /// </summary>
    private static float EvaluateLiftWeightAtUV(Settings s, float u, float v)
    {
        // Distance scaling (lets you widen/narrow the influence of the hotspot)
        float distScale = Mathf.Max(0.01f, s.LiftDistanceScale);

        // For “hotspot” modes we define an anchor point and compute:
        //   weight = (1 - distance/maxDistance) ^ falloff
        // where falloff controls how tight the highlight is.

        float falloff = Mathf.Max(0.01f, s.LiftFalloff);

        // Helper local function to compute a hotspot-based weight.
        float Hotspot(float ax, float ay)
        {
            float dx = (u - ax);
            float dy = (v - ay);

            // maxDist chosen so corners roughly map into 0..1 range.
            // Multiply by distScale so user can widen/narrow.
            float maxDist = 0.7071f * distScale;

            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float t = 1f - Mathf.Clamp01(d / maxDist);

            return Mathf.Pow(t, falloff);
        }

        switch (s.LiftMode)
        {
            case LiftMode.CentreLift:
                return Hotspot(0.5f, 0.5f);

            case LiftMode.EdgesLift:
                {
                    // Bright at edges, fades inward:
                    // distance-to-nearest-edge is 0 at the edge, ~0.5 at centre.
                    float dEdge = Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v)); // 0 at edge, 0.5 centre
                    float t = 1f - Mathf.Clamp01(dEdge / (0.5f * distScale));            // 1 at edge, 0 centre
                    return Mathf.Pow(t, falloff);
                }

            case LiftMode.MidTopLift:
                return Hotspot(0.5f, 1f);

            case LiftMode.TopRightLift:
                return Hotspot(1f, 1f);

            case LiftMode.MidRightLift:
                return Hotspot(1f, 0.5f);

            case LiftMode.BottomRightLift:
                return Hotspot(1f, 0f);

            case LiftMode.BottomMidLift:
                return Hotspot(0.5f, 0f);

            case LiftMode.BottomLeftLift:
                return Hotspot(0f, 0f);

            case LiftMode.MidLeftLift:
                return Hotspot(0f, 0.5f);

            case LiftMode.TopLeftLift:
                return Hotspot(0f, 1f);

            default:
                return Hotspot(0.5f, 0.5f);
        }
    }

    /// <summary>
    /// Returns a 0..1 vignette factor where 0 is centre and 1 is edges/corners.
    /// Used to darken edges slightly.
    /// </summary>
    private static float EvaluateEdgeVignette(float u, float v)
    {
        float dx = (u - 0.5f);
        float dy = (v - 0.5f);
        float r = Mathf.Sqrt(dx * dx + dy * dy) / 0.7071f;
        r = Mathf.Clamp01(r);

        // A mild curve for smoother vignette.
        return Mathf.Pow(r, 1.4f);
    }

    // ----------------------------
    // Rounded rect SDF + helpers
    // ----------------------------

    /// <summary>
    /// Signed Distance Function for a rounded rectangle aligned to the texture.
    /// Returns:
    ///  - negative inside
    ///  - 0 at the edge
    ///  - positive outside
    /// </summary>
    private static float SignedDistanceRoundedRect(float px, float py, float width, float height, float radius)
    {
        float cx = width * 0.5f;
        float cy = height * 0.5f;

        float hx = (width * 0.5f) - radius;
        float hy = (height * 0.5f) - radius;

        float dx = Mathf.Abs(px - cx) - hx;
        float dy = Mathf.Abs(py - cy) - hy;

        float ax = Mathf.Max(dx, 0f);
        float ay = Mathf.Max(dy, 0f);

        float outsideDist = Mathf.Sqrt(ax * ax + ay * ay);
        float insideDist = Mathf.Min(Mathf.Max(dx, dy), 0f);

        return outsideDist + insideDist - radius;
    }


    /// <summary>
    /// Signed distance to a rounded rectangle defined by INSETS from the outer texture edges.
    /// This is handy for drawing an "inner rounded outline" in a full-bleed square.
    /// </summary>
    private static float SignedDistanceRoundedRectInset(float px, float py, float width, float height, float inset, float radius)
    {
        // We shrink the available rect by 'inset' on all sides.
        float w = Mathf.Max(1f, width - inset * 2f);
        float h = Mathf.Max(1f, height - inset * 2f);

        // Shift pixel coordinates so the inset rect’s origin is (inset, inset).
        float localX = px - inset;
        float localY = py - inset;

        return SignedDistanceRoundedRect(localX, localY, w, h, radius);
    }



    /// <summary>
    /// Smoothstep with explicit edge0/edge1. Produces 0..1 with a smooth transition.
    /// </summary>
    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Mathf.InverseLerp(edge0, edge1, x);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Multiply only RGB (alpha preserved).
    /// </summary>
    private static Color MultiplyRgb(Color c, float m)
    {
        c.r = Mathf.Clamp01(c.r * m);
        c.g = Mathf.Clamp01(c.g * m);
        c.b = Mathf.Clamp01(c.b * m);
        return c;
    }

    // ----------------------------
    // Gradient interpolation helpers
    // ----------------------------

    /// <summary>
    /// Hue-aware HSV interpolation (wraps hue the short way around the circle).
    /// </summary>
    private static Color LerpHSV(Color a, Color b, float t)
    {
        Color.RGBToHSV(a, out float ah, out float asat, out float av);
        Color.RGBToHSV(b, out float bh, out float bsat, out float bv);

        // Wrap hue the short way (e.g. 350° -> 10° should go through 0°, not around the long way)
        float dh = Mathf.DeltaAngle(ah * 360f, bh * 360f) / 360f;
        float h = Mathf.Repeat(ah + dh * t, 1f);

        float s = Mathf.Lerp(asat, bsat, t);
        float v = Mathf.Lerp(av, bv, t);

        return Color.HSVToRGB(h, s, v, hdr: false);
    }

    /*
        OKLab / OKLCH
        ------------
        This is a perceptual-ish colour space that tends to interpolate “nicely” for UI gradients,
        especially for “rarity ramp” styles.

        We:
          1) Convert sRGB -> linear
          2) Convert linear RGB -> OKLab
          3) Convert OKLab -> OKLCH
          4) Interpolate L, C, and Hue (hue wrapped)
          5) Convert back OKLCH -> OKLab -> linear RGB -> sRGB
    */

    private static Color LerpOKLCH(Color a, Color b, float t)
    {
        OKLCH ca = RGBToOKLCH(a);
        OKLCH cb = RGBToOKLCH(b);

        // Interpolate L and C linearly
        float L = Mathf.Lerp(ca.L, cb.L, t);
        float C = Mathf.Lerp(ca.C, cb.C, t);

        // Interpolate hue the short way around
        float dh = ShortestAngleRadians(ca.h, cb.h);
        float h = WrapRadians(ca.h + dh * t);

        Color outRgb = OKLCHToRGB(new OKLCH(L, C, h));
        outRgb.a = 1f;
        return outRgb;
    }

    private struct OKLCH
    {
        public float L;   // 0..1-ish
        public float C;   // chroma
        public float h;   // hue radians 0..2pi
        public OKLCH(float L, float C, float h) { this.L = L; this.C = C; this.h = h; }
    }

    private static float WrapRadians(float r)
    {
        float twoPi = Mathf.PI * 2f;
        r %= twoPi;
        if (r < 0f) r += twoPi;
        return r;
    }

    private static float ShortestAngleRadians(float a, float b)
    {
        float twoPi = Mathf.PI * 2f;
        float d = (b - a) % twoPi;
        if (d > Mathf.PI) d -= twoPi;
        if (d < -Mathf.PI) d += twoPi;
        return d;
    }

    private static OKLCH RGBToOKLCH(Color srgb)
    {
        // sRGB -> linear
        Vector3 lin = new Vector3(SrgbToLinear(srgb.r), SrgbToLinear(srgb.g), SrgbToLinear(srgb.b));

        // linear RGB -> OKLab (via LMS)
        // Reference matrices commonly used for OKLab.
        float l = 0.4122214708f * lin.x + 0.5363325363f * lin.y + 0.0514459929f * lin.z;
        float m = 0.2119034982f * lin.x + 0.6806995451f * lin.y + 0.1073969566f * lin.z;
        float s = 0.0883024619f * lin.x + 0.2817188376f * lin.y + 0.6299787005f * lin.z;

        // Non-linear transform (cube root)
        float l_ = Mathf.Pow(l, 1f / 3f);
        float m_ = Mathf.Pow(m, 1f / 3f);
        float s_ = Mathf.Pow(s, 1f / 3f);

        // OKLab
        float L = 0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_;
        float A = 1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_;
        float B = 0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_;

        // OKLCH
        float C = Mathf.Sqrt(A * A + B * B);
        float h = Mathf.Atan2(B, A);
        h = WrapRadians(h);

        return new OKLCH(L, C, h);
    }

    private static Color OKLCHToRGB(OKLCH c)
    {
        // OKLCH -> OKLab
        float A = c.C * Mathf.Cos(c.h);
        float B = c.C * Mathf.Sin(c.h);

        float l_ = c.L + 0.3963377774f * A + 0.2158037573f * B;
        float m_ = c.L - 0.1055613458f * A - 0.0638541728f * B;
        float s_ = c.L - 0.0894841775f * A - 1.2914855480f * B;

        float l = l_ * l_ * l_;
        float m = m_ * m_ * m_;
        float s = s_ * s_ * s_;

        // LMS -> linear RGB
        float rLin = +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float gLin = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float bLin = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

        // linear -> sRGB
        float r = LinearToSrgb(rLin);
        float g = LinearToSrgb(gLin);
        float b = LinearToSrgb(bLin);

        return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
    }

    private static float SrgbToLinear(float c)
    {
        // Standard sRGB transfer function
        if (c <= 0.04045f) return c / 12.92f;
        return Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static float LinearToSrgb(float c)
    {
        // Standard sRGB transfer function
        if (c <= 0.0031308f) return 12.92f * c;
        return 1.055f * Mathf.Pow(Mathf.Max(0f, c), 1f / 2.4f) - 0.055f;
    }

    // ----------------------------
    // Output + import helpers
    // ----------------------------

    private string GetOutputFolderPathOrNull()
    {
        if (_settings.OutputFolder == null) return null;

        string assetPath = AssetDatabase.GetAssetPath(_settings.OutputFolder);
        if (string.IsNullOrEmpty(assetPath)) return null;
        if (!AssetDatabase.IsValidFolder(assetPath)) return null;

        string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
        return Path.Combine(projectRoot, assetPath);
    }

    private static void WriteTextureToPng(Texture2D tex, string absoluteFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteFilePath)!);
        byte[] png = tex.EncodeToPNG();
        File.WriteAllBytes(absoluteFilePath, png);
    }

    private static void ImportAsSprite(string absoluteFilePath, Settings s)
    {
        string unityPath = ToUnityAssetPath(absoluteFilePath);

        AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceUpdate);

        TextureImporter importer = AssetImporter.GetAtPath(unityPath) as TextureImporter;
        if (importer == null) return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;

        // UI typically wants no mipmaps.
        importer.mipmapEnabled = !s.DisableMipMaps;

        importer.sRGBTexture = true;
        importer.filterMode = FilterMode.Bilinear;

        if (s.ClampWrapMode)
            importer.wrapMode = TextureWrapMode.Clamp;

        importer.spritePixelsPerUnit = Mathf.Max(1f, s.PixelsPerUnit);

        importer.SaveAndReimport();
    }

    /// <summary>
    /// Imports and configures a sprite given a Unity asset path (e.g. "Assets/Icons/MySprite.png").
    /// This avoids any reliance on absolute disk paths and is often the most reliable way to drive imports in batch.
    /// </summary>
    private static void ImportAsSpriteUnityPath(string unityAssetPath, Settings s)
    {
        if (string.IsNullOrEmpty(unityAssetPath)) return;

        // Ensure Unity imports/updates the asset record.
        AssetDatabase.ImportAsset(unityAssetPath, ImportAssetOptions.ForceUpdate);

        // Fetch the importer for this path.
        TextureImporter importer = AssetImporter.GetAtPath(unityAssetPath) as TextureImporter;
        if (importer == null) return;

        // Configure as Sprite (2D/UI).
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;

        // UI typically wants no mipmaps.
        importer.mipmapEnabled = !s.DisableMipMaps;

        importer.sRGBTexture = true;
        importer.filterMode = FilterMode.Bilinear;

        if (s.ClampWrapMode)
            importer.wrapMode = TextureWrapMode.Clamp;

        importer.spritePixelsPerUnit = Mathf.Max(1f, s.PixelsPerUnit);

        // Apply importer changes.
        importer.SaveAndReimport();
    }


    private static string ToUnityAssetPath(string absoluteFilePath)
    {
        string projectRoot = Directory.GetParent(Application.dataPath)!.FullName.Replace("\\", "/");
        string abs = absoluteFilePath.Replace("\\", "/");

        if (!abs.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File path is not inside this Unity project.");

        return abs.Substring(projectRoot.Length + 1);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Rarity_Background";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    private static Settings CloneSettings(Settings s)
    {
        // Simple manual clone so batch generation can tweak fill mode/colours per entry
        // without mutating the user's live settings.
        return new Settings
        {
            OutputFolder = s.OutputFolder,
            FileName = s.FileName,
            PixelsPerUnit = s.PixelsPerUnit,
            DisableMipMaps = s.DisableMipMaps,
            ClampWrapMode = s.ClampWrapMode,

            FillMode = s.FillMode,
            BaseColor = s.BaseColor,
            GradientColorA = s.GradientColorA,
            GradientColorB = s.GradientColorB,
            GradientSpace = s.GradientSpace,
            GradientDirection = s.GradientDirection,
            RadialGradientPower = s.RadialGradientPower,

            Size = s.Size,
            CornerRadiusPx = s.CornerRadiusPx,
            OutlineThicknessPx = s.OutlineThicknessPx,
            EdgeSoftnessPx = s.EdgeSoftnessPx,

            MuteToBlack = s.MuteToBlack,
            OutlineDarken = s.OutlineDarken,
            // Ensure outline colour behaviour is preserved in Batch generation (and any cloned settings usage)
            OutlineColourMode = s.OutlineColourMode,
            ForcedBlackOutlineColor = s.ForcedBlackOutlineColor,
            CustomOutlineColor = s.CustomOutlineColor,
            OutlineStrength = s.OutlineStrength,

            EdgeVignette = s.EdgeVignette,

            LiftMode = s.LiftMode,
            LiftAmount = s.LiftAmount,
            LiftFalloff = s.LiftFalloff,
            LiftDistanceScale = s.LiftDistanceScale,

            ShapeMode = s.ShapeMode,
            InnerOutlineCornerRadiusPx = s.InnerOutlineCornerRadiusPx,
            InnerOutlineEdgeSoftnessPx = s.InnerOutlineEdgeSoftnessPx
        };
    }

    // ----------------------------
    // Preview helpers (checker)
    // ----------------------------

    private static void DrawCheckerBackground(Rect r, float cellSize)
    {
        // Very lightweight checker: draw rectangles using GUI.color.
        // This makes alpha regions obvious in the preview.
        Color c0 = new Color(0.20f, 0.20f, 0.20f, 1f);
        Color c1 = new Color(0.27f, 0.27f, 0.27f, 1f);

        int cols = Mathf.CeilToInt(r.width / cellSize);
        int rows = Mathf.CeilToInt(r.height / cellSize);

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                Rect cell = new Rect(r.x + x * cellSize, r.y + y * cellSize, cellSize, cellSize);
                EditorGUI.DrawRect(cell, ((x + y) % 2 == 0) ? c0 : c1);
            }
        }
    }
}
#endif
