using System.Text.RegularExpressions;
using ReelsMagician.Core.Analysis;
using ReelsMagician.Core.Projects;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The memory warning (issue #54): what llama.cpp says about its devices and options, how much
/// each model needs, which memory it is held against, when that warns, how llama-server is told
/// to fit a model, how a shortage in its log is explained, and the record that explains a crash.
/// A wrong figure here builds and runs, and only shows as a warning nobody needed, or none where
/// a model is about to take the system down.
/// </summary>
public static class MemoryFitChecks
{
    private const long MiB = 1L << 20, GiB = 1L << 30;

    // Suseł: Radeon 760M, Vulkan (RADV), llama.cpp build 12189, as `llama-server --list-devices` printed it.
    private const string SuselDevices =
        "Available devices:\n  Vulkan0: AMD Radeon 760M Graphics (RADV PHOENIX) (19062 MiB, 18838 MiB free)\n";

    public static void Run()
    {
        ListDevices();
        FitOption();
        Estimates();
        Thresholds();
        Budgets();
        LinuxAmdgpuMemory();
        GpuLayers();
        FitTarget();
        OutOfMemoryLogs();
        CrashRecord();
        Probe();
        SetupTab();
    }

    private static void ListDevices()
    {
        Check.Section("llama-server --list-devices is read for each device's memory (issue #54)");

        var vulkan = LlamaProbe.ParseDevices(SuselDevices);
        Check.Equal(1, vulkan?.Count ?? -1, "Suseł's Vulkan output lists one device");
        Check.Equal("AMD Radeon 760M Graphics (RADV PHOENIX)", vulkan?[0].Name, "its name keeps its own brackets");
        Check.Equal(19062 * MiB, vulkan?[0].TotalBytes ?? 0, "its total is 19,062 MiB, VRAM and GTT together");
        Check.Equal(18838 * MiB, vulkan?[0].FreeBytes ?? 0, "and 18,838 MiB of it are free");

        // A CUDA build logs its own lines first, on the other stream, and lists every card.
        var cuda = LlamaProbe.ParseDevices(
            "ggml_cuda_init: found 2 CUDA devices:\r\n  Device 0: NVIDIA GeForce RTX 4070, compute capability 8.9, VMM: yes\r\n" +
            "Available devices:\r\n  CUDA0: NVIDIA GeForce RTX 4070 (12281 MiB, 11053 MiB free)\r\n" +
            "  CUDA1: NVIDIA GeForce RTX 3060 (12287 MiB, 12011 MiB free)\r\n");
        Check.True(cuda is { Count: 2 } && cuda[0].Name == "NVIDIA GeForce RTX 4070" && cuda[1].TotalBytes == 12287 * MiB,
            "a CUDA build's two cards are both read, with Windows line endings and its log lines before the list");

        Check.True(LlamaProbe.ParseDevices("Available devices:\n") is { Count: 0 },
            "a build that finds no GPU lists none: it runs on the processor");
        Check.True(LlamaProbe.ParseDevices("error: invalid argument: --list-devices\n") is null,
            "an older build without the option says nothing about devices");
        Check.True(LlamaProbe.ParseDevices("") is null, "nor does no output at all");
    }

    private static void FitOption()
    {
        Check.Section("llama-server --help says whether it can fit a model to the GPU (issue #54)");

        // As build 12189 prints it.
        const string help =
            "-ngl,  --gpu-layers, --n-gpu-layers N   max. number of layers to store in VRAM, either an exact number,\n" +
            "                                        'auto', or 'all' (default: auto)\n" +
            "-fit,  --fit [on|off]                   whether to adjust unset arguments to fit in device memory ('on' or\n" +
            "                                        'off', default: 'on')\n" +
            "-fitt, --fit-target MiB0,MiB1,MiB2,...\n" +
            "-fitc, --fit-ctx N                      minimum ctx size that can be set by --fit option, default: 4096\n";
        Check.True(LlamaProbe.HelpListsFit(help), "build 12189's help lists --fit");
        Check.True(!LlamaProbe.HelpListsFit("-ngl,  --gpu-layers, --n-gpu-layers N   number of layers to store in VRAM\n"),
            "an older build's help, without it, does not");
        Check.True(!LlamaProbe.HelpListsFit("-fitt, --fit-target MiB\n-fitc, --fit-ctx N\n"),
            "--fit-target and --fit-ctx alone are not --fit");

        Check.True(LlamaProbe.HelpListsFitTarget(help), "build 12189's help lists --fit-target (issue #71)");
        Check.True(!LlamaProbe.HelpListsFitTarget("-fit,  --fit [on|off]                   whether to adjust unset arguments\n" +
                                                  "-fitc, --fit-ctx N                      minimum ctx size\n"),
            "a help with --fit and --fit-ctx but no --fit-target does not");
        Check.True(!LlamaProbe.HelpListsFitTarget("-ngl,  --gpu-layers, --n-gpu-layers N   number of layers to store in VRAM\n"),
            "nor does an older build's help");
    }

    private static void Estimates()
    {
        Check.Section("Each model's need: weights, KV cache for its context, and buffers (issue #54)");

        var small = ModelCatalog.LlmModels[0];
        var q4 = ModelCatalog.LlmModels[1];
        var q6 = ModelCatalog.LlmModels[2];
        Check.Equal(144L * 1024, q4.KvBytesPerToken, "Qwen3 8B's KV cache costs 144 KiB per token: 36 × 8 × 128 × 2 × 2 bytes");
        Check.Equal(q4.KvBytesPerToken, small.KvBytesPerToken, "and Qwen3 4B's the same, with the same 36 layers and 8 KV heads");
        Check.True(ModelCatalog.LlmModels.All(m => m.KvBytesPerToken > 0), "every Qwen model has a KV cost");
        Check.True(ModelCatalog.WhisperModels.All(m => m.RuntimeBytes > m.ApproxBytes),
            "every Whisper model needs more memory than its file, for its caches and buffers");

        // Measured on Suseł (amdgpu's VRAM + GTT counters once the server listened), app's arguments.
        Calibrated(q4, 32768, 9602, "Qwen3 8B (Q4_K_M) at 32k, the issue's 9,602 MiB");
        Calibrated(q4, 4096, 5514, "Qwen3 8B (Q4_K_M) at 4k, the issue's 5,514 MiB");
        Calibrated(q4, 16384, 7265, "Qwen3 8B (Q4_K_M) at 16k, 7,265 MiB");
        Calibrated(small, 16384, 5118, "Qwen3 4B at 16k, 5,118 MiB");
        Calibrated(q6, 32768, 11220, "Qwen3 8B (Q6_K) at 32k, 11,220 MiB");

        Check.Equal("9.7 GB", MemoryFit.Format(MemoryFit.LlmNeed(q4, 32768)), "8B Q4_K_M at its 32k context reads as 9.7 GB, not \"~6 GB\"");
        Check.Equal("11 GB", MemoryFit.Format(MemoryFit.LlmNeed(q6, 32768)), "8B Q6_K at 32k reads as 11 GB, as the issue works out");
        Check.Equal("5.1 GB", MemoryFit.Format(MemoryFit.LlmNeed(small, 16384)), "4B at 16k reads as 5.1 GB, about the issue's 5 GiB");
        Check.Equal("8 GB", MemoryFit.Format(8176 * MiB), "an 8 GB card's 8,176 MiB reads as 8 GB");
        Check.Equal("19 GB", MemoryFit.Format(19062 * MiB), "Suseł's 19,062 MiB as 19 GB");
        Check.Equal("850 MB", MemoryFit.Format(850 * MiB), "less than a GB in MB");
        Check.Equal("16k", MemoryFit.FormatContext(16384), "a 16,384-token context is 16k");
        Check.Equal("10,000", MemoryFit.FormatContext(10000), "a context set by hand that is no multiple of 1024 is spelled out");

        Check.Equal("Qwen3 8B (Q6_K)", q6.ShortName, "sentences name a model without \"Instruct\" and its remark");
        Check.Equal("Whisper large-v3-turbo (f16)", ModelCatalog.WhisperModels[3].ShortName, "and Whisper's the same way");
        Check.True(ModelCatalog.LlmModels.All(m => !Regex.IsMatch(m.Notes, "VRAM|GB")),
            "the model notes no longer state memory, which the estimate states for the context in use");

        var need = MemoryFit.AssessLlm(q4, 32768, new MemoryBudget(MemoryKind.Graphics, null)).NeedText;
        Check.Equal("Needs about 9.7 GB of graphics memory at a 32k context.", need, "the line under the drop-down");
        Check.Equal("Needs about 850 MB of memory.",
            MemoryFit.AssessWhisper(ModelCatalog.DefaultWhisper, new MemoryBudget(MemoryKind.System, 25 * GiB)).NeedText,
            "and Whisper's, which has no context to set");
    }

    // Never under the measurement, so a warning is never missed, and within 10 % over it.
    private static void Calibrated(LlmModelSpec model, int context, long measuredMib, string what)
    {
        var estimate = MemoryFit.LlmNeed(model, context) / (double)MiB;
        Check.True(estimate >= measuredMib && estimate <= measuredMib * 1.10,
            $"the estimate covers the measured {what}, within 10 % ({estimate:0} MiB)");
    }

    private static void Thresholds()
    {
        Check.Section("When a model warns, and what the warning says (issue #54)");

        var suselGpu = new MemoryBudget(MemoryKind.Graphics, 19062 * MiB, "AMD Radeon 760M Graphics (RADV PHOENIX)");
        foreach (var model in ModelCatalog.LlmModels)
            Check.True(!MemoryFit.AssessLlm(model, model.RecommendedContext, suselGpu).MayNotFit,
                $"on Suseł, {model.ShortName} at its {MemoryFit.FormatContext(model.RecommendedContext)} context does not warn");
        Check.True(MemoryFit.AssessLlm(ModelCatalog.DefaultLlm, 32768, suselGpu).Warning is null,
            "in particular 8B Q4_K_M, which a VRAM-only check (6 GB) would have warned about");
        Check.True(MemoryFit.AssessLlm(ModelCatalog.DefaultLlm, 32768, new MemoryBudget(MemoryKind.Graphics, 6144 * MiB)).MayNotFit,
            "which it would, against the 6 GiB of VRAM alone");

        var card = new MemoryBudget(MemoryKind.Graphics, 8 * GiB, "Radeon RX 7600");
        var q6 = MemoryFit.AssessLlm(ModelCatalog.LlmModels[2], 32768, card);
        Check.Equal(
            "Qwen3 8B (Q6_K) needs about 11 GB of graphics memory at this context, and this computer has 8 GB. It may run " +
            "slowly or fail, and in rare cases it can freeze the system. You can still try it.",
            q6.Warning, "on an 8 GB card, 8B Q6_K warns in the issue's words");
        Check.True(MemoryFit.AssessLlm(ModelCatalog.LlmModels[1], 32768, card).MayNotFit, "8B Q4_K_M at 32k (9.7 GB) warns there too");
        Check.True(!MemoryFit.AssessLlm(ModelCatalog.LlmModels[1], 16384, card).MayNotFit,
            "but not at 16k (7.4 GB): the context in use counts, not the model's usual one");
        Check.True(!MemoryFit.AssessLlm(ModelCatalog.LlmModels[0], 16384, card).MayNotFit, "and 4B (5.1 GB) never does");

        var need = MemoryFit.LlmNeed(ModelCatalog.DefaultLlm, 16384);
        Check.True(!MemoryFit.AssessLlm(ModelCatalog.DefaultLlm, 16384, new MemoryBudget(MemoryKind.Graphics, need)).MayNotFit,
            "a model that needs exactly what there is does not warn");
        Check.True(MemoryFit.AssessLlm(ModelCatalog.DefaultLlm, 16384, new MemoryBudget(MemoryKind.Graphics, need - 1)).MayNotFit,
            "one byte less, and it does");
        Check.True(!MemoryFit.AssessLlm(ModelCatalog.LlmModels[2], 32768, new MemoryBudget(MemoryKind.Graphics, null)).MayNotFit,
            "when nothing reports the memory, nothing warns");

        // The computer's own memory keeps 2 GB for the system, the desktop and the app.
        var laptop = new MemoryBudget(MemoryKind.System, 7_800 * MiB);
        var cpu = MemoryFit.AssessLlm(ModelCatalog.DefaultLlm, 16384, laptop);
        Check.True(cpu.MayNotFit, "on the processor of an 8 GB laptop, 8B at 16k (7.4 GB) warns, with 2 GB kept for the system");
        Check.Contains(cpu.Warning ?? "", "needs about 7.4 GB of memory at this context, and this computer has 7.6 GB, about 2 GB of which the system needs for itself.",
            "and says so");
        Check.True(!MemoryFit.AssessLlm(ModelCatalog.LlmModels[0], 16384, laptop).MayNotFit, "4B (5.1 GB) fits in its other 5.6 GB");
        var turbo = ModelCatalog.WhisperModels[3];
        Check.True(MemoryFit.AssessWhisper(turbo, new MemoryBudget(MemoryKind.System, 3 * GiB)).MayNotFit,
            "Whisper large-v3-turbo (f16), 1.9 GB, warns on a 3 GB computer");
        Check.True(!MemoryFit.AssessWhisper(turbo, new MemoryBudget(MemoryKind.System, 4 * GiB)).MayNotFit, "not on a 4 GB one");
        Check.True(MemoryFit.AssessWhisper(turbo, new MemoryBudget(MemoryKind.System, 3 * GiB)).Warning?.Contains("at this context") == false,
            "Whisper's warning names no context");
        Check.True(MemoryFit.SystemMemoryBytes() is > 0, $"this computer's memory is known ({MemoryFit.Format(MemoryFit.SystemMemoryBytes() ?? 0)})");
    }

    private static void Budgets()
    {
        Check.Section("Which memory a model is held against (issue #54)");

        var amdApu = new GpuInfo("AMD GPU [1002:15bf] (amdgpu)", GpuVendor.Amd, true, 19062 * MiB);
        var apuCaps = new GpuCapabilities([amdApu], true, 1);

        var installed = new Toolset
        {
            Gpu = apuCaps, LlamaServerPath = "/llama/llama-server", LlamaBuild = BuildKind.Vulkan,
            LlamaFeatures = new LlamaFeatures(LlamaProbe.ParseDevices(SuselDevices), true),
        };
        var fromLlama = MemoryFit.LlmBudget(installed, 25 * GiB, TargetOs.Linux);
        Check.True(fromLlama is { Kind: MemoryKind.Graphics, Bytes: 19062 * MiB, Device: "AMD Radeon 760M Graphics (RADV PHOENIX)" },
            "llama.cpp's own list wins: Suseł's 19,062 MiB on its Radeon");

        installed.LlamaFeatures = new LlamaFeatures([], true);
        Check.True(MemoryFit.LlmBudget(installed, 25 * GiB, TargetOs.Linux) is { Kind: MemoryKind.System, Bytes: 25 * GiB },
            "a llama.cpp that lists no GPU runs on the processor, in the computer's memory");

        installed.LlamaFeatures = new LlamaFeatures(
            [new DeviceMemory("CUDA0", 12 * GiB), new DeviceMemory("CUDA1", 8 * GiB)], true);
        Check.True(MemoryFit.LlmBudget(installed, 25 * GiB, TargetOs.Windows) is { Kind: MemoryKind.Graphics, Bytes: 20 * GiB },
            "two listed cards add up, as llama.cpp splits the layers across them");

        installed.LlamaFeatures = null;
        installed.LlamaBuild = BuildKind.Cpu;
        Check.True(MemoryFit.LlmBudget(installed, 25 * GiB, TargetOs.Linux).Kind == MemoryKind.System,
            "an installed CPU build that could not be asked runs in the computer's memory, GPU or not");

        var none = new Toolset { Gpu = apuCaps };
        Check.True(MemoryFit.LlmBudget(none, 25 * GiB, TargetOs.Linux) is { Kind: MemoryKind.Graphics, Bytes: 19062 * MiB },
            "before llama.cpp is installed, the Vulkan build this Radeon gets is held to what amdgpu reports");
        Check.True(MemoryFit.LlmBudget(new Toolset { Gpu = new GpuCapabilities([amdApu with { HasVulkanDriver = false }], true, 0) },
                25 * GiB, TargetOs.Linux).Kind == MemoryKind.System,
            "without a Vulkan driver it gets the CPU build, in the computer's memory");

        var desktop = new GpuCapabilities(
            [new GpuInfo("Intel(R) UHD Graphics 770", GpuVendor.Intel, true), new GpuInfo("AMD Radeon RX 7600", GpuVendor.Amd, true, 8 * GiB)],
            true, 2);
        Check.True(MemoryFit.LlmBudget(new Toolset { Gpu = desktop }, 32 * GiB, TargetOs.Windows) is { Bytes: 8 * GiB, Device: "AMD Radeon RX 7600" },
            "beside integrated graphics that report nothing, the discrete card with the most memory is the one");
        Check.True(MemoryFit.LlmBudget(new Toolset { Gpu = new GpuCapabilities([new GpuInfo("Intel(R) UHD Graphics 770", GpuVendor.Intel, true)], true, 1) },
                32 * GiB, TargetOs.Windows) is { Kind: MemoryKind.Graphics, Bytes: null },
            "and a card that reports no memory gives no figure, so no warning");

        var nvidia = new GpuCapabilities([new GpuInfo("NVIDIA GeForce RTX 4060", GpuVendor.Nvidia, true, 8 * GiB)], true, 1,
            new CudaSupport(new Version(13, 0), new Version(8, 9), 8 * GiB));
        Check.True(MemoryFit.LlmBudget(new Toolset { Gpu = nvidia }, 32 * GiB, TargetOs.Windows) is { Kind: MemoryKind.Graphics, Bytes: 8 * GiB },
            "an NVIDIA card's CUDA build is held to the memory CUDA reports");
        Check.True(MemoryFit.WhisperBudget(new Toolset { Gpu = nvidia }, 32 * GiB, TargetOs.Windows) is { Kind: MemoryKind.Graphics, Bytes: 8 * GiB },
            "and so is whisper.cpp's CUDA build there");
        Check.True(MemoryFit.WhisperBudget(new Toolset { Gpu = apuCaps }, 25 * GiB, TargetOs.Linux) is { Kind: MemoryKind.System, Bytes: 25 * GiB },
            "on Linux, whisper.cpp runs on the processor, in the computer's memory");
        Check.True(MemoryFit.WhisperBudget(new Toolset { Gpu = apuCaps }, null, TargetOs.Linux).Bytes is null,
            "and when that is unknown, nothing warns");
    }

    private static void LinuxAmdgpuMemory()
    {
        Check.Section("amdgpu's memory counters before llama.cpp is installed (issue #54)");

        var drm = Path.Combine(Path.GetTempPath(), "reelsmagician-amdgpu");
        if (Directory.Exists(drm)) Directory.Delete(drm, recursive: true);

        string Card(string name, string driver, params (string File, string Value)[] files)
        {
            var device = Path.Combine(drm, name, "device");
            Directory.CreateDirectory(device);
            File.WriteAllText(Path.Combine(device, "vendor"), driver == "i915" ? "0x8086\n" : "0x1002\n");
            File.WriteAllText(Path.Combine(device, "device"), $"0x15b{name[^1]}\n");
            File.WriteAllText(Path.Combine(device, "uevent"), $"DRIVER={driver}\n");
            foreach (var (file, value) in files) File.WriteAllText(Path.Combine(device, file), value + "\n");
            return device;
        }

        // Suseł's Radeon 760M: no mem_info_vram_vendor, as on any APU.
        Card("card1", "amdgpu", ("mem_info_vram_total", "6442450944"), ("mem_info_gtt_total", "13545947136"));
        Card("card2", "amdgpu", ("mem_info_vram_total", "8573157376"), ("mem_info_gtt_total", "16777216000"), ("mem_info_vram_vendor", "samsung"));
        Card("card3", "amdgpu");
        Card("card4", "i915");
        var gpus = GpuProbe.DetectLinux(drm, ["radeon_icd.x86_64.json"], hasLoader: true).Adapters;

        Check.Equal(19062L, (gpus.ElementAtOrDefault(0)?.MemoryBytes ?? 0) / MiB,
            "an APU offers VRAM and GTT together: Suseł's 6,144 + 12,918 MiB, what llama.cpp reports");
        Check.Equal(8573157376L, gpus.ElementAtOrDefault(1)?.MemoryBytes ?? 0, "a card that names its memory's vendor offers its VRAM alone");
        Check.True(gpus.ElementAtOrDefault(2) is { MemoryBytes: null }, "an amdgpu card without the counters reports nothing");
        Check.True(gpus.ElementAtOrDefault(3) is { MemoryBytes: null }, "nor does an Intel one, whose driver has none");

        Check.True(gpus.ElementAtOrDefault(0) is { Integrated: true }, "the APU is integrated (issue #71)");
        Check.True(gpus.ElementAtOrDefault(1) is { Integrated: false }, "the card that names its memory's vendor is not");
        Check.True(gpus.ElementAtOrDefault(2) is { Integrated: false }, "nor is one without the counters, which says neither");
        Check.True(gpus.ElementAtOrDefault(3) is { Integrated: false }, "nor the Intel one, which the probe can't tell");

        Directory.Delete(drm, recursive: true);
    }

    private static void GpuLayers()
    {
        Check.Section("llama-server fits a model to the GPU instead of failing (issue #54)");

        Check.Equal("--fit on", string.Join(' ', LlamaServerSession.GpuLayerArgs(-1, supportsFit: true)),
            "with LlamaGpuLayers unset, a build with --fit gets no -ngl, which would switch fitting off");
        Check.Equal("-ngl 999", string.Join(' ', LlamaServerSession.GpuLayerArgs(-1, supportsFit: false)),
            "an older build without it gets every layer, as before");
        Check.Equal("-ngl 20", string.Join(' ', LlamaServerSession.GpuLayerArgs(20, supportsFit: true)),
            "a number set in settings.json is passed as it is");
        Check.Equal("-ngl 0", string.Join(' ', LlamaServerSession.GpuLayerArgs(0, supportsFit: true)),
            "including 0, the processor only");

        Check.Equal("--fit on --fit-target 3072", string.Join(' ', LlamaServerSession.GpuLayerArgs(-1, supportsFit: true, 3072)),
            "a margin goes after --fit as --fit-target, in MiB (issue #71)");
        Check.Equal("-ngl 999", string.Join(' ', LlamaServerSession.GpuLayerArgs(-1, supportsFit: false, 3072)),
            "but never to a build without --fit, which has nothing to apply it to");
        Check.Equal("-ngl 20", string.Join(' ', LlamaServerSession.GpuLayerArgs(20, supportsFit: true, 3072)),
            "nor with a number set in settings.json, which switches fitting off");
    }

    private static void FitTarget()
    {
        Check.Section("An integrated GPU gets a larger --fit margin (issue #71)");

        // Suseł: the Radeon 760M, an APU by amdgpu's counters, with build 12189 and its Vulkan list.
        var apu = new GpuInfo("AMD GPU [1002:15bf] (amdgpu)", GpuVendor.Amd, true, 19062 * MiB, Integrated: true);
        var discrete = new GpuInfo("AMD GPU [1002:7480] (amdgpu)", GpuVendor.Amd, true, 8 * GiB);
        Toolset Machine(BuildKind? build, LlamaFeatures? features, params GpuInfo[] adapters) => new()
        {
            Gpu = new GpuCapabilities(adapters, true, adapters.Count(a => a.HasVulkanDriver)),
            LlamaServerPath = "/llama/llama-server", LlamaBuild = build, LlamaFeatures = features,
        };
        var b12189 = new LlamaFeatures(LlamaProbe.ParseDevices(SuselDevices), SupportsFit: true, SupportsFitTarget: true);

        Check.Equal(3072, LlamaServerSession.IntegratedFitTargetMib, "the margin is 3,072 MiB, the one that served on Suseł");
        Check.Equal((int?)3072, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, b12189, apu)),
            "Suseł's APU with the Vulkan build gets it");
        Check.Equal((int?)3072, LlamaServerSession.FitTargetMib(Machine(null, b12189, apu)),
            "and so does a custom build there, which the list shows on the GPU");
        Check.Equal((int?)3072, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, b12189 with { Devices = null }, apu)),
            "and a build that could not list its devices, going by the system's word");

        Check.Equal((int?)null, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, b12189, discrete)),
            "a discrete card keeps llama.cpp's own margin");
        Check.Equal((int?)null, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, b12189, apu, discrete)),
            "and so does an APU beside a discrete card, as llama.cpp loads the model onto the discrete one");
        Check.Equal((int?)3072, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, b12189, apu, discrete with { HasVulkanDriver = false })),
            "unless the discrete card has no Vulkan driver, so the APU is the one llama.cpp sees");
        Check.Equal((int?)null, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, b12189,
                new GpuInfo("Intel(R) UHD Graphics 770", GpuVendor.Intel, true))),
            "a GPU the system can't tell apart is left alone");
        Check.Equal((int?)null, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, b12189 with { SupportsFitTarget = false }, apu)),
            "a build whose help doesn't list --fit-target is never given it");
        Check.Equal((int?)null, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, b12189 with { SupportsFit = false }, apu)),
            "nor one without --fit");
        Check.Equal((int?)null, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, null, apu)),
            "nor a build that hasn't been asked yet");
        Check.Equal((int?)null, LlamaServerSession.FitTargetMib(Machine(BuildKind.Vulkan, b12189 with { Devices = [] }, apu)),
            "a llama.cpp that lists no GPU runs on the processor, with no GPU margin to set");
        Check.Equal((int?)null, LlamaServerSession.FitTargetMib(Machine(BuildKind.Cpu, b12189 with { Devices = null }, apu)),
            "and so does an installed CPU build");
        Check.Equal((int?)null, LlamaServerSession.FitTargetMib(new Toolset { LlamaFeatures = b12189 }),
            "with no GPU detected at all, nothing changes");

        // The whole command line llama-server starts with, as the analysis builds it.
        var suselTools = Machine(BuildKind.Vulkan, b12189, apu);
        suselTools.LlmModelPath = "/models/Qwen3-8B-Q4_K_M.gguf";
        var line = string.Join(' ', LlamaServerSession.ServerArgs(suselTools, new AppSettings(), 32768, 18709));
        Check.Contains(line, "-m /models/Qwen3-8B-Q4_K_M.gguf --host 127.0.0.1 --port 18709 -c 32768 --fit on --fit-target 3072 -t ",
            "on Suseł, llama-server starts with --fit on --fit-target 3072, after the context");
        Check.True(!LlamaServerSession.ServerArgs(Machine(BuildKind.Vulkan, b12189, discrete), new AppSettings(), 32768, 18709)
                .Contains("--fit-target"),
            "on a discrete card, with no --fit-target");
        Check.True(!LlamaServerSession.ServerArgs(suselTools, new AppSettings { LlamaGpuLayers = 30 }, 32768, 18709).Contains("--fit-target"),
            "and with LlamaGpuLayers set, none either");
    }

    private static void OutOfMemoryLogs()
    {
        Check.Section("A model that ran out of memory is explained, not shown as a log (issue #54)");

        // Build 12189 on Suseł with -ngl 999, Qwen3 8B (Q6_K) and a 96k context.
        var radv = ModelDidNotFitException.FromLog(
            "0.00.189.003 W common_fit_params: failed to fit params to free device memory: n_gpu_layers already set by user to 999, abort\n" +
            "0.11.010.935 I cmn          init: llama threadpool init, n_threads = 6\n" +
            "radv/amdgpu: Not enough memory for command submission.\n" +
            "0.13.565.017 E ggml_vulkan: device lost on Vulkan0\n" +
            "terminate called after throwing an instance of 'vk::DeviceLostError'\n" +
            "  what():  vk::Queue::submit: ErrorDeviceLost\n");
        Check.Equal(ModelDidNotFitException.GraphicsMessage, radv?.Message,
            "RADV's \"Not enough memory for command submission\" reads as the graphics memory being too small");
        Check.True(radv?.Evidence.Contains("radv/amdgpu: Not enough memory for command submission.") == true &&
                   radv.Evidence.All(l => !l.Contains("n_gpu_layers")),
            "and that line is the evidence, not fit's own \"failed to fit\" notice");

        Check.Equal(ModelDidNotFitException.GraphicsMessage, ModelDidNotFitException.FromLog(
                "ggml_vulkan: Device memory allocation of size 4831838208 failed.\n" +
                "ggml_vulkan: vk::Device::allocateMemory: ErrorOutOfDeviceMemory\n" +
                "llama_model_load: error loading model: unable to allocate Vulkan0 buffer\n")?.Message,
            "a Vulkan allocation that fails is graphics memory");
        Check.Equal(ModelDidNotFitException.GraphicsMessage, ModelDidNotFitException.FromLog(
                "ggml_backend_cuda_buffer_type_alloc_buffer: allocating 4608.00 MiB on device 0: cudaMalloc failed: out of memory\n" +
                "CUDA error: out of memory\n")?.Message,
            "and so is CUDA's out of memory");
        Check.Equal(ModelDidNotFitException.SystemMessage, ModelDidNotFitException.FromLog(
                "ggml_backend_cpu_buffer_type_alloc_buffer: failed to allocate buffer of size 4831838208\n" +
                "llama_model_load: error loading model: unable to allocate CPU buffer\n")?.Message,
            "a CPU buffer that can't be allocated is the computer's memory");
        Check.Equal(ModelDidNotFitException.SystemMessage,
            ModelDidNotFitException.FromLog("terminate called after throwing an instance of 'std::bad_alloc'\n")?.Message,
            "as is the C++ runtime's bad_alloc, as from whisper.cpp");

        Check.True(ModelDidNotFitException.FromLog(
                "0.00.045.359 I srv    load_model: loading model '/models/Qwen3-8B-Q4_K_M.gguf'\n" +
                "0.04.097.298 W resolve_fused_ops: layer 1 is assigned to device Vulkan0 but Flash Attention over K/V rows is assigned to device CPU (usually due to missing support)\n" +
                "0.04.469.588 I srv    load_model: initializing, n_slots = 4, n_ctx_slot = 16384, kv_unified = 'true'\n" +
                "0.04.474.409 I srv  server_on_li: listening on http://127.0.0.1:18709\n") is null,
            "a server that started, warnings and all, shows no shortage");
        Check.True(ModelDidNotFitException.FromLog("llama_model_load: error loading model: invalid magic characters\n") is null,
            "nor a broken model file");
        Check.True(ModelDidNotFitException.FromLog("0.13.565.017 E ggml_vulkan: device lost on Vulkan0\n") is null,
            "nor a lost device alone, which a GPU hang ends in too");
    }

    private static void CrashRecord()
    {
        Check.Section("A model that was running when the app went down is explained at the next start (issue #54)");

        var dir = Path.Combine(Path.GetTempPath(), "reelsmagician-modelrun");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        var file = Path.Combine(dir, "model-running.json");

        Check.True(ModelRunMarker.TakeLeftover(file) is null, "with no record, there is nothing to explain");

        var fit = MemoryFit.AssessLlm(ModelCatalog.LlmModels[2], 32768, new MemoryBudget(MemoryKind.Graphics, 8 * GiB, "Radeon RX 7600"));
        var run = ModelRun.Of(ModelStage.Analysis, "qwen3-8b-q6", fit);
        ModelRunMarker.Begin(file, run);
        Check.True(File.Exists(file), "a record is written before the model starts");
        ModelRunMarker.End(file);
        Check.True(!File.Exists(file) && ModelRunMarker.TakeLeftover(file) is null, "and removed when its stage ends");

        ModelRunMarker.Begin(file, run);
        var leftover = ModelRunMarker.TakeLeftover(file);
        Check.True(leftover == run, "one left behind by a crash comes back as it was written");
        Check.True(!File.Exists(file), "and is removed, so it is explained once");
        Check.True(leftover?.WasTooBig == true, "8B Q6_K on an 8 GB card was too big");
        var text = leftover?.Explanation ?? "";
        Check.Contains(text, "Qwen3 8B (Q6_K) was analysing the transcript", "the explanation says what was running");
        Check.Contains(text, "It needs about 11 GB of graphics memory at that context, and this computer has 8 GB on Radeon RX 7600.",
            "with its estimate and the device");
        Check.Contains(text, "Choose a smaller model on the Setup tab.", "and what to do");
        Check.Equal("ReelsMagician closed while analysing", leftover?.Title, "its title names the stage");

        var fitted = ModelRun.Of(ModelStage.Transcription, "large-v3-turbo-q5",
            MemoryFit.AssessWhisper(ModelCatalog.DefaultWhisper, new MemoryBudget(MemoryKind.System, 25 * GiB)));
        Check.True(!fitted.WasTooBig && fitted.Explanation.Contains("may have been too much", StringComparison.Ordinal),
            "a model that should have fit is suggested more carefully");

        Directory.CreateDirectory(dir);
        File.WriteAllText(file, "{ not json");
        Check.True(ModelRunMarker.TakeLeftover(file) is null && !File.Exists(file), "an unreadable record is dropped, not shown");
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>The probe runs a stand-in llama-server, once per build, and keeps what it said.</summary>
    private static void Probe()
    {
        Check.Section("llama-server is asked once per build, off the UI thread (issue #54)");

        if (OperatingSystem.IsWindows())
        {
            // A batch file can't stand in for an .exe here; the parsing above is what differs.
            Check.True(true, "the stand-in llama-server is a shell script, so this part runs on Linux");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "reelsmagician-probe");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "llama-server");
        var calls = Path.Combine(dir, "calls.txt");
        File.WriteAllText(exe,
            "#!/bin/sh\n" +
            $"echo \"$1\" >> '{calls}'\n" +
            "case \"$1\" in\n" +
            $"  --list-devices) printf '{SuselDevices.Replace("\n", "\\n")}' ;;\n" +
            "  --help) printf '%s\\n' '-fit,  --fit [on|off]                   whether to adjust unset arguments' " +
            "'-fitt, --fit-target MiB0,MiB1,MiB2,...' ;;\n" +
            "esac\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var cache = Path.Combine(dir, "cache", "llama-devices.json");
        var probe = new LlamaProbe(cache);
        Check.True(probe.Cached(exe, "gpu") is null, "a build that was never asked has nothing cached");

        var features = probe.ProbeAsync(exe, "gpu", CancellationToken.None).GetAwaiter().GetResult();
        Check.True(features is { SupportsFit: true, SupportsFitTarget: true, Devices: [{ TotalBytes: 19062 * MiB }] },
            "the stand-in's devices, --fit and --fit-target are read");
        Check.True(File.Exists(cache), "and kept in the data folder");
        Check.True(new LlamaProbe(cache).Cached(exe, "gpu") is { SupportsFit: true, SupportsFitTarget: true, Devices.Count: 1 },
            "where the next start finds them without running anything");
        probe.ProbeAsync(exe, "gpu", CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(2, File.ReadAllLines(calls).Length, "asking again runs nothing more");

        // What a version before issue #71 kept: the same entry, with no answer for --fit-target.
        File.WriteAllText(cache, Regex.Replace(File.ReadAllText(cache), @",\s*""supportsFitTarget"":\s*true", ""));
        Check.True(!File.ReadAllText(cache).Contains("supportsFitTarget", StringComparison.Ordinal) && probe.Cached(exe, "gpu") is null,
            "an answer kept before the app asked about --fit-target counts as not asked (issue #71)");
        Check.True(probe.ProbeAsync(exe, "gpu", CancellationToken.None).GetAwaiter().GetResult() is { SupportsFitTarget: true } &&
                   File.ReadAllLines(calls).Length == 4,
            "so that build is asked once more, and says it has the option");
        Check.True(probe.Cached(exe, "gpu") is { SupportsFitTarget: true }, "which is kept for the next start");

        Check.True(probe.Cached(exe, "another gpu") is null, "a different GPU asks again");

        File.AppendAllText(exe, "# a new build\n");
        Check.True(probe.Cached(exe, "gpu") is null, "and so does a changed build");

        Check.True(probe.ProbeAsync(Path.Combine(dir, "missing"), "gpu", CancellationToken.None).GetAwaiter().GetResult() is null,
            "a llama-server that can't run gives no answer, and the system's figures are used");
        Directory.Delete(dir, recursive: true);
    }

    private static void SetupTab()
    {
        Check.Section("The Setup tab shows each model's need and warns under its drop-down (issue #54)");

        var root = IconChecks.FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;

        var app = Path.Combine(root, "src", "ReelsMagician.App");
        var window = File.ReadAllText(Path.Combine(app, "MainWindow.axaml"));
        foreach (var (model, warning) in new[] { ("Whisper", "WhisperFitWarning"), ("Llm", "LlmFitWarning") })
        {
            Check.Contains(window, $"ItemsSource=\"{{Binding {model}Options}}\"", $"the {model} drop-down lists the models with their fit");
            Check.Contains(window, $"SelectedItem=\"{{Binding Selected{model}Option}}\"", $"and selects one of them");
            Check.True(Regex.IsMatch(window, $"<local:InfoBox[^>]*Kind=\"Warning\"[^>]*Text=\"{{Binding {warning}}}\""),
                $"a warning box under it shows {warning}");
        }
        Check.True(Regex.IsMatch(window, "<DataTemplate x:Key=\"ModelOption\"[^>]*>\\s*<StackPanel[^>]*>\\s*<local:FaIcon Data=\"\\{StaticResource fa-triangle-exclamation\\}\"[^>]*IsVisible=\"\\{Binding MayNotFit\\}\""),
            "a model that may not fit is marked with the amber triangle");
        Check.Equal(2, Regex.Matches(window, "ItemTemplate=\"\\{StaticResource ModelOption\\}\"").Count,
            "in both drop-downs");

        var code = File.ReadAllText(Path.Combine(app, "ViewModels", "MainViewModel.cs"));
        Check.Contains(code, "AcceptedMemoryRisks", "the question before a run is remembered per model");
        Check.Contains(code, "ModelRunMarker.TakeLeftover", "a crash left behind is explained at the next start");
    }
}
