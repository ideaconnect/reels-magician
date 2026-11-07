using System.IO.Compression;
using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;
using ReelsMagician.Core.Rendering;
using ReelsMagician.Core.Toolchain;
using ReelsMagician.Core.Transcription;

namespace ReelsMagician.SmokeTests;

public static class UnitChecks
{
    public static void Run()
    {
        TranscriptEditing();
        SubtitleParsing();
        SubtitleTrackSelection();
        WhisperJsonParsing();
        Paragraphs();
        CropMath();
        CaptionGeneration();
        CaptionsNeverOverlap();
        CaptionsStayLongEnough();
        TimingSequencing();
        DataFolderCleanup();
        DataFolderMigration();
        LlamaBuildSelection();
        ArchiveMerge();
        JsonRecovery();
        CaptionTimelineMapping();
    }

    private static void TranscriptEditing()
    {
        Check.Section("Transcript editing keeps word timings");

        var segment = new TranscriptSegment
        {
            Start = 10,
            End = 12,
            Text = "the quick brown fox",
            Words =
            [
                new(10.0, 10.4, "the"),
                new(10.4, 10.9, "quick"),
                new(10.9, 11.5, "brown"),
                new(11.5, 12.0, "fox"),
            ],
        };

        // A typo fix in the middle must not shift the words around it.
        segment.Text = "the quick brown dog";
        segment.RetimeWords();

        Check.Equal(4, segment.Words.Count, "word count follows the edited text");
        Check.Near(10.4, segment.Words[1].Start, 0.001, "untouched word keeps its start");
        Check.Near(10.9, segment.Words[2].Start, 0.001, "untouched word keeps its start");
        Check.Equal("dog", segment.Words[3].Text, "replaced word takes the new text");
        Check.True(segment.Words[3].Start >= segment.Words[2].End - 0.001,
            "replaced word stays after its predecessor");

        // An inserted word should be timed between its neighbours, not appended at the end.
        var inserted = new TranscriptSegment
        {
            Start = 0,
            End = 4,
            Text = "one two three",
            Words = [new(0, 1, "one"), new(1, 2, "two"), new(2, 3, "three")],
        };
        inserted.Text = "one two and three";
        inserted.RetimeWords();
        Check.Equal(4, inserted.Words.Count, "inserted word is added");
        Check.Equal("and", inserted.Words[2].Text, "inserted word lands in position");
        Check.True(inserted.Words[2].Start >= 2 - 0.001 && inserted.Words[2].End <= 2.001 + 1,
            "inserted word is timed between its neighbours");

        // Every word must stay inside the cue after clamping.
        segment.ClampWords();
        Check.True(segment.Words.All(w => w.Start >= segment.Start - 1e-6 && w.End <= segment.End + 1e-6),
            "all words stay inside the cue");
    }

    private static void SubtitleParsing()
    {
        Check.Section("Subtitle parsing");

        const string srt = """
            1
            00:00:01,000 --> 00:00:03,500
            Hello there, friend.

            2
            00:00:04,000 --> 00:00:06,000
            This is the second line.
            """;

        var fromSrt = SubtitleParser.Parse(srt);
        Check.Equal(2, fromSrt.Segments.Count, "SRT cue count");
        Check.Near(1.0, fromSrt.Segments[0].Start, 0.001, "SRT start time");
        Check.Near(3.5, fromSrt.Segments[0].End, 0.001, "SRT end time");
        Check.Equal("Hello there, friend.", fromSrt.Segments[0].Text, "SRT text");

        // YouTube auto-captions: inline word timings plus a rolling duplicate of the prior line.
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:03.000
            we are <00:00:01.500><c>going </c><00:00:02.100><c>to </c><00:00:02.400><c>build</c>

            00:00:03.000 --> 00:00:05.000
            we are going to build <00:00:03.500><c>something </c><00:00:04.100><c>great</c>
            """;

        var fromVtt = SubtitleParser.Parse(vtt);
        Check.True(fromVtt.Segments.Count >= 1, "VTT produced cues");
        Check.True(fromVtt.Segments[0].Words.Count >= 3, "VTT inline word timings were kept");
        Check.Near(1.5, fromVtt.Segments[0].Words.First(w => w.Text.StartsWith("going")).Start, 0.01,
            "inline word timestamp is used");
        Check.True(fromVtt.Segments.Count < 2 || !fromVtt.Segments[1].Text.StartsWith("we are going to build"),
            "rolling caption duplicate was trimmed");

        // The shape YouTube actually serves: each word-timed cue opens with a line holding a single
        // space, then repeats the previous line, and a 10 ms snapshot of the finished line sits
        // between cues. Kept as a joined array because a raw string would drop the lone spaces.
        var youTube = string.Join("\n",
        [
            "WEBVTT", "Kind: captions", "Language: en", "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            " ",
            "we<00:00:01.400><c> are</c><00:00:01.800><c> going</c><00:00:02.300><c> to</c>",
            "",
            "00:00:03.000 --> 00:00:03.010 align:start position:0%",
            "we are going to",
            " ",
            "",
            "00:00:03.010 --> 00:00:05.000 align:start position:0%",
            "we are going to",
            "build<00:00:03.500><c> something</c><00:00:04.200><c> great</c>",
            "",
            "00:00:05.000 --> 00:00:05.010 align:start position:0%",
            "build something great",
            " ",
            "",
            "00:00:05.010 --> 00:00:07.000 align:start position:0%",
            "build something great",
            "and<00:00:05.600><c> ship</c><00:00:06.200><c> it</c>",
            "",
            "00:00:07.000 --> 00:00:07.010 align:start position:0%",
            "and ship it",
            " ",
        ]);

        var fromYouTube = SubtitleParser.Parse(youTube);
        Check.Equal("we are going to|build something great|and ship it",
            string.Join("|", fromYouTube.Segments.Select(s => s.Text)), "each spoken line appears exactly once");
        Check.Equal(10, fromYouTube.WordCount, "no word is counted twice");
        Check.Near(1.4, fromYouTube.Segments[0].Words[1].Start, 0.01,
            "the first line keeps its word timings, though its cue opens with a blank-looking line");
        Check.Near(3.5, fromYouTube.Segments[1].Words[1].Start, 0.01,
            "a rolling cue keeps the timings of its new words");
    }

    private static void WhisperJsonParsing()
    {
        Check.Section("Whisper JSON parsing");

        // Shaped exactly like whisper.cpp --output-json-full, including the control tokens it
        // emits: [_BEG_] and the numbered [_TT_n] timestamp markers.
        const string json = """
        {
          "result": { "language": "en" },
          "transcription": [
            {
              "offsets": { "from": 0, "to": 3980 },
              "text": " Most people set enormous goals.",
              "tokens": [
                { "text": "[_BEG_]",   "offsets": { "from": 0,    "to": 0    }, "p": 0.92 },
                { "text": " Most",     "offsets": { "from": 110,  "to": 250  }, "p": 0.97 },
                { "text": " people",   "offsets": { "from": 250,  "to": 590  }, "p": 1.0  },
                { "text": " set",      "offsets": { "from": 660,  "to": 760  }, "p": 1.0  },
                { "text": " enorm",    "offsets": { "from": 880,  "to": 1100 }, "p": 1.0  },
                { "text": "ous",       "offsets": { "from": 1100, "to": 1330 }, "p": 1.0  },
                { "text": " goals.",   "offsets": { "from": 1330, "to": 1650 }, "p": 1.0  },
                { "text": "[_TT_199]", "offsets": { "from": 1650, "to": 1650 }, "p": 0.5  }
              ]
            }
          ]
        }
        """;

        var transcript = WhisperTranscriber.ParseWhisperJson(json);
        Check.Equal("en", transcript.Language, "language is read from the result block");
        Check.Equal(1, transcript.Segments.Count, "one cue parsed");

        var segment = transcript.Segments[0];
        Check.Near(0, segment.Start, 0.001, "cue start comes from the millisecond offsets");
        Check.Near(3.98, segment.End, 0.001, "cue end comes from the millisecond offsets");

        var words = segment.Words.Select(w => w.Text).ToList();
        Check.Equal(5, words.Count, "sub-word tokens merged into whole words");
        Check.Equal("enormous", words[3], "split token pieces are rejoined");
        Check.True(words.All(w => !w.Contains("[_")), "no control marker survives into a word");
        Check.True(words.All(w => w.Trim().Length == w.Length), "words carry no stray whitespace");
        Check.Near(0.11, segment.Words[0].Start, 0.001, "word timing comes from its own token");
        Check.Near(1.65, segment.Words[^1].End, 0.001, "the last word ends where its token does");

        // The whole point: these words become caption text, so a leak is visible on screen.
        var style = SubtitleStyle.Presets()[0];
        var ass = AssBuilder.Build(
            AssBuilder.BuildGroups(
                segment.Words.Select(w => new CaptionWord(w.Start, w.End, w.Text)), style), style);
        Check.True(!ass.Contains("[_"), "no control marker reaches the burned-in captions");
    }

    private static void SubtitleTrackSelection()
    {
        Check.Section("Subtitle track selection");

        var dir = Path.Combine(Path.GetTempPath(), "reelsmagician-subpick");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        // Exactly what YouTube offers for a Polish video: the real track, the original
        // auto-caption, and machine translations into and out of other languages.
        foreach (var tag in new[] { "en", "en-pl", "pl", "pl-orig", "pl-pl" })
            File.WriteAllText(Path.Combine(dir, $"source.{tag}.vtt"), "WEBVTT\n");

        var picked = SourceResolver.SelectSubtitleForTests(dir, "pl");
        Check.Equal("source.pl-orig.vtt", Path.GetFileName(picked ?? ""),
            "the original-language track wins");

        File.Delete(Path.Combine(dir, "source.pl-orig.vtt"));
        picked = SourceResolver.SelectSubtitleForTests(dir, "pl");
        Check.Equal("source.pl.vtt", Path.GetFileName(picked ?? ""),
            "the plain language track is next best");

        File.Delete(Path.Combine(dir, "source.pl.vtt"));
        picked = SourceResolver.SelectSubtitleForTests(dir, "pl");
        Check.Equal("source.pl-pl.vtt", Path.GetFileName(picked ?? ""),
            "a same-language variant beats a translation");

        // The bug this guards: burning "English from Polish" captions over Polish speech.
        Check.True(Path.GetFileName(picked ?? "") != "source.en-pl.vtt",
            "an auto-translated track is never preferred over the spoken language");

        picked = SourceResolver.SelectSubtitleForTests(dir, "en");
        Check.Equal("source.en.vtt", Path.GetFileName(picked ?? ""),
            "an English video picks the English track");

        Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        Check.True(SourceResolver.SelectSubtitleForTests(dir, "pl") is null,
            "no caption files means no selection");
        Directory.Delete(dir, recursive: true);

        Check.True(SourceResolver.LooksLikeUrl("https://youtu.be/ggOnoGOKxos"), "a youtu.be link is a URL");
        Check.True(!SourceResolver.LooksLikeUrl(@"C:\videos\clip.mp4"), "a Windows path is not a URL");
    }

    private static void Paragraphs()
    {
        Check.Section("Paragraph grouping");

        var transcript = new Transcript();
        // Twelve cues of 3s each, with a long pause after the fourth.
        for (var i = 0; i < 12; i++)
        {
            var start = i * 3.0 + (i >= 4 ? 2.0 : 0);
            transcript.Segments.Add(new TranscriptSegment
            {
                Start = start,
                End = start + 2.8,
                Text = i == 3 ? $"Sentence {i} ends here." : $"Clause number {i} continues",
            });
        }
        transcript.Normalize();

        var paragraphs = ParagraphBuilder.Build(transcript);
        Check.True(paragraphs.Count >= 2, $"grouped into multiple paragraphs (got {paragraphs.Count})");
        Check.True(paragraphs.All(p => p.Duration <= 31), "no paragraph exceeds the duration cap");
        Check.True(paragraphs.All(p => p.Text.Length > 0), "no empty paragraphs");
        Check.Equal(0, paragraphs[0].Index, "paragraphs are indexed from zero");
        Check.True(paragraphs.Zip(paragraphs.Skip(1)).All(pair => pair.Second.Start >= pair.First.Start),
            "paragraphs are in order");

        var rendered = ParagraphBuilder.Render(paragraphs);
        Check.Contains(rendered, "[0]", "rendered prompt text carries indices");
    }

    private static void CropMath()
    {
        Check.Section("Crop geometry and expressions");

        var (w, h) = CropPlanner.ComputeCropSize(3840, 2160, 1080, 1920);
        Check.Equal(1215, w, "4K landscape gives the widest exact 9:16 window");
        Check.Equal(2160, h, "4K landscape crop uses the full height");

        var (pw, ph) = CropPlanner.ComputeCropSize(1080, 1920, 1080, 1920);
        Check.Equal(1080, pw, "an already-vertical source is not cropped horizontally");
        Check.Equal(1920, ph, "an already-vertical source is not cropped vertically");

        // The window must be exactly 9:16 for every source size, or the final scale would have
        // to stretch it. Odd widths are acceptable: the crop is always followed by a scale.
        foreach (var (sw, sh) in new[]
                 {
                     (1920, 1080), (1280, 720), (2560, 1440), (3840, 2160), (4096, 2160),
                     (1440, 1080), (720, 1280), (640, 480), (3000, 3000), (1999, 1001),
                 })
        {
            var (cw, ch) = CropPlanner.ComputeCropSize(sw, sh, 1080, 1920);
            Check.True(cw * 1920 == ch * 1080, $"{sw}×{sh}: crop {cw}×{ch} is exactly 9:16");
            Check.True(cw <= sw && ch <= sh, $"{sw}×{sh}: crop fits inside the source");
            Check.True(cw + 9 > sw || ch + 16 > sh, $"{sw}×{sh}: crop is the largest such window");
        }

        // Anamorphic and rotated sources are planned in the geometry the viewer sees.
        var anamorphic = new MediaInfo("dv.mp4", 10, 1440, 1080, 25, true, "mpeg2video", "mp2", 0, 4, 3);
        Check.True(!anamorphic.HasSquarePixels, "a 4:3 sample aspect ratio is recognised as anamorphic");
        Check.Equal(1920, anamorphic.DisplayWidth, "1440×1080 at 4:3 displays 1920 wide");
        Check.Equal(1080, anamorphic.DisplayHeight, "the display height is the stored height");
        var rotated = anamorphic with { Rotation = 90 };
        Check.Equal(1080, rotated.DisplayWidth, "rotation swaps the display width");
        Check.Equal(1920, rotated.DisplayHeight, "rotation swaps the display height");
        var unknownSar = new MediaInfo("x.mp4", 10, 1920, 1080, 25, true, "h264", "aac", 0, 0, 1);
        Check.True(unknownSar.HasSquarePixels, "an unknown sample aspect ratio counts as square");

        var anamorphicChain = VerticalFilterChain.Build(anamorphic, null, 1080, 1920);
        Check.Equal("scale=w=1920:h=1080:flags=lanczos", anamorphicChain[0],
            "an anamorphic source is resampled to square pixels before anything else");
        Check.Contains(string.Join(',', anamorphicChain), "force_original_aspect_ratio=increase",
            "the final scale keeps the picture's proportions");

        // A subject walking left to right should produce a monotonically increasing crop path.
        var info = new MediaInfo("x.mp4", 60, 3840, 2160, 30, true, "h264", "aac", 0);
        var hits = new List<SubjectHit>();
        for (var i = 0; i < 40; i++)
            hits.Add(new SubjectHit(i / 3.0, 0.25 + i * 0.01, 0.5, 0.1, 0.15, 0.9, SubjectKind.Face));

        var path = CropPlanner.Plan(hits, info, 13.3, 1080, 1920, new FramingOptions());
        Check.True(path.Keyframes.Count >= 2, "a moving subject produces a moving crop");
        Check.True(path.Keyframes.Count <= 48, "keyframe count is capped");
        Check.Equal(SubjectKind.Face, path.DominantSubject, "the face is recognised as the subject");
        Check.True(path.Keyframes.All(k => k.X >= 0 && k.X <= 3840 - path.CropWidth + 0.001),
            "the crop window never leaves the frame");
        Check.True(path.Keyframes[^1].X > path.Keyframes[0].X, "the crop follows the subject rightwards");

        // The render chain may only crop and scale uniformly: every scale in it keeps the
        // incoming proportions and is followed by a centre crop.
        var squareChain = VerticalFilterChain.Build(info, path, 1080, 1920);
        Check.True(path.CropWidth * 1920 == path.CropHeight * 1080, "the planned crop is exactly 9:16");
        Check.True(squareChain[0].StartsWith("crop=", StringComparison.Ordinal),
            "a square-pixel source goes straight to the crop");
        Check.True(squareChain.Where(f => f.StartsWith("scale=", StringComparison.Ordinal))
                .All(f => f.Contains("force_original_aspect_ratio=increase", StringComparison.Ordinal)),
            "no scale in the chain can change the picture's proportions");
        Check.True(squareChain.IndexOf("crop=w=1080:h=1920") >
                   squareChain.FindIndex(f => f.Contains("force_original_aspect_ratio", StringComparison.Ordinal)),
            "a centre crop trims the scaled frame to the reel size");

        var expression = path.BuildExpression(horizontal: true);
        Check.Contains(expression, "if(lt(t,", "expression is time-driven");
        Check.True(!expression.Contains(','.ToString() + " "), "expression has no stray spacing");

        // A stationary subject must produce a locked, constant crop.
        var still = Enumerable.Range(0, 30)
            .Select(i => new SubjectHit(i / 3.0, 0.5, 0.5, 0.1, 0.15, 0.9, SubjectKind.Face))
            .ToList();
        var stillPath = CropPlanner.Plan(still, info, 10, 1080, 1920, new FramingOptions());
        Check.True(stillPath.IsStatic, "a stationary subject yields a locked crop");
        Check.True(double.TryParse(stillPath.BuildExpression(true),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _),
            "a locked crop emits a plain constant");

        // One rogue detection should not drag the camera.
        var jittery = Enumerable.Range(0, 30)
            .Select(i => new SubjectHit(i / 3.0, i == 15 ? 0.95 : 0.5, 0.5, 0.1, 0.15, 0.9, SubjectKind.Face))
            .ToList();
        var jitteryPath = CropPlanner.Plan(jittery, info, 10, 1080, 1920, new FramingOptions());
        Check.True(jitteryPath.MotionRange < 60, $"a single outlier is rejected (moved {jitteryPath.MotionRange:0} px)");
    }

    private static void CaptionGeneration()
    {
        Check.Section("Caption generation");

        var words = new List<CaptionWord>
        {
            new(0.0, 0.4, "This"),
            new(0.4, 0.8, "is"),
            new(0.8, 1.4, "a"),
            new(1.4, 2.0, "test."),
            new(2.0, 2.5, "Second"),
            new(2.5, 3.1, "line"),
            new(3.1, 3.6, "here."),
        };

        var style = SubtitleStyle.Presets().First(s => s.Mode == CaptionMode.WordHighlight);
        style.MaxWordsPerCaption = 4;

        var groups = AssBuilder.BuildGroups(words, style);
        Check.True(groups.Count >= 2, $"words split into caption lines (got {groups.Count})");
        Check.True(groups.All(g => g.Words.Count <= 4), "no line exceeds the word limit");
        Check.True(groups.All(g => g.End > g.Start), "every caption has a positive duration");

        var ass = AssBuilder.Build(groups, style);
        Check.Contains(ass, "PlayResX: 1080", "ASS targets the 1080-wide canvas");
        Check.Contains(ass, "PlayResY: 1920", "ASS targets the 1920-tall canvas");
        Check.Contains(ass, "[V4+ Styles]", "ASS declares a style block");
        Check.Contains(ass, "Style: Caption,", "ASS defines the caption style");

        // Word highlighting emits one event per word so the highlight lands on the syllable.
        var events = ass.Split('\n').Count(l => l.StartsWith("Dialogue:", StringComparison.Ordinal));
        Check.Equal(words.Count, events, "one dialogue event per word in highlight mode");
        // Vertical only: \fscx changes the glyph advance width, which widens the line and shifts
        // every other word sideways as the highlight sweeps along it.
        Check.Contains(ass, "\\fscy", "highlighted word is scaled vertically");
        Check.True(!RenderChecks.HighlightOverridesContain(ass, "\\fscx"),
            "the per-word highlight never scales horizontally");
        Check.Contains(ass, "{\\r}", "the override is reset after the highlighted word");
        Check.True(ass.ToUpperInvariant().Contains("THIS"), "uppercase styling is applied");

        // Plain mode collapses to one event per line.
        var plain = SubtitleStyle.Presets().First(s => s.Mode == CaptionMode.PlainText);
        var plainGroups = AssBuilder.BuildGroups(words, plain);
        var plainAss = AssBuilder.Build(plainGroups, plain);
        var plainEvents = plainAss.Split('\n').Count(l => l.StartsWith("Dialogue:", StringComparison.Ordinal));
        Check.Equal(plainGroups.Count, plainEvents, "one dialogue event per line in plain mode");

        // Braces in the transcript must not be read as ASS override tags.
        var risky = AssBuilder.Build(
            AssBuilder.BuildGroups([new CaptionWord(0, 1, "{drop}")], plain), plain);
        Check.Contains(risky, "\\{drop\\}", "braces in the text are escaped");

        // A still preview must draw exactly one line. Rendering the live events into a frame
        // would stack every already-spoken word's event and ghost the text.
        var still = AssBuilder.BuildStill(groups[0], 2, style);
        var stillEvents = still.Split('\n').Count(l => l.StartsWith("Dialogue:", StringComparison.Ordinal));
        Check.Equal(1, stillEvents, "a still preview emits exactly one dialogue event");
        Check.Contains(still, "{\\c&H", "the still marks the active word");
        Check.True(!still.Contains("\\t("), "the still is frozen, with no entrance animation");

        var plainStill = AssBuilder.BuildStill(groups[0], 2, plain);
        Check.True(!plainStill.Contains("{\\c&H"), "a plain still marks no word");

        Check.Equal(0, PreviewRenderer.ActiveWordIndex(groups[0], 0.1), "first word is active at the start");
        Check.Equal(1, PreviewRenderer.ActiveWordIndex(groups[0], 0.5), "the word spoken mid-line is the active one");
        Check.Equal(groups[0].Words.Count - 1,
            PreviewRenderer.ActiveWordIndex(groups[0], 999), "past the end, the last word stays marked");

        // In the gap between two words the preview marks the word just spoken, exactly as the
        // rendered events do, rather than jumping ahead to the one still to come.
        var gappy = AssBuilder.BuildGroups(
            [new CaptionWord(0.0, 0.3, "ONE"), new CaptionWord(0.9, 1.2, "TWO"), new CaptionWord(1.3, 1.6, "THREE")],
            style);
        Check.Equal(0, PreviewRenderer.ActiveWordIndex(gappy[0], 0.6),
            "between two words, the word just spoken stays marked");
        Check.Equal(1, PreviewRenderer.ActiveWordIndex(gappy[0], 0.9),
            "and the next word takes over exactly when it starts");

        Check.Equal("&H00FFFFFF", SubtitleStyle.ToAssColor("#FFFFFF"), "white converts to ASS BGR");
        Check.Equal("&H000000FF", SubtitleStyle.ToAssColor("#FF0000"), "red converts to ASS BGR");
        Check.Equal("&H80000000", SubtitleStyle.ToAssColor("#000000", 0.498), "opacity maps to the alpha byte");

        // Inline overrides need the 6-digit form; the 8-digit style form makes libass drop the
        // whole tag block, which silently kills the highlight.
        Check.Equal("&H3DD9FF&", SubtitleStyle.ToOverrideColor("#FFD93D"), "override colour is 6-digit BGR");
        Check.Contains(ass, "{\\c&H", "the highlight uses an inline colour override");
        Check.True(!ass.Contains("\\c&H00"), "no 8-digit colour leaks into an inline override");

        // \move carries its own position, so pairing it with \pos would be a conflict.
        var slide = SubtitleStyle.Presets().First(s => s.Animation == CaptionAnimation.SlideUp);
        var slideAss = AssBuilder.Build(AssBuilder.BuildGroups(words, slide), slide);
        Check.True(slideAss.Split('\n')
                .Where(l => l.Contains("\\move("))
                .All(l => !l.Contains("\\pos(")),
            "slide-up events never carry both \\move and \\pos");

        // Every non-first event in a pop caption should still scale its highlighted word.
        var popStyle = SubtitleStyle.Presets().First(s => s.Animation == CaptionAnimation.Pop);
        var popAss = AssBuilder.Build(AssBuilder.BuildGroups(words, popStyle), popStyle);
        Check.Contains(popAss, "\\t(0,90,", "pop captions animate on entry");
        Check.True(popAss.Contains($"\\fscy{(int)Math.Round(popStyle.HighlightScale * 100)}"),
            "pop captions still grow the highlighted word");
        Check.True(!RenderChecks.HighlightOverridesContain(popAss, "\\fscx"),
            "pop captions never scale a word horizontally");
    }

    /// <summary>
    /// Parses the Dialogue lines back out of an ASS document, so the checks below assert on what
    /// libass will actually be given rather than on intermediate objects.
    /// </summary>
    private static List<(double Start, double End, string Text)> ParseEvents(string ass)
    {
        var events = new List<(double, double, string)>();
        foreach (var line in ass.Split('\n'))
        {
            if (!line.StartsWith("Dialogue:", StringComparison.Ordinal)) continue;
            var fields = line.Split(',', 10);
            if (fields.Length < 10) continue;
            events.Add((
                SubtitleParser.ParseTime(fields[1]),
                SubtitleParser.ParseTime(fields[2]),
                System.Text.RegularExpressions.Regex.Replace(fields[9], @"\{[^}]*\}", "").Trim()));
        }
        return events;
    }

    /// <summary>
    /// The regression guard for the reported bug: captions overlapping on screen, where the next
    /// line appears while the previous is still up. Two events share a \pos, so any overlap in
    /// time draws both lines on top of each other.
    /// </summary>
    private static void CaptionsNeverOverlap()
    {
        Check.Section("Captions never overlap");

        // Every way real word timings go wrong, in one list.
        var cases = new (string Name, List<CaptionWord> Words)[]
        {
            ("identical start times", // Whisper does this routinely
                [new(0.00, 0.50, "one"), new(0.50, 0.50, "two"), new(0.50, 0.50, "three"),
                 new(0.50, 0.90, "four"), new(1.20, 1.60, "five"), new(1.60, 2.00, "six")]),

            ("a word overrunning the next",
                [new(0.0, 2.0, "long"), new(0.4, 0.8, "short"), new(0.5, 3.0, "overrun"),
                 new(0.6, 1.0, "next"), new(2.5, 3.0, "after"), new(3.0, 3.4, "last")]),

            ("very fast speech", // 20 words inside half a second
                [.. Enumerable.Range(0, 20).Select(i => new CaptionWord(i * 0.025, i * 0.025 + 0.02, $"w{i}"))]),

            ("all words at one instant",
                [.. Enumerable.Range(0, 8).Select(_ => new CaptionWord(1.0, 1.0, "same"))]),

            ("reversed input",
                [new(2.0, 2.4, "b"), new(1.0, 1.4, "a"), new(3.0, 3.4, "c"), new(0.5, 0.9, "z")]),

            ("normal speech",
                [new(0.0, 0.45, "This"), new(0.5, 0.9, "is"), new(1.0, 1.6, "a"),
                 new(1.7, 2.3, "normal"), new(2.4, 3.0, "line."), new(3.2, 3.8, "Second"),
                 new(3.9, 4.4, "sentence"), new(4.5, 5.1, "here.")]),
        };

        foreach (var style in SubtitleStyle.Presets())
        {
            foreach (var (name, words) in cases)
            {
                var groups = AssBuilder.BuildGroups(words, style);
                var events = ParseEvents(AssBuilder.Build(groups, style));
                var label = $"[{style.Name} / {name}]";

                // Lines must not collide with each other.
                var lineOverlap = groups.Zip(groups.Skip(1)).Count(p => p.Second.Start < p.First.End - 1e-6);
                Check.Equal(0, lineOverlap, $"{label} no caption line runs into the next");

                // Nor may the per-word events inside them.
                var eventOverlap = events.Zip(events.Skip(1)).Count(p => p.Second.Start < p.First.End - 1e-6);
                Check.Equal(0, eventOverlap, $"{label} no dialogue event overlaps the next");

                var unordered = events.Zip(events.Skip(1)).Count(p => p.Second.Start < p.First.Start - 1e-6);
                Check.Equal(0, unordered, $"{label} events are emitted in time order");

                Check.True(events.All(e => e.End >= e.Start - 1e-6), $"{label} no event ends before it starts");
                Check.True(events.Count > 0, $"{label} something was emitted");
            }
        }

        // The opposite failure mode: fixing overlap by shortening everything would make captions
        // blink out between lines. Through continuous speech the text must stay on screen.
        foreach (var style in SubtitleStyle.Presets())
        {
            var words = cases.Last().Words; // normal, continuously spoken
            var groups = AssBuilder.BuildGroups(words, style);
            var span = words[^1].End - words[0].Start;
            var covered = groups.Sum(g => g.End - g.Start);

            Check.True(covered >= span * 0.9,
                $"[{style.Name}] captions cover continuous speech ({covered / span:P0} of {span:0.0}s)");

            var biggestGap = groups.Zip(groups.Skip(1)).Select(p => p.Second.Start - p.First.End)
                .DefaultIfEmpty(0).Max();
            Check.True(biggestGap < 0.3,
                $"[{style.Name}] no visible blink between lines (largest gap {biggestGap:0.###}s)");
        }

        // Text must survive intact — the fix moves times, never words.
        var plain = SubtitleStyle.Presets().First(s => s.Mode == CaptionMode.PlainText);
        var source = cases.Last().Words;
        var rendered = string.Join(" ", AssBuilder.BuildGroups(source, plain).Select(g => g.Text));
        foreach (var w in source)
            Check.True(rendered.Contains(w.Text, StringComparison.OrdinalIgnoreCase),
                $"the word '{w.Text}' survived caption grouping");
    }

    /// <summary>
    /// The regression guard for "the subtitles disappear too fast": every line must stay on
    /// screen long enough to read, without buying that time by overlapping or desyncing.
    /// </summary>
    private static void CaptionsStayLongEnough()
    {
        Check.Section("Captions stay on screen long enough to read");

        var style = SubtitleStyle.Presets().First(s => s.Mode == CaptionMode.WordHighlight);

        // The measured failure: a fragment wedged between two lines that already abut, left with
        // four frames on screen.
        var starved = new List<CaptionWord>
        {
            new(14.35, 14.80, "TO"), new(14.85, 15.30, "JUZ"),
            new(15.35, 15.90, "POZOSTAWIAM"), new(15.95, 16.31, "W"),
            new(16.31, 16.38, "WASZEJ"), new(16.38, 16.45, "OCENIE."),
            new(16.45, 16.90, "CZASAMI"), new(16.95, 17.40, "COS"),
            new(17.45, 17.90, "TAM"), new(17.95, 18.18, "SIE"),
        };

        var groups = AssBuilder.BuildGroups(starved, style, reelEnd: 20);

        foreach (var g in groups)
        {
            var duration = g.End - g.Start;
            Check.True(duration >= Math.Min(style.MinCaptionSeconds, 20) - 0.01,
                $"'{g.Text[..Math.Min(24, g.Text.Length)]}' is on screen for {duration:0.00}s, not a flash");

            var cps = g.Text.Length / Math.Max(0.01, duration);
            Check.True(cps <= style.MaxReadingCharsPerSecond + 1,
                $"'{g.Text[..Math.Min(24, g.Text.Length)]}' reads at {cps:0} cps, within the limit");
        }

        // The fix must not have been bought by breaking the guarantees that came before it.
        Check.Equal(0, groups.Zip(groups.Skip(1)).Count(p => p.Second.Start < p.First.End - 1e-6),
            "lines still never overlap");
        Check.Equal(string.Join(" ", starved.Select(w => w.Text)),
            string.Join(" ", groups.SelectMany(g => g.Words).Select(w => w.Text)),
            "no word is lost or reordered by merging");

        // Every line must still begin exactly when its first word is spoken, or the highlight
        // would run ahead of the speech.
        Check.True(groups.All(g => Math.Abs(g.Start - g.Words[0].Start) < 1e-6),
            "each line still starts on its first word");

        var events = ParseEvents(AssBuilder.Build(groups, style));
        Check.Equal(0, events.Zip(events.Skip(1)).Count(p => p.Second.Start < p.First.End - 1e-6),
            "the rendered events still never overlap");

        // A gap must be either nothing at all or long enough to read as a pause. The range in
        // between is a flicker, and broadcast validators reject it outright.
        var spaced = new List<CaptionWord>
        {
            new(0.0, 0.4, "FIRST"), new(0.5, 0.9, "LINE"), new(1.0, 1.4, "OF"), new(1.5, 1.9, "TEXT"),
            // A 0.6s pause — too short to be a real beat, long enough to blink if left alone.
            new(2.5, 2.9, "AFTER"), new(3.0, 3.4, "SHORT"), new(3.5, 3.9, "PAUSE"), new(4.0, 4.4, "HERE"),
            // A 3s pause — a genuine beat that should stay empty.
            new(7.4, 7.8, "AFTER"), new(7.9, 8.3, "LONG"), new(8.4, 8.8, "PAUSE"), new(8.9, 9.3, "NOW"),
        };
        var spacedGroups = AssBuilder.BuildGroups(spaced, style, reelEnd: 11);
        var betweenLines = spacedGroups.Zip(spacedGroups.Skip(1))
            .Select(p => p.Second.Start - p.First.End).ToList();

        Check.True(betweenLines.All(g => g <= 1e-6 || g >= 0.8 - 1e-6),
            $"every gap is either closed or a real pause (got {string.Join(", ", betweenLines.Select(g => $"{g:0.00}s"))})");
        Check.True(betweenLines.Any(g => g >= 0.8),
            "a genuine pause is still left empty rather than papered over");

        // The closing line should use the rest of the reel rather than vanishing after its last word.
        var trailing = new List<CaptionWord>
        {
            new(0.0, 0.5, "FIRST"), new(0.6, 1.1, "LINE"), new(1.2, 1.7, "HERE"), new(1.8, 2.3, "NOW"),
            new(9.0, 9.3, "LAST"), new(9.35, 9.6, "WORDS"),
        };
        var withEnd = AssBuilder.BuildGroups(trailing, style, reelEnd: 12);
        Check.True(withEnd[^1].End >= 11.9,
            $"the closing line holds to the end of the reel (ends at {withEnd[^1].End:0.00}s of 12s)");

        var withoutEnd = AssBuilder.BuildGroups(trailing, style);
        Check.True(withoutEnd[^1].End < 11.0,
            "with no reel length given, the closing line just holds briefly as before");

        // Fast speech must not collapse into one enormous line.
        var rapid = Enumerable.Range(0, 40)
            .Select(i => new CaptionWord(i * 0.12, i * 0.12 + 0.10, $"word{i}")).ToList();
        var rapidGroups = AssBuilder.BuildGroups(rapid, style, reelEnd: 6);
        // Rescuing a line from below the readable floor may grow it past the ordinary cap,
        // but never without bound — a caption that wraps off the frame helps nobody.
        Check.True(rapidGroups.All(g => g.Words.Count <= style.MaxWordsPerCaption * 3),
            "even under fast speech a line stays boundedly short of a wall of text");
        Check.True(rapidGroups.Count > 1, "fast speech still produces multiple lines");
        Check.Equal(0, rapidGroups.Zip(rapidGroups.Skip(1)).Count(p => p.Second.Start < p.First.End - 1e-6),
            "fast speech lines do not overlap");

        // A single short clip has nothing to merge with; it must still not crash or overlap.
        var tiny = AssBuilder.BuildGroups([new CaptionWord(0, 0.2, "HI")], style, reelEnd: 3);
        Check.Equal(1, tiny.Count, "a one-word reel yields one line");
        Check.True(tiny[0].End >= 2.9, "and that line holds to the end of the reel");

        // A starved fragment must join the neighbour on its own side of a pause. Folding it into
        // the line before the pause would put its words on screen during the silence and hide
        // the beat the speaker left.
        var beat = new List<CaptionWord>
        {
            new(0.0, 0.4, "BEFORE"), new(0.5, 0.9, "THE"), new(1.0, 1.4, "PAUSE"), new(1.5, 1.9, "ENDS."),
            // 1.5s of silence, then a one-word line that cannot stand alone...
            new(3.4, 3.6, "SO"),
            // ...because the next line follows within a line break's width of it.
            new(4.33, 4.7, "WHAT"), new(4.75, 5.1, "COMES"), new(5.15, 5.5, "AFTER"), new(5.55, 5.9, "IT"),
        };
        var beatGroups = AssBuilder.BuildGroups(beat, style, reelEnd: 7);
        var so = beatGroups.First(g => g.Words.Any(w => w.Text == "SO"));
        Check.True(so.Words[0].Text == "SO",
            $"a fragment after a pause joins the line that follows it, not the one before ('{so.Text}')");
        Check.True(beatGroups.All(g => g.Words.Zip(g.Words.Skip(1))
                .All(p => p.Second.Start - p.First.End < 0.8)),
            "no line spans the pause");
        Check.True(beatGroups[0].End <= 2.5 + 1e-6,
            $"the line before the pause does not hold through it (ends at {beatGroups[0].End:0.00}s)");
        Check.True(so.End - so.Start >= style.MinCaptionSeconds - 1e-6,
            "and the fragment still gets its reading time");

        // An isolated short word between two real beats stands alone and simply stays up for its
        // minimum time; it must not be folded across a pause just to get that time.
        var isolated = new List<CaptionWord>
        {
            new(0.0, 0.4, "BEFORE"), new(0.5, 0.9, "THE"), new(1.0, 1.4, "PAUSE"), new(1.5, 1.9, "ENDS."),
            new(3.4, 3.6, "OK."),
            new(5.4, 5.8, "THEN"), new(5.9, 6.3, "ON"), new(6.4, 6.8, "WE"), new(6.9, 7.3, "GO"),
        };
        var isolatedGroups = AssBuilder.BuildGroups(isolated, style, reelEnd: 9);
        var ok = isolatedGroups.First(g => g.Words.Any(w => w.Text == "OK."));
        Check.Equal(1, ok.Words.Count, $"a word between two beats stays on its own line ('{ok.Text}')");
        Check.True(Math.Abs(ok.Start - 3.4) < 1e-6, "it appears exactly when it is spoken");
        Check.True(ok.End - ok.Start >= style.MinCaptionSeconds - 1e-6,
            $"and stays up for its minimum time ({ok.End - ok.Start:0.00}s)");

        // When the only neighbour is across the pause, reaching over it still beats a flash.
        var stranded = new List<CaptionWord>
        {
            new(0.0, 0.4, "ONE"), new(0.5, 0.9, "TWO"),
            new(2.5, 2.7, "SO"),
        };
        var strandedGroups = AssBuilder.BuildGroups(stranded, style, reelEnd: 3.0);
        Check.Equal(1, strandedGroups.Count, "a fragment with nowhere else to go still folds across a pause");
        Check.True(strandedGroups[0].End - strandedGroups[0].Start >= AssBuilder.HardFloorSeconds,
            "so that it does not flash past");
    }

    private static void TimingSequencing()
    {
        Check.Section("Timing sequencer");

        // Overlapping input becomes ordered and non-overlapping inside the window.
        var spans = new List<(double Start, double End)>
        {
            (0.0, 2.0), (0.5, 0.6), (0.5, 3.0), (0.5, 0.7), (2.5, 2.6),
        };
        TimingSequencer.Sequence(spans, 0, 3);
        Check.True(TimingSequencer.IsSequenced(spans), "overlapping spans are sequenced");
        Check.True(spans.All(s => s.Start >= -1e-6 && s.End <= 3 + 1e-6), "spans stay inside the window");
        Check.True(spans.All(s => s.End - s.Start >= TimingSequencer.MinSpanSeconds - 1e-6),
            "every span meets the minimum duration");

        // More spans than the window can hold: share it out rather than overlapping.
        var crowded = Enumerable.Range(0, 40).Select(_ => (Start: 1.0, End: 1.0)).ToList();
        TimingSequencer.Sequence(crowded, 1.0, 1.5);
        Check.True(TimingSequencer.IsSequenced(crowded), "a crowded window still yields no overlap");
        Check.True(crowded[^1].End <= 1.5 + 1e-6, "a crowded window is not exceeded");

        // Legal input is left alone, so real pauses survive for caption line breaking.
        var clean = new List<(double Start, double End)> { (0.0, 0.4), (1.5, 1.9), (3.0, 3.4) };
        TimingSequencer.Sequence(clean, 0, 4);
        Check.Near(1.5, clean[1].Start, 1e-6, "a well-spaced span keeps its start");
        Check.Near(0.4, clean[0].End, 1e-6, "a well-spaced span keeps its end, preserving the pause");

        Check.True(TimingSequencer.IsSequenced(new List<(double, double)>()), "an empty run is trivially valid");

        // A cue whose words overrun its end must not pile them all onto the boundary.
        var segment = new TranscriptSegment
        {
            Start = 10,
            End = 11,
            Text = "a b c d",
            Words = [new(10.0, 10.4, "a"), new(10.3, 12.0, "b"), new(11.5, 12.5, "c"), new(11.9, 13.0, "d")],
        };
        segment.ClampWords();
        var got = segment.Words.Select(w => (w.Start, w.End)).ToList();
        Check.True(TimingSequencer.IsSequenced(got), "words overrunning a cue are sequenced, not collapsed");
        Check.True(segment.Words.All(w => w.Start >= 10 - 1e-6 && w.End <= 11 + 1e-6),
            "clamped words stay inside their cue");
        Check.Equal(4, segment.Words.Select(w => w.Start).Distinct().Count(),
            "clamped words keep distinct start times");

        // Whisper really does emit cues whose start and end are identical, immediately followed by
        // a cue starting at the same instant. Padding the first to a usable length then collides
        // with the second; trimming it back collapses it to nothing. Both must be avoided.
        var degenerate = new Transcript
        {
            Segments =
            [
                new() { Start = 74.00, End = 74.46, Text = "Tutaj", Words = [new(74.00, 74.46, "Tutaj")] },
                new() { Start = 74.46, End = 74.46, Text = "już", Words = [new(74.46, 74.46, "już")] },
                new() { Start = 74.46, End = 75.20, Text = "Yoga", Words = [new(74.46, 75.20, "Yoga")] },
                new() { Start = 75.20, End = 75.20, Text = "Slim", Words = [new(75.20, 75.20, "Slim")] },
                new() { Start = 75.20, End = 76.00, Text = "7x", Words = [new(75.20, 76.00, "7x")] },
            ],
        };
        var wordsBefore = string.Join(" ", degenerate.Segments.Select(s => s.Text));
        degenerate.Normalize();

        Check.True(degenerate.Segments.All(s => s.End > s.Start),
            "no cue is left with zero duration");
        Check.True(degenerate.Segments.Zip(degenerate.Segments.Skip(1))
                .All(p => p.Second.Start >= p.First.End - 1e-6),
            "cues do not overlap after the degenerate ones are fixed");

        // A cue Whisper gave no time at all is folded into its neighbour rather than being kept
        // as a caption that flashes for a couple of frames. The words must all survive, in order —
        // which cue carries them is an implementation detail.
        Check.Equal(wordsBefore, string.Join(" ", degenerate.Segments.Select(s => s.Text)),
            "no word is lost or reordered when timeless cues are folded");
        Check.True(degenerate.Segments.Count < 5,
            $"the timeless cues were folded away ({degenerate.Segments.Count} cues remain of 5)");
        Check.True(degenerate.Segments.All(s => s.Duration >= 0.1 - 1e-6),
            "every surviving cue has a usable duration");
        Check.True(degenerate.Segments[0].Start >= 74.00 - 1e-6 && degenerate.Segments[0].Start < 74.2,
            "a well-formed cue keeps its original start");
        var degenerateWords = degenerate.AllWords().Select(w => (w.Start, w.End)).ToList();
        Check.True(TimingSequencer.IsSequenced(degenerateWords),
            "words from degenerate cues are still ordered and non-overlapping");

        // Normalize must clamp words against final cue bounds, not bounds it later moves.
        var transcript = new Transcript
        {
            Segments =
            [
                new() { Start = 0, End = 5, Text = "first", Words = [new(0, 4.8, "first")] },
                new() { Start = 2, End = 6, Text = "second", Words = [new(2, 5.5, "second")] },
            ],
        };
        transcript.Normalize();
        Check.True(transcript.Segments[0].Words.All(w => w.End <= transcript.Segments[0].End + 1e-6),
            "a shrunk cue has its words re-clamped");
        var all = transcript.AllWords().Select(w => (w.Start, w.End)).ToList();
        Check.True(TimingSequencer.IsSequenced(all), "words across cues do not overlap");
    }

    /// <summary>
    /// The cleanup button deletes gigabytes on a click, so what it will and — more importantly —
    /// will not touch is worth pinning down.
    /// </summary>
    private static void DataFolderCleanup()
    {
        Check.Section("Data folder cleanup");

        var root = Path.Combine(Path.GetTempPath(), "reelsmagician-cleanup");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

        var paths = new ToolPaths(root);
        paths.EnsureCreated();

        void Seed(string dir, string name, int bytes)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
        }

        Seed(paths.Models, "big-model.bin", 4096);
        Seed(Path.Combine(paths.Tools, "ffmpeg", "bin"), "ffmpeg.exe", 2048);
        Seed(paths.Cache, "speech16k.wav", 1024);
        Seed(paths.Downloads, "leftover.zip", 512);
        Seed(paths.Logs, "today.log", 128);
        File.WriteAllText(paths.SettingsFile, "{}");

        var cleaner = new DataFolderCleaner(paths, NullProgressSink.Instance);
        var survey = cleaner.Survey();

        Check.Equal(5, survey.Count, "every removable category is listed");
        Check.True(survey.All(i => i.Exists), "each seeded category is found");

        var models = survey.First(i => i.Key == DataFolderCleaner.ModelsKey);
        Check.Equal(4096L, models.Bytes, "a category's size is measured from disk");
        Check.True(!models.SelectedByDefault, "models are not ticked by default — they cost most to replace");

        var working = survey.First(i => i.Key == DataFolderCleaner.WorkingKey);
        Check.True(working.SelectedByDefault, "working files are ticked by default, being safe to clear");
        Check.Equal(1024L + 512L, working.Bytes, "working files span the cache and downloads folders");

        Check.True(cleaner.TotalBytes() >= 4096 + 2048 + 1024 + 512 + 128,
            "the total covers every category");

        // Clearing working files must not disturb the expensive downloads.
        var cleared = cleaner.Remove([DataFolderCleaner.WorkingKey]);
        Check.True(cleared.Complete, "clearing working files succeeds");
        Check.Equal(1024L + 512L, cleared.BytesFreed, "the freed total matches what was there");
        Check.True(File.Exists(Path.Combine(paths.Models, "big-model.bin")),
            "clearing working files leaves the models alone");
        Check.True(Directory.Exists(paths.Cache), "the folder skeleton is recreated afterwards");

        // Selecting nothing must delete nothing.
        var nothing = cleaner.Remove([]);
        Check.Equal(0L, nothing.BytesFreed, "removing no categories frees nothing");
        Check.True(File.Exists(Path.Combine(paths.Models, "big-model.bin")), "and deletes nothing");

        // The guard that matters: never step outside the data folder.
        Check.True(!cleaner.IsInsideDataFolder(Path.GetTempPath()),
            "a path outside the data folder is refused");
        Check.True(!cleaner.IsInsideDataFolder(root),
            "the data folder root itself is refused — this clears parts, never the whole tree");
        Check.True(!cleaner.IsInsideDataFolder(Path.Combine(root, "..", "somewhere-else")),
            "a traversal out of the folder is refused");
        Check.True(!cleaner.IsInsideDataFolder(OperatingSystem.IsWindows() ? @"C:\Windows\System32" : "/usr/bin"),
            "an unrelated system path is refused");
        Check.True(cleaner.IsInsideDataFolder(paths.Models), "a real category inside the folder is allowed");
        Check.True(cleaner.IsInsideDataFolder(Path.Combine(paths.Cache, "deep", "file.bin")),
            "a nested path inside the folder is allowed");

        // A neighbouring folder with the same prefix must not be mistaken for a child.
        Check.True(!cleaner.IsInsideDataFolder(root + "-other"),
            "a sibling folder sharing the name prefix is refused");

        var everything = cleaner.Remove(
            [DataFolderCleaner.ModelsKey, DataFolderCleaner.ToolsKey, DataFolderCleaner.LogsKey,
             DataFolderCleaner.SettingsKey]);
        Check.True(everything.BytesFreed >= 4096 + 2048 + 128, "removing the rest frees the rest");
        Check.True(!File.Exists(paths.SettingsFile), "settings are removed when explicitly chosen");
        Check.True(Directory.Exists(paths.Root), "the data folder itself survives");

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// Renaming the app must not cost an existing install the gigabytes it has already downloaded.
    /// </summary>
    private static void DataFolderMigration()
    {
        Check.Section("Data folder carried over from the old name");

        var parent = Path.Combine(Path.GetTempPath(), "reelsmagician-migration");
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);

        var legacy = new ToolPaths(Path.Combine(parent, "Selfroller"));
        Directory.CreateDirectory(legacy.Models);
        File.WriteAllBytes(Path.Combine(legacy.Models, "big-model.bin"), new byte[4096]);
        Directory.CreateDirectory(legacy.WhisperDir);
        File.WriteAllText(Path.Combine(legacy.WhisperDir, "selfroller-backend.txt"), "whisper-blas-bin-x64.zip");

        // On Windows a file held open blocks the move, and the old folder must then be used where
        // it is. Linux renames a folder whatever is open inside it, so there is nothing to block.
        if (OperatingSystem.IsWindows())
        {
            using (File.Open(Path.Combine(legacy.Models, "big-model.bin"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check.Equal(legacy.Root, ToolPaths.ResolveRoot(parent), "a locked old folder is used in place");
            }
            Check.True(!Directory.Exists(Path.Combine(parent, ToolPaths.FolderName)), "and nothing is half-moved");
            Check.True(File.Exists(Path.Combine(legacy.WhisperDir, ToolPaths.BackendMarkerName)),
                "its backend marker still takes the new name");
        }

        var root = ToolPaths.ResolveRoot(parent);
        var paths = new ToolPaths(root);
        Check.Equal(Path.Combine(parent, ToolPaths.FolderName), root, "once free, the old folder is moved over");
        Check.True(!Directory.Exists(legacy.Root), "it is moved, not copied");
        Check.True(File.Exists(Path.Combine(paths.Models, "big-model.bin")), "downloaded models come along");
        Check.Equal("whisper-blas-bin-x64.zip",
            File.ReadAllText(Path.Combine(paths.WhisperDir, ToolPaths.BackendMarkerName)),
            "the backend marker comes along too");

        // Once the new folder exists, a leftover old one is never merged in or touched.
        Directory.CreateDirectory(legacy.Root);
        Check.Equal(root, ToolPaths.ResolveRoot(parent), "an existing new folder wins");
        Check.True(Directory.Exists(legacy.Root), "and a leftover old one is left alone");

        Directory.Delete(parent, recursive: true);
    }

    /// <summary>
    /// Which llama.cpp build each GPU gets. A wrong pick is silent: a CUDA build the card or
    /// driver cannot run still starts, then does everything on the CPU.
    /// </summary>
    private static void LlamaBuildSelection()
    {
        Check.Section("llama.cpp build selection");

        // The Windows assets of a real release, in the order GitHub lists them.
        var release = new GhRelease
        {
            TagName = "b12032",
            Assets = new[]
            {
                "cudart-llama-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-arm64.zip",
                "cudart-llama-bin-win-cuda-13.4-x64.zip", "llama-b12032-bin-win-cpu-arm64.zip",
                "llama-b12032-bin-win-cpu-x64.zip", "llama-b12032-bin-win-cuda-12.4-x64.zip",
                "llama-b12032-bin-win-cuda-13.4-arm64.zip", "llama-b12032-bin-win-cuda-13.4-x64.zip",
                "llama-b12032-bin-win-opencl-adreno-arm64.zip", "llama-b12032-bin-win-openvino-2026.4.1-x64.zip",
                "llama-b12032-bin-win-rocm-10.0-x64.zip", "llama-b12032-bin-win-sycl-x64.zip",
                "llama-b12032-bin-win-vulkan-arm64.zip", "llama-b12032-bin-win-vulkan-x64.zip",
            }.Select(n => new GhAsset { Name = n }).ToList(),
        };

        string? Pick(CudaSupport? cuda, bool vulkan = true) =>
            GitHubReleases.SelectAsset([release],
                ToolBuilds.LlamaRules(new GpuCapabilities([], vulkan, vulkan ? 1 : 0, cuda), TargetOs.Windows))?.Asset.Name;

        CudaSupport Nvidia(string driver, string compute) => new(Version.Parse(driver), Version.Parse(compute));

        Check.Equal("llama-b12032-bin-win-cuda-13.4-x64.zip", Pick(Nvidia("13.4", "8.9")),
            "a current RTX card and driver get CUDA 13");
        Check.Equal("llama-b12032-bin-win-cuda-13.4-x64.zip", Pick(Nvidia("13.6", "12.0")),
            "a newer driver still runs the CUDA 13 build");
        Check.Equal("llama-b12032-bin-win-cuda-12.4-x64.zip", Pick(Nvidia("13.0", "8.9")),
            "a driver older than the CUDA 13 build gets CUDA 12");
        Check.Equal("llama-b12032-bin-win-cuda-12.4-x64.zip", Pick(Nvidia("13.4", "6.1")),
            "a Pascal card, which CUDA 13 dropped, gets CUDA 12");
        Check.Equal("llama-b12032-bin-win-vulkan-x64.zip", Pick(Nvidia("12.2", "8.6")),
            "a driver too old for every CUDA build gets Vulkan");
        Check.Equal("llama-b12032-bin-win-vulkan-x64.zip", Pick(Nvidia("13.4", "3.5")),
            "a card too old for every CUDA build gets Vulkan");
        Check.Equal("llama-b12032-bin-win-vulkan-x64.zip", Pick(null), "AMD and Intel get Vulkan");
        Check.Equal("llama-b12032-bin-win-cpu-x64.zip", Pick(null, vulkan: false),
            "no GPU driver at all gets the CPU build");

        Check.True(ToolBuilds.LlamaCudaVersion("cudart-llama-bin-win-cuda-13.4-x64.zip") is null,
            "a runtime archive is never taken for the build itself");
        Check.True(ToolBuilds.LlamaCudaVersion("llama-b12032-bin-win-cuda-13.4-arm64.zip") is null,
            "nor is an ARM build");
        var build = ToolBuilds.LlamaCudaVersion("llama-b12032-bin-win-cuda-13.4-x64.zip");
        Check.True(build is not null &&
                   release.Assets.Any(a => a.Name == ToolBuilds.LlamaCudaRuntimeAsset(build)),
            "the build's runtime archive is found in the same release");

        Check.True(ToolBuilds.ExpectsLlamaCuda(new GpuCapabilities([], true, 1, Nvidia("13.0", "8.9")), TargetOs.Windows),
            "the setup screen expects the larger CUDA download on a CUDA-capable card");
        Check.True(!ToolBuilds.ExpectsLlamaCuda(new GpuCapabilities([], true, 1, Nvidia("11.8", "8.9")), TargetOs.Windows),
            "and not on a driver too old for it");
    }

    /// <summary>The CUDA runtime is unpacked over llama.cpp, so it must add to the folder, not replace it.</summary>
    private static void ArchiveMerge()
    {
        Check.Section("Archive extraction");

        var root = Path.Combine(Path.GetTempPath(), "reelsmagician-extract");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        string Zip(string name, params string[] entries)
        {
            var path = Path.Combine(root, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var entry in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write(entry);
            return path;
        }

        var target = Path.Combine(root, "llama");
        ArchiveExtractor.Extract(Zip("build.zip", "llama-server.exe", "ggml-cuda.dll"),
            target, NullProgressSink.Instance, "build");
        ArchiveExtractor.Extract(Zip("runtime.zip", "cudart64_13.dll", "cublas64_13.dll"),
            target, NullProgressSink.Instance, "runtime", merge: true);

        Check.True(File.Exists(Path.Combine(target, "llama-server.exe")), "merging keeps what was there");
        Check.True(File.Exists(Path.Combine(target, "cudart64_13.dll")), "and lays the runtime next to it");
        Check.True(!Directory.Exists(target + ".staging"), "no staging folder is left behind");

        ArchiveExtractor.Extract(Zip("next.zip", "llama-server.exe"), target, NullProgressSink.Instance, "build");
        Check.True(!File.Exists(Path.Combine(target, "cudart64_13.dll")), "a plain extract still replaces the folder");

        Directory.Delete(root, recursive: true);
    }

    private static void JsonRecovery()
    {
        Check.Section("Model output parsing");

        Check.Equal("{\"a\":1}", QwenAnalyzer.ExtractJson("{\"a\":1}"), "plain JSON passes through");
        Check.Equal("{\"a\":1}", QwenAnalyzer.ExtractJson("<think>hmm</think>{\"a\":1}"), "thinking block is stripped");
        Check.Equal("{\"a\":1}", QwenAnalyzer.ExtractJson("```json\n{\"a\":1}\n```"), "code fence is stripped");
        Check.Equal("{\"a\":1}", QwenAnalyzer.ExtractJson("Sure! {\"a\":1} Hope that helps."), "prose is stripped");
        Check.Equal("{}", QwenAnalyzer.ExtractJson(""), "empty output degrades to an empty object");
    }

    private static void CaptionTimelineMapping()
    {
        Check.Section("Caption mapping onto the reel timeline");

        var transcript = new Transcript();
        for (var i = 0; i < 60; i++)
            transcript.Segments.Add(new TranscriptSegment
            {
                Start = i,
                End = i + 0.9,
                Text = $"word{i}",
                Words = [new(i, i + 0.9, $"word{i}")],
            });

        // Two non-adjacent clips: 10-20s and 40-50s.
        var project = new ReelsMagician.Core.Projects.ReelProject
        {
            Transcript = transcript,
            RenderOptions = new RenderOptions(CrossfadeSeconds: 0.1),
            Style = SubtitleStyle.Presets()[0],
            RenderClips =
            [
                new(new ClipRange(10, 20, 0, 0, ""), new CropPath { CropWidth = 1080, CropHeight = 1920 }),
                new(new ClipRange(40, 50, 1, 1, ""), new CropPath { CropWidth = 1080, CropHeight = 1920 }),
            ],
        };

        var captions = ReelsMagician.Core.Projects.ReelPipeline.BuildCaptions(project);
        var all = captions.SelectMany(g => g.Words).ToList();

        Check.True(all.Count > 0, "captions were produced");
        Check.True(all.All(w => w.Start >= -0.001), "no caption starts before the reel does");

        var fade = ReelRenderer.ResolveCrossfade(project.RenderClips, project.RenderOptions);
        var reelDuration = 20 - 10 + (50 - 40) - fade;
        Check.True(all.All(w => w.End <= reelDuration + 0.5),
            $"no caption runs past the reel end ({reelDuration:0.##}s)");

        // The first word of the second clip is word40, which must land right after clip one.
        var word40 = all.FirstOrDefault(w => w.Text == "word40");
        Check.True(word40 is not null, "a word from the second clip is present");
        if (word40 is not null)
            Check.Near(10 - fade, word40.Start, 0.15, "the second clip's words are offset by the first clip minus the fade");

        // Words from the discarded middle must not appear.
        Check.True(all.All(w => w.Text != "word25" && w.Text != "word30"),
            "words outside the selected clips are dropped");
    }
}
