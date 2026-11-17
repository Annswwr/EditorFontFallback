using UnityEditor;

namespace EditorFontFallback.Editor
{
    public static class EditorFontFallbackMenu
    {
        private const string EnabledMenuPath = "Tools/Editor Font Fallback/Enabled";
        private const string DebugMessagesMenuPath = "Tools/Editor Font Fallback/Debug Messages";
        private const string RescanFontsMenuPath = "Tools/Editor Font Fallback/Rescan Fonts";

        // Editor preferences
        private const string EnabledKey = "EditorFontFallback.Enabled";
        private const string DebugMessagesKey = "EditorFontFallback.DebugMessages";

        public static bool IsEnabled => EditorPrefs.GetBool(EnabledKey, true);
        public static bool IsDebugMessagesEnabled => EditorPrefs.GetBool(DebugMessagesKey, false);

        [MenuItem(EnabledMenuPath, false, 1)]
        private static void ToggleEnabled()
        {
            var isEnabled = !IsEnabled;
            EditorPrefs.SetBool(EnabledKey, isEnabled);

            if (isEnabled)
            {
                EditorComplexTextLayout.Enable();
            }
            else
            {
                EditorFontFallbackInjector.Disable();
                EditorComplexTextLayout.Disable();
            }

            EditorUtility.RequestScriptReload();
        }

        [MenuItem(EnabledMenuPath, true)]
        private static bool ValidateEnabled()
        {
            Menu.SetChecked(EnabledMenuPath, IsEnabled);
            return true;
        }

        [MenuItem(DebugMessagesMenuPath, false, 2)]
        private static void ToggleDebugMessages() => EditorPrefs.SetBool(DebugMessagesKey, !IsDebugMessagesEnabled);

        [MenuItem(DebugMessagesMenuPath, true)]
        private static bool ValidateDebugMessages()
        {
            Menu.SetChecked(DebugMessagesMenuPath, IsDebugMessagesEnabled);
            return true;
        }

        [MenuItem(RescanFontsMenuPath, false, 20)]
        private static void RescanFonts()
        {
            EditorFontFallbackInjector.ClearScan();
            EditorUtility.RequestScriptReload();
        }
    }
}
