using System;
using System.Collections.Generic;
using System.Linq;
using UiAssemblerSlice.Editor.Assembler;
using UnityEditor;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Review
{
    /// Reassign picker for ReviewWindow's review rows. Replaces an earlier
    /// GenericMenu-per-catalog-entry approach - fine for this fixture's ~65
    /// entries, but breaks down at real-project scale (thousands of
    /// entries): GenericMenu has no search/filter, and materializing one
    /// menu item per catalog entry on every click means both building and
    /// scrolling it gets slow and unusable.
    ///
    /// Instead: nothing is listed until the user types a query (so OnGUI
    /// never has to lay out thousands of rows in one frame), matches are
    /// filtered by id/feature substring, and results are capped at
    /// MaxResults so a broad query still stays fast to draw.
    public class CatalogPickerWindow : EditorWindow
    {
        private const int MaxResults = 200;

        private List<CatalogEntryData> _catalog;
        private Func<CatalogEntryData, Texture2D> _getPreview;
        private Action<string> _onPick;
        private string _currentId;
        private string _search = "";
        private Vector2 _scroll;
        private bool _focused;

        public static void Show(Rect activatorScreenRect, List<CatalogEntryData> catalog, string currentId,
            Func<CatalogEntryData, Texture2D> getPreview, Action<string> onPick)
        {
            var window = CreateInstance<CatalogPickerWindow>();
            window._catalog = catalog;
            window._currentId = currentId;
            window._getPreview = getPreview;
            window._onPick = onPick;
            window.titleContent = new GUIContent("Reassign Asset");
            window.ShowAsDropDown(activatorScreenRect, new Vector2(380, 420));
        }

        private void OnGUI()
        {
            GUI.SetNextControlName("CatalogPickerSearch");
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
            if (!_focused)
            {
                _focused = true;
                EditorGUI.FocusTextInControl("CatalogPickerSearch");
            }

            if (string.IsNullOrEmpty(_search))
            {
                EditorGUILayout.HelpBox($"Type to search {_catalog.Count} catalog entries (by id or feature).", MessageType.None);
                return;
            }

            var matches = _catalog
                .Where(e => (e.Id != null && e.Id.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                         || (e.Feature != null && e.Feature.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var entry in matches.Take(MaxResults))
            {
                EditorGUILayout.BeginHorizontal();
                var tex = _getPreview(entry);
                GUILayout.Box(tex != null ? new GUIContent(tex) : GUIContent.none, GUILayout.Width(32), GUILayout.Height(32));
                var label = entry.Id == _currentId ? entry.Id + "  (current)" : entry.Id;
                if (GUILayout.Button(label, EditorStyles.label, GUILayout.Height(32)))
                {
                    _onPick(entry.Id);
                    Close();
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            if (matches.Count > MaxResults)
            {
                EditorGUILayout.HelpBox($"{matches.Count - MaxResults} more match(es) - refine your search.", MessageType.None);
            }
        }
    }
}
