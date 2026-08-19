using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MaaNOP.ChildSessionLauncher;

// Adapted from BetterGI 0.63.0 ChildSessionProcessLauncher.cs.
// Reuses the verified approach: Windows Task Scheduler COM (Schedule.Service) with a temporary
// task, RunEx(flags = TASK_RUN_USE_SESSION_ID, sessionId = childSessionId) to start the process
// inside the Child Session, then delete the temporary task.
//
// Deviations (justified):
//  - RunLevel = LeastPrivilege (0) instead of Highest (1). BetterGI uses Highest because it needs
//    elevated input injection for game automation. The two PoC targets need no elevation, so
//    LeastPrivilege avoids unnecessary UAC at the spawned process.
//  - COM calls use Type.InvokeMember reflection instead of `dynamic`, to stay free of any DLR /
//    Microsoft.CSharp dependency. Equivalent IDispatch dispatch.
internal static class ChildSessionProcessLauncher
{
    private const int TaskActionExecute = 0;
    private const int TaskCreate = 2;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLeastPrivilege = 0;
    private const int TaskRunLevelHighest = 1;
    private const int TaskRunUseSessionId = 0x4;

    internal static Task LaunchAsync(
        uint childSessionId,
        string executablePath,
        string arguments = "",
        string? workingDirectory = null)
    {
        return LaunchAtRunLevelAsync(
            childSessionId,
            executablePath,
            arguments,
            workingDirectory,
            TaskRunLevelLeastPrivilege);
    }

    internal static Task LaunchElevatedAsync(
        uint childSessionId,
        string executablePath,
        string arguments = "",
        string? workingDirectory = null)
    {
        return LaunchAtRunLevelAsync(
            childSessionId,
            executablePath,
            arguments,
            workingDirectory,
            TaskRunLevelHighest);
    }

    private static Task LaunchAtRunLevelAsync(
        uint childSessionId,
        string executablePath,
        string arguments,
        string? workingDirectory,
        int runLevel)
    {
        var fullPath = ValidateExecutablePath(executablePath);
        var workDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? (Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory)
            : workingDirectory;
        return Task.Run(() =>
            LaunchWithTemporaryTask(childSessionId, fullPath, arguments, workDir, runLevel));
    }

    private static void LaunchWithTemporaryTask(
        uint childSessionId,
        string executablePath,
        string arguments,
        string workingDirectory,
        int runLevel)
    {
        // Guard: the target Child Session must still be the one the caller expects.
        var actual = ChildSessionNativeMethods.TryGetChildSessionId();
        if (actual != childSessionId)
        {
            throw new InvalidOperationException(
                $"目标 Child Session 已发生变化。请求会话 {childSessionId}，当前会话 "
                + (actual?.ToString(CultureInfo.InvariantCulture) ?? "无") + "。");
        }

        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("当前 Windows 未提供任务计划程序 COM 服务。");
        var taskName = $"MaaNOP-ChildSession-Launch-{Guid.NewGuid():N}";
        string accountName;
        using (var currentIdentity = WindowsIdentity.GetCurrent())
        {
            accountName = currentIdentity.Name;
        }

        object? schedulerObj = null;
        object? rootFolderObj = null;
        object? taskDefObj = null;
        object? actionObj = null;
        object? registeredTaskObj = null;
        object? runningTaskObj = null;
        var taskRegistered = false;

        try
        {
            schedulerObj = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("无法创建任务计划程序 COM 对象。");
            InvokeMember(schedulerObj, "Connect");

            rootFolderObj = InvokeMember(schedulerObj, "GetFolder", "\\");
            taskDefObj = InvokeMember(schedulerObj, "NewTask", 0);

            var registrationInfo = GetProperty(taskDefObj, "RegistrationInfo");
            SetProperty(registrationInfo, "Author", "MaaNOP");
            SetProperty(
                registrationInfo,
                "Description",
                $"临时启动 {Path.GetFileName(executablePath)} 到 Child Session {childSessionId}");

            var settings = GetProperty(taskDefObj, "Settings");
            SetProperty(settings, "Enabled", true);
            SetProperty(settings, "Hidden", true);
            SetProperty(settings, "AllowDemandStart", true);
            SetProperty(settings, "DisallowStartIfOnBatteries", false);
            SetProperty(settings, "StopIfGoingOnBatteries", false);
            SetProperty(settings, "ExecutionTimeLimit", "PT0S");

            var principal = GetProperty(taskDefObj, "Principal");
            SetProperty(principal, "UserId", accountName);
            SetProperty(principal, "LogonType", TaskLogonInteractiveToken);
            SetProperty(principal, "RunLevel", runLevel);

            var actions = GetProperty(taskDefObj, "Actions");
            actionObj = InvokeMember(actions, "Create", TaskActionExecute);
            SetProperty(actionObj, "Path", executablePath);
            SetProperty(actionObj, "Arguments", arguments);
            SetProperty(actionObj, "WorkingDirectory", workingDirectory);

            registeredTaskObj = InvokeMember(
                rootFolderObj,
                "RegisterTaskDefinition",
                taskName,
                taskDefObj,
                TaskCreate,
                accountName,
                null,
                TaskLogonInteractiveToken,
                null);
            taskRegistered = true;

            runningTaskObj = InvokeMember(
                registeredTaskObj,
                "RunEx",
                null,
                TaskRunUseSessionId,
                checked((int)childSessionId),
                null);

            if (runningTaskObj is null)
            {
                throw new InvalidOperationException("任务计划程序没有返回运行实例。");
            }
        }
        finally
        {
            if (taskRegistered && rootFolderObj is not null)
            {
                try
                {
                    InvokeMember(rootFolderObj, "DeleteTask", taskName, 0);
                }
                catch (COMException)
                {
                    // Temporary task already started the target; cleanup failure must not abort it.
                }
            }

            ReleaseComObject(runningTaskObj);
            ReleaseComObject(registeredTaskObj);
            ReleaseComObject(actionObj);
            ReleaseComObject(taskDefObj);
            ReleaseComObject(rootFolderObj);
            ReleaseComObject(schedulerObj);
        }
    }

    private static string ValidateExecutablePath(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("要启动的程序不存在。", fullPath);
        }

        if (!string.Equals(
                Path.GetExtension(fullPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("当前只允许选择 .exe 程序。", nameof(executablePath));
        }

        return fullPath;
    }

    private static object? GetProperty(object? target, string propertyName)
    {
        EnsureCom(target, propertyName);
        return target!.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target,
            args: null,
            CultureInfo.InvariantCulture);
    }

    private static void SetProperty(object? target, string propertyName, object? value)
    {
        EnsureCom(target, propertyName);
        target!.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target,
            new object?[] { value },
            CultureInfo.InvariantCulture);
    }

    private static object? InvokeMember(object? target, string methodName, params object?[]? args)
    {
        EnsureCom(target, methodName);
        return target!.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            binder: null,
            target,
            args,
            CultureInfo.InvariantCulture);
    }

    private static void EnsureCom(object? target, string member)
    {
        if (target is null)
        {
            throw new InvalidOperationException($"COM 对象为 null，无法调用 {member}。");
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
