using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace EditorFontFallback.Editor
{
    /// <summary>
    /// Fixes text shaping for various complex scripts / languages
    /// Ensures Unity's Advanced text generator is used instead of Standard
    /// </summary>
    [InitializeOnLoad]
    public static class EditorComplexTextLayout
    {
        private const double CheckInterval = 1;  // Seconds

        private static readonly HashSet<VisualElement> ChangedRoots = new();
        private static double _nextCheckTime;

        static EditorComplexTextLayout()
        {
            if (EditorFontFallbackMenu.IsEnabled)
                Enable();
        }

        public static void Enable()
        {
            EditorApplication.update -= CheckWindows;
            EditorApplication.update += CheckWindows;
            EditorWindow.windowFocusChanged -= RefreshWindows;
            EditorWindow.windowFocusChanged += RefreshWindows;
            RefreshWindows();
        }

        public static void Disable()
        {
            EditorApplication.update -= CheckWindows;
            EditorWindow.windowFocusChanged -= RefreshWindows;

            // Revert roots this changed
            foreach (var root in ChangedRoots.Where(root => root != null))
                root.style.unityTextGenerator = StyleKeyword.Null;

            ChangedRoots.Clear();
        }

        private static void CheckWindows()
        {
            if (EditorApplication.timeSinceStartup < _nextCheckTime)
                return;

            _nextCheckTime = EditorApplication.timeSinceStartup + CheckInterval;
            RefreshWindows();
        }

        private static void RefreshWindows()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                var root = window.rootVisualElement;
                if (root?.panel == null || IsAdvanced(root))
                    continue;

                // Set on the root so every element inherits it, including ones added later
                root.style.unityTextGenerator = TextGeneratorType.Advanced;
                ChangedRoots.Add(root);
            }

            return;

            static bool IsAdvanced(VisualElement root)
                => root.style.unityTextGenerator is { keyword: StyleKeyword.Undefined, value: TextGeneratorType.Advanced };
        }
    }
}
