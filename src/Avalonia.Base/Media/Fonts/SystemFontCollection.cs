using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    internal class SystemFontCollection : FontCollectionBase
    {
        private readonly IFontManagerImpl _platformImpl;

        public SystemFontCollection(IFontManagerImpl platformImpl)
        {
            _platformImpl = platformImpl ?? throw new ArgumentNullException(nameof(platformImpl));

            var familyNames = _platformImpl.GetInstalledFontFamilyNames().Where(x => !string.IsNullOrEmpty(x));

            foreach (var familyName in familyNames)
            {
                AddFontFamily(familyName);
            }
        }

        public override Uri Key => FontManager.SystemFontsKey;

        public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight,
            FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
        {
            var typeface = new Typeface(familyName, style, weight, stretch).Normalize(out familyName);
            var key = typeface.ToFontCollectionKey();

            // Find an exact match first
            if (TryGetGlyphTypeface(familyName, key, allowNearestMatch: false, out glyphTypeface))
            {
                return true;
            }

            //Check cache first to avoid unnecessary calls to the font manager
            if (_glyphTypefaceCache.TryGetValue(familyName, out var glyphTypefaces) && glyphTypefaces.TryGetValue(key, out glyphTypeface))
            {
                return glyphTypeface != null;
            }

            //Try to create the glyph typeface via system font manager
            if (!_platformImpl.TryCreateGlyphTypeface(familyName, style, weight, stretch, out var platformTypeface))
            {
                //Add null to cache to avoid future calls
                TryAddGlyphTypeface(familyName, key, null);

                return false;
            }

            var platformFamilyName = platformTypeface.FamilyName;

            // The font manager didn't return a perfect match either. Find the nearest match ourselves.
            if (key != platformTypeface.ToFontCollectionKey() &&
                TryGetGlyphTypeface(familyName, key, allowNearestMatch: true, out glyphTypeface))
            {
                platformTypeface.Dispose();
                return true;
            }

            var createdGlyphTypeface = GlyphTypeface.TryCreate(platformTypeface);

            if (createdGlyphTypeface is null ||
                !TryAddCreatedGlyphTypeface(createdGlyphTypeface, key, out glyphTypeface, out _))
            {
                return false;
            }

            TryAddGlyphTypeface(glyphTypeface);

            if (!string.Equals(platformFamilyName, glyphTypeface.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                TryAddGlyphTypeface(platformFamilyName, key, glyphTypeface);
            }

            if (!string.Equals(familyName, glyphTypeface.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                TryAddGlyphTypeface(familyName, key, glyphTypeface);
            }

            //Requested glyph typeface should be in cache now
            return TryGetGlyphTypeface(familyName, key, allowNearestMatch: false, out glyphTypeface);
        }

        public override bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        {
            return _platformImpl.TryGetFamilyTypefaces(familyName, out familyTypefaces);
        }

        public override bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch, string? familyName,
           CultureInfo? culture, out Typeface match)
        {
            // Delegate to the base algorithm. The platform call is exposed through
            // TryMatchCharacterFromPlatform and invoked at most once per (script-bucket, culture)
            // pair by the base implementation, after every cached candidate has been considered.
            return base.TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out match);
        }

        protected override bool TryMatchCharacterFromPlatform(
            int codepoint,
            FontCollectionKey key,
            string? familyName,
            CultureInfo? culture,
            [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
        {
            glyphTypeface = null;

            if (!_platformImpl.TryMatchCharacter(codepoint, key.Style, key.Weight, key.Stretch, familyName, culture, out var platformTypeface))
            {
                return false;
            }

            var platformFamilyName = platformTypeface.FamilyName;
            var platformKey = new FontCollectionKey(platformTypeface.Style, platformTypeface.Weight, platformTypeface.Stretch);

            // Check cache first to avoid creating a duplicate GlyphTypeface.
            if (_glyphTypefaceCache.TryGetValue(platformFamilyName, out var glyphTypefaces) &&
                glyphTypefaces.TryGetValue(platformKey, out var existing) &&
                existing != null)
            {
                platformTypeface.Dispose();
                glyphTypeface = existing;
                return true;
            }

            var createdGlyphTypeface = GlyphTypeface.TryCreate(platformTypeface);

            if (createdGlyphTypeface is null ||
                !TryAddCreatedGlyphTypeface(createdGlyphTypeface, platformKey, out glyphTypeface, out _))
            {
                return false;
            }

            if (!string.Equals(platformFamilyName, glyphTypeface.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                TryAddGlyphTypeface(platformFamilyName, platformKey, glyphTypeface);
            }

            return true;
        }
    }
}
