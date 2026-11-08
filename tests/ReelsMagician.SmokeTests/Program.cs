using ReelsMagician.SmokeTests;

// Modes:
//   (none)     pure-logic checks plus a real ffmpeg render of a synthetic source
//   logic      the pure-logic checks only, needing no tools at all (what CI runs)
//   install    downloads and installs what "Install everything missing" does, then reports what
//              was selected: never yt-dlp or Deno
//   e2e        full pipeline over a generated speech video: Whisper, Qwen, framing, render
//   link       fetches yt-dlp and Deno if missing, then resolves a real compatible video link end
//              to end (download, probe, subtitles)
//   recaption  rebuild captions from cached real transcripts and assert no overlap
//   licences   holds the About tab's licence table to a self-contained publish of the app:
//              licences out/publish (what CI runs after publishing on each system)
var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

switch (mode)
{
    case "licences":
        if (args.Length < 2) Check.True(false, "the licences mode is given a publish folder");
        else LicenceChecks.RunOnPublish(args[1]);
        break;

    case "install":
        await EndToEnd.InstallAsync();
        break;

    case "recaption":
        EndToEnd.Recaption();
        break;

    case "link":
        await EndToEnd.ResolveLinkAsync(args.Length > 1 ? args[1] : "https://youtu.be/ggOnoGOKxos");
        break;

    case "e2e":
        await EndToEnd.RunAsync(args.Length > 1 ? args[1] : null);
        break;

    case "logic":
        RunLogicChecks();
        break;

    default:
        RunLogicChecks();
        await RenderChecks.RunAsync();
        break;
}

return Check.Report();

// The checks that need no tools at all. CI runs them as the logic mode, and the default mode
// adds a real render, so a check class listed here runs in both.
static void RunLogicChecks()
{
    UnitChecks.Run();
    PlatformChecks.Run();
    CaptionParsingChecks.Run();
    SourceAndCleanupChecks.Run();
    ToolBuildChecks.Run();
    LinkToolChecks.Run();
    StepProgressChecks.Run();
    IconChecks.Run();
    AppearanceChecks.Run();
    ThemeChecks.Run();
    AppIconChecks.Run();
    ScrollShadowChecks.Run();
    LicenceChecks.Run();
    InfoBoxChecks.Run();
}
