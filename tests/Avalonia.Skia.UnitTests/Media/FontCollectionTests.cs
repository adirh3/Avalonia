#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class FontCollectionTests
    {
        private const string NotoMono =
          "resm:Avalonia.Skia.UnitTests.Assets?assembly=Avalonia.Skia.UnitTests";

        [Win32Fact("Relies on some installed font family")]
        public void Should_Cache_Nearest_Match()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                var fontCollection = new TestSystemFontCollection(FontManager.Current.PlatformImpl);

                Assert.True(fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, FontWeight.ExtraBlack, FontStretch.Normal, out var glyphTypeface));

                Assert.True(fontCollection.GlyphTypefaceCache.TryGetValue("Arial", out var glyphTypefaces));

                Assert.Equal(2, glyphTypefaces.Count);

                Assert.True(glyphTypefaces.ContainsKey(new FontCollectionKey(FontStyle.Normal, FontWeight.Black, FontStretch.Normal)));

                fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, FontWeight.ExtraBlack, FontStretch.Normal, out var otherGlyphTypeface);

                Assert.Equal(glyphTypeface, otherGlyphTypeface);
            }
        }

        /// <summary>
        /// Verifies that the cache stores typefaces under the REQUESTED key, not just the actual key.
        /// This prevents memory leaks when the returned typeface's properties differ from requested.
        /// </summary>
        [Win32Fact("Relies on some installed font family")]
        public void Should_Cache_Under_Requested_Key()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                var fontCollection = new TestSystemFontCollection(FontManager.Current.PlatformImpl);

                // Request SemiBold - the actual font returned may have a different weight
                Assert.True(fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out var glyphTypeface));

                Assert.True(fontCollection.GlyphTypefaceCache.TryGetValue("Arial", out var glyphTypefaces));

                // The requested key should be in the cache
                var requestedKey = new FontCollectionKey(FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal);
                Assert.True(glyphTypefaces.ContainsKey(requestedKey), "Cache should contain the requested key");

                // Second request should return the same instance (no new typeface created)
                Assert.True(fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out var secondGlyphTypeface));
                Assert.Same(glyphTypeface, secondGlyphTypeface);
            }
        }

        /// <summary>
        /// Verifies case-insensitive font family name lookups use the same cached instance.
        /// </summary>
        [Win32Fact("Relies on some installed font family")]
        public void Should_Handle_Case_Insensitive_Family_Names()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                var fontCollection = new TestSystemFontCollection(FontManager.Current.PlatformImpl);

                Assert.True(fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var lower));
                Assert.True(fontCollection.TryGetGlyphTypeface("ARIAL", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var upper));
                Assert.True(fontCollection.TryGetGlyphTypeface("ArIaL", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var mixed));

                Assert.Same(lower, upper);
                Assert.Same(lower, mixed);
            }
        }

        /// <summary>
        /// Tests that repeated requests for the same font don't create new instances.
        /// This is the core memory leak prevention test.
        /// </summary>
        [Fact]
        public void Should_Not_Leak_When_Typeface_FamilyName_Differs_From_Requested()
        {
            var leakTestFontManager = new LeakTestFontManagerImpl(returnDifferentFamilyName: true);
            
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: leakTestFontManager)))
            {
                var fontCollection = new TestSystemFontCollection(leakTestFontManager);
                
                // First request
                Assert.True(fontCollection.TryGetGlyphTypeface("TestFont", FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out _));
                
                var createCountAfterFirst = leakTestFontManager.CreateCount;
                
                // Multiple subsequent requests - should NOT create new typefaces
                for (int i = 0; i < 10; i++)
                {
                    Assert.True(fontCollection.TryGetGlyphTypeface("TestFont", FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out _));
                }
                
                // No additional typefaces should have been created
                Assert.Equal(createCountAfterFirst, leakTestFontManager.CreateCount);
            }
        }

        /// <summary>
        /// A mock font manager that returns typefaces with different family names than requested.
        /// This simulates fonts like "Microsoft YaHei" that return "Microsoft YaHei UI".
        /// </summary>
        private class LeakTestFontManagerImpl : IFontManagerImpl
        {
            public int CreateCount { get; private set; }
            private readonly bool _returnDifferentFamilyName;
            
            public LeakTestFontManagerImpl(bool returnDifferentFamilyName = false)
            {
                _returnDifferentFamilyName = returnDifferentFamilyName;
            }
            
            public string GetDefaultFontFamilyName() => "TestFont";
            public string[] GetInstalledFontFamilyNames(bool checkForUpdates = false) => new[] { "TestFont" };
            
            public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, 
                FontStretch fontStretch, string? familyName, CultureInfo? culture, out Typeface typeface)
            {
                typeface = new Typeface("TestFont");
                return true;
            }
            
            public bool TryCreateGlyphTypeface(string familyName, FontStyle style, FontWeight weight,
                FontStretch stretch, [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
            {
                CreateCount++;
                var returnedFamilyName = _returnDifferentFamilyName ? familyName + " UI" : familyName;
                glyphTypeface = new FakeGlyphTypeface(returnedFamilyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
                return true;
            }
            
            public bool TryCreateGlyphTypeface(System.IO.Stream stream, FontSimulations fontSimulations, 
                [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
            {
                CreateCount++;
                glyphTypeface = new FakeGlyphTypeface("TestFont", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
                return true;
            }
        }
        
        private class FakeGlyphTypeface : IGlyphTypeface
        {
            public FakeGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch)
            {
                FamilyName = familyName;
                Style = style;
                Weight = weight;
                Stretch = stretch;
            }
            
            public string FamilyName { get; }
            public FontStyle Style { get; }
            public FontWeight Weight { get; }
            public FontStretch Stretch { get; }
            public int GlyphCount => 1;
            public FontMetrics Metrics => new FontMetrics { DesignEmHeight = 1000, Ascent = -800, Descent = 200 };
            public FontSimulations FontSimulations => FontSimulations.None;
            
            public ushort GetGlyph(uint codepoint) => 1;
            public bool TryGetGlyph(uint codepoint, out ushort glyph) { glyph = 1; return true; }
            public int GetGlyphAdvance(ushort glyph) => 500;
            public int[] GetGlyphAdvances(ReadOnlySpan<ushort> glyphs) => new int[glyphs.Length];
            public ushort[] GetGlyphs(ReadOnlySpan<uint> codepoints) => new ushort[codepoints.Length];
            public bool TryGetGlyphMetrics(ushort glyph, out GlyphMetrics metrics) { metrics = default; return true; }
            public bool TryGetTable(uint tag, out byte[] table) { table = Array.Empty<byte>(); return false; }
            public void Dispose() { }
        }

        /// <summary>
        /// This is THE leak test. Tests the full SystemFontCollection path.
        /// When requesting "Segoe UI Variable Text", the platform returns a typeface with
        /// FamilyName="Segoe UI Variable". Without the fix, this causes cache misses and leaks.
        /// </summary>
        [Win32Fact("Relies on real font system behavior")]
        public void SystemFontCollection_Must_Return_Same_Instance_For_Repeated_Requests()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                var fontCollection = new TestSystemFontCollection(FontManager.Current.PlatformImpl);
                
                // Use Segoe UI Variable Text - a variable font
                const string fontFamily = "Segoe UI Variable Text";
                
                // First request
                Assert.True(fontCollection.TryGetGlyphTypeface(fontFamily, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out var glyph1),
                    $"Font '{fontFamily}' not found");
                
                // Verify we're using the real implementation
                Assert.IsType<GlyphTypefaceImpl>(glyph1);
                
                // Second request - MUST return same instance
                Assert.True(fontCollection.TryGetGlyphTypeface(fontFamily, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out var glyph2));
                
                Assert.Same(glyph1, glyph2);
            }
        }

        /// <summary>
        /// Simulates the virtualized list leak scenario through SystemFontCollection.
        /// </summary>
        [Win32Fact("Relies on real font system behavior")]
        public void SystemFontCollection_Must_Return_Same_Instance_After_1000_Requests()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                var fontCollection = new TestSystemFontCollection(FontManager.Current.PlatformImpl);
                
                const string fontFamily = "Segoe UI Variable Text";
                
                // First request
                Assert.True(fontCollection.TryGetGlyphTypeface(fontFamily, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, out var firstGlyph),
                    $"Font '{fontFamily}' not found");
                
                // Verify we're using the real implementation
                Assert.IsType<GlyphTypefaceImpl>(firstGlyph);
                
                // 1000 requests - all must return same instance
                for (int i = 0; i < 1000; i++)
                {
                    Assert.True(fontCollection.TryGetGlyphTypeface(fontFamily, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, out var glyph));
                    Assert.Same(firstGlyph, glyph);
                }
            }
        }

        /// <summary>
        /// LEAK TEST using Segoe UI Variable Text.
        /// This test checks if repeated requests return the same cached instance.
        /// </summary>
        [Win32Fact("Uses Segoe UI Variable Text")]
        public void Synthetic_Bold_Must_Return_Same_Instance_To_Prevent_Leak()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                // First request
                Assert.True(FontManager.Current.TryGetGlyphTypeface(
                    new Typeface("Segoe UI Variable Text", FontStyle.Normal, FontWeight.Bold), out var glyph1));
                
                // Second request - MUST return same instance
                Assert.True(FontManager.Current.TryGetGlyphTypeface(
                    new Typeface("Segoe UI Variable Text", FontStyle.Normal, FontWeight.Bold), out var glyph2));
                
                // THIS IS THE LEAK TEST
                Assert.Same(glyph1, glyph2);
            }
        }

        /// <summary>
        /// Tests that requesting Bold for "Segoe UI Variable Text" returns a typeface
        /// that correctly reports Bold weight (via the family name suffix detection).
        /// </summary>
        [Win32Fact("Uses Segoe UI Variable")]
        public void Segoe_UI_Variable_Bold_Should_Report_Bold_Weight()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                // Request Bold for Segoe UI Variable Text (the named instance)
                Assert.True(FontManager.Current.TryGetGlyphTypeface(
                    new Typeface("Segoe UI Variable Text", FontStyle.Normal, FontWeight.Bold), out var glyphTypeface));
                
                var impl = (GlyphTypefaceImpl)glyphTypeface;
                
                // The typeface should report Bold weight
                Assert.Equal(FontWeight.Bold, glyphTypeface.Weight);
                
                // Should NOT use FontSimulations.Bold (the font has native Bold via variable axes)
                Assert.Equal(FontSimulations.None, glyphTypeface.FontSimulations);
            }
        }
        
        /// <summary>
        /// Tests different weights for Segoe UI Variable Text to verify weight detection works.
        /// </summary>
        [Win32Theory("Uses Segoe UI Variable Text")]
        [InlineData(FontWeight.Normal)]
        [InlineData(FontWeight.Bold)]
        [InlineData(FontWeight.SemiBold)]
        [InlineData(FontWeight.Light)]
        public void Segoe_UI_Variable_Text_Should_Report_Correct_Weight(FontWeight requestedWeight)
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                Assert.True(FontManager.Current.TryGetGlyphTypeface(
                    new Typeface("Segoe UI Variable Text", FontStyle.Normal, requestedWeight), out var glyphTypeface));
                
                var impl = (GlyphTypefaceImpl)glyphTypeface;
                
                // Log what we got for debugging
                var info = $"Requested={requestedWeight}, " +
                           $"FamilyName={glyphTypeface.FamilyName}, " +
                           $"TypographicFamilyName={impl.TypographicFamilyName}, " +
                           $"ActualWeight={glyphTypeface.Weight}, " +
                           $"FontSimulations={glyphTypeface.FontSimulations}";
                
                // The typeface should report the requested weight (or close to it)
                // Variable fonts should handle weights natively without FontSimulations
                Assert.True(glyphTypeface.FontSimulations == FontSimulations.None, 
                    $"Expected no FontSimulations for variable font. {info}");
            }
        }

        /// <summary>
        /// This test verifies that synthetic bold typefaces are properly cached.
        /// When we request Bold for a font that only has Regular, the system creates
        /// a synthetic bold. This must be cached under the requested key.
        /// </summary>
        [Win32Fact("Relies on real font system behavior")]
        public void Synthetic_Bold_Typeface_Should_Be_Cached_Correctly()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                var fontCollection = new TestSystemFontCollection(FontManager.Current.PlatformImpl);
                
                // Request Bold weight
                Assert.True(fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, out var firstTypeface));
                
                // Count how many entries are in the cache for Arial
                fontCollection.GlyphTypefaceCache.TryGetValue("Arial", out var typefacesAfterFirst);
                var countAfterFirst = typefacesAfterFirst?.Count ?? 0;
                
                // Request Bold again - should return same instance
                Assert.True(fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, out var secondTypeface));
                
                // Must be the same instance (cache hit)
                Assert.Same(firstTypeface, secondTypeface);
                
                // Cache count should not have increased
                fontCollection.GlyphTypefaceCache.TryGetValue("Arial", out var typefacesAfterSecond);
                var countAfterSecond = typefacesAfterSecond?.Count ?? 0;
                Assert.Equal(countAfterFirst, countAfterSecond);
            }
        }

        /// <summary>
        /// Comprehensive leak test: simulates virtualized list scenario where the same
        /// font+weight combination is requested many times during scrolling.
        /// </summary>
        [Win32Fact("Relies on real font system behavior")]
        public void Should_Not_Leak_GlyphTypefaces_During_Repeated_Requests()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                var fontCollection = new TestSystemFontCollection(FontManager.Current.PlatformImpl);
                
                // First request to populate the cache
                Assert.True(fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out var firstTypeface));
                
                fontCollection.GlyphTypefaceCache.TryGetValue("Arial", out var typefacesAfterFirst);
                var countAfterFirst = typefacesAfterFirst?.Count ?? 0;
                
                // Simulate 1000 requests (like scrolling a virtualized list)
                for (int i = 0; i < 1000; i++)
                {
                    Assert.True(fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out var typeface));
                    
                    // Every request should return the same cached instance
                    Assert.Same(firstTypeface, typeface);
                }
                
                // Cache should not have grown
                fontCollection.GlyphTypefaceCache.TryGetValue("Arial", out var typefacesAfterLoop);
                var countAfterLoop = typefacesAfterLoop?.Count ?? 0;
                Assert.Equal(countAfterFirst, countAfterLoop);
            }
        }

        /// <summary>
        /// Test that verifies when we request SemiBold and get back a typeface,
        /// we can look it up again with the same SemiBold weight.
        /// </summary>
        [Win32Fact("Verifies cache key consistency")]
        public void Requested_Weight_Should_Match_Returned_Typeface_For_Cache_Lookup()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new FontManagerImpl())))
            {
                var fontCollection = new TestSystemFontCollection(FontManager.Current.PlatformImpl);
                
                // Request SemiBold
                var requestedWeight = FontWeight.SemiBold;
                Assert.True(fontCollection.TryGetGlyphTypeface("Arial", FontStyle.Normal, requestedWeight, FontStretch.Normal, out var firstTypeface));
                
                // The typeface's Weight property value
                var actualWeight = firstTypeface.Weight;
                
                // Check that cache contains the REQUESTED key
                fontCollection.GlyphTypefaceCache.TryGetValue("Arial", out var glyphTypefaces);
                var requestedKey = new FontCollectionKey(FontStyle.Normal, requestedWeight, FontStretch.Normal);
                
                Assert.True(glyphTypefaces!.ContainsKey(requestedKey), 
                    $"Cache must contain requested key (weight={requestedWeight}). " +
                    $"Actual typeface weight={actualWeight}, FontSimulations={firstTypeface.FontSimulations}. " +
                    $"Cache keys: {string.Join(", ", glyphTypefaces.Keys.Select(k => $"({k.Style},{k.Weight},{k.Stretch})"))}");
            }
        }

        private class TestSystemFontCollection : SystemFontCollection
        {
            public TestSystemFontCollection(IFontManagerImpl platformImpl) : base(platformImpl)
            {
            }

            public IDictionary<string, ConcurrentDictionary<FontCollectionKey, IGlyphTypeface?>> GlyphTypefaceCache => _glyphTypefaceCache;
        }

        [Fact]
        public void Should_Use_Fallback()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new CustomFontManagerImpl())))
            {
                var source = new Uri(NotoMono, UriKind.Absolute);

                var fallback = new FontFallback { FontFamily = new FontFamily("Arial"), UnicodeRange = new UnicodeRange('A', 'A') };

                var fontCollection = new CustomizableFontCollection(source, source, new[] { fallback  });

                Assert.True(fontCollection.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, null, out var match));

                Assert.Equal("Arial", match.FontFamily.Name);
            }
        }

        [Fact]
        public void Should_Ignore_FontFamily()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: new CustomFontManagerImpl())))
            {
                var key = new Uri(NotoMono, UriKind.Absolute);

                var ignorable = new FontFamily(new Uri(NotoMono, UriKind.Absolute), "Noto Mono");

                var fontCollection = new CustomizableFontCollection(key, key, null, new[] { ignorable });

                var typeface = new Typeface(ignorable);

                var glyphTypeface = typeface.GlyphTypeface;

                Assert.False(fontCollection.TryCreateSyntheticGlyphTypeface(
                    typeface.GlyphTypeface,
                    FontStyle.Italic,
                    FontWeight.DemiBold,
                    FontStretch.Normal,
                    out var syntheticGlyphTypeface));
            }
        }

        private class CustomizableFontCollection : EmbeddedFontCollection
        {
            private readonly IReadOnlyList<FontFallback>? _fallbacks;
            private readonly IReadOnlyList<FontFamily>? _ignorables;

            public CustomizableFontCollection(Uri key, Uri source, IReadOnlyList<FontFallback>? fallbacks = null, IReadOnlyList<FontFamily>? ignorables = null) : base(key, source)
            {
                _fallbacks = fallbacks;
                _ignorables = ignorables;
            }

            public override bool TryMatchCharacter(
                int codepoint, 
                FontStyle style, 
                FontWeight weight, 
                FontStretch stretch, 
                string? familyName, 
                CultureInfo? culture, 
                out Typeface match)
            {
                if(_fallbacks is not null)
                {
                    foreach (var fallback in _fallbacks)
                    {
                        if (fallback.UnicodeRange.IsInRange(codepoint))
                        {
                            match = new Typeface(fallback.FontFamily, style, weight, stretch);

                            return true;
                        }
                    }
                }

                return base.TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out match);
            }

            public override bool TryCreateSyntheticGlyphTypeface(
                IGlyphTypeface glyphTypeface, 
                FontStyle style, 
                FontWeight weight,
                FontStretch stretch, 
                [NotNullWhen(true)] out IGlyphTypeface? syntheticGlyphTypeface)
            {
                syntheticGlyphTypeface = null;

                if(_ignorables is not null)
                {
                    foreach (var ignorable in _ignorables)
                    {
                        if (glyphTypeface.FamilyName == ignorable.Name || glyphTypeface is IGlyphTypeface2 glyphTypeface2 && glyphTypeface2.TypographicFamilyName == ignorable.Name)
                        {
                            return false;
                        }
                    }
                }

                return base.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);
            }
        }
    }
}
