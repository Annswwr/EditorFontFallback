using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EditorFontFallback.Editor
{
    /// <summary>
    /// Identifies the best system fonts to use for each missing Unicode block that the Unity Editor can't render.
    /// </summary>
    public static class EditorFontFallbackDiscovery
    {
        public readonly struct Result
        {
            public readonly string[] Fonts;
            public readonly int InstalledFontCount;
            public readonly int LoadedFontCount;
            public readonly int EditorCoveredBlockCount;

            public Result(string[] fonts, int installedFontCount, int loadedFontCount, int editorCoveredBlockCount)
            {
                Fonts = fonts;
                InstalledFontCount = installedFontCount;
                LoadedFontCount = loadedFontCount;
                EditorCoveredBlockCount = editorCoveredBlockCount;
            }
        }

        // Mainly covers the Basic Multilingual Plane
        private static readonly (int start, int endExclusive) ScanRange = (0x0080, 0x10000);

        // Ignore icons / dingbats / other incompatible fonts (appears to crash Unity if used as a fallback)
        private static readonly (int start, int endExclusive)[] ExcludedRanges =
        {
            (0xE000, 0xF900),   // Surrogates / Private Use Area
            (0xFFF0, 0x10000),  // Specials
        };

        private static readonly int[] BlockSampleOffsets = Enumerable.Range(0, 16).Select(i => i * 8).ToArray();

        // Fonts with pictograms in place of letters
        private static readonly string[] SymbolFontNames = { "Symbol", "Marlett", "MT Extra", "Bookshelf Symbol 7", "MS Reference Specialty" };
        private static readonly string[] SymbolFontWords = { "dings", "Dingbats", "MDL2", "Icons" };

        public static Result Scan(ICollection<string> coveredFamilies)  // Reads font files without loading them into Unity
        {
            var faces = Font.GetPathsToOSFonts()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(FontFile.Read)
                .Where(face => face.Style == "Regular")
                .ToList();
            var coveredByEditor = faces
                .Where(face => coveredFamilies.Contains(face.Family))
                .SelectMany(face => GetBlockHits(face).Keys)
                .ToHashSet();

            var bestPerBlock = new Dictionary<int, (int hits, int score, int size, string font)>();
            var loadedCount = 0;
            foreach (var face in faces)
            {
                // Skips color font faces (crashes Unity if used as fallback)
                if (face.IsColor || face.IsSymbol || IsSymbolFont(face.Family))
                    continue;

                loadedCount++;
                var blockHits = GetBlockHits(face);
                var score = GetScore(face.Family);
                var size = blockHits.Count;
                foreach (var (block, hits) in blockHits)
                {
                    if (coveredByEditor.Contains(block))
                        continue;

                    if (!bestPerBlock.TryGetValue(block, out var current)
                        || hits > current.hits
                        || (hits == current.hits && (score > current.score || (score == current.score && size < current.size))))
                    {
                        bestPerBlock[block] = (hits, score, size, face.Family);
                    }
                }
            }

            var fonts = bestPerBlock.Values.Select(best => best.font).Distinct().ToArray();
            return new Result(fonts, faces.Count, loadedCount, coveredByEditor.Count);
        }

        public static bool IsSymbolFont(string font)
            => SymbolFontNames.Contains(font, StringComparer.OrdinalIgnoreCase)
               || SymbolFontWords.Any(word => font.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);

        private static Dictionary<int, int> GetBlockHits(FontFile face)
        {
            var hits = new Dictionary<int, int>();
            for (var start = ScanRange.start; start < ScanRange.endExclusive; start += 128)
            {
                if (IsExcludedBlock(start))
                    continue;

                var count = BlockSampleOffsets.Count(offset => face.HasCharacter(start + offset));
                if (count > 0)
                    hits[start >> 7] = count;
            }

            return hits;
        }

        private static bool IsExcludedBlock(int blockStart)
            => ExcludedRanges.Any(range => blockStart >= range.start && blockStart < range.endExclusive);

        private static int GetScore(string font)
        {
            var score = 0;
            if (font.StartsWith("Noto Sans", StringComparison.OrdinalIgnoreCase))
                score += 3;
            else if (font.StartsWith("Noto", StringComparison.OrdinalIgnoreCase))
                score += 2;

            if (font.EndsWith(" UI", StringComparison.OrdinalIgnoreCase))
                score += 2;

            if (font.EndsWith(" Text", StringComparison.OrdinalIgnoreCase))
                score += 2;

            if (font.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase))
                score += 1;

            return score;
        }
    }
}
