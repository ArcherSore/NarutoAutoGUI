namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyLaunchCommand()
    {
        Require(LaunchCommand.Parse([]) == new LaunchCommand(), "没有参数时必须是普通手动启动。");
        var boot = new LaunchCommand(AutoStart: true);
        var full = LaunchCommand.Parse(["--autostart", "-i", "日常 任务", "-q"]);
        Require(full == new LaunchCommand(true, true, "日常 任务"), "配置名含空格时必须整体作为 --instance 的值。");
        foreach (var spelling in new[] { new[] { "--instance", "小号" }, ["--INSTANCE=小号"], ["-i=小号"], ["-I", "小号"] }) {
            Require(LaunchCommand.Parse(spelling).Instance == "小号", $"必须识别 --instance 写法：{spelling[0]}。");
        }
        Require(LaunchCommand.Parse(["--AutoStart", "--quit-after-run"]) == new LaunchCommand(true, true),
            "长参数不区分大小写。");
        Require(LaunchCommand.Parse(["--unknown", "-x", "value", "--autostart"]) == boot,
            "无法识别的参数必须被忽略，不能影响其余参数。");
        Require(LaunchCommand.Parse(["-i", "--autostart"]) == boot,
            "--instance 缺少值时不能把下一个参数当作配置名。");
        Require(LaunchCommand.Parse(["-i", "  "]).Instance is null, "空白配置名按未指定处理。");
    }

    private static void VerifyRunningCopies()
    {
        const string own = @"C:\Bunshin\Bunshin.exe";
        Require(RunningCopies.Exists(own, 10, [(10, own), (11, @"c:\bunshin\bunshin.exe")]),
            "另一个会话里已有同一程序在运行时，系统发起的启动必须让位（路径不区分大小写）。");
        Require(!RunningCopies.Exists(own, 10, [(10, own), (12, @"D:\Other\Bunshin.exe"), (13, null)]),
            "只有自己，或别处的另一份程序、无法读取路径的进程，都不算已有副本。");
    }
}
