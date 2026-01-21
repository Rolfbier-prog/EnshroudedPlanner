#nullable enable
using System;
using System.Collections.Generic;

namespace EnshroudedPlanner
{
    /// <summary>
    /// Entfernt ein platziertes Piece (Undo/Redo-fähig).
    /// </summary>
    public sealed class RemovePieceCommand : IEditorCommand
    {
        private readonly ProjectData _project;
        private readonly PlacedPiece _piece;
        private int _index = -1;

        public RemovePieceCommand(ProjectData project, PlacedPiece piece)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            _piece = piece;
        }

        public void Do()
        {
            _index = _project.PlacedPieces.IndexOf(_piece);
            if (_index < 0) return;

            _project.PlacedPieces.RemoveAt(_index);
        }

        public void Undo()
        {
            if (_index < 0) return;

            // Clamp falls sich die Liste geändert hat
            if (_index > _project.PlacedPieces.Count)
                _index = _project.PlacedPieces.Count;

            _project.PlacedPieces.Insert(_index, _piece);
        }
    }
}
