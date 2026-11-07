using ReelsMagician.SmokeTests;

// Modes:
//   (none)     pure-logic checks plus a real ffmpeg render of a synthetic source
//   logic      the pure-logic checks only, needing no tools at all (what CI runs)
//   install    downloads and installs the whole toolchain, then reports what was selected
//   tools      the same, but only the required toolchain: never yt-dlp or Deno
//   e2e        full pipeline over a generated speech video: Whisper, Qwen, framing, render
//   link       resolve a real compatible video link end to end (download, probe, subtitles)
//   recaption  rebuild captions from cached real transcripts and assert no overlap
var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

switch (mode)
{
    case "install":
        await EndToEnd.InstallAsync();
        break;

    case "tools":
        await EndToEnd.InstallAsync(includeLinkTools: false);
        break;

    case "recaption":
        EndToEnd.Recaption();
        break;

    case "link":
        await EndToEnd.LinkAsync(args.Length > 1 ? args[1] : "https://youtu.be/ggOnoGOKxos");
        break;

    case "e2e":
        await EndToEnd.RunAsync(args.Length > 1 ? args[1] : null);
        break;

    case "logic":
        UnitChecks.Run();
        PlatformChecks.Run();
        ToolBuildChecks.Run();
        LinkToolChecks.Run();
        break;

    default:
        UnitChecks.Run();
        PlatformChecks.Run();
        ToolBuildChecks.Run();
        LinkToolChecks.Run();
        await RenderChecks.RunAsync();
        break;
}

return Check.Report();
