<#
.SYNOPSIS
  Builds a 4K test video with real spoken narration, for exercising the pipeline end to end.

.DESCRIPTION
  Uses the Windows speech engine to narrate a short script, then pairs it with a 3840x2160
  picture that has a moving subject, so transcription, segment analysis, subject tracking and
  the vertical crop all have something real to work on. No network access and no external
  media are required.

.EXAMPLE
  pwsh tools\make-speech-video.ps1 -OutputDir "$env:TEMP\reelsmagician-e2e"
#>
param(
  [string]$OutputDir = (Join-Path $env:TEMP 'reelsmagician-e2e'),
  [string]$Title = 'Why Small Habits Beat Big Goals',
  [string]$FfmpegPath
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if (-not $FfmpegPath) {
  $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
  if ($cmd) {
    $FfmpegPath = $cmd.Source
  } else {
    $local = Join-Path $env:LOCALAPPDATA 'ReelsMagician\tools\ffmpeg'
    if (Test-Path $local) {
      $found = Get-ChildItem $local -Recurse -Filter ffmpeg.exe | Select-Object -First 1
      if ($found) { $FfmpegPath = $found.FullName }
    }
  }
}
if (-not $FfmpegPath) { throw 'ffmpeg not found. Install it from the app''s Setup tab first.' }

# Distinct paragraphs with clear topic shifts, so the analyser has something to choose between.
$script = @(
  'Most people set enormous goals in January and abandon them by February. The problem is not willpower. The problem is that the goal is too far away to feel real.',
  'Here is the thing nobody tells you. A habit that takes two minutes will beat a habit that takes an hour, because the two minute version actually happens.',
  'I want to talk about the mathematics of this for a moment. If you improve by one percent every day for a year, you end up thirty seven times better than where you started.',
  'That compounding effect is invisible at first. For weeks it looks like nothing is happening. This is the valley of disappointment, and it is where almost everyone quits.',
  'So here is what I want you to do. Pick one habit. Make it so small that it feels almost embarrassing. Then do it every single day for two weeks.',
  'The identity shift matters more than the outcome. You are not trying to run a marathon. You are becoming the kind of person who does not miss a workout.',
  'Thanks for watching, and I will see you in the next video.'
) -join ' '

$wav = Join-Path $OutputDir 'narration.wav'
Write-Host "Synthesising narration..."

Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
  $synth.Rate = 0
  $synth.SetOutputToWaveFile($wav)
  $synth.Speak($script)
} finally {
  $synth.Dispose()
}

if (-not (Test-Path $wav)) { throw "Speech synthesis produced no file." }
Write-Host "  narration: $wav ($([math]::Round((Get-Item $wav).Length / 1MB, 1)) MB)"

$safeTitle = ($Title -replace '[\\/:*?"<>|]', '_')
$out = Join-Path $OutputDir "$safeTitle.mp4"

# A 4K picture whose subject drifts across the frame, so the crop planner has to track
# something rather than sit still. The graph goes into a file because PowerShell mangles the
# quoting that ffmpeg expressions need when they are passed as a command-line argument.
$filterFile = Join-Path $OutputDir 'graph.txt'
$graph = "[0][1]overlay=x='620+1500*(0.5+0.5*sin(t/9))':y=520[a];[a][2]overlay=x=0:y=1860[v]"
Set-Content -Path $filterFile -Value $graph -Encoding ascii -NoNewline

Write-Host "Rendering 4K test video..."
& $FfmpegPath -hide_banner -loglevel error -y `
  -f lavfi -i 'color=c=0x0E1424:s=3840x2160:r=30' `
  -f lavfi -i 'color=c=0xE8C48A:s=520x680:r=30' `
  -f lavfi -i 'color=c=0x1E3A5F:s=3840x300:r=30' `
  -i $wav `
  -filter_complex_script $filterFile `
  -map '[v]' -map '3:a' `
  -c:v libx264 -preset veryfast -crf 26 -pix_fmt yuv420p `
  -c:a aac -b:a 192k -ar 48000 `
  -shortest $out

if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed with exit code $LASTEXITCODE" }

$size = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host "Done: $out ($size MB)"
Write-Output $out
