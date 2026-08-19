using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("Vellume.Core")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyProduct("Vellume.Core")]
[assembly: AssemblyTitle("Vellume.Core")]
[assembly: AssemblyVersion("1.0.0.0")]
[module: RefSafetyRules(11)]
namespace Vellume.Core;

public sealed class AppPaths
{
	public string RootDirectory { get; }

	public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

	public string LogDirectory => Path.Combine(RootDirectory, "Logs");

	public string LogFile => Path.Combine(LogDirectory, "vellume.log");

	public string WebViewDataDirectory => Path.Combine(RootDirectory, "WebView2");

	public string SessionMarkerFile => Path.Combine(RootDirectory, "session.active");

	public AppPaths(string? rootDirectory = null)
	{
		RootDirectory = rootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vellume");
	}

	public void EnsureCreated()
	{
		Directory.CreateDirectory(RootDirectory);
		Directory.CreateDirectory(LogDirectory);
		Directory.CreateDirectory(WebViewDataDirectory);
	}
}
public interface IMonitorService
{
	IReadOnlyList<DisplayInfo> GetDisplays();
}
public interface IWallpaperController : IAsyncDisposable
{
	WallpaperPlaybackState State { get; }

	event EventHandler<WallpaperStateChangedEventArgs>? StateChanged;

	event EventHandler<ExclusiveGpuIsolationChangedEventArgs>? ExclusiveGpuIsolationChanged;

	event EventHandler<int>? QuickSlotInvoked;

	event EventHandler<int>? QuickSlotSecondaryInvoked;

	event EventHandler<QuickSlotLayoutChangedEventArgs>? QuickSlotLayoutChanged;

	Task ApplyAsync(WallpaperItem wallpaper, DisplayInfo display, PlaybackOptions options, WidgetSettings widgets, CancellationToken cancellationToken = default(CancellationToken));

	Task StopAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task PauseAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task ResumeAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task UpdatePlaybackAsync(PlaybackOptions options, CancellationToken cancellationToken = default(CancellationToken));

	Task UpdateWidgetsAsync(WidgetSettings widgets, DisplayInfo? display = null, CancellationToken cancellationToken = default(CancellationToken));

	Task UpdateQuickSlotRuntimeAsync(WidgetSettings widgets, CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> ToggleMediaPlaybackAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> SkipToNextMediaAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> SkipToPreviousMediaAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task<string> InstallCodexCompanionBridgeAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task CompleteDesktopWallpaperHandoffAsync(CancellationToken cancellationToken = default(CancellationToken));
}
public interface ISystemShortcutService
{
	bool RecycleBinHasItems();

	Task LaunchAsync(SystemShortcutKind kind, CancellationToken cancellationToken = default(CancellationToken));

	Task EmptyRecycleBinAsync(nint ownerWindow, CancellationToken cancellationToken = default(CancellationToken));
}
public interface ISettingsStore
{
	Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default(CancellationToken));
}
public interface IAppLogger
{
	Task InfoAsync(string message);

	Task ErrorAsync(string message, Exception? exception = null);
}
public sealed class FileAppLogger : IAppLogger, IDisposable
{
	private readonly string _logPath;

	private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

	private bool _disposed;

	public FileAppLogger(string logPath)
	{
		_logPath = logPath;
	}

	public Task InfoAsync(string message)
	{
		return WriteAsync("INFO", message, null);
	}

	public Task ErrorAsync(string message, Exception? exception = null)
	{
		return WriteAsync("ERROR", message, exception);
	}

	private async Task WriteAsync(string level, string message, Exception? exception)
	{
		if (_disposed)
		{
			return;
		}
		StringBuilder builder = new StringBuilder().Append(DateTimeOffset.Now.ToString("O")).Append(" [").Append(level)
			.Append("] ")
			.AppendLine(message);
		if (exception != null)
		{
			builder.AppendLine(exception.ToString());
		}
		await _writeLock.WaitAsync();
		try
		{
			string directoryName = Path.GetDirectoryName(_logPath);
			if (!string.IsNullOrWhiteSpace(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			await File.AppendAllTextAsync(_logPath, builder.ToString());
		}
		finally
		{
			_writeLock.Release();
		}
	}

	public void Dispose()
	{
		_disposed = true;
		_writeLock.Dispose();
	}
}
public sealed class JsonSettingsStore : ISettingsStore
{
	private readonly string _settingsPath;

	private readonly SemaphoreSlim _accessLock = new SemaphoreSlim(1, 1);

	private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		Converters = { (JsonConverter)new JsonStringEnumConverter() }
	};

	public JsonSettingsStore(string settingsPath)
	{
		_settingsPath = settingsPath;
	}

	public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await _accessLock.WaitAsync(cancellationToken);
		try
		{
			if (!File.Exists(_settingsPath))
			{
				return new AppSettings();
			}
			FileStream stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
			AppSettings result;
			try
			{
				AppSettings appSettings = (await JsonSerializer.DeserializeAsync<AppSettings>(stream, _jsonOptions, cancellationToken)) ?? new AppSettings();
				int schemaVersion = appSettings.SchemaVersion;
				AppSettings appSettings2 = appSettings;
				if (appSettings2.Library == null)
				{
					appSettings2.Library = new List<WallpaperItem>();
				}
				appSettings2 = appSettings;
				if (appSettings2.Playback == null)
				{
					appSettings2.Playback = new PlaybackOptions();
				}
				appSettings2 = appSettings;
				if (appSettings2.Widgets == null)
				{
					appSettings2.Widgets = new WidgetSettings();
				}
				appSettings2 = appSettings;
				if (appSettings2.Dock == null)
				{
					appSettings2.Dock = new DockSettings();
				}
				WidgetSettings widgets = appSettings.Widgets;
				if (widgets.QuickSlots == null)
				{
					widgets.QuickSlots = new List<QuickSlotItem>();
				}
				widgets = appSettings.Widgets;
				if (widgets.QuickSlotGroups == null)
				{
					widgets.QuickSlotGroups = new List<QuickSlotGroup>();
				}
				widgets = appSettings.Widgets;
				if (widgets.SystemShortcuts == null)
				{
					widgets.SystemShortcuts = new List<SystemShortcutSetting>();
				}
				if (schemaVersion < 5)
				{
					foreach (QuickSlotGroup quickSlotGroup in appSettings.Widgets.QuickSlotGroups)
					{
						quickSlotGroup.Columns = 5;
					}
				}
				if (schemaVersion < 6)
				{
					MigrateLegacySystemShortcuts(appSettings.Widgets);
				}
				if (schemaVersion < 7)
				{
					AddPowerShellShortcut(appSettings.Widgets);
				}
				if (schemaVersion < 8)
				{
					ConsolidatePresetGroups(appSettings.Widgets);
				}
				if (schemaVersion < 10)
				{
					LocalizePresetNames(appSettings.Widgets);
				}
				if (schemaVersion < 12)
				{
					MigrateQuickSlotsToDock(appSettings.Widgets);
				}
				if (schemaVersion < 13 && (appSettings.Playback.ApplicationRules == null || appSettings.Playback.ApplicationRules.Count == 0))
				{
					appSettings.Playback.ApplicationRules = ApplicationPlaybackRule.CreateDefaults();
				}
				PlaybackOptions playback = appSettings.Playback;
				if (playback.ApplicationRules == null)
				{
					playback.ApplicationRules = ApplicationPlaybackRule.CreateDefaults();
				}
				if (schemaVersion == 14)
				{
					MigrateDefaultUnrealRulesToRendererIsolation(appSettings.Playback);
				}
				appSettings.SchemaVersion = 15;
				appSettings.Playback = appSettings.Playback.Copy();
				appSettings.Dock = appSettings.Dock.Copy();
				WidgetSettings widgetSettings = appSettings.Widgets.Copy();
				appSettings.Widgets.QuickSlots = widgetSettings.QuickSlots;
				appSettings.Widgets.SystemShortcuts = widgetSettings.SystemShortcuts;
				appSettings.Widgets.QuickSlotGroups = widgetSettings.QuickSlotGroups;
				result = appSettings;
			}
			finally
			{
				if (stream != null)
				{
					await stream.DisposeAsync();
				}
			}
			return result;
		}
		finally
		{
			_accessLock.Release();
		}
	}

	public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default(CancellationToken))
	{
		await _accessLock.WaitAsync(cancellationToken);
		try
		{
			settings.SchemaVersion = 15;
			settings.Playback = (settings.Playback ?? new PlaybackOptions()).Copy();
			settings.Dock = (settings.Dock ?? new DockSettings()).Copy();
			Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? throw new InvalidOperationException("The settings path has no parent directory."));
			string temporaryPath = _settingsPath + ".tmp";
			FileStream stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
			try
			{
				await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken);
				await stream.FlushAsync(cancellationToken);
			}
			finally
			{
				if (stream != null)
				{
					await stream.DisposeAsync();
				}
			}
			File.Move(temporaryPath, _settingsPath, overwrite: true);
		}
		finally
		{
			_accessLock.Release();
		}
	}

	private static void MigrateDefaultUnrealRulesToRendererIsolation(PlaybackOptions playback)
	{
		List<ApplicationPlaybackRule> pausedRunningRules = (playback.ApplicationRules ?? new List<ApplicationPlaybackRule>()).Where((ApplicationPlaybackRule rule) => rule != null && rule.Condition == ApplicationRuleCondition.IsRunning && rule.Action == ApplicationPlaybackAction.Pause && IsUnrealEditorProcessName(rule.ProcessName)).ToList();
		if (!new string[3] { "UnrealEditor", "UE4Editor", "UE5Editor" }.All((string processName) => pausedRunningRules.Any((ApplicationPlaybackRule rule) => NormalizeExecutableName(rule.ProcessName).Equals(processName, StringComparison.OrdinalIgnoreCase))))
		{
			return;
		}
		foreach (ApplicationPlaybackRule item in pausedRunningRules)
		{
			item.Action = ApplicationPlaybackAction.StopFreeMemory;
		}
	}

	private static bool IsUnrealEditorProcessName(string? processName)
	{
		string text = NormalizeExecutableName(processName);
		if (!text.Equals("UnrealEditor", StringComparison.OrdinalIgnoreCase) && !text.Equals("UE4Editor", StringComparison.OrdinalIgnoreCase))
		{
			return text.Equals("UE5Editor", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static string NormalizeExecutableName(string? processName)
	{
		string text = processName?.Trim() ?? string.Empty;
		if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			string text2 = text;
			text = text2.Substring(0, text2.Length - 4);
		}
		return text;
	}

	private static void MigrateLegacySystemShortcuts(WidgetSettings widgets)
	{
		List<SystemShortcutSetting> list = SystemShortcutSetting.CreateDefaults();
		string value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
		QuickSlotItem[] array = widgets.QuickSlots.ToArray();
		foreach (QuickSlotItem quickSlotItem in array)
		{
			SystemShortcutKind? kind = (quickSlotItem.TargetPath.Equals("shell:MyComputerFolder", StringComparison.OrdinalIgnoreCase) ? new SystemShortcutKind?(SystemShortcutKind.ThisPc) : (quickSlotItem.TargetPath.Equals("shell:RecycleBinFolder", StringComparison.OrdinalIgnoreCase) ? new SystemShortcutKind?(SystemShortcutKind.RecycleBin) : (quickSlotItem.TargetPath.Equals(value, StringComparison.OrdinalIgnoreCase) ? new SystemShortcutKind?(SystemShortcutKind.FileExplorer) : null)));
			if (kind.HasValue)
			{
				SystemShortcutSetting systemShortcutSetting = list.First((SystemShortcutSetting item) => item.Kind == kind.Value);
				if (!systemShortcutSetting.IsVisible)
				{
					systemShortcutSetting.IsVisible = true;
					systemShortcutSetting.Slot = quickSlotItem.Slot;
				}
				widgets.QuickSlots.Remove(quickSlotItem);
			}
		}
		widgets.SystemShortcuts = list;
	}

	private static void ConsolidatePresetGroups(WidgetSettings widgets)
	{
		WidgetSettings widgetSettings = widgets;
		if (widgetSettings.QuickSlotGroups == null)
		{
			List<QuickSlotGroup> list2 = (widgetSettings.QuickSlotGroups = new List<QuickSlotGroup>());
		}
		widgetSettings = widgets;
		if (widgetSettings.QuickSlots == null)
		{
			List<QuickSlotItem> list4 = (widgetSettings.QuickSlots = new List<QuickSlotItem>());
		}
		HashSet<string> groupIds = GetGroupIds(widgets, "Work", "작업", "Development", "Productivity");
		HashSet<string> groupIds2 = GetGroupIds(widgets, "Apps", "앱", "Communication", "Browsers");
		HashSet<string> groupIds3 = GetGroupIds(widgets, "Game", "게임", "Games", "Launchers");
		QuickSlotGroup quickSlotGroup = FindOrCreateGroup(widgets, "general", "기본", "General");
		QuickSlotGroup work = FindOrCreateGroup(widgets, "work", "작업", "Work", "Development", "Productivity");
		QuickSlotGroup apps = FindOrCreateGroup(widgets, "apps", "앱", "Apps", "Communication", "Browsers");
		QuickSlotGroup game = FindOrCreateGroup(widgets, "game", "게임", "Game", "Games", "Launchers");
		quickSlotGroup.Name = "기본";
		work.Name = "작업";
		apps.Name = "앱";
		game.Name = "게임";
		groupIds.Add(work.Id);
		groupIds2.Add(apps.Id);
		groupIds3.Add(game.Id);
		foreach (QuickSlotItem quickSlot in widgets.QuickSlots)
		{
			QuickSlotGroup quickSlotGroup2 = null;
			if (IsEpicGamesLauncher(quickSlot))
			{
				quickSlotGroup2 = game;
			}
			else if (groupIds.Contains(quickSlot.GroupId) || HasName(quickSlot.GroupName, "작업", "Work", "Development", "Productivity"))
			{
				quickSlotGroup2 = work;
			}
			else if (groupIds2.Contains(quickSlot.GroupId) || HasName(quickSlot.GroupName, "앱", "Apps", "Communication", "Browsers"))
			{
				quickSlotGroup2 = apps;
			}
			else if (groupIds3.Contains(quickSlot.GroupId) || HasName(quickSlot.GroupName, "게임", "Game", "Games", "Launchers"))
			{
				quickSlotGroup2 = game;
			}
			if (quickSlotGroup2 != null)
			{
				quickSlot.GroupId = quickSlotGroup2.Id;
				quickSlot.GroupName = quickSlotGroup2.Name;
			}
		}
		widgets.QuickSlotGroups.RemoveAll((QuickSlotGroup group) => (!string.Equals(group.Id, work.Id, StringComparison.OrdinalIgnoreCase) && HasName(group.Name, "작업", "Work", "Development", "Productivity")) || (!string.Equals(group.Id, apps.Id, StringComparison.OrdinalIgnoreCase) && HasName(group.Name, "앱", "Apps", "Communication", "Browsers")) || (!string.Equals(group.Id, game.Id, StringComparison.OrdinalIgnoreCase) && HasName(group.Name, "게임", "Game", "Games", "Launchers")));
	}

	private static HashSet<string> GetGroupIds(WidgetSettings widgets, params string[] names)
	{
		string[] names2 = names;
		return (from @group in widgets.QuickSlotGroups
			where HasName(@group.Name, names2)
			select @group.Id into id
			where !string.IsNullOrWhiteSpace(id)
			select id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	private static QuickSlotGroup FindOrCreateGroup(WidgetSettings widgets, string presetId, params string[] preferredNames)
	{
		string presetId2 = presetId;
		QuickSlotGroup quickSlotGroup = widgets.QuickSlotGroups.FirstOrDefault((QuickSlotGroup group) => string.Equals(group.Id, presetId2, StringComparison.OrdinalIgnoreCase));
		if (quickSlotGroup != null)
		{
			return quickSlotGroup;
		}
		foreach (string name in preferredNames)
		{
			QuickSlotGroup quickSlotGroup2 = widgets.QuickSlotGroups.FirstOrDefault((QuickSlotGroup group) => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));
			if (quickSlotGroup2 != null)
			{
				return quickSlotGroup2;
			}
		}
		QuickSlotGroup quickSlotGroup3 = (presetId2.Equals("general", StringComparison.OrdinalIgnoreCase) ? new QuickSlotGroup
		{
			Id = "general",
			Name = "기본",
			X = 80,
			Y = 80,
			Columns = 5
		} : QuickSlotGroup.CreatePresetGroups().Single((QuickSlotGroup group) => group.Id.Equals(presetId2, StringComparison.OrdinalIgnoreCase)));
		widgets.QuickSlotGroups.Add(quickSlotGroup3);
		return quickSlotGroup3;
	}

	private static void LocalizePresetNames(WidgetSettings widgets)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["general"] = "기본",
			["work"] = "작업",
			["apps"] = "앱",
			["game"] = "게임"
		};
		foreach (QuickSlotGroup quickSlotGroup2 in widgets.QuickSlotGroups)
		{
			if (dictionary.TryGetValue(quickSlotGroup2.Id, out var value))
			{
				quickSlotGroup2.Name = value;
				continue;
			}
			QuickSlotGroup quickSlotGroup = quickSlotGroup2;
			string name;
			switch (quickSlotGroup2.Name)
			{
			case "General":
				name = "기본";
				break;
			case "Work":
				name = "작업";
				break;
			case "Apps":
				name = "앱";
				break;
			case "Game":
			case "Games":
				name = "게임";
				break;
			default:
				name = quickSlotGroup2.Name;
				break;
			}
			quickSlotGroup.Name = name;
		}
		Dictionary<string, string> dictionary2 = widgets.QuickSlotGroups.Where((QuickSlotGroup group) => !string.IsNullOrWhiteSpace(group.Id)).ToDictionary<QuickSlotGroup, string, string>((QuickSlotGroup group) => group.Id, (QuickSlotGroup group) => group.Name, StringComparer.OrdinalIgnoreCase);
		foreach (QuickSlotItem quickSlot in widgets.QuickSlots)
		{
			if (dictionary2.TryGetValue(quickSlot.GroupId, out var value2))
			{
				quickSlot.GroupName = value2;
			}
		}
		foreach (SystemShortcutSetting systemShortcut in widgets.SystemShortcuts)
		{
			bool flag = string.IsNullOrWhiteSpace(systemShortcut.Name);
			if (!flag)
			{
				bool flag2;
				switch (systemShortcut.Name)
				{
				case "This PC":
				case "File Explorer":
				case "Recycle Bin":
					flag2 = true;
					break;
				default:
					flag2 = false;
					break;
				}
				flag = flag2;
			}
			if (flag)
			{
				systemShortcut.Name = SystemShortcutSetting.GetDefaultName(systemShortcut.Kind);
			}
		}
	}

	private static bool IsEpicGamesLauncher(QuickSlotItem slot)
	{
		if (!string.Equals(slot.Name, "Epic Games Launcher", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(Path.GetFileName(slot.TargetPath), "EpicGamesLauncher.exe", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool HasName(string? value, params string[] candidates)
	{
		string value2 = value;
		return candidates.Any((string candidate) => string.Equals(value2, candidate, StringComparison.OrdinalIgnoreCase));
	}

	private static void AddPowerShellShortcut(WidgetSettings widgets)
	{
		if (widgets.SystemShortcuts == null)
		{
			List<SystemShortcutSetting> list2 = (widgets.SystemShortcuts = new List<SystemShortcutSetting>());
		}
		if (!widgets.SystemShortcuts.Any((SystemShortcutSetting item) => item.Kind == SystemShortcutKind.PowerShell))
		{
			SystemShortcutSetting systemShortcutSetting = SystemShortcutSetting.CreateDefaults().Single((SystemShortcutSetting item) => item.Kind == SystemShortcutKind.PowerShell);
			systemShortcutSetting.IsVisible = true;
			widgets.SystemShortcuts.Add(systemShortcutSetting);
		}
	}

	private static void MigrateQuickSlotsToDock(WidgetSettings widgets)
	{
		widgets.ShowQuickSlots = false;
		WidgetSettings widgetSettings = widgets;
		if (widgetSettings.QuickSlots == null)
		{
			List<QuickSlotItem> list2 = (widgetSettings.QuickSlots = new List<QuickSlotItem>());
		}
		widgetSettings = widgets;
		if (widgetSettings.SystemShortcuts == null)
		{
			List<SystemShortcutSetting> list4 = (widgetSettings.SystemShortcuts = new List<SystemShortcutSetting>());
		}
		widgetSettings = widgets;
		if (widgetSettings.QuickSlotGroups == null)
		{
			List<QuickSlotGroup> list6 = (widgetSettings.QuickSlotGroups = new List<QuickSlotGroup>());
		}
		HashSet<string> generalIds = (from @group in widgets.QuickSlotGroups
			where @group.Id.Equals("general", StringComparison.OrdinalIgnoreCase) || @group.Name.Equals("기본", StringComparison.OrdinalIgnoreCase) || @group.Name.Equals("General", StringComparison.OrdinalIgnoreCase)
			select @group.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		widgets.QuickSlotGroups.RemoveAll((QuickSlotGroup group) => generalIds.Contains(group.Id));
		if (widgets.QuickSlotGroups.Count == 0)
		{
			widgets.QuickSlotGroups = QuickSlotGroup.CreatePresetGroups();
		}
		QuickSlotGroup quickSlotGroup = widgets.QuickSlotGroups.FirstOrDefault((QuickSlotGroup group) => group.Id.Equals("apps", StringComparison.OrdinalIgnoreCase)) ?? widgets.QuickSlotGroups[0];
		foreach (QuickSlotItem quickSlot in widgets.QuickSlots)
		{
			quickSlot.ShowInDock = true;
			if (generalIds.Contains(quickSlot.GroupId) || string.IsNullOrWhiteSpace(quickSlot.GroupId))
			{
				quickSlot.GroupId = quickSlotGroup.Id;
				quickSlot.GroupName = quickSlotGroup.Name;
			}
		}
		int num = 10;
		foreach (SystemShortcutSetting item in widgets.SystemShortcuts.OrderBy((SystemShortcutSetting item) => item.Kind))
		{
			item.IsVisible = false;
			item.ShowInDock = true;
			item.DockOrder = num;
			num += 10;
		}
		foreach (QuickSlotItem item2 in from item in widgets.QuickSlots
			orderby (item.DockOrder > 0) ? item.DockOrder : int.MaxValue, item.Slot
			select item)
		{
			item2.DockOrder = num;
			num += 10;
		}
	}
}
public enum WallpaperKind
{
	Image,
	Gif,
	Video,
	Web
}
public enum WallpaperFit
{
	Fill,
	Fit,
	Stretch
}
public enum WallpaperPlaybackState
{
	Stopped,
	Playing,
	Paused,
	Error
}
public enum ApplicationPlaybackAction
{
	KeepRunning,
	Pause,
	StopFreeMemory
}
public enum ApplicationRuleCondition
{
	IsForeground,
	IsRunning
}
public enum WidgetSide
{
	Left,
	Right
}
public enum SystemShortcutKind
{
	ThisPc,
	FileExplorer,
	RecycleBin,
	PowerShell
}
public sealed class WallpaperItem
{
	public Guid Id { get; set; } = Guid.NewGuid();


	public string Name { get; set; } = string.Empty;


	public string SourcePath { get; set; } = string.Empty;


	public WallpaperKind Kind { get; set; }

	public bool IsBuiltIn { get; set; }
}
public sealed class DisplayInfo
{
	public string Id { get; init; } = string.Empty;


	public string Name { get; init; } = string.Empty;


	public int X { get; init; }

	public int Y { get; init; }

	public int Width { get; init; }

	public int Height { get; init; }

	public bool IsPrimary { get; init; }

	public override string ToString()
	{
		return $"{Name} · {Width} × {Height}{(IsPrimary ? " · 기본 모니터" : string.Empty)}";
	}
}
public sealed class ApplicationPlaybackRule
{
	public string ProcessName { get; set; } = string.Empty;


	public ApplicationRuleCondition Condition { get; set; }

	public ApplicationPlaybackAction Action { get; set; } = ApplicationPlaybackAction.Pause;


	public ApplicationPlaybackRule Copy()
	{
		return new ApplicationPlaybackRule
		{
			ProcessName = (ProcessName?.Trim() ?? string.Empty),
			Condition = Condition,
			Action = Action
		};
	}

	public static List<ApplicationPlaybackRule> CreateDefaults()
	{
		int num = 3;
		List<ApplicationPlaybackRule> list = new List<ApplicationPlaybackRule>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<ApplicationPlaybackRule> span = CollectionsMarshal.AsSpan(list);
		int num2 = 0;
		span[num2] = CreateUnrealRule("UnrealEditor");
		num2++;
		span[num2] = CreateUnrealRule("UE4Editor");
		num2++;
		span[num2] = CreateUnrealRule("UE5Editor");
		return list;
	}

	private static ApplicationPlaybackRule CreateUnrealRule(string processName)
	{
		return new ApplicationPlaybackRule
		{
			ProcessName = processName,
			Condition = ApplicationRuleCondition.IsRunning,
			Action = ApplicationPlaybackAction.StopFreeMemory
		};
	}
}
public sealed class PlaybackOptions
{
	public bool IsMuted { get; set; } = true;


	public double Volume { get; set; } = 0.25;


	public WallpaperFit Fit { get; set; }

	public double Brightness { get; set; } = 1.0;


	public double Contrast { get; set; } = 1.0;


	public double Saturation { get; set; } = 1.0;


	public bool PauseWhenForegroundAppIsMaximized { get; set; } = true;


	public bool FreezeWidgetsWhenPaused { get; set; }

	public List<ApplicationPlaybackRule> ApplicationRules { get; set; } = ApplicationPlaybackRule.CreateDefaults();


	public PlaybackOptions Copy()
	{
		return new PlaybackOptions
		{
			IsMuted = IsMuted,
			Volume = Math.Clamp(Volume, 0.0, 1.0),
			Fit = Fit,
			Brightness = Math.Clamp(Brightness, 0.25, 1.25),
			Contrast = Math.Clamp(Contrast, 0.5, 1.5),
			Saturation = Math.Clamp(Saturation, 0.0, 1.5),
			PauseWhenForegroundAppIsMaximized = PauseWhenForegroundAppIsMaximized,
			FreezeWidgetsWhenPaused = FreezeWidgetsWhenPaused,
			ApplicationRules = (from rule in (ApplicationRules ?? new List<ApplicationPlaybackRule>()).OfType<ApplicationPlaybackRule>()
				select rule.Copy()).ToList()
		};
	}
}
public sealed class QuickSlotItem
{
	public int Slot { get; set; }

	public string Name { get; set; } = string.Empty;


	public string TargetPath { get; set; } = string.Empty;


	public string IconPath { get; set; } = string.Empty;


	public string GroupId { get; set; } = string.Empty;


	public string GroupName { get; set; } = string.Empty;


	public SystemShortcutKind? SystemKind { get; set; }

	public bool IsDynamicStateActive { get; set; }

	public bool ShowInDock { get; set; }

	public int DockOrder { get; set; }

	[JsonIgnore]
	public bool IsRunning { get; set; }

	[JsonIgnore]
	public bool HasAttention { get; set; }

	public QuickSlotItem Copy()
	{
		return new QuickSlotItem
		{
			Slot = Math.Clamp(Slot, 1, 99),
			Name = Name,
			TargetPath = TargetPath,
			IconPath = IconPath,
			GroupId = GroupId,
			GroupName = GroupName,
			SystemKind = SystemKind,
			IsDynamicStateActive = IsDynamicStateActive,
			ShowInDock = ShowInDock,
			DockOrder = Math.Max(0, DockOrder),
			IsRunning = IsRunning,
			HasAttention = HasAttention
		};
	}
}
public sealed class SystemShortcutSetting
{
	public SystemShortcutKind Kind { get; set; }

	public string Name { get; set; } = string.Empty;


	public bool IsVisible { get; set; }

	public int Slot { get; set; }

	public string CustomIconPath { get; set; } = string.Empty;


	public bool ShowInDock { get; set; } = true;


	public int DockOrder { get; set; }

	[JsonIgnore]
	public bool IsRunning { get; set; }

	[JsonIgnore]
	public bool HasAttention { get; set; }

	public SystemShortcutSetting Copy()
	{
		SystemShortcutSetting obj = new SystemShortcutSetting
		{
			Kind = Kind,
			Name = (string.IsNullOrWhiteSpace(Name) ? GetDefaultName(Kind) : Name.Trim()),
			IsVisible = IsVisible
		};
		int slot = Slot;
		obj.Slot = ((slot >= 1 && slot <= 99) ? Slot : 0);
		obj.CustomIconPath = CustomIconPath?.Trim() ?? string.Empty;
		obj.ShowInDock = ShowInDock;
		obj.DockOrder = Math.Max(0, DockOrder);
		obj.IsRunning = IsRunning;
		obj.HasAttention = HasAttention;
		return obj;
	}

	public static List<SystemShortcutSetting> CreateDefaults()
	{
		int num = 4;
		List<SystemShortcutSetting> list = new List<SystemShortcutSetting>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<SystemShortcutSetting> span = CollectionsMarshal.AsSpan(list);
		int num2 = 0;
		span[num2] = new SystemShortcutSetting
		{
			Kind = SystemShortcutKind.ThisPc,
			Name = GetDefaultName(SystemShortcutKind.ThisPc),
			DockOrder = 10
		};
		num2++;
		span[num2] = new SystemShortcutSetting
		{
			Kind = SystemShortcutKind.FileExplorer,
			Name = GetDefaultName(SystemShortcutKind.FileExplorer),
			DockOrder = 20
		};
		num2++;
		span[num2] = new SystemShortcutSetting
		{
			Kind = SystemShortcutKind.RecycleBin,
			Name = GetDefaultName(SystemShortcutKind.RecycleBin),
			DockOrder = 30
		};
		num2++;
		span[num2] = new SystemShortcutSetting
		{
			Kind = SystemShortcutKind.PowerShell,
			Name = GetDefaultName(SystemShortcutKind.PowerShell),
			DockOrder = 40
		};
		return list;
	}

	public static string GetDefaultName(SystemShortcutKind kind)
	{
		return kind switch
		{
			SystemShortcutKind.ThisPc => "내 PC", 
			SystemShortcutKind.FileExplorer => "파일 탐색기", 
			SystemShortcutKind.RecycleBin => "휴지통", 
			SystemShortcutKind.PowerShell => "PowerShell", 
			_ => kind.ToString(), 
		};
	}

	public static string GetDefaultTargetPath(SystemShortcutKind kind)
	{
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		switch (kind)
		{
		case SystemShortcutKind.PowerShell:
		{
			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
			if (File.Exists(text))
			{
				return text;
			}
			return Path.Combine(folderPath, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
		}
		default:
			return $"system:{kind}";
		case SystemShortcutKind.FileExplorer:
			return Path.Combine(folderPath, "explorer.exe");
		}
	}
}
public sealed class DockSettings
{
	public bool IsEnabled { get; set; } = true;


	public bool ShowRunningApplications { get; set; } = true;


	public bool HideWindowsTaskbar { get; set; }

	public int IconSize { get; set; } = 54;


	public int BottomMargin { get; set; } = 12;


	public DockSettings Copy()
	{
		return new DockSettings
		{
			IsEnabled = IsEnabled,
			ShowRunningApplications = ShowRunningApplications,
			HideWindowsTaskbar = HideWindowsTaskbar,
			IconSize = Math.Clamp(IconSize, 40, 80),
			BottomMargin = Math.Clamp(BottomMargin, 0, 80)
		};
	}
}
public sealed class QuickSlotGroup
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");


	public string Name { get; set; } = "앱";


	public int X { get; set; } = 360;


	public int Y { get; set; } = 70;


	public int Columns { get; set; } = 5;


	public QuickSlotGroup Copy()
	{
		return new QuickSlotGroup
		{
			Id = Id,
			Name = Name,
			X = Math.Clamp(X, 0, 10000),
			Y = Math.Clamp(Y, 0, 10000),
			Columns = Math.Clamp(Columns, 1, 10)
		};
	}

	public static List<QuickSlotGroup> CreatePresetGroups()
	{
		int num = 3;
		List<QuickSlotGroup> list = new List<QuickSlotGroup>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<QuickSlotGroup> span = CollectionsMarshal.AsSpan(list);
		int num2 = 0;
		span[num2] = CreatePreset("work", "작업", 80, 320);
		num2++;
		span[num2] = CreatePreset("apps", "앱", 500, 320);
		num2++;
		span[num2] = CreatePreset("game", "게임", 920, 320);
		return list;
	}

	private static QuickSlotGroup CreatePreset(string id, string name, int x, int y)
	{
		return new QuickSlotGroup
		{
			Id = id,
			Name = name,
			X = x,
			Y = y,
			Columns = 5
		};
	}
}
public sealed class WidgetSettings
{
	public bool IsEnabled { get; set; } = true;


	public bool ShowClock { get; set; } = true;


	public bool ShowCalendar { get; set; }

	public bool ShowSystemInfo { get; set; } = true;


	public bool ShowQuickSlots { get; set; }

	public bool ShowMediaStatus { get; set; }

	public bool ShowCodexCompanion { get; set; } = true;


	public WidgetSide ClockSide { get; set; } = WidgetSide.Right;


	public WidgetSide CalendarSide { get; set; }

	public WidgetSide SystemInfoSide { get; set; } = WidgetSide.Right;


	public WidgetSide MediaStatusSide { get; set; }

	public WidgetSide CodexCompanionSide { get; set; } = WidgetSide.Right;


	public bool SnapQuickSlotsToGrid { get; set; } = true;


	public int QuickSlotGridSize { get; set; } = 40;


	public List<QuickSlotItem> QuickSlots { get; set; } = new List<QuickSlotItem>();


	public List<SystemShortcutSetting> SystemShortcuts { get; set; } = SystemShortcutSetting.CreateDefaults();


	public List<QuickSlotGroup> QuickSlotGroups { get; set; } = QuickSlotGroup.CreatePresetGroups();


	public WidgetSettings Copy()
	{
		int num = Math.Clamp(QuickSlotGridSize, 10, 200);
		List<QuickSlotGroup> groups = (from @group in QuickSlotGroups.Where((QuickSlotGroup @group) => !string.IsNullOrWhiteSpace(@group.Id)).GroupBy<QuickSlotGroup, string>((QuickSlotGroup @group) => @group.Id, StringComparer.OrdinalIgnoreCase)
			select @group.First().Copy()).ToList();
		if (groups.Count == 0)
		{
			groups.Add(new QuickSlotGroup());
		}
		if (SnapQuickSlotsToGrid)
		{
			foreach (QuickSlotGroup item in groups)
			{
				item.X = SnapToGrid(item.X, num);
				item.Y = SnapToGrid(item.Y, num);
			}
		}
		HashSet<string> validGroupIds = groups.Select((QuickSlotGroup group) => group.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		string fallbackGroupId = groups[0].Id;
		List<QuickSlotItem> list = (from item in QuickSlots.Where(delegate(QuickSlotItem item)
			{
				int slot3 = item.Slot;
				return slot3 >= 1 && slot3 <= 99;
			})
			orderby item.Slot
			group item by item.Slot into @group
			select @group.First()).Select(delegate(QuickSlotItem item)
		{
			QuickSlotItem copy = item.Copy();
			if (!validGroupIds.Contains(copy.GroupId))
			{
				copy.GroupId = fallbackGroupId;
			}
			copy.GroupName = groups.First((QuickSlotGroup group) => group.Id.Equals(copy.GroupId, StringComparison.OrdinalIgnoreCase)).Name;
			return copy;
		}).ToList();
		Dictionary<SystemShortcutKind, SystemShortcutSetting> existingSystemShortcuts = (from item in SystemShortcuts ?? new List<SystemShortcutSetting>()
			group item by item.Kind).ToDictionary((IGrouping<SystemShortcutKind, SystemShortcutSetting> group) => group.Key, (IGrouping<SystemShortcutKind, SystemShortcutSetting> group) => group.First());
		SystemShortcutSetting value;
		List<SystemShortcutSetting> list2 = (from defaultItem in SystemShortcutSetting.CreateDefaults()
			select (!existingSystemShortcuts.TryGetValue(defaultItem.Kind, out value)) ? defaultItem : value.Copy()).ToList();
		HashSet<int> occupiedSlots = list.Select((QuickSlotItem item) => item.Slot).ToHashSet();
		foreach (SystemShortcutSetting item2 in list2.Where((SystemShortcutSetting item) => item.IsVisible))
		{
			int slot2 = item2.Slot;
			if (slot2 < 1 || slot2 > 99 || occupiedSlots.Contains(item2.Slot))
			{
				item2.Slot = Enumerable.Range(1, 99).FirstOrDefault((int slot) => !occupiedSlots.Contains(slot));
			}
			if (item2.Slot == 0)
			{
				item2.IsVisible = false;
			}
			else
			{
				occupiedSlots.Add(item2.Slot);
			}
		}
		return new WidgetSettings
		{
			IsEnabled = IsEnabled,
			ShowClock = ShowClock,
			ShowCalendar = false,
			ShowSystemInfo = ShowSystemInfo,
			ShowQuickSlots = ShowQuickSlots,
			ShowMediaStatus = false,
			ShowCodexCompanion = ShowCodexCompanion,
			ClockSide = ClockSide,
			CalendarSide = CalendarSide,
			SystemInfoSide = SystemInfoSide,
			MediaStatusSide = MediaStatusSide,
			CodexCompanionSide = CodexCompanionSide,
			SnapQuickSlotsToGrid = SnapQuickSlotsToGrid,
			QuickSlotGridSize = num,
			QuickSlots = list,
			SystemShortcuts = list2,
			QuickSlotGroups = groups
		};
	}

	public IReadOnlyList<QuickSlotItem> GetDesktopQuickSlots(bool recycleBinHasItems)
	{
		List<QuickSlotItem> list = QuickSlots.Select((QuickSlotItem item) => item.Copy()).ToList();
		QuickSlotGroup quickSlotGroup = QuickSlotGroups.FirstOrDefault((QuickSlotGroup group) => group.Id.Equals("general", StringComparison.OrdinalIgnoreCase)) ?? QuickSlotGroups.FirstOrDefault((QuickSlotGroup group) => group.Name.Equals("기본", StringComparison.OrdinalIgnoreCase) || group.Name.Equals("General", StringComparison.OrdinalIgnoreCase)) ?? QuickSlotGroups.First();
		foreach (SystemShortcutSetting item in SystemShortcuts.Where(delegate(SystemShortcutSetting item)
		{
			if (item.IsVisible)
			{
				int slot = item.Slot;
				if (slot >= 1)
				{
					return slot <= 99;
				}
				return false;
			}
			return false;
		}))
		{
			list.Add(new QuickSlotItem
			{
				Slot = item.Slot,
				Name = item.Name,
				TargetPath = SystemShortcutSetting.GetDefaultTargetPath(item.Kind),
				IconPath = item.CustomIconPath,
				GroupId = quickSlotGroup.Id,
				GroupName = quickSlotGroup.Name,
				SystemKind = item.Kind,
				IsDynamicStateActive = (item.Kind == SystemShortcutKind.RecycleBin && recycleBinHasItems),
				IsRunning = item.IsRunning,
				HasAttention = item.HasAttention
			});
		}
		return list.OrderBy((QuickSlotItem item) => item.Slot).ToList();
	}

	private static int SnapToGrid(int value, int gridSize)
	{
		return Math.Max(0, (int)Math.Round((double)value / (double)gridSize, MidpointRounding.AwayFromZero) * gridSize);
	}
}
public sealed class AppSettings
{
	public int SchemaVersion { get; set; } = 15;


	public List<WallpaperItem> Library { get; set; } = new List<WallpaperItem>();


	public WallpaperItem? ActiveWallpaper { get; set; }

	public string? SelectedMonitorId { get; set; }

	public PlaybackOptions Playback { get; set; } = new PlaybackOptions();


	public WidgetSettings Widgets { get; set; } = new WidgetSettings();


	public DockSettings Dock { get; set; } = new DockSettings();


	public bool RestoreOnStartup { get; set; } = true;

}
public sealed class WallpaperStateChangedEventArgs : EventArgs
{
	public WallpaperPlaybackState State { get; }

	public string Message { get; }

	public WallpaperStateChangedEventArgs(WallpaperPlaybackState state, string message)
	{
		State = state;
		Message = message;
		base..ctor();
	}
}
public sealed class QuickSlotLayoutChangedEventArgs : EventArgs
{
	public string GroupId { get; }

	public int X { get; }

	public int Y { get; }

	public QuickSlotLayoutChangedEventArgs(string groupId, int x, int y)
	{
		GroupId = groupId;
		X = x;
		Y = y;
		base..ctor();
	}
}
public sealed class ExclusiveGpuApplicationEventArgs : EventArgs
{
	public int ProcessId { get; }

	public string ProcessName { get; }

	public ExclusiveGpuApplicationEventArgs(int processId, string processName)
	{
		ProcessId = processId;
		ProcessName = processName;
		base..ctor();
	}
}
public sealed class ExclusiveGpuIsolationChangedEventArgs : EventArgs
{
	public bool IsActive { get; }

	public int ProcessId { get; }

	public string ProcessName { get; }

	public ExclusiveGpuIsolationChangedEventArgs(bool isActive, int processId, string processName)
	{
		IsActive = isActive;
		ProcessId = processId;
		ProcessName = processName;
		base..ctor();
	}
}
public static class WallpaperValidator
{
	private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

	private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm" };

	public static WallpaperKind DetectKind(string path)
	{
		if (Directory.Exists(path))
		{
			if (!File.Exists(Path.Combine(path, "index.html")))
			{
				throw new NotSupportedException("A web wallpaper folder must contain index.html.");
			}
			return WallpaperKind.Web;
		}
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("월페이퍼 소스를 찾지 못했습니다.", path);
		}
		string extension = Path.GetExtension(path);
		if (ImageExtensions.Contains(extension))
		{
			return WallpaperKind.Image;
		}
		if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
		{
			return WallpaperKind.Gif;
		}
		if (VideoExtensions.Contains(extension))
		{
			return WallpaperKind.Video;
		}
		if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
		{
			return WallpaperKind.Web;
		}
		throw new NotSupportedException("The '" + extension + "' file type is not supported by Vellume.");
	}

	public static string ResolveWebEntryPoint(string sourcePath)
	{
		if (!Directory.Exists(sourcePath))
		{
			return sourcePath;
		}
		return Path.Combine(sourcePath, "index.html");
	}

	public static WallpaperItem CreateItem(string path, bool isBuiltIn = false)
	{
		WallpaperKind kind = DetectKind(path);
		string fullPath = Path.GetFullPath(path);
		string name = (Directory.Exists(fullPath) ? new DirectoryInfo(fullPath).Name : Path.GetFileNameWithoutExtension(fullPath));
		return new WallpaperItem
		{
			Name = name,
			SourcePath = fullPath,
			Kind = kind,
			IsBuiltIn = isBuiltIn
		};
	}
}
