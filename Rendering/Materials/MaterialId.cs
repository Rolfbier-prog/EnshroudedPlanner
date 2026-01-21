namespace EnshroudedPlanner.Rendering.Materials;

public enum MaterialId
{
    StoneGrayLight = 0,
    StoneGrayDark = 1,
    WoodBrownLight = 2,
    WoodBrownDark = 3,
    BrickSand = 4,
    BrickRed = 5,
    MetalLight = 6,
    MetalDark = 7,
    GlowYellow = 8,
    GlowBlue = 9,
    GlowRed = 10,
    GlowWhite = 11,

    /// <summary>
    /// Platzhalter für "kein Material" (dunkles, mattes Blau ohne Textur).
    /// Wird absichtlich NICHT aus dem Atlas geladen.
    /// </summary>
    NoMaterialBlue = 100,
}
