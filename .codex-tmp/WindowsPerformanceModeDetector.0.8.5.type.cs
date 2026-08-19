using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vellume.Core;

namespace Vellume.Windows;

public sealed class WindowsPerformanceModeDetector
{
	private readonly object _sync = new object();

	private DateTimeOffset _lastCaptureAt;

	private ExclusiveGpuProcessSnapshot _lastCapture;

	private string _lastRuleKey = string.Empty;

	public bool RequiresExclusiveGpuIsolation()
	{
		return CaptureExclusiveGpuApplication(ApplicationPlaybackRule.CreateDefaults()).IsRunning;
	}

	internal void Invalidate()
	{
		lock (_sync)
		{
			_lastCaptureAt = default(DateTimeOffset);
			_lastCapture = default(ExclusiveGpuProcessSnapshot);
			_lastRuleKey = string.Empty;
		}
	}

	public bool IsExclusiveGpuApplicationForeground()
	{
		nint foregroundWindow = NativeMethods.GetForegroundWindow();
		if (foregroundWindow == IntPtr.Zero || !NativeMethods.IsWindow(foregroundWindow))
		{
			return false;
		}
		NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var processId);
		if (processId == 0)
		{
			return false;
		}
		try
		{
			using Process process = Process.GetProcessById(checked((int)processId));
			return IsExclusiveGpuProcessName(process.ProcessName);
		}
		catch
		{
			return false;
		}
	}

	internal ExclusiveGpuProcessSnapshot CaptureExclusiveGpuApplication(IReadOnlyList<ApplicationPlaybackRule>? rules = null)
	{
		if (rules == null)
		{
			rules = ApplicationPlaybackRule.CreateDefaults();
		}
		var source = (from rule in rules
			where rule != null && rule.Condition == ApplicationRuleCondition.IsRunning && !string.IsNullOrWhiteSpace(rule.ProcessName)
			select new
			{
				ProcessName = NormalizeProcessName(rule.ProcessName),
				Action = rule.Action
			} into rule
			where !string.IsNullOrWhiteSpace(rule.ProcessName)
			orderby rule.Action descending
			select rule).ToArray();
		string text = string.Join("|", source.Select(rule => $"{rule.ProcessName}:{rule.Action}"));
		lock (_sync)
		{
			DateTimeOffset utcNow = DateTimeOffset.UtcNow;
			if (text.Equals(_lastRuleKey, StringComparison.Ordinal) && utcNow - _lastCaptureAt < TimeSpan.FromMilliseconds(150.0))
			{
				return _lastCapture;
			}
			_lastCapture = CaptureCore(source.Select(rule => rule.ProcessName));
			_lastCaptureAt = utcNow;
			_lastRuleKey = text;
			return _lastCapture;
		}
	}

	internal static bool IsExclusiveGpuProcessName(string processName)
	{
		string processName2 = processName;
		return ApplicationPlaybackRule.CreateDefaults().Any((ApplicationPlaybackRule rule) => NormalizeProcessName(rule.ProcessName).Equals(NormalizeProcessName(processName2), StringComparison.OrdinalIgnoreCase));
	}

	internal static bool ShouldIsolateExclusiveProcess(bool hasVisibleTopLevelWindow, bool hasNonMinimizedTopLevelWindow)
	{
		return !hasVisibleTopLevelWindow || hasNonMinimizedTopLevelWindow;
	}

	private static ExclusiveGpuProcessSnapshot CaptureCore(IEnumerable<string> processNames)
	{
		foreach (string item in processNames.Distinct<string>(StringComparer.OrdinalIgnoreCase))
		{
			Process[] processesByName;
			try
			{
				processesByName = Process.GetProcessesByName(item);
			}
			catch
			{
				continue;
			}
			try
			{
				Process[] array = processesByName;
				foreach (Process process in array)
				{
					try
					{
						if (!process.HasExited && RequiresIsolation(process.Id))
						{
							return new ExclusiveGpuProcessSnapshot(IsRunning: true, process.Id, process.ProcessName);
						}
					}
					catch
					{
					}
				}
			}
			finally
			{
				Process[] array = processesByName;
				for (int i = 0; i < array.Length; i++)
				{
					array[i].Dispose();
				}
			}
		}
		return default(ExclusiveGpuProcessSnapshot);
	}

	internal static string NormalizeProcessName(string? processName)
	{
		string text = processName?.Trim() ?? string.Empty;
		if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			return text;
		}
		string text2 = text;
		return text2.Substring(0, text2.Length - 4);
	}

	private static bool RequiresIsolation(int processId)
	{
		bool hasVisibleTopLevelWindow = false;
		bool hasNonMinimizedTopLevelWindow = false;
		NativeMethods.EnumWindows(delegate(nint windowHandle, nint _)
		{
			NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId2);
			if (processId2 != processId || !NativeMethods.IsWindowVisible(windowHandle))
			{
				return true;
			}
			hasVisibleTopLevelWindow = true;
			if (!NativeMethods.IsIconic(windowHandle))
			{
				hasNonMinimizedTopLevelWindow = true;
				return false;
			}
			return true;
		}, IntPtr.Zero);
		return ShouldIsolateExclusiveProcess(hasVisibleTopLevelWindow, hasNonMinimizedTopLevelWindow);
	}
}
