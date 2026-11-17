using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using TextElement = UnityEngine.UIElements.TextElement;

namespace EditorFontFallback.Editor
{
    /// <summary>
    /// Adds compatible system fonts found using <see cref="EditorFontFallbackDiscovery"/> as an Editor fallback.
    /// </summary>
    [InitializeOnLoad]
    public static class EditorFontFallbackInjector
    {
        public static event Action OnFallbacksChanged;

        // Session state
        private const string IsInitializedKey = "EditorFontFallback.IsInitialized";
        private const string FallbackFontsCacheKey = "EditorFontFallback.FallbackFonts";

        // Editor preferences
        private const string ScanStampKey = "EditorFontFallback.ScanStamp";
        private const string ScanFontsKey = "EditorFontFallback.ScanFonts";

        private const string AssetNameSuffix = " - Regular SDF";
        private const int ScanVersion = 2;  // Bump to discard saved scans
        private const int MaxRetries = 10;

        private static readonly List<FontAsset> InjectedAssets = new();
        private static readonly Lazy<PropertyInfo> DefaultTextSettings = new(()
            => Type.GetType("UnityEditor.EditorTextSettings, UnityEditor.CoreModule")?.GetProperty("defaultTextSettings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
        private static readonly Lazy<MethodInfo> EmptyTextHandleCache = new(()
            => Type.GetType("UnityEngine.IMGUITextHandle, UnityEngine.IMGUIModule")?.GetMethod("EmptyCache", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
        private static int _attempts;

        private static string GlobalFallbackAssetPath => $"UIPackageResources/FontAssets/DynamicOSFontAssets/GlobalFallback/GlobalFallback - {PlatformSuffix}.asset";

        private static string PlatformSuffix => Application.platform switch
        {
            RuntimePlatform.WindowsEditor => "Win",
            RuntimePlatform.OSXEditor => "OSX",
            RuntimePlatform.LinuxEditor => "Linux",
            _ => "Win",
        };

        static EditorFontFallbackInjector()
        {
            if (!EditorFontFallbackMenu.IsEnabled)
                return;

            _attempts = 0;
            EditorApplication.delayCall += Initialize;
        }

        public static void Disable()
        {
            var settingsRemovedCount = 0;
            if (GetTextSettings() is { fallbackFontAssets: not null } settings)
            {
                var fallbacks = settings.fallbackFontAssets;
                settingsRemovedCount = fallbacks.RemoveAll(f => f == null || InjectedAssets.Contains(f));
                settings.fallbackFontAssets = fallbacks;
            }

            var globalRemovedCount = 0;
            if (GetGlobalFallback() is { fallbackFontAssetTable: not null } global)
            {
                var table = global.fallbackFontAssetTable;
                globalRemovedCount = table.RemoveAll(f => f == null || InjectedAssets.Contains(f));
                global.fallbackFontAssetTable = table;
            }

            Log($"Removed {settingsRemovedCount} from TextSettings.fallbackFontAssets, {globalRemovedCount} from GlobalFallback.fallbackFontAssetTable.");
            InjectedAssets.Clear();
            RefreshText();
        }

        public static void ClearScan()
        {
            EditorPrefs.DeleteKey(ScanStampKey);
            EditorPrefs.DeleteKey(ScanFontsKey);
            SessionState.EraseString(FallbackFontsCacheKey);
            SessionState.EraseBool(IsInitializedKey);
        }

        private static void Initialize()
        {
            if (!EditorFontFallbackMenu.IsEnabled)
                return;

            _attempts++;
            try
            {
                if (SessionState.GetBool(IsInitializedKey, false))
                    AddFallbackFonts();
                else
                    PickFallbackFonts();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{nameof(EditorFontFallbackInjector)}] Failed to add fallback fonts on attempt {_attempts}. ({e})");
                Retry();
            }
        }

        private static void Retry()
        {
            if (_attempts < MaxRetries)
                EditorApplication.delayCall += Initialize;
            else
                Debug.LogWarning($"[{nameof(EditorFontFallbackInjector)}] Failed to add fallback fonts after {_attempts} attempts. (Giving up)");
        }

        private static void Log(string message)
        {
            if (EditorFontFallbackMenu.IsDebugMessagesEnabled)
                Debug.Log($"[{nameof(EditorFontFallbackInjector)}] {message}");
        }


        #region Picking fonts

        private static void PickFallbackFonts()
        {
            // Use the saved scan while the installed fonts are the same
            var stamp = GetFontStamp();
            var fonts = EditorPrefs.GetString(ScanStampKey, "") == stamp ? SplitFonts(EditorPrefs.GetString(ScanFontsKey, "")) : Array.Empty<string>();
            if (fonts.Length > 0)
            {
                Log($"Reused the saved scan with {fonts.Length} fallback candidate(s).");
            }
            else
            {
                var result = EditorFontFallbackDiscovery.Scan(GetUnityFallbackFamilies());
                Log($"Scanned {result.InstalledFontCount} installed fonts, checked {result.LoadedFontCount}, Unity's fallbacks cover {result.EditorCoveredBlockCount} block(s), found {result.Fonts.Length} fallback candidate(s).");
                fonts = result.Fonts;
            }

            if (fonts.Length == 0)
            {
                Debug.LogWarning($"[{nameof(EditorFontFallbackInjector)}] Failed to find fallback fonts on attempt {_attempts}. (Retrying)");
                Retry();
                return;
            }

            EditorPrefs.SetString(ScanStampKey, stamp);
            EditorPrefs.SetString(ScanFontsKey, string.Join(";", fonts));
            SessionState.SetString(FallbackFontsCacheKey, string.Join(";", fonts));
            SessionState.SetBool(IsInitializedKey, true);
            EditorApplication.delayCall += Initialize;
        }

        private static string GetFontStamp()
        {
            // FNV-1a hash of the installed font names
            var hash = 2166136261;
            foreach (var letter in string.Join(";", Font.GetOSInstalledFontNames().OrderBy(font => font, StringComparer.Ordinal)))
                hash = (hash ^ letter) * 16777619u;

            return $"{ScanVersion}:{Application.unityVersion}:{hash}";
        }

        private static HashSet<string> GetUnityFallbackFamilies()
        {
            var assets = (GetTextSettings()?.fallbackFontAssets ?? new List<FontAsset>()).Concat(GetGlobalFallback()?.fallbackFontAssetTable ?? new List<FontAsset>());
            return assets
                .Where(asset => asset != null && !asset.name.EndsWith(AssetNameSuffix, StringComparison.Ordinal))
                .Select(asset => asset.faceInfo.familyName)
                .Where(family => !string.IsNullOrEmpty(family))
                .ToHashSet();
        }

        private static string[] SplitFonts(string fonts) => fonts.Split(';', StringSplitOptions.RemoveEmptyEntries);

        #endregion


        #region Fallback lists

        private static void AddFallbackFonts()
        {
            var cached = SessionState.GetString(FallbackFontsCacheKey, "");
            var families = SplitFonts(cached).Where(font => !EditorFontFallbackDiscovery.IsSymbolFont(font)).ToArray();
            if (families.Length == 0)
            {
                Debug.LogWarning($"[{nameof(EditorFontFallbackInjector)}] Failed to read the cached fonts '{cached}' on attempt {_attempts}. (Scanning again)");
                SessionState.EraseString(FallbackFontsCacheKey);
                SessionState.EraseBool(IsInitializedKey);
                Retry();
                return;
            }

            // Reuse fallbacks that survived a domain reload
            RemoveUnusedFonts(families);
            var missing = families.Where(font => !TryReuseFallbackFont(font)).ToArray();
            if (missing.Length > 0)
            {
                var addedCount = missing.Count(AddFallbackFont);
                Log($"Attempt {_attempts} - {families.Length} candidate(s), reused {families.Length - missing.Length}, added {addedCount}.");
                if (addedCount < missing.Length)
                {
                    Retry();
                    return;
                }
            }

            RefreshText();
            Log($"Fallback fonts active [{string.Join(", ", families)}].");
        }

        private static bool TryReuseFallbackFont(string font)
        {
            var name = font + AssetNameSuffix;
            var settings = GetTextSettings();
            var global = GetGlobalFallback();
            var asset = settings?.fallbackFontAssets?.Find(f => f != null && f.name == name)
                        ?? global?.fallbackFontAssetTable?.Find(f => f != null && f.name == name);
            if (asset == null)
                return false;

            AddToLists(asset);
            return true;
        }

        private static bool AddFallbackFont(string font)
        {
            var asset = FontAsset.CreateFontAsset(font, "Regular");
            if (asset == null)
                return false;

            SetHideFlags(asset);
            if (!AddToLists(asset))
            {
                Debug.LogWarning($"[{nameof(EditorFontFallbackInjector)}] Failed to add '{font}'. (Editor text settings not found)");
                return false;
            }

            return true;
        }

        private static bool AddToLists(FontAsset asset)  // False if neither list was found
        {
            var settings = GetTextSettings();
            if (settings != null)
            {
                var fallbacks = settings.fallbackFontAssets ?? new List<FontAsset>();
                fallbacks.RemoveAll(f => f == null || (f != asset && f.name == asset.name));
                if (!fallbacks.Contains(asset))
                    fallbacks.Add(asset);

                settings.fallbackFontAssets = fallbacks;
            }

            var global = GetGlobalFallback();
            if (global != null)
            {
                var table = global.fallbackFontAssetTable ?? new List<FontAsset>();
                table.RemoveAll(f => f == null || (f != asset && f.name == asset.name));
                if (!table.Contains(asset))
                    table.Add(asset);

                global.fallbackFontAssetTable = table;
            }

            if (!InjectedAssets.Contains(asset))
                InjectedAssets.Add(asset);

            return settings != null || global != null;
        }

        private static void RemoveUnusedFonts(ICollection<string> families)  // Fonts this added that the scan no longer picks
        {
            if (GetTextSettings() is { fallbackFontAssets: not null } settings)
            {
                var fallbacks = settings.fallbackFontAssets;
                fallbacks.RemoveAll(IsUnused);
                settings.fallbackFontAssets = fallbacks;
            }

            if (GetGlobalFallback() is { fallbackFontAssetTable: not null } global)
            {
                var table = global.fallbackFontAssetTable;
                table.RemoveAll(IsUnused);
                global.fallbackFontAssetTable = table;
            }

            return;

            bool IsUnused(FontAsset asset)
                => asset != null && asset.name.EndsWith(AssetNameSuffix, StringComparison.Ordinal)
                   && !families.Contains(asset.name.Substring(0, asset.name.Length - AssetNameSuffix.Length));
        }

        private static void SetHideFlags(FontAsset asset)
        {
            asset.hideFlags = HideFlags.HideAndDontSave;
            foreach (var atlas in asset.atlasTextures)
            {
                if (atlas != null)
                    atlas.hideFlags = HideFlags.HideAndDontSave;
            }

            if (asset.material != null)
                asset.material.hideFlags = HideFlags.HideAndDontSave;
        }

        private static TextSettings GetTextSettings() => DefaultTextSettings.Value?.GetValue(null) as TextSettings;

        private static FontAsset GetGlobalFallback() => EditorGUIUtility.Load(GlobalFallbackAssetPath) as FontAsset;

        #endregion


        #region Refreshing text

        private static void RefreshText()
        {
            ClearTextHandleCache();
            MarkElementsDirty();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            OnFallbacksChanged?.Invoke();
        }

        private static void ClearTextHandleCache()
        {
            try
            {
                EmptyTextHandleCache.Value?.Invoke(null, null);
            }
            catch
            {
                // ignored
            }
        }

        private static void MarkElementsDirty()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                var root = window.rootVisualElement;
                if (root?.panel != null)
                    root.Query<TextElement>().ForEach(textElement => textElement.MarkDirtyText());
            }
        }

        #endregion
    }
}
