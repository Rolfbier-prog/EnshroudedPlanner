// Elements/ElementRegistry.cs
// SYS-1: zentrale Registry (Metadata). Darf keinerlei Placement/Offset/Rotation-Logik verändern.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using EnshroudedPlanner;


namespace EnshroudedPlanner.Elements;

public sealed class ElementRegistry
{
    public Dictionary<string, CategoryDefinition> Categories { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ElementDefinition> Elements { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PieceIdToElementId { get; } = new(StringComparer.Ordinal);

    public bool TryGet(string id, out ElementDefinition def) => Elements.TryGetValue(id, out def);

    public ElementDefinition Get(string id) => Elements[id];

    public string? TryGetElementIdByPieceId(string pieceId)
        => PieceIdToElementId.TryGetValue(pieceId, out var id) ? id : null;

    public IEnumerable<ElementDefinition> GetByCategory(string categoryId)
        => Elements.Values.Where(e => string.Equals(e.CategoryId, categoryId, StringComparison.Ordinal));

    /// <summary>
    /// Build Registry as a pure metadata view over the existing PieceLibrary.
    /// IMPORTANT: No placement math here (offsets/anchors/rotations remain unchanged elsewhere).
    /// </summary>
    public static ElementRegistry BuildFromPieceLibrary(PieceLibrary lib)
    {
        var r = new ElementRegistry();

        // Palette-Kategorien (gewünschte UI-Struktur: links Kategorie, rechts Elemente)
        // IMPORTANT: Das ist nur Metadaten/Grouping – PieceLibrary/Placement bleibt unverändert.
        r.Categories["PAL_1M"] = new CategoryDefinition { Id = "PAL_1M", DisplayName = "🧱🏠⛰ 1M", SortIndex = 0 };
        r.Categories["PAL_2M"] = new CategoryDefinition { Id = "PAL_2M", DisplayName = "🧱 2M", SortIndex = 1 };
        r.Categories["PAL_4M"] = new CategoryDefinition { Id = "PAL_4M", DisplayName = "🧱 4M", SortIndex = 2 };
        r.Categories["PAL_ROOF_2M"] = new CategoryDefinition { Id = "PAL_ROOF_2M", DisplayName = "🏠 Dächer (2 M)", SortIndex = 3 };
        r.Categories["PAL_ROOF_4M"] = new CategoryDefinition { Id = "PAL_ROOF_4M", DisplayName = "🏠 Dächer (4 M)", SortIndex = 4 };
        r.Categories["PAL_TERRAIN"] = new CategoryDefinition { Id = "PAL_TERRAIN", DisplayName = "⛰ Terrain", SortIndex = 5 };
        r.Categories["ALTAR"] = new CategoryDefinition { Id = "ALTAR", DisplayName = "🔥 Flammenaltar", SortIndex = 6 };

        // Zusätzliche System-Kategorien (noch nicht in UI verdrahten – nur vorbereiten)
        r.Categories["PREFABS"] = new CategoryDefinition { Id = "PREFABS", DisplayName = "📦 Vorgefertigte Bauteile (Snippets)", SortIndex = 10_000 };
        r.Categories["PARTS"] = new CategoryDefinition { Id = "PARTS", DisplayName = "🧩 Bauteile (Custom)", SortIndex = 10_001 };

        // Pieces -> Elements (metadata only)
        foreach (var piece in lib.Pieces)
        {
            if (string.IsNullOrWhiteSpace(piece.Id)) continue;

            var elId = "piece:" + piece.Id;

            // Facet + Palette-Category aus der bestehenden PieceLibrary ableiten.
            // (Aktuell existieren evtl. noch keine ROOF/TERRAIN Pieces – Kategorien bleiben trotzdem sichtbar.)
            var sourceCat = piece.CategoryId ?? "";
            // Facet is a lightweight UI tag (Structure/Roof/Terrain/...).
            // IMPORTANT: This must not affect placement/offset/rotation logic; it's metadata only.
            var facet = (sourceCat.StartsWith("ROOF", StringComparison.OrdinalIgnoreCase) || sourceCat.StartsWith("DACH", StringComparison.OrdinalIgnoreCase))
                ? ElementFacet.Roof
                : sourceCat.StartsWith("TERRAIN", StringComparison.OrdinalIgnoreCase)
                    ? ElementFacet.Terrain
                    : ElementFacet.Structure;

            string palCat;
            if (string.Equals(sourceCat, "ALTAR", StringComparison.Ordinal))
            {
                palCat = "ALTAR";
            }
            else if (facet == ElementFacet.Roof)
            {
                // Special case: 1M roof pieces belong into 1M category (facet filter: Roof).
                // 2M/4M roofs go into their dedicated roof categories.
                palCat = sourceCat.EndsWith("_1M", StringComparison.OrdinalIgnoreCase) ? "PAL_1M"
                       : sourceCat.EndsWith("_2M", StringComparison.OrdinalIgnoreCase) ? "PAL_ROOF_2M"
                       : "PAL_ROOF_4M";
            }
            else if (facet == ElementFacet.Terrain)
            {
                // Special case: 1M terrain pieces belong into 1M category (facet filter: Terrain).
                // Bigger terrain pieces go into the dedicated Terrain category.
                palCat = sourceCat.EndsWith("_1M", StringComparison.OrdinalIgnoreCase) ? "PAL_1M" : "PAL_TERRAIN";
            }
            else
            {
                // Struktur nach Größentier (_1M/_2M/_4M)
                palCat = sourceCat.EndsWith("_1M", StringComparison.OrdinalIgnoreCase) ? "PAL_1M"
                       : sourceCat.EndsWith("_2M", StringComparison.OrdinalIgnoreCase) ? "PAL_2M"
                       : sourceCat.EndsWith("_4M", StringComparison.OrdinalIgnoreCase) ? "PAL_4M"
                       : "PAL_1M";
            }

            var def = new ElementDefinition
            {
                Id = elId,
                Kind = ElementKind.Piece,
                DisplayName = string.IsNullOrWhiteSpace(piece.DisplayName) ? piece.Id : piece.DisplayName,
                CategoryId = palCat,
                SourceCategoryId = sourceCat,
                Facet = facet,

                Footprint = new FootprintSpec(
                    SizeX: piece.Size?.X ?? 1,
                    SizeY: piece.Size?.Y ?? 1,
                    SizeZ: piece.Size?.Z ?? 1,
                    UsesMajorGrid: false),

                Rotation = new RotationSpec(AllowRotation: true, StepDegrees: 90),

                Material = new MaterialSpec(
                    UsesSelectedMaterial: true,
                    AllowNoMaterial: true,
                    FixedMaterialId: null),

                Preview = new PreviewSpec(PreviewType.GhostMesh, ShowFootprintOutline: false),

                PieceId = piece.Id,
            };

            r.Elements[elId] = def;
            r.PieceIdToElementId[piece.Id] = elId;
        }

        return r;
    }
}
