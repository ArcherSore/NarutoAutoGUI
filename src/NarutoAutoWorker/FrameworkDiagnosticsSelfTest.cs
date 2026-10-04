using System.Diagnostics;
using System.Runtime.InteropServices;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Custom;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

// Opt-in native integration test. Each case runs in its own published Worker process, without RDP or a game.
internal static class FrameworkDiagnosticsSelfTest
{
    internal static int Run(string projectRoot, string scenario)
    {
        try {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var root = Path.GetFullPath(projectRoot);
            if (Directory.Exists(root)) {
                throw new InvalidOperationException("原生测试要求新的输出目录，避免旧图片干扰结果。");
            }
            var options = scenario switch {
                "defaults" => new MaaFrameworkOptions(),
                "disabled" => new MaaFrameworkOptions(false, false, false),
                "draw-only" => new MaaFrameworkOptions(false, true, false),
                "debug-only" => new MaaFrameworkOptions(false, false, true),
                "all" => new MaaFrameworkOptions(true, true, true),
                _ => throw new ArgumentException($"未知测试场景：{scenario}")
            };
            WorkerFrameworkOptions.Apply(root, options, (level, source, message) => {
                var line = $"{level} {source} {message}";
                Console.WriteLine(line);
                File.AppendAllText(Path.Combine(root, "framework-options.log"), line + Environment.NewLine);
            });
            var resourcePath = Path.Combine(root, "resource");
            Directory.CreateDirectory(Path.Combine(resourcePath, "pipeline"));
            Directory.CreateDirectory(Path.Combine(resourcePath, "image"));
            var imagePath = Path.Combine(resourcePath, "image", "fixture.png");
            CreateFixtureImage(imagePath);
            File.WriteAllText(Path.Combine(resourcePath, "pipeline", "test.json"), """
                {
                  "Recognize": {
                    "recognition": "TemplateMatch", "template": "fixture.png", "threshold": 0.99,
                    "action": "DoNothing", "next": ["NeverMatch"], "timeout": 200, "rate_limit": 20,
                    "pre_delay": 0, "post_delay": 0
                  },
                  "NeverMatch": {
                    "recognition": "TemplateMatch", "template": "fixture.png", "threshold": 0.99,
                    "inverse": true, "action": "DoNothing", "pre_delay": 0, "post_delay": 0
                  }
                }
                """);

            using var controller = new MaaCustomController(new FixtureController(File.ReadAllBytes(imagePath)),
                LinkOption.Start, CheckStatusOption.ThrowIfNotSucceeded);
            if (!controller.SetOption(ControllerOption.ScreenshotTargetLongSide, 64)) {
                throw new InvalidOperationException("原生测试截图尺寸设置失败。");
            }
            using var resource = new MaaResource(CheckStatusOption.ThrowIfNotSucceeded, resourcePath);
            using var tasker = new MaaTasker {
                Controller = controller, Resource = resource, DisposeOptions = DisposeOptions.None
            };
            if (!tasker.IsInitialized) {
                throw new InvalidOperationException("原生测试 Tasker 未初始化。");
            }
            var job = tasker.AppendTask("Recognize");
            var completion = Task.Run(job.Wait);
            if (!completion.Wait(TimeSpan.FromSeconds(15)) || completion.Result != MaaJobStatus.Failed) {
                throw new InvalidOperationException("测试任务必须通过真实识别后在 NeverMatch 超时失败。");
            }
            if (!tasker.GetTaskDetail(job.Id, out _, out var nodes, out _) || nodes.Length == 0
                || !tasker.GetNodeDetail(nodes[0], out var name, out var recognitionId, out _, out _)
                || name != "Recognize") {
                throw new InvalidOperationException("测试未执行成功的 TemplateMatch，不能证明可视化图片保存。");
            }
            using var raw = new MaaImageBuffer();
            using var draws = new MaaImageListBuffer();
            using var box = new MaaRectBuffer();
            if (!tasker.GetRecognitionDetail(recognitionId, out _, out _, out var hit, box, out _, raw, draws)
                || !hit || !raw.IsEmpty != options.DebugMode) {
                throw new InvalidOperationException("DebugMode 必须独立控制识别原图的缓存。");
            }
            var errors = VerifyImages(root, "on_error", ".png", options.SaveOnError);
            var visions = VerifyImages(root, "vision", ".jpg", options.SaveDraw);
            if (!File.Exists(Path.Combine(root, "debug", "maafw.log"))) {
                throw new InvalidOperationException("显式 LogDir 未产生框架日志。");
            }
            using var process = Process.GetCurrentProcess();
            var runtime = process.Modules.Cast<ProcessModule>()
                .Single(module => module.ModuleName.Equals("MaaFramework.dll", StringComparison.OrdinalIgnoreCase));
            if (!runtime.FileName.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException($"测试加载了发布目录以外的 runtime：{runtime.FileName}");
            }
            Console.WriteLine($"NATIVE FRAMEWORK TEST PASS: {scenario}; on_error={errors}; vision={visions}; "
                + $"debugRaw={!raw.IsEmpty}; runtime={NativeBindingContext.LibraryVersion}; DLL={runtime.FileName}; "
                + $"LogDir={Path.Combine(root, "debug")}");
            return 0;
        } catch (Exception exception) {
            Console.Error.WriteLine($"NATIVE FRAMEWORK TEST FAIL: {scenario}: {exception}");
            return 1;
        }
    }

    private static int VerifyImages(string root, string folder, string extension, bool expected)
    {
        var path = Path.Combine(root, "debug", folder);
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*" + extension, SearchOption.AllDirectories) : [];
        if ((files.Length > 0) != expected) {
            throw new InvalidOperationException($"{folder} 图片保存不符合开关：expected={expected}, count={files.Length}。");
        }
        foreach (var file in files) {
            using var image = new MaaImageBuffer();
            if (!image.TrySetEncodedData(File.ReadAllBytes(file)) || image.IsEmpty
                || image.Width < 64 || image.Height < 64) {
                throw new InvalidOperationException($"框架保存的图片无法解码或尺寸无效：{file}");
            }
        }
        return files.Length;
    }

    private static void CreateFixtureImage(string path)
    {
        var pixels = new byte[64 * 64 * 3];
        new Random(42).NextBytes(pixels);
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try {
            using var image = new MaaImageBuffer();
            const int cv8Uc3 = 16;
            if (!image.TrySetRawData(pinned.AddrOfPinnedObject(), 64, 64, cv8Uc3)
                || !image.TryGetEncodedData(out byte[]? png) || png is null) {
                throw new InvalidOperationException("无法生成识别用 PNG 测试图。");
            }
            File.WriteAllBytes(path, png);
        } finally {
            pinned.Free();
        }
    }

    private sealed class FixtureController(byte[] png) : IMaaCustomController
    {
        public string Name { get; set; } = "diagnostics-self-test";
        public void Dispose()
        {
        }
        public bool Connect() => true;
        public bool Connected() => true;
        public bool RequestUuid(IMaaStringBuffer buffer) => buffer.TrySetValue("diagnostics-self-test");
        public ControllerFeatures GetFeatures() => default;
        public bool Screencap(IMaaImageBuffer buffer) => buffer.TrySetEncodedData(png);
        public bool GetInfo(IMaaStringBuffer buffer) => buffer.TrySetValue("{}");
        public bool StartApp(string intent) => false;
        public bool StopApp(string intent) => false;
        public bool Click(int x, int y) => false;
        public bool Swipe(int x1, int y1, int x2, int y2, int duration) => false;
        public bool TouchDown(int contact, int x, int y, int pressure) => false;
        public bool TouchMove(int contact, int x, int y, int pressure) => false;
        public bool TouchUp(int contact) => false;
        public bool ClickKey(int key) => false;
        public bool InputText(string text) => false;
        public bool KeyDown(int key) => false;
        public bool KeyUp(int key) => false;
        public bool Scroll(int dx, int dy) => false;
        public bool RelativeMove(int dx, int dy) => false;
        public bool Shell(string command, long timeout, IMaaStringBuffer buffer) => false;
        public bool Inactive() => true;
    }
}
