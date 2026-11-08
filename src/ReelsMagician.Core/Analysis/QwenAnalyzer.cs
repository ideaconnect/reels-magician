using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReelsMagician.Core.Diagnostics;
using ReelsMagician.Core.Localization;

namespace ReelsMagician.Core.Analysis;

/// <summary>
/// Uses Qwen to choose the most compelling stretch of a long video.
///
/// The work is split in two passes so it scales to hour-long transcripts without blowing the
/// context window: a map pass scores every paragraph in windows, then a reduce pass assembles
/// the highest-scoring material into complete reels of the requested length. Both passes reason
/// over whole paragraphs, and both are told the video's topic so scoring is anchored to it.
/// </summary>
public sealed partial class QwenAnalyzer(LlamaServerSession server, IProgressSink sink)
{
    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    private const int WindowSize = 28;
    private const int WindowOverlap = 3;

    public async Task<ReelPlan> PlanAsync(
        string topic, IReadOnlyList<Paragraph> paragraphs, ReelPlanOptions options, CancellationToken ct)
    {
        var plan = new ReelPlan { Topic = topic };
        if (paragraphs.Count == 0)
        {
            plan.Notes = Lang.Ui[nameof(Strings.Plan_EmptyTranscript)];
            return plan;
        }

        var scores = await ScoreParagraphsAsync(topic, paragraphs, ct).ConfigureAwait(false);

        var candidates = await AssembleAsync(topic, paragraphs, scores, options, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            sink.Warn("analyze", "The model returned no usable reel, falling back to the best-scoring run.");
            candidates = [BuildFallback(paragraphs, scores, options)];
            plan.UsedFallback = true;
            plan.Notes = Lang.Ui[nameof(Strings.Plan_Fallback)];
        }

        plan.Candidates = [.. candidates.OrderByDescending(c => c.Score)];
        return plan;
    }

    /// <summary>Map pass: score every paragraph for short-form potential, in overlapping windows.</summary>
    public async Task<Dictionary<int, ParagraphScore>> ScoreParagraphsAsync(
        string topic, IReadOnlyList<Paragraph> paragraphs, CancellationToken ct)
    {
        var scores = new Dictionary<int, ParagraphScore>();
        var windows = BuildWindows(paragraphs.Count, WindowSize, WindowOverlap);

        for (var w = 0; w < windows.Count; w++)
        {
            ct.ThrowIfCancellationRequested();
            var (from, to) = windows[w];
            var slice = paragraphs.Skip(from).Take(to - from).ToList();

            sink.Report("analyze",
                Lang.Ui.Format(nameof(Strings.Analysis_Scoring), from + 1, to, paragraphs.Count),
                (double)w / windows.Count);

            try
            {
                var parsed = await ScoreWindowAsync(topic, slice, ct).ConfigureAwait(false);
                foreach (var s in parsed)
                {
                    // Overlapping windows see some paragraphs twice; keep the more confident read.
                    if (!scores.TryGetValue(s.Index, out var existing) || s.Score > existing.Score)
                        scores[s.Index] = s;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                sink.Warn("analyze", $"Scoring window {from + 1}–{to} failed ({ex.Message}); using neutral scores.");
            }
        }

        foreach (var p in paragraphs)
            scores.TryAdd(p.Index, ParagraphScore.Neutral(p.Index));

        sink.Report("analyze", Lang.Ui[nameof(Strings.Analysis_Scored)], 1);
        return scores;
    }

    private async Task<List<ParagraphScore>> ScoreWindowAsync(
        string topic, List<Paragraph> slice, CancellationToken ct)
    {
        var system =
            "You are a short-form video editor who picks the moments that make people stop scrolling. " +
            "You judge complete paragraphs, never isolated words, and you answer only with JSON.";

        var user = new StringBuilder()
            .AppendLine($"VIDEO TOPIC: {topic}")
            .AppendLine()
            .AppendLine("Below are numbered paragraphs from this video's transcript, with their start time and length.")
            .AppendLine()
            .AppendLine("Rate each paragraph from 0 to 10 on how well it would work inside a vertical short-form reel:")
            .AppendLine("  9-10  a self-contained hook, a surprising claim, a sharp punchline, or a strong payoff")
            .AppendLine("  6-8   a clear, quotable point that stands on its own")
            .AppendLine("  3-5   useful but only as connective tissue")
            .AppendLine("  0-2   filler, housekeeping, sponsor reads, rambling, or unintelligible speech")
            .AppendLine()
            .AppendLine("Reward paragraphs that stay on the video's topic, that a viewer can understand with no")
            .AppendLine("prior context, and that end on a complete thought. Punish anything that depends on")
            .AppendLine("something said elsewhere, or that trails off mid-sentence.")
            .AppendLine()
            .AppendLine("PARAGRAPHS:")
            .Append(ParagraphBuilder.Render(slice))
            .AppendLine()
            .AppendLine("Reply with JSON: {\"scores\":[{\"i\":<paragraph number>,\"s\":<0-10>,\"why\":\"<8 words max>\"}]}")
            .AppendLine("Include every paragraph number exactly once.")
            .ToString();

        var schema = new
        {
            type = "object",
            properties = new
            {
                scores = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            i = new { type = "integer" },
                            s = new { type = "number" },
                            why = new { type = "string" },
                        },
                        required = new[] { "i", "s" },
                    },
                },
            },
            required = new[] { "scores" },
        };

        var raw = await CompleteJsonAsync(system, user, schema, maxTokens: 2048, temperature: 0.2, ct)
            .ConfigureAwait(false);

        var result = new List<ParagraphScore>();
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("scores", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        var valid = slice.Select(p => p.Index).ToHashSet();
        foreach (var item in arr.EnumerateArray())
        {
            if (!item.TryGetProperty("i", out var iv) || !iv.TryGetInt32(out var index)) continue;
            if (!valid.Contains(index)) continue;
            var score = item.TryGetProperty("s", out var sv) && sv.ValueKind == JsonValueKind.Number
                ? Math.Clamp(sv.GetDouble(), 0, 10) : 5;
            var why = item.TryGetProperty("why", out var wv) ? wv.GetString() ?? "" : "";
            result.Add(new ParagraphScore(index, score, why));
        }
        return result;
    }

    /// <summary>Reduce pass: build complete reels of the requested length from the best material.</summary>
    private async Task<List<ReelCandidate>> AssembleAsync(
        string topic,
        IReadOnlyList<Paragraph> paragraphs,
        Dictionary<int, ParagraphScore> scores,
        ReelPlanOptions options,
        CancellationToken ct)
    {
        sink.Report("analyze", Lang.Ui[nameof(Strings.Analysis_Assembling)], null);

        // Feed the model the strongest paragraphs plus their immediate neighbours, so it can
        // still build a run that flows rather than a list of disconnected highlights.
        var shortlist = SelectShortlist(paragraphs, scores, options);
        var rendered = new StringBuilder();
        foreach (var p in shortlist)
        {
            var s = scores.TryGetValue(p.Index, out var sc) ? sc.Score : 5;
            rendered.Append('[').Append(p.Index).Append("] (")
                    .Append(TimeSpan.FromSeconds(p.Start).ToString(@"h\:mm\:ss")).Append(", ")
                    .Append(p.Duration.ToString("0")).Append("s, score ").Append(s.ToString("0.#")).Append(") ")
                    .AppendLine(p.Text.Replace('\n', ' '));
        }

        var system =
            "You assemble vertical short-form reels from long videos. You work in whole paragraphs, " +
            "you keep the edit coherent, and you answer only with JSON.";

        var user = new StringBuilder()
            .AppendLine($"VIDEO TOPIC: {topic}")
            .AppendLine($"TARGET REEL LENGTH: {options.TargetSeconds:0} seconds " +
                        $"(acceptable range {options.MinSeconds:0}-{options.MaxSeconds:0} seconds)")
            .AppendLine()
            .AppendLine("These are the strongest paragraphs from the transcript. Numbers are the original")
            .AppendLine("paragraph indices, so gaps in the numbering mean weaker material was left out.")
            .AppendLine()
            .Append(rendered)
            .AppendLine()
            .AppendLine($"Produce {options.CandidateCount} different reel proposals. For each one:")
            .AppendLine("  - Pick paragraph ranges whose combined length lands inside the target range.")
            .AppendLine($"  - Use at most {options.MaxClipsPerReel} ranges; prefer one or two contiguous runs.")
            .AppendLine("  - Open on the strongest hook. The first three seconds decide whether anyone stays.")
            .AppendLine("  - End on a complete thought, never mid-sentence.")
            .AppendLine("  - Only join non-adjacent ranges when the jump still reads as one argument.")
            .AppendLine("  - Make the proposals genuinely different from each other, not variations of one idea.")
            .AppendLine()
            .AppendLine("Reply with JSON:")
            .AppendLine("{\"reels\":[{\"title\":\"<short, specific>\",\"hook\":\"<the opening line's promise>\"," +
                        "\"reason\":\"<why this works, one sentence>\",\"score\":<0-10>," +
                        "\"ranges\":[{\"from\":<index>,\"to\":<index>}]}]}")
            .AppendLine("A range is inclusive and 'to' must be greater than or equal to 'from'.")
            .ToString();

        var schema = new
        {
            type = "object",
            properties = new
            {
                reels = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string" },
                            hook = new { type = "string" },
                            reason = new { type = "string" },
                            score = new { type = "number" },
                            ranges = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        from = new { type = "integer" },
                                        to = new { type = "integer" },
                                    },
                                    required = new[] { "from", "to" },
                                },
                            },
                        },
                        required = new[] { "title", "ranges" },
                    },
                },
            },
            required = new[] { "reels" },
        };

        var candidates = new List<ReelCandidate>();
        try
        {
            var raw = await CompleteJsonAsync(system, user, schema, maxTokens: 1800, temperature: 0.7, ct)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("reels", out var reels) && reels.ValueKind == JsonValueKind.Array)
            {
                foreach (var reel in reels.EnumerateArray())
                {
                    var candidate = MaterialiseCandidate(reel, paragraphs, scores, options);
                    if (candidate is not null) candidates.Add(candidate);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sink.Warn("analyze", $"Reel assembly failed ({ex.Message}).");
        }

        return candidates;
    }

    /// <summary>Converts one model proposal into real time ranges, or rejects it as unusable.</summary>
    private ReelCandidate? MaterialiseCandidate(
        JsonElement reel,
        IReadOnlyList<Paragraph> paragraphs,
        Dictionary<int, ParagraphScore> scores,
        ReelPlanOptions options)
    {
        if (!reel.TryGetProperty("ranges", out var ranges) || ranges.ValueKind != JsonValueKind.Array)
            return null;

        var pairs = new List<(int From, int To)>();
        foreach (var r in ranges.EnumerateArray())
        {
            if (!r.TryGetProperty("from", out var fv) || !fv.TryGetInt32(out var from)) continue;
            if (!r.TryGetProperty("to", out var tv) || !tv.TryGetInt32(out var to)) continue;
            from = Math.Clamp(from, 0, paragraphs.Count - 1);
            to = Math.Clamp(to, 0, paragraphs.Count - 1);
            if (to < from) (from, to) = (to, from);
            pairs.Add((from, to));
        }
        if (pairs.Count == 0) return null;

        var merged = MergeRanges(pairs);
        if (merged.Count > options.MaxClipsPerReel)
            merged = [.. merged
                .OrderByDescending(p => Enumerable.Range(p.From, p.To - p.From + 1)
                    .Sum(i => scores.TryGetValue(i, out var s) ? s.Score : 5))
                .Take(options.MaxClipsPerReel)
                .OrderBy(p => p.From)];

        var clips = merged
            .Select(p => ToClip(p.From, p.To, paragraphs, options))
            .Where(c => c.Duration >= options.MinClipSeconds)
            .ToList();
        if (clips.Count == 0) return null;

        clips = FitToTarget(clips, paragraphs, scores, options);

        var candidate = new ReelCandidate
        {
            Title = GetString(reel, "title", "Untitled reel"),
            Hook = GetString(reel, "hook", ""),
            Reason = GetString(reel, "reason", ""),
            Score = reel.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number
                ? Math.Clamp(sc.GetDouble(), 0, 10)
                : clips.Average(c => AverageScore(c, scores)),
            Clips = clips,
        };
        return candidate;
    }

    /// <summary>Trims or extends a candidate so its runtime lands inside the requested window.</summary>
    private static List<ClipRange> FitToTarget(
        List<ClipRange> clips,
        IReadOnlyList<Paragraph> paragraphs,
        Dictionary<int, ParagraphScore> scores,
        ReelPlanOptions options)
    {
        var total = clips.Sum(c => c.Duration);

        // Too long: shorten the weakest clip's tail, never cutting below the clip minimum.
        while (total > options.MaxSeconds && clips.Count > 0)
        {
            var weakestIndex = clips
                .Select((c, i) => (Index: i, Score: AverageScore(c, scores)))
                .OrderBy(x => x.Score)
                .First().Index;

            var weakest = clips[weakestIndex];
            var excess = total - options.TargetSeconds;
            var room = weakest.Duration - options.MinClipSeconds;
            if (room <= 0.2)
            {
                if (clips.Count == 1) break;
                clips.RemoveAt(weakestIndex);
                total = clips.Sum(c => c.Duration);
                continue;
            }

            var trim = Math.Min(excess, room);
            clips[weakestIndex] = weakest with { End = weakest.End - trim };
            total = clips.Sum(c => c.Duration);
            if (trim < 0.2) break;
        }

        // Too short: grow outward one paragraph at a time from the strongest neighbour.
        var guard = 0;
        while (total < options.MinSeconds && guard++ < 24)
        {
            var best = (Clip: -1, Side: 0, Score: double.MinValue, Paragraph: -1);

            for (var i = 0; i < clips.Count; i++)
            {
                var before = clips[i].FromParagraph - 1;
                var after = clips[i].ToParagraph + 1;

                if (before >= 0 && !Covered(clips, before))
                {
                    var s = scores.TryGetValue(before, out var sb) ? sb.Score : 5;
                    if (s > best.Score) best = (i, -1, s, before);
                }
                if (after < paragraphs.Count && !Covered(clips, after))
                {
                    var s = scores.TryGetValue(after, out var sa) ? sa.Score : 5;
                    if (s > best.Score) best = (i, +1, s, after);
                }
            }
            if (best.Clip < 0) break;

            var clip = clips[best.Clip];
            clips[best.Clip] = best.Side < 0
                ? ToClip(best.Paragraph, clip.ToParagraph, paragraphs, options)
                : ToClip(clip.FromParagraph, best.Paragraph, paragraphs, options);
            total = clips.Sum(c => c.Duration);
        }

        return [.. clips.OrderBy(c => c.Start)];
    }

    private static bool Covered(List<ClipRange> clips, int paragraphIndex) =>
        clips.Any(c => paragraphIndex >= c.FromParagraph && paragraphIndex <= c.ToParagraph);

    private static double AverageScore(ClipRange clip, Dictionary<int, ParagraphScore> scores)
    {
        var count = clip.ToParagraph - clip.FromParagraph + 1;
        if (count <= 0) return 5;
        return Enumerable.Range(clip.FromParagraph, count)
            .Average(i => scores.TryGetValue(i, out var s) ? s.Score : 5);
    }

    private static ClipRange ToClip(
        int from, int to, IReadOnlyList<Paragraph> paragraphs, ReelPlanOptions options)
    {
        from = Math.Clamp(from, 0, paragraphs.Count - 1);
        to = Math.Clamp(to, from, paragraphs.Count - 1);

        // A little lead-in catches the first word's onset; the tail lets the sentence land.
        var start = Math.Max(0, paragraphs[from].Start - options.LeadInSeconds);
        var end = paragraphs[to].End + options.TailSeconds;
        if (to + 1 < paragraphs.Count) end = Math.Min(end, paragraphs[to + 1].Start);

        var text = string.Join(" ", paragraphs.Skip(from).Take(to - from + 1).Select(p => p.Text));
        return new ClipRange(start, Math.Max(start + 0.5, end), from, to, text);
    }

    private static List<(int From, int To)> MergeRanges(List<(int From, int To)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.From).ToList();
        var merged = new List<(int From, int To)>();
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r.From <= merged[^1].To + 1)
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
            else
                merged.Add(r);
        }
        return merged;
    }

    /// <summary>Picks the paragraphs worth showing the assembly pass, keeping local context.</summary>
    private static List<Paragraph> SelectShortlist(
        IReadOnlyList<Paragraph> paragraphs,
        Dictionary<int, ParagraphScore> scores,
        ReelPlanOptions options)
    {
        // Enough material for several distinct reels, with neighbours so runs can be extended.
        var wanted = (int)Math.Ceiling(options.TargetSeconds * options.CandidateCount * 2.5 /
                                       Math.Max(4, paragraphs.Average(p => p.Duration)));
        wanted = Math.Clamp(wanted, 12, 60);
        if (paragraphs.Count <= wanted) return [.. paragraphs];

        var keep = paragraphs
            .OrderByDescending(p => scores.TryGetValue(p.Index, out var s) ? s.Score : 5)
            .Take(wanted)
            .Select(p => p.Index)
            .ToHashSet();

        foreach (var index in keep.ToList())
        {
            if (index - 1 >= 0) keep.Add(index - 1);
            if (index + 1 < paragraphs.Count) keep.Add(index + 1);
        }

        return [.. paragraphs.Where(p => keep.Contains(p.Index))];
    }

    /// <summary>
    /// Deterministic backstop: the highest-scoring contiguous run that fits the target length.
    /// Used when the model is unavailable or returns nothing usable.
    /// </summary>
    public static ReelCandidate BuildFallback(
        IReadOnlyList<Paragraph> paragraphs,
        Dictionary<int, ParagraphScore> scores,
        ReelPlanOptions options)
    {
        var best = (From: 0, To: 0, Score: double.MinValue);

        for (var from = 0; from < paragraphs.Count; from++)
        {
            double duration = 0, total = 0;
            for (var to = from; to < paragraphs.Count; to++)
            {
                duration += paragraphs[to].Duration;
                total += scores.TryGetValue(to, out var s) ? s.Score : 5;
                if (duration < options.MinSeconds) continue;
                if (duration > options.MaxSeconds) break;

                var average = total / (to - from + 1);
                // Nudge toward runs that land near the requested length.
                var fit = 1 - Math.Abs(duration - options.TargetSeconds) / Math.Max(1, options.TargetSeconds);
                var combined = average + fit;
                if (combined > best.Score) best = (from, to, combined);
            }
        }

        if (best.Score == double.MinValue)
            best = (0, Math.Min(paragraphs.Count - 1, 3), 0);

        var clip = ToClip(best.From, best.To, paragraphs, options);
        return new ReelCandidate
        {
            Title = "Best-scoring stretch",
            Hook = paragraphs[best.From].Text.Length > 90
                ? paragraphs[best.From].Text[..90] + "…"
                : paragraphs[best.From].Text,
            Reason = "Chosen automatically from the paragraph scores.",
            Score = AverageScore(clip, scores),
            Clips = [clip],
        };
    }

    private async Task<string> CompleteJsonAsync(
        string system, string user, object schema, int maxTokens, double temperature, CancellationToken ct)
    {
        var request = new Dictionary<string, object?>
        {
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                // Qwen3 is a hybrid reasoning model; the marker keeps it out of thinking mode
                // so the whole budget goes to the answer.
                new { role = "user", content = user + "\n/no_think" },
            },
            ["temperature"] = temperature,
            ["top_p"] = 0.9,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
            ["chat_template_kwargs"] = new { enable_thinking = false },
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "reelsmagician", strict = true, schema },
            },
        };

        string content;
        try
        {
            content = await server.CompleteAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Older llama.cpp builds only understand the looser json_object mode.
            sink.Debug("analyze", $"json_schema rejected ({ex.Message}); retrying with json_object.");
            request["response_format"] = new { type = "json_object" };
            content = await server.CompleteAsync(request, ct).ConfigureAwait(false);
        }

        return ExtractJson(content);
    }

    /// <summary>Strips reasoning blocks and prose so only the JSON document is parsed.</summary>
    public static string ExtractJson(string content)
    {
        var text = ThinkBlock().Replace(content, "").Trim();

        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence);
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start) text = text[(start + 1)..end].Trim();
        }

        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open >= 0 && close > open) text = text[open..(close + 1)];

        return text.Length == 0 ? "{}" : text;
    }

    private static List<(int From, int To)> BuildWindows(int count, int size, int overlap)
    {
        var windows = new List<(int, int)>();
        if (count <= 0) return windows;

        var step = Math.Max(1, size - overlap);
        for (var start = 0; start < count; start += step)
        {
            var end = Math.Min(count, start + size);
            windows.Add((start, end));
            if (end >= count) break;
        }
        return windows;
    }

    private static string GetString(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s ? s : fallback;
}
