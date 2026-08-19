using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32.SafeHandles;
using NAudio.Dsp;
using NAudio.Wave;
using Vellume.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;
using Windows.Media.Control;
using Windows.Storage.Streams;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: InternalsVisibleTo("Vellume.Tests")]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0", FrameworkDisplayName = ".NET 8.0")]
[assembly: AssemblyCompany("Vellume.Windows")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyProduct("Vellume.Windows")]
[assembly: AssemblyTitle("Vellume.Windows")]
[assembly: TargetPlatform("Windows10.0.17763.0")]
[assembly: SupportedOSPlatform("Windows10.0.17763.0")]
[assembly: AssemblyVersion("1.0.0.0")]
[module: RefSafetyRules(11)]
namespace Vellume.Windows;

internal sealed class AudioSpectrumSampler : IDisposable
{
	private const int FftExponent = 11;

	private const int FftLength = 2048;

	private const int BandCount = 24;

	private readonly object _gate = new object();

	private readonly Complex[] _fftBuffer = new Complex[2048];

	private readonly float[] _bands = new float[24];

	private WasapiLoopbackCapture? _capture;

	private int _fftPosition;

	private int _sampleRate = 48000;

	private bool _disposed;

	public bool IsAvailable { get; private set; }

	public event EventHandler? SpectrumChanged;

	public void Start()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_capture != null)
		{
			return;
		}
		try
		{
			_capture = new WasapiLoopbackCapture();
			_sampleRate = _capture.WaveFormat.SampleRate;
			_capture.DataAvailable += CaptureOnDataAvailable;
			_capture.RecordingStopped += CaptureOnRecordingStopped;
			_capture.StartRecording();
			IsAvailable = true;
		}
		catch
		{
			DisposeCapture();
			IsAvailable = false;
		}
	}

	public float[] GetBands()
	{
		lock (_gate)
		{
			float[] array = new float[24];
			for (int i = 0; i < 24; i++)
			{
				_bands[i] *= 0.86f;
				if (_bands[i] < 0.006f)
				{
					_bands[i] = 0f;
				}
				array[i] = _bands[i];
			}
			return array;
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			DisposeCapture();
		}
	}

	private void CaptureOnDataAvailable(object? sender, WaveInEventArgs eventArgs)
	{
		WasapiLoopbackCapture capture = _capture;
		if (capture == null)
		{
			return;
		}
		WaveFormat waveFormat = capture.WaveFormat;
		int num = Math.Max(1, waveFormat.BitsPerSample / 8);
		int num2 = num * waveFormat.Channels;
		if (num2 <= 0)
		{
			return;
		}
		bool flag = false;
		lock (_gate)
		{
			for (int i = 0; i + num2 <= eventArgs.BytesRecorded; i += num2)
			{
				double num3 = 0.0;
				for (int j = 0; j < waveFormat.Channels; j++)
				{
					num3 += (double)ReadSample(eventArgs.Buffer, i + j * num, waveFormat);
				}
				num3 /= (double)Math.Max(1, waveFormat.Channels);
				_fftBuffer[_fftPosition].X = (float)(num3 * FastFourierTransform.HammingWindow(_fftPosition, 2048));
				_fftBuffer[_fftPosition].Y = 0f;
				_fftPosition++;
				if (_fftPosition == 2048)
				{
					CalculateBands();
					_fftPosition = 0;
					flag = true;
				}
			}
		}
		if (flag)
		{
			this.SpectrumChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	private void CalculateBands()
	{
		FastFourierTransform.FFT(forward: true, 11, _fftBuffer);
		double num = Math.Min(18000.0, (double)_sampleRate / 2.0);
		for (int i = 0; i < 24; i++)
		{
			double num2 = 55.0 * Math.Pow(num / 55.0, (double)i / 24.0);
			double num3 = 55.0 * Math.Pow(num / 55.0, ((double)i + 1.0) / 24.0);
			int num4 = Math.Clamp((int)(num2 * 2048.0 / (double)_sampleRate), 1, 1023);
			int num5 = Math.Clamp((int)Math.Ceiling(num3 * 2048.0 / (double)_sampleRate), num4 + 1, 1024);
			double val = 0.0;
			for (int j = num4; j < num5; j++)
			{
				Complex complex = _fftBuffer[j];
				val = Math.Max(val, Math.Sqrt(complex.X * complex.X + complex.Y * complex.Y));
			}
			float num6 = (float)Math.Clamp((20.0 * Math.Log10(Math.Max(val, 1E-05)) + 62.0) / 50.0, 0.0, 1.0);
			_bands[i] = ((num6 > _bands[i]) ? num6 : (_bands[i] * 0.72f + num6 * 0.28f));
		}
	}

	private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
	{
		IsAvailable = false;
	}

	private void DisposeCapture()
	{
		WasapiLoopbackCapture capture = _capture;
		_capture = null;
		if (capture != null)
		{
			capture.DataAvailable -= CaptureOnDataAvailable;
			capture.RecordingStopped -= CaptureOnRecordingStopped;
			try
			{
				capture.StopRecording();
			}
			catch
			{
			}
			capture.Dispose();
			IsAvailable = false;
		}
	}

	private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
	{
		bool flag = format.BitsPerSample == 32;
		if (flag)
		{
			WaveFormatEncoding encoding = format.Encoding;
			bool flag2 = ((encoding == WaveFormatEncoding.IeeeFloat || encoding == WaveFormatEncoding.Extensible) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			return BitConverter.ToSingle(buffer, offset);
		}
		if (format.BitsPerSample == 16)
		{
			return (float)BitConverter.ToInt16(buffer, offset) / 32768f;
		}
		if (format.BitsPerSample == 24)
		{
			int num = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
			if (((uint)num & 0x800000u) != 0)
			{
				num |= -16777216;
			}
			return (float)num / 8388608f;
		}
		if (format.BitsPerSample == 32)
		{
			return (float)BitConverter.ToInt32(buffer, offset) / 2.1474836E+09f;
		}
		return 0f;
	}
}
internal sealed class BoundedDesktopPanelHost : IDisposable
{
	private readonly NativeRedirectBridgeHost _bridge;

	private readonly LayeredWpfPanelHost _surface;

	private bool _disposed;

	internal nint Handle => _bridge.Handle;

	internal nint SurfaceHandle => _surface.Handle;

	internal int Width => _surface.Width;

	internal int Height => _surface.Height;

	internal long PresentationGeneration => _surface.PresentationGeneration;

	internal bool LastPresentSucceeded => _surface.LastPresentSucceeded;

	internal int LastNonTransparentPixelCount => _surface.LastNonTransparentPixelCount;

	internal bool IsReady
	{
		get
		{
			if (!_disposed && _bridge.IsReady)
			{
				return _surface.IsReady;
			}
			return false;
		}
	}

	internal BoundedDesktopPanelHost(string name, nint desktopHandle, int x, int y, int width, int height)
	{
		_bridge = new NativeRedirectBridgeHost(name + " Redirect Bridge", desktopHandle, x, y, width, height);
		try
		{
			_surface = new LayeredWpfPanelHost(name + " Alpha Surface", _bridge.Handle, 0, 0, width, height);
		}
		catch
		{
			_bridge.Dispose();
			throw;
		}
	}

	internal bool IsAttachedTo(nint desktopHandle)
	{
		if (IsReady && _bridge.IsAttachedTo(desktopHandle))
		{
			return _surface.IsAttachedTo(_bridge.Handle);
		}
		return false;
	}

	internal void Render(FrameworkElement visual)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_surface.Render(visual);
	}

	internal void SetWindowVisible(bool visible)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!IsReady)
		{
			return;
		}
		bool flag = NativeMethods.IsWindowVisible(_bridge.Handle);
		bool flag2 = NativeMethods.IsWindowVisible(_surface.Handle);
		if ((visible && flag && flag2) || (!visible && !flag && !flag2))
		{
			return;
		}
		if (visible)
		{
			if (_surface.PresentCachedFrame())
			{
				_surface.SetWindowVisible(visible: true);
				_bridge.SetWindowVisible(visible: true);
			}
		}
		else
		{
			_bridge.SetWindowVisible(visible: false);
			_surface.SetWindowVisible(visible: false);
		}
	}

	internal void AttachTo(nint desktopHandle, int x, int y, int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		bool num = NativeMethods.IsWindowVisible(_bridge.Handle) || NativeMethods.IsWindowVisible(_surface.Handle);
		_bridge.SetWindowVisible(visible: false);
		_surface.SetWindowVisible(visible: false);
		_bridge.AttachTo(desktopHandle, x, y, width, height);
		_surface.AttachTo(_bridge.Handle, 0, 0, width, height);
		if (!IsAttachedTo(desktopHandle))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows가 위젯의 bounded 데스크톱 호스트를 다시 연결하지 못했습니다.");
		}
		_surface.PresentCachedFrame();
		if (num)
		{
			SetWindowVisible(visible: true);
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_surface.Dispose();
			_bridge.Dispose();
		}
	}
}
internal sealed record CodexCompanionSnapshot(string State, string Headline, string Detail, string Workspace, string Model, DateTimeOffset UpdatedAtUtc)
{
	public static CodexCompanionSnapshot Disconnected()
	{
		return new CodexCompanionSnapshot("disconnected", "CODEX 연결 안 됨", "Vellume 설정에서 연결을 설치하세요", string.Empty, string.Empty, DateTimeOffset.MinValue);
	}
}
internal sealed class CodexCompanionStatusReader : IDisposable
{
	private readonly string _statusPath;

	private readonly FileSystemWatcher? _watcher;

	private bool _disposed;

	public event EventHandler? Changed;

	public CodexCompanionStatusReader(string statusPath)
	{
		_statusPath = statusPath;
		string directoryName = System.IO.Path.GetDirectoryName(statusPath);
		string fileName = System.IO.Path.GetFileName(statusPath);
		if (!string.IsNullOrWhiteSpace(directoryName) && !string.IsNullOrWhiteSpace(fileName))
		{
			Directory.CreateDirectory(directoryName);
			_watcher = new FileSystemWatcher(directoryName, fileName)
			{
				NotifyFilter = (NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime),
				IncludeSubdirectories = false,
				EnableRaisingEvents = true
			};
			_watcher.Changed += WatcherOnStatusChanged;
			_watcher.Created += WatcherOnStatusChanged;
			_watcher.Deleted += WatcherOnStatusChanged;
			_watcher.Renamed += WatcherOnStatusRenamed;
		}
	}

	public async Task<CodexCompanionSnapshot> ReadAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_statusPath))
		{
			return CodexCompanionSnapshot.Disconnected();
		}
		try
		{
			FileStream stream = new FileStream(_statusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
			CodexCompanionSnapshot result2;
			try
			{
				JsonDocument jsonDocument = await JsonDocument.ParseAsync(stream, default(JsonDocumentOptions), cancellationToken);
				using (jsonDocument)
				{
					JsonElement rootElement = jsonDocument.RootElement;
					DateTimeOffset dateTimeOffset = File.GetLastWriteTimeUtc(_statusPath);
					if (rootElement.TryGetProperty("updatedAtUtc", out var value) && DateTimeOffset.TryParse(value.GetString(), out var result))
					{
						dateTimeOffset = result;
					}
					result2 = ((!(DateTimeOffset.UtcNow - dateTimeOffset > TimeSpan.FromMinutes(30.0))) ? new CodexCompanionSnapshot(GetString(rootElement, "state", "ready"), LocalizeHeadline(GetString(rootElement, "headline", "CODEX 대기 중")), LocalizeDetail(GetString(rootElement, "detail", "새 작업을 기다리는 중")), GetString(rootElement, "workspace", string.Empty), GetString(rootElement, "model", string.Empty), dateTimeOffset) : new CodexCompanionSnapshot("offline", "CODEX 신호 대기", "최근 Hook 신호가 없습니다", GetString(rootElement, "workspace", string.Empty), GetString(rootElement, "model", string.Empty), dateTimeOffset));
				}
			}
			finally
			{
				if (stream != null)
				{
					await stream.DisposeAsync();
				}
			}
			return result2;
		}
		catch
		{
			return CodexCompanionSnapshot.Disconnected();
		}
	}

	private static string GetString(JsonElement root, string name, string fallback)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
		{
			return fallback;
		}
		return value.GetString();
	}

	private static string LocalizeHeadline(string value)
	{
		return value.ToUpperInvariant() switch
		{
			"CODEX BRIDGE OFFLINE" => "CODEX 연결 안 됨", 
			"CODEX READY" => "CODEX 대기 중", 
			"CODEX ONLINE" => "CODEX 연결됨", 
			"THINKING" => "생각하는 중", 
			"BUILDING" => "빌드하는 중", 
			"EDITING" => "편집하는 중", 
			"USING A TOOL" => "도구 사용 중", 
			"TASK COMPLETE" => "작업 완료", 
			"CODEX OFFLINE" => "CODEX 오프라인", 
			_ => value, 
		};
	}

	private static string LocalizeDetail(string value)
	{
		return value switch
		{
			"Connect once from Vellume settings" => "Vellume 설정에서 연결을 설치하세요", 
			"Waiting for a task" => "새 작업을 기다리는 중", 
			"Session connected" => "세션이 연결되었습니다", 
			"Planning the next move" => "다음 작업을 계획하는 중", 
			"Running a local command" => "로컬 명령을 실행하는 중", 
			"Updating project files" => "프로젝트 파일을 수정하는 중", 
			"Ready for the next request" => "다음 요청을 기다리는 중", 
			"Session ended" => "세션이 종료되었습니다", 
			_ => value, 
		};
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			if (_watcher != null)
			{
				_watcher.EnableRaisingEvents = false;
				_watcher.Changed -= WatcherOnStatusChanged;
				_watcher.Created -= WatcherOnStatusChanged;
				_watcher.Deleted -= WatcherOnStatusChanged;
				_watcher.Renamed -= WatcherOnStatusRenamed;
				_watcher.Dispose();
			}
		}
	}

	private void WatcherOnStatusChanged(object sender, FileSystemEventArgs eventArgs)
	{
		this.Changed?.Invoke(this, EventArgs.Empty);
	}

	private void WatcherOnStatusRenamed(object sender, RenamedEventArgs eventArgs)
	{
		this.Changed?.Invoke(this, EventArgs.Empty);
	}
}
internal sealed class CodexCompanionBridgeInstaller
{
	private const string HookDescription = "Vellume Codex 컴패니언 상태 연결";

	private static readonly string[] HookEvents = new string[5] { "SessionStart", "UserPromptSubmit", "PreToolUse", "Stop", "SessionEnd" };

	private readonly string _bridgeDirectory;

	private readonly string _statusPath;

	private readonly string? _pipeName;

	public CodexCompanionBridgeInstaller(string bridgeDirectory, string statusPath, string? pipeName = null)
	{
		_bridgeDirectory = bridgeDirectory;
		_statusPath = statusPath;
		_pipeName = pipeName;
	}

	public async Task<string> InstallAsync(CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_bridgeDirectory);
		string scriptPath = System.IO.Path.Combine(_bridgeDirectory, "vellume-codex-hook.ps1");
		await File.WriteAllTextAsync(scriptPath, BuildHookScript(_statusPath, _pipeName), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
		string text = Environment.GetEnvironmentVariable("CODEX_HOME") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
		Directory.CreateDirectory(text);
		string hooksPath = System.IO.Path.Combine(text, "hooks.json");
		JsonObject jsonObject = await ReadHooksAsync(hooksPath, cancellationToken);
		JsonObject jsonObject2 = jsonObject;
		if (jsonObject2["description"] == null)
		{
			jsonObject2["description"] = "Vellume Codex 컴패니언 상태 연결";
		}
		JsonObject jsonObject3 = jsonObject["hooks"] as JsonObject;
		if (jsonObject3 == null)
		{
			jsonObject3 = (JsonObject)(jsonObject["hooks"] = new JsonObject());
		}
		string text2 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
		string text3 = "\"" + text2 + "\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
		string[] hookEvents = HookEvents;
		foreach (string propertyName in hookEvents)
		{
			JsonArray jsonArray = jsonObject3[propertyName] as JsonArray;
			if (jsonArray == null)
			{
				jsonArray = (JsonArray)(jsonObject3[propertyName] = new JsonArray());
			}
			RemoveExistingVellumeHandlers(jsonArray, scriptPath);
			JsonObject jsonObject4 = new JsonObject
			{
				["type"] = "command",
				["command"] = text3,
				["commandWindows"] = text3,
				["timeout"] = 3
			};
			jsonArray.Add(new JsonObject { ["hooks"] = new JsonArray(jsonObject4) });
		}
		string temporaryPath = hooksPath + ".vellume.tmp";
		await File.WriteAllTextAsync(temporaryPath, jsonObject.ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true
		}), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
		File.Move(temporaryPath, hooksPath, overwrite: true);
		return hooksPath;
	}

	private static async Task<JsonObject> ReadHooksAsync(string hooksPath, CancellationToken cancellationToken)
	{
		if (!File.Exists(hooksPath))
		{
			return new JsonObject();
		}
		string json = await File.ReadAllTextAsync(hooksPath, cancellationToken);
		try
		{
			return (JsonNode.Parse(json) as JsonObject) ?? throw new InvalidDataException("Codex hooks.json의 최상위 값은 JSON 객체여야 합니다.");
		}
		catch (JsonException innerException)
		{
			throw new InvalidDataException("Codex hooks.json이 올바른 JSON이 아닙니다. Vellume은 파일을 변경하지 않았습니다.", innerException);
		}
	}

	private static void RemoveExistingVellumeHandlers(JsonArray groups, string scriptPath)
	{
		string scriptPath2 = scriptPath;
		JsonObject[] array = groups.OfType<JsonObject>().ToArray();
		foreach (JsonObject jsonObject in array)
		{
			if (jsonObject["hooks"] is JsonArray jsonArray)
			{
				JsonObject[] array2 = (from handler in jsonArray.OfType<JsonObject>()
					where ContainsScriptPath(handler["command"], scriptPath2) || ContainsScriptPath(handler["commandWindows"], scriptPath2)
					select handler).ToArray();
				foreach (JsonObject item in array2)
				{
					jsonArray.Remove(item);
				}
				if (jsonArray.Count == 0)
				{
					groups.Remove(jsonObject);
				}
			}
		}
	}

	private static bool ContainsScriptPath(JsonNode? command, string scriptPath)
	{
		if (command is JsonValue)
		{
			return command.GetValue<string>().Contains(scriptPath, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static string BuildHookScript(string statusPath, string? pipeName)
	{
		string value = statusPath.Replace("'", "''");
		string text = pipeName?.Replace("'", "''");
		string value2 = (string.IsNullOrWhiteSpace(text) ? string.Empty : ("$pipe = $null\n$writer = $null\ntry {\n    $pipe = [IO.Pipes.NamedPipeClientStream]::new(\n        '.',\n        '" + text + "',\n        [IO.Pipes.PipeDirection]::Out,\n        [IO.Pipes.PipeOptions]::Asynchronous)\n    $pipe.Connect(250)\n    $writer = [IO.StreamWriter]::new(\n        $pipe,\n        [Text.UTF8Encoding]::new($false),\n        1024,\n        $true)\n    $writer.WriteLine($statusJson)\n    $writer.Flush()\n    $delivered = $true\n} catch {\n    if ($env:VELLUME_CODEX_HOOK_DEBUG -eq '1') {\n        [Console]::Error.WriteLine($_.Exception.ToString())\n    }\n    $delivered = $false\n} finally {\n    if ($writer) {\n        $writer.Dispose()\n    }\n    if ($pipe) {\n        $pipe.Dispose()\n    }\n}"));
		return $"$ErrorActionPreference = 'Stop'\n$eventName = ''\ntry {{\n    $raw = [Console]::In.ReadToEnd()\n    $payload = $raw | ConvertFrom-Json\n    $eventName = [string]$payload.hook_event_name\n    $workspace = ''\n    if ($payload.cwd) {{\n        $workspace = Split-Path -Leaf ([string]$payload.cwd)\n        if ([string]::IsNullOrWhiteSpace($workspace)) {{\n            $workspace = [string]$payload.cwd\n        }}\n    }}\n    $state = 'ready'\n    $headline = 'CODEX 대기 중'\n    $detail = '새 작업을 기다리는 중'\n    switch ($eventName) {{\n        'SessionStart' {{\n            $headline = 'CODEX 연결됨'\n            $detail = '세션이 연결되었습니다'\n        }}\n        'UserPromptSubmit' {{\n            $state = 'working'\n            $headline = '생각하는 중'\n            $detail = '다음 작업을 계획하는 중'\n        }}\n        'PreToolUse' {{\n            $state = 'working'\n            $tool = [string]$payload.tool_name\n            if ($tool -eq 'Bash') {{\n                $headline = '빌드하는 중'\n                $detail = '로컬 명령을 실행하는 중'\n            }} elseif ($tool -eq 'apply_patch') {{\n                $headline = '편집하는 중'\n                $detail = '프로젝트 파일을 수정하는 중'\n            }} else {{\n                $headline = '도구 사용 중'\n                $detail = $tool\n            }}\n        }}\n        'Stop' {{\n            $state = 'complete'\n            $headline = '작업 완료'\n            $detail = '다음 요청을 기다리는 중'\n        }}\n        'SessionEnd' {{\n            $state = 'offline'\n            $headline = 'CODEX 오프라인'\n            $detail = '세션이 종료되었습니다'\n        }}\n    }}\n    $status = [ordered]@{{\n        schemaVersion = 1\n        state = $state\n        headline = $headline\n        detail = $detail\n        workspace = $workspace\n        model = [string]$payload.model\n        eventName = $eventName\n        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')\n    }}\n    $statusJson = $status | ConvertTo-Json -Compress\n    $delivered = $false\n    {value2}\n    if (-not $delivered) {{\n        $statusPath = '{value}'\n        $directory = Split-Path -Parent $statusPath\n        New-Item -ItemType Directory -Force -Path $directory | Out-Null\n        $temporaryPath = \"$statusPath.$([Guid]::NewGuid().ToString('N')).tmp\"\n        [IO.File]::WriteAllText(\n            $temporaryPath,\n            $statusJson,\n            [Text.UTF8Encoding]::new($false))\n        Move-Item -LiteralPath $temporaryPath -Destination $statusPath -Force\n    }}\n}} catch {{\n    # The status bridge is advisory and must never interrupt Codex.\n}}\nif ($eventName -eq 'Stop') {{\n    [Console]::Out.Write('{{}}')\n}}\nexit 0";
	}
}
internal sealed class CodexCompanionPipeServer : IAsyncDisposable
{
	public const string PipeName = "Vellume.Codex.Status.v1";

	private const int MaximumMessageLength = 65536;

	private readonly string _statusPath;

	private readonly IAppLogger _logger;

	private readonly string _pipeName;

	private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

	private readonly object _startGate = new object();

	private Task? _listenTask;

	private bool _disposed;

	public CodexCompanionPipeServer(string statusPath, IAppLogger logger, string pipeName = "Vellume.Codex.Status.v1")
	{
		_statusPath = statusPath;
		_logger = logger;
		_pipeName = pipeName;
	}

	public void Start()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_startGate)
		{
			if (_listenTask == null)
			{
				_listenTask = ListenAsync(_cancellation.Token);
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		await _cancellation.CancelAsync();
		try
		{
			if (_listenTask != null)
			{
				await _listenTask;
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_cancellation.Dispose();
		}
	}

	private async Task ListenAsync(CancellationToken cancellationToken)
	{
		int consecutiveFailures = 0;
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				NamedPipeServerStream pipe = CreatePipe();
				try
				{
					await pipe.WaitForConnectionAsync(cancellationToken);
					using StreamReader reader = new StreamReader(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
					string text = await ReadMessageAsync(reader, cancellationToken);
					if (text != null)
					{
						await PersistAsync(text, cancellationToken);
					}
					consecutiveFailures = 0;
				}
				finally
				{
					if (pipe != null)
					{
						await pipe.DisposeAsync();
					}
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception exception)
			{
				consecutiveFailures++;
				if (consecutiveFailures == 1 || consecutiveFailures % 6 == 0)
				{
					await _logger.ErrorAsync("Codex 컴패니언 로컬 연결 처리에 실패했습니다. 백오프 후 다시 시도합니다.", exception);
				}
				try
				{
					await Task.Delay(TimeSpan.FromSeconds(Math.Min(30.0, Math.Pow(2.0, Math.Min(5, consecutiveFailures - 1)))), cancellationToken);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}
	}

	private NamedPipeServerStream CreatePipe()
	{
		try
		{
			PipeSecurity pipeSecurity = new PipeSecurity();
			PipeAccessRights rights = PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance;
			pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights, AccessControlType.Allow));
			pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-15-2-1"), rights, AccessControlType.Allow));
			return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, pipeSecurity);
		}
		catch (UnauthorizedAccessException)
		{
			return new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
		}
	}

	private static async Task<string?> ReadMessageAsync(StreamReader reader, CancellationToken cancellationToken)
	{
		char[] buffer = new char[4096];
		StringBuilder message = new StringBuilder();
		while (message.Length <= 65536)
		{
			int num = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
			if (num == 0)
			{
				break;
			}
			int num2 = Array.IndexOf(buffer, '\n', 0, num);
			if (num2 >= 0)
			{
				message.Append(buffer, 0, num2);
				break;
			}
			message.Append(buffer, 0, num);
		}
		if (message.Length == 0 || message.Length > 65536)
		{
			return null;
		}
		string text = message.ToString().TrimEnd('\r');
		using JsonDocument jsonDocument = JsonDocument.Parse(text);
		return (jsonDocument.RootElement.ValueKind == JsonValueKind.Object) ? text : null;
	}

	private async Task PersistAsync(string message, CancellationToken cancellationToken)
	{
		string directoryName = System.IO.Path.GetDirectoryName(_statusPath);
		if (!string.IsNullOrWhiteSpace(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		string temporaryPath = $"{_statusPath}.{Guid.NewGuid():N}.tmp";
		try
		{
			await File.WriteAllTextAsync(temporaryPath, message, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
			File.Move(temporaryPath, _statusPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}
}
internal sealed class DesktopHostLocator
{
	private const long WsExNoRedirectionBitmap = 2097152L;

	public bool IsRaisedDesktop { get; private set; }

	public nint ShellViewHandle { get; private set; }

	public nint BackgroundWorkerHandle { get; private set; }

	public nint InteractiveHostHandle { get; private set; }

	public nint FindWallpaperHost()
	{
		IsRaisedDesktop = false;
		ShellViewHandle = IntPtr.Zero;
		BackgroundWorkerHandle = IntPtr.Zero;
		InteractiveHostHandle = IntPtr.Zero;
		nint num = NativeMethods.FindWindowW("Progman", null);
		if (num == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		nint result = NativeMethods.GetWindowLongPtr(num, -20);
		long num2 = ((IntPtr)result).ToInt64();
		IsRaisedDesktop = (num2 & 0x200000) != 0;
		NativeMethods.SendMessageTimeoutW(num, 1324u, new IntPtr(13), new IntPtr(1), 0u, 1000u, out result);
		if (IsRaisedDesktop)
		{
			ShellViewHandle = NativeMethods.FindWindowExW(num, IntPtr.Zero, "SHELLDLL_DefView", null);
			BackgroundWorkerHandle = NativeMethods.FindWindowExW(num, IntPtr.Zero, "WorkerW", null);
			if (ShellViewHandle != IntPtr.Zero && BackgroundWorkerHandle != IntPtr.Zero)
			{
				InteractiveHostHandle = num;
				return num;
			}
			return IntPtr.Zero;
		}
		NativeMethods.GetWindowThreadProcessId(num, out var explorerProcessId);
		NativeMethods.GetWindowRect(num, out var desktopRectangle);
		nint wallpaperHost = IntPtr.Zero;
		nint iconHost = IntPtr.Zero;
		List<nint> workerWindows = new List<nint>();
		NativeMethods.EnumWindows(delegate(nint topLevel, nint _)
		{
			if (IsUsableWorker(topLevel, explorerProcessId, desktopRectangle))
			{
				workerWindows.Add(topLevel);
			}
			nint num4 = NativeMethods.FindWindowExW(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null);
			if (num4 == IntPtr.Zero)
			{
				return true;
			}
			iconHost = topLevel;
			ShellViewHandle = num4;
			nint num5 = NativeMethods.FindWindowExW(IntPtr.Zero, topLevel, "WorkerW", null);
			if (IsUsableWorker(num5, explorerProcessId, desktopRectangle))
			{
				wallpaperHost = num5;
			}
			return true;
		}, IntPtr.Zero);
		if (wallpaperHost != IntPtr.Zero)
		{
			InteractiveHostHandle = ((iconHost != IntPtr.Zero) ? iconHost : num);
			return wallpaperHost;
		}
		if (IsUsableWorker(iconHost, explorerProcessId, desktopRectangle))
		{
			InteractiveHostHandle = iconHost;
			return iconHost;
		}
		nint num3 = workerWindows.FirstOrDefault((nint handle) => handle != iconHost);
		if (num3 != IntPtr.Zero)
		{
			InteractiveHostHandle = ((iconHost != IntPtr.Zero) ? iconHost : num);
			return num3;
		}
		return IntPtr.Zero;
	}

	private static bool IsUsableWorker(nint windowHandle, uint explorerProcessId, NativeMethods.Rect desktopRectangle)
	{
		if (windowHandle == IntPtr.Zero || !GetWindowClass(windowHandle).Equals("WorkerW", StringComparison.OrdinalIgnoreCase) || !NativeMethods.IsWindowVisible(windowHandle))
		{
			return false;
		}
		NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
		if (processId != explorerProcessId || !NativeMethods.GetWindowRect(windowHandle, out var rectangle))
		{
			return false;
		}
		int num = rectangle.Right - rectangle.Left;
		int num2 = rectangle.Bottom - rectangle.Top;
		int num3 = Math.Max(1, desktopRectangle.Right - desktopRectangle.Left);
		int num4 = Math.Max(1, desktopRectangle.Bottom - desktopRectangle.Top);
		if (num >= Math.Min(640, num3 / 2))
		{
			return num2 >= Math.Min(360, num4 / 2);
		}
		return false;
	}

	private static string GetWindowClass(nint windowHandle)
	{
		StringBuilder stringBuilder = new StringBuilder(256);
		if (NativeMethods.GetClassNameW(windowHandle, stringBuilder, stringBuilder.Capacity) <= 0)
		{
			return string.Empty;
		}
		return stringBuilder.ToString();
	}
}
public static class ExecutablePathIdentity
{
	public static bool AreEquivalent(string firstPath, string secondPath)
	{
		return GetCanonicalExecutablePath(firstPath).Equals(GetCanonicalExecutablePath(secondPath), StringComparison.OrdinalIgnoreCase);
	}

	public static string GetCanonicalExecutablePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return string.Empty;
		}
		try
		{
			string fullPath = System.IO.Path.GetFullPath(path);
			string fileName = System.IO.Path.GetFileName(fullPath);
			DirectoryInfo parent = Directory.GetParent(fullPath);
			if (parent?.Parent != null && IsVersionDirectory(parent.Name))
			{
				return System.IO.Path.Combine(parent.Parent.FullName, fileName);
			}
			return fullPath;
		}
		catch
		{
			return path.Trim();
		}
	}

	private static bool IsVersionDirectory(string directoryName)
	{
		string text;
		if (!directoryName.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
		{
			text = directoryName;
		}
		else
		{
			text = directoryName.Substring(4, directoryName.Length - 4);
		}
		string[] array = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
		int result;
		if (array.Length >= 2)
		{
			return array.All((string part) => int.TryParse(part, out result));
		}
		return false;
	}
}
internal readonly record struct ScreenBounds(int Left, int Top, int Right, int Bottom)
{
	public int Width => Math.Max(0, Right - Left);

	public int Height => Math.Max(0, Bottom - Top);
}
internal readonly record struct ForegroundWindowSnapshot(bool IsValid, bool IsVisible, bool IsOwnProcess, uint ProcessId, string ProcessName, string WindowClass, bool IsMaximized, ScreenBounds WindowBounds, ScreenBounds MonitorBounds, ScreenBounds WorkAreaBounds);
internal static class ForegroundAppPausePolicy
{
	private const double FullWorkspaceCoverageThreshold = 0.88;

	private static readonly HashSet<string> DesktopWindowClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW", "SHELLDLL_DefView", "SysListView32", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

	private static readonly HashSet<string> ShellProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer", "ApplicationFrameHost", "SystemSettings", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "LockApp" };

	private static readonly string[] HeavyProcessPrefixes = new string[8] { "UnrealEditor", "UE4Editor", "UE5Editor", "Unity", "devenv", "blender", "Photoshop", "Substance3D" };

	private static readonly HashSet<string> HeavyWindowClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UnrealWindow", "UnityWndClass" };

	internal static bool ShouldPause(ForegroundWindowSnapshot snapshot)
	{
		if (IsEligibleForegroundApp(snapshot) && !IsShellOrNavigationWindow(snapshot))
		{
			if (!snapshot.IsMaximized && !CoversWorkspace(snapshot))
			{
				return IsKnownHeavyApplication(snapshot);
			}
			return true;
		}
		return false;
	}

	internal static ApplicationPlaybackAction ResolveAction(ForegroundWindowSnapshot snapshot, PlaybackOptions options)
	{
		if (!options.PauseWhenForegroundAppIsMaximized || !IsEligibleForegroundApp(snapshot) || IsShellOrNavigationWindow(snapshot))
		{
			return ApplicationPlaybackAction.KeepRunning;
		}
		string processName = WindowsPerformanceModeDetector.NormalizeProcessName(snapshot.ProcessName);
		ApplicationPlaybackRule applicationPlaybackRule = (options.ApplicationRules ?? new List<ApplicationPlaybackRule>()).FirstOrDefault((ApplicationPlaybackRule rule) => rule != null && !string.IsNullOrWhiteSpace(rule.ProcessName) && WindowsPerformanceModeDetector.NormalizeProcessName(rule.ProcessName).Equals(processName, StringComparison.OrdinalIgnoreCase) && (rule.Condition == ApplicationRuleCondition.IsForeground || rule.Condition == ApplicationRuleCondition.IsRunning));
		if (applicationPlaybackRule != null)
		{
			return applicationPlaybackRule.Action;
		}
		if (!snapshot.IsMaximized && !CoversWorkspace(snapshot) && !IsKnownHeavyApplication(snapshot))
		{
			return ApplicationPlaybackAction.KeepRunning;
		}
		return ApplicationPlaybackAction.Pause;
	}

	internal static bool IsEligibleForegroundApp(ForegroundWindowSnapshot snapshot)
	{
		if (snapshot.IsValid && snapshot.IsVisible && !snapshot.IsOwnProcess && snapshot.ProcessId != 0)
		{
			return !DesktopWindowClasses.Contains(snapshot.WindowClass);
		}
		return false;
	}

	internal static bool IsShellOrNavigationWindow(ForegroundWindowSnapshot snapshot)
	{
		bool flag = ShellProcessNames.Contains(snapshot.ProcessName);
		if (!flag)
		{
			string windowClass = snapshot.WindowClass;
			bool flag2 = ((windowClass == "CabinetWClass" || windowClass == "ExploreWClass") ? true : false);
			flag = flag2;
		}
		return flag;
	}

	internal static bool CoversWorkspace(ForegroundWindowSnapshot snapshot)
	{
		ScreenBounds screenBounds = ((snapshot.WorkAreaBounds.Width > 0 && snapshot.WorkAreaBounds.Height > 0) ? snapshot.WorkAreaBounds : snapshot.MonitorBounds);
		if (screenBounds.Width <= 0 || screenBounds.Height <= 0 || snapshot.WindowBounds.Width <= 0 || snapshot.WindowBounds.Height <= 0)
		{
			return false;
		}
		int num = Math.Max(0, Math.Min(snapshot.WindowBounds.Right, screenBounds.Right) - Math.Max(snapshot.WindowBounds.Left, screenBounds.Left));
		int num2 = Math.Max(0, Math.Min(snapshot.WindowBounds.Bottom, screenBounds.Bottom) - Math.Max(snapshot.WindowBounds.Top, screenBounds.Top));
		double num3 = (double)screenBounds.Width * (double)screenBounds.Height;
		return (double)num * (double)num2 / num3 >= 0.88;
	}

	internal static bool IsKnownHeavyApplication(ForegroundWindowSnapshot snapshot)
	{
		if (!HeavyWindowClasses.Contains(snapshot.WindowClass))
		{
			return HeavyProcessPrefixes.Any((string prefix) => snapshot.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
		}
		return true;
	}
}
internal sealed class ForegroundAppPauseDetector
{
	internal ForegroundWindowSnapshot Capture()
	{
		nint foregroundWindow = NativeMethods.GetForegroundWindow();
		if (foregroundWindow == IntPtr.Zero || !NativeMethods.IsWindow(foregroundWindow))
		{
			return default(ForegroundWindowSnapshot);
		}
		StringBuilder stringBuilder = new StringBuilder(128);
		NativeMethods.GetClassNameW(foregroundWindow, stringBuilder, stringBuilder.Capacity);
		NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var processId);
		string processName = TryGetProcessName(processId);
		NativeMethods.Rect rectangle;
		bool windowRect = NativeMethods.GetWindowRect(foregroundWindow, out rectangle);
		nint num = NativeMethods.MonitorFromWindow(foregroundWindow, 2u);
		NativeMethods.MonitorInfoEx monitorInfoEx = default(NativeMethods.MonitorInfoEx);
		monitorInfoEx.Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>();
		monitorInfoEx.DeviceName = string.Empty;
		NativeMethods.MonitorInfoEx monitorInfo = monitorInfoEx;
		bool flag = num != IntPtr.Zero && NativeMethods.GetMonitorInfoW(num, ref monitorInfo);
		return new ForegroundWindowSnapshot(IsValid: true, NativeMethods.IsWindowVisible(foregroundWindow), processId == (uint)Environment.ProcessId, processId, processName, stringBuilder.ToString(), NativeMethods.IsZoomed(foregroundWindow), windowRect ? ToBounds(rectangle) : default(ScreenBounds), flag ? ToBounds(monitorInfo.Monitor) : default(ScreenBounds), flag ? ToBounds(monitorInfo.WorkArea) : default(ScreenBounds));
	}

	private static ScreenBounds ToBounds(NativeMethods.Rect rectangle)
	{
		return new ScreenBounds(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
	}

	private static string TryGetProcessName(uint processId)
	{
		if (processId == 0)
		{
			return string.Empty;
		}
		try
		{
			using Process process = Process.GetProcessById(checked((int)processId));
			return process.ProcessName;
		}
		catch
		{
			return string.Empty;
		}
	}
}
internal interface IWallpaperRenderer
{
	nint Handle { get; }

	string BackendDisplayName { get; }

	bool IsStaticPerformanceMode { get; }

	bool IsDesktopWallpaperIsolationActive { get; }

	bool HasCompositionWindow { get; }

	bool IsReady { get; }

	string GetAttachmentDiagnostics();

	Task AttachToAsync(nint hostHandle, DisplayInfo display, bool raisedDesktop, nint shellViewHandle, nint backgroundWorkerHandle, CancellationToken cancellationToken);

	Task InitializeAndNavigateAsync(WallpaperItem wallpaper, PlaybackOptions options, CancellationToken cancellationToken);

	Task CapturePreviewAsync(string outputPath, CancellationToken cancellationToken);

	bool IsAttachedTo(nint hostHandle);

	Task SetWindowVisibleAsync(bool visible, CancellationToken cancellationToken);

	Task PauseAsync();

	Task ResumeAsync();

	Task EnterStaticPerformanceModeAsync();

	Task ExitStaticPerformanceModeAsync();

	Task<bool> DestroyCompositionWindowForIsolationAsync(CancellationToken cancellationToken);

	Task UpdatePlaybackAsync(PlaybackOptions options);

	Task CloseSafelyAsync();
}
internal sealed class LayeredWpfPanelHost : IDisposable
{
	private nint _memoryDc;

	private nint _dib;

	private nint _previousBitmap;

	private nint _bits;

	private byte[] _pixelBuffer = Array.Empty<byte>();

	private int _surfaceWidth;

	private int _surfaceHeight;

	private bool _disposed;

	internal nint Handle { get; private set; }

	internal int Width { get; private set; }

	internal int Height { get; private set; }

	internal long PresentationGeneration { get; private set; }

	internal bool LastPresentSucceeded { get; private set; }

	internal int LastNonTransparentPixelCount { get; private set; }

	internal bool HasPresentedFrame
	{
		get
		{
			if (LastPresentSucceeded)
			{
				return _pixelBuffer.Length == checked(Width * Height * 4);
			}
			return false;
		}
	}

	internal bool IsReady
	{
		get
		{
			if (!_disposed && Handle != IntPtr.Zero)
			{
				return NativeMethods.IsWindow(Handle);
			}
			return false;
		}
	}

	internal LayeredWpfPanelHost(string name, nint parentHandle, int x, int y, int width, int height)
	{
		Width = width;
		Height = height;
		Handle = NativeMethods.CreateWindowExW(134742176u, "Static", name, 1174405120u, x, y, width, height, parentHandle, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
		if (Handle == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 알파 위젯 패널을 만들지 못했습니다.");
		}
	}

	internal bool IsAttachedTo(nint parentHandle)
	{
		if (IsReady)
		{
			return NativeMethods.GetParent(Handle) == parentHandle;
		}
		return false;
	}

	internal void SetWindowVisible(bool visible)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (Handle != IntPtr.Zero && NativeMethods.IsWindow(Handle) && (!visible || HasPresentedFrame))
		{
			NativeMethods.ShowWindow(Handle, visible ? 4 : 0);
		}
	}

	internal void AttachTo(nint parentHandle, int x, int y, int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		NativeMethods.SetParent(Handle, parentHandle);
		Position(x, y, width, height);
		if (NativeMethods.GetParent(Handle) != parentHandle)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "알파 위젯 패널을 데스크톱에 다시 연결하지 못했습니다.");
		}
	}

	internal void Position(int x, int y, int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		bool num = Width != width || Height != height;
		Width = width;
		Height = height;
		if (!NativeMethods.SetWindowPos(Handle, IntPtr.Zero, x, y, width, height, 52u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "알파 위젯 패널의 위치를 갱신하지 못했습니다.");
		}
		if (num)
		{
			ReleasePresentationSurface();
		}
	}

	internal void Render(FrameworkElement visual)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (Width <= 0 || Height <= 0)
		{
			return;
		}
		visual.Measure(new System.Windows.Size(Width, Height));
		visual.Arrange(new Rect(0.0, 0.0, Width, Height));
		visual.UpdateLayout();
		RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(Width * 2, Height * 2, 192.0, 192.0, PixelFormats.Pbgra32);
		renderTargetBitmap.Render(visual);
		DrawingVisual drawingVisual = new DrawingVisual();
		RenderOptions.SetBitmapScalingMode(drawingVisual, BitmapScalingMode.HighQuality);
		using (DrawingContext drawingContext = drawingVisual.RenderOpen())
		{
			drawingContext.DrawImage(renderTargetBitmap, new Rect(0.0, 0.0, Width, Height));
		}
		RenderTargetBitmap renderTargetBitmap2 = new RenderTargetBitmap(Width, Height, 96.0, 96.0, PixelFormats.Pbgra32);
		renderTargetBitmap2.Render(drawingVisual);
		EnsurePresentationSurface();
		renderTargetBitmap2.CopyPixels(stride: checked(Width * 4), pixels: _pixelBuffer, offset: 0);
		int num = 0;
		for (int i = 3; i < _pixelBuffer.Length; i += 4)
		{
			if (_pixelBuffer[i] != 0)
			{
				num++;
			}
		}
		LastNonTransparentPixelCount = num;
		Present();
	}

	internal bool PresentCachedFrame()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_pixelBuffer.Length != checked(Width * Height * 4))
		{
			return false;
		}
		Present();
		return true;
	}

	private void EnsurePresentationSurface()
	{
		if (_memoryDc != IntPtr.Zero && _dib != IntPtr.Zero && _bits != IntPtr.Zero && _surfaceWidth == Width && _surfaceHeight == Height)
		{
			return;
		}
		ReleasePresentationSurface();
		int num = checked(Width * Height * 4);
		nint num2 = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
		if (num2 == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "알파 위젯용 메모리 DC를 만들지 못했습니다.");
		}
		nint num3 = IntPtr.Zero;
		nint num4 = IntPtr.Zero;
		try
		{
			NativeMethods.BitmapInfo bitmapInfo = default(NativeMethods.BitmapInfo);
			bitmapInfo.Header = new NativeMethods.BitmapInfoHeader
			{
				Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
				Width = Width,
				Height = -Height,
				Planes = 1,
				BitCount = 32,
				Compression = 0u,
				SizeImage = (uint)num
			};
			NativeMethods.BitmapInfo bitmapInfo2 = bitmapInfo;
			num3 = NativeMethods.CreateDIBSection(num2, ref bitmapInfo2, 0u, out var bitmapBits, IntPtr.Zero, 0u);
			if (num3 == IntPtr.Zero || bitmapBits == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "알파 위젯용 32비트 화면을 만들지 못했습니다.");
			}
			num4 = NativeMethods.SelectObject(num2, num3);
			if (num4 == IntPtr.Zero || num4 == new IntPtr(-1))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "알파 위젯용 32비트 화면을 선택하지 못했습니다.");
			}
			_memoryDc = num2;
			_dib = num3;
			_previousBitmap = num4;
			_bits = bitmapBits;
			_pixelBuffer = new byte[num];
			_surfaceWidth = Width;
			_surfaceHeight = Height;
			num2 = IntPtr.Zero;
			num3 = IntPtr.Zero;
			num4 = IntPtr.Zero;
		}
		finally
		{
			if (num2 != IntPtr.Zero && num4 != IntPtr.Zero && num4 != new IntPtr(-1))
			{
				NativeMethods.SelectObject(num2, num4);
			}
			if (num3 != IntPtr.Zero)
			{
				NativeMethods.DeleteObject(num3);
			}
			if (num2 != IntPtr.Zero)
			{
				NativeMethods.DeleteDC(num2);
			}
		}
	}

	private void Present()
	{
		LastPresentSucceeded = false;
		EnsurePresentationSurface();
		Marshal.Copy(_pixelBuffer, 0, _bits, _pixelBuffer.Length);
		NativeMethods.NativeSize nativeSize = default(NativeMethods.NativeSize);
		nativeSize.Width = Width;
		nativeSize.Height = Height;
		NativeMethods.NativeSize size = nativeSize;
		NativeMethods.NativePoint sourcePoint = default(NativeMethods.NativePoint);
		NativeMethods.BlendFunction blendFunction = default(NativeMethods.BlendFunction);
		blendFunction.BlendOp = 0;
		blendFunction.SourceConstantAlpha = byte.MaxValue;
		blendFunction.AlphaFormat = 1;
		NativeMethods.BlendFunction blend = blendFunction;
		if (!NativeMethods.UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, _memoryDc, ref sourcePoint, 0u, ref blend, 2u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 알파 위젯 프레임을 표시하지 못했습니다.");
		}
		LastPresentSucceeded = true;
		PresentationGeneration++;
	}

	private void ReleasePresentationSurface()
	{
		if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero && _previousBitmap != new IntPtr(-1))
		{
			NativeMethods.SelectObject(_memoryDc, _previousBitmap);
		}
		if (_dib != IntPtr.Zero)
		{
			NativeMethods.DeleteObject(_dib);
		}
		if (_memoryDc != IntPtr.Zero)
		{
			NativeMethods.DeleteDC(_memoryDc);
		}
		_memoryDc = IntPtr.Zero;
		_dib = IntPtr.Zero;
		_previousBitmap = IntPtr.Zero;
		_bits = IntPtr.Zero;
		_pixelBuffer = Array.Empty<byte>();
		_surfaceWidth = 0;
		_surfaceHeight = 0;
		LastPresentSucceeded = false;
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			ReleasePresentationSurface();
			if (Handle != IntPtr.Zero && NativeMethods.IsWindow(Handle))
			{
				NativeMethods.DestroyWindow(Handle);
			}
			Handle = IntPtr.Zero;
		}
	}
}
internal static class MikuPalette
{
	internal static readonly System.Windows.Media.Color Graphite = System.Windows.Media.Color.FromRgb(55, 59, 62);

	internal static readonly System.Windows.Media.Color Silver = System.Windows.Media.Color.FromRgb(190, 200, 209);

	internal static readonly System.Windows.Media.Color Mint = System.Windows.Media.Color.FromRgb(134, 206, 203);

	internal static readonly System.Windows.Media.Color Teal = System.Windows.Media.Color.FromRgb(19, 122, 127);

	internal static readonly System.Windows.Media.Color Pink = System.Windows.Media.Color.FromRgb(225, 40, 133);

	internal static System.Windows.Media.Color WithAlpha(System.Windows.Media.Color color, byte alpha)
	{
		return System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B);
	}
}
internal enum MediaPlaybackCommand
{
	Previous,
	Toggle,
	Next
}
internal sealed class NativeMediaControlsWindow
{
	private NativeWpfChildHost? _nativeHost;

	private HwndSource? _source;

	private nint _interactiveHostHandle;

	private int _relativeX;

	private int _relativeY;

	private int _width;

	private int _height;

	private double _scale = 1.0;

	private Border? _previousButton;

	private Border? _toggleButton;

	private Border? _nextButton;

	private bool _isAvailable;

	private bool _isPlaying;

	private bool _isWindowVisible = true;

	private bool _disposed;

	internal nint Handle => _nativeHost?.Handle ?? IntPtr.Zero;

	internal bool IsReady
	{
		get
		{
			if (!_disposed)
			{
				return _nativeHost?.IsReady ?? false;
			}
			return false;
		}
	}

	internal event EventHandler<MediaPlaybackCommand>? CommandRequested;

	internal void AttachTo(nint interactiveHostHandle, DisplayInfo display, WidgetSide side)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!NativeMethods.GetWindowRect(interactiveHostHandle, out var rectangle))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 미디어 버튼 영역을 읽지 못했습니다.");
		}
		_interactiveHostHandle = interactiveHostHandle;
		_scale = ((display.Width <= 1450) ? 0.82 : 1.0);
		int num = ((side == WidgetSide.Left) ? 28 : 188);
		double num2 = ((side == WidgetSide.Left) ? 30.0 : ((double)display.Width - 42.0 - 430.0));
		double num3 = ((side == WidgetSide.Left) ? (96.0 * _scale) : (430.0 - 334.0 * _scale));
		_relativeX = display.X - rectangle.Left + (int)Math.Round(num2 + num3);
		_relativeY = display.Y - rectangle.Top + (int)Math.Round((double)num + 56.0 * _scale);
		_height = Math.Max(25, (int)Math.Round(30.0 * _scale));
		int num4 = Math.Max(5, (int)Math.Round(7.0 * _scale));
		_width = _height * 3 + num4 * 2;
		if (_nativeHost != null)
		{
			_nativeHost.AttachTo(interactiveHostHandle, _relativeX, _relativeY, _width, _height);
			BuildVisualTree();
			ApplyButtonRegion();
			ApplyWindowVisibility();
		}
	}

	internal Task InitializeAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellationToken.ThrowIfCancellationRequested();
		if (_source != null)
		{
			return Task.CompletedTask;
		}
		_nativeHost = new NativeWpfChildHost("Vellume Native Media Controls", _interactiveHostHandle, _relativeX, _relativeY, _width, _height, clickThrough: false);
		_source = _nativeHost.Source;
		BuildVisualTree();
		ApplyButtonRegion();
		ApplyWindowVisibility();
		return Task.CompletedTask;
	}

	internal void SetWindowVisible(bool visible)
	{
		if (!_disposed && _isWindowVisible != visible)
		{
			_isWindowVisible = visible;
			ApplyWindowVisibility();
		}
	}

	internal Task UpdateAsync(bool isAvailable, bool isPlaying, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_isAvailable = isAvailable;
		_isPlaying = isPlaying;
		UpdateButtonStates();
		return Task.CompletedTask;
	}

	internal bool IsAttachedTo(nint interactiveHostHandle)
	{
		return _nativeHost?.IsAttachedTo(interactiveHostHandle) ?? false;
	}

	internal Task CloseSafelyAsync()
	{
		if (_disposed)
		{
			return Task.CompletedTask;
		}
		_disposed = true;
		_nativeHost?.Dispose();
		_nativeHost = null;
		_source = null;
		_previousButton = null;
		_toggleButton = null;
		_nextButton = null;
		return Task.CompletedTask;
	}

	private void BuildVisualTree()
	{
		if (_source != null)
		{
			Canvas canvas = new Canvas
			{
				Width = _width,
				Height = _height,
				Background = System.Windows.Media.Brushes.Transparent,
				SnapsToDevicePixels = false,
				UseLayoutRounding = false
			};
			int num = Math.Max(5, (int)Math.Round(7.0 * _scale));
			_previousButton = CreateButton(MediaPlaybackCommand.Previous);
			_toggleButton = CreateButton(MediaPlaybackCommand.Toggle);
			_nextButton = CreateButton(MediaPlaybackCommand.Next);
			AddButton(canvas, _previousButton, 0.0);
			AddButton(canvas, _toggleButton, _height + num);
			AddButton(canvas, _nextButton, (_height + num) * 2);
			_source.RootVisual = canvas;
			UpdateButtonStates();
		}
	}

	private void ApplyWindowVisibility()
	{
		nint handle = Handle;
		if (handle != IntPtr.Zero && NativeMethods.IsWindow(handle))
		{
			NativeMethods.ShowWindow(handle, _isWindowVisible ? 4 : 0);
		}
	}

	private Border CreateButton(MediaPlaybackCommand command)
	{
		Border border = new Border();
		border.Width = _height;
		border.Height = _height;
		border.CornerRadius = new CornerRadius((double)_height / 2.0);
		border.BorderThickness = new Thickness(0.0);
		border.BorderBrush = System.Windows.Media.Brushes.Transparent;
		border.Background = System.Windows.Media.Brushes.Transparent;
		border.Cursor = Cursors.Hand;
		Border border2 = border;
		border2.ToolTip = command switch
		{
			MediaPlaybackCommand.Previous => "이전 곡", 
			MediaPlaybackCommand.Toggle => "재생 또는 일시정지", 
			_ => "다음 곡", 
		};
		border.MouseLeftButtonUp += delegate(object _, MouseButtonEventArgs eventArgs)
		{
			if (_isAvailable)
			{
				eventArgs.Handled = true;
				this.CommandRequested?.Invoke(this, command);
			}
		};
		return border;
	}

	private static void AddButton(Canvas canvas, Border button, double left)
	{
		Canvas.SetLeft(button, left);
		Canvas.SetTop(button, 0.0);
		canvas.Children.Add(button);
	}

	private void UpdateButtonStates()
	{
		Border[] array = new Border[3] { _previousButton, _toggleButton, _nextButton };
		foreach (Border border in array)
		{
			if (border != null)
			{
				border.IsHitTestVisible = _isAvailable;
				border.Cursor = (_isAvailable ? Cursors.Hand : Cursors.Arrow);
			}
		}
	}

	internal static Geometry CreateGlyphGeometry(MediaPlaybackCommand command, bool isPlaying)
	{
		return Geometry.Parse(command switch
		{
			MediaPlaybackCommand.Previous => "M 3,3 H 5 V 13 H 3 Z M 13,3 L 13,13 L 5,8 Z", 
			MediaPlaybackCommand.Next => "M 11,3 H 13 V 13 H 11 Z M 3,3 L 11,8 L 3,13 Z", 
			_ => (!isPlaying) ? "M 4,3 L 13,8 L 4,13 Z" : "M 4,3 H 7 V 13 H 4 Z M 9,3 H 12 V 13 H 9 Z", 
		});
	}

	private void ApplyButtonRegion()
	{
		if (_source == null)
		{
			return;
		}
		nint num = NativeMethods.CreateRectRgn(0, 0, 0, 0);
		if (num == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 미디어 버튼 영역을 만들지 못했습니다.");
		}
		try
		{
			int num2 = Math.Max(5, (int)Math.Round(7.0 * _scale));
			for (int i = 0; i < 3; i++)
			{
				int num3 = i * (_height + num2);
				nint num4 = NativeMethods.CreateEllipticRgn(num3, 0, num3 + _height, _height);
				if (num4 == IntPtr.Zero)
				{
					throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 미디어 버튼 모양을 만들지 못했습니다.");
				}
				try
				{
					if (NativeMethods.CombineRgn(num, num, num4, 2) == 0)
					{
						throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 미디어 버튼 영역을 합치지 못했습니다.");
					}
				}
				finally
				{
					NativeMethods.DeleteObject(num4);
				}
			}
			if (NativeMethods.SetWindowRgn(_nativeHost.Handle, num, redraw: true) == 0)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 미디어 버튼 영역을 적용하지 못했습니다.");
			}
			num = IntPtr.Zero;
		}
		finally
		{
			if (num != IntPtr.Zero)
			{
				NativeMethods.DeleteObject(num);
			}
		}
	}
}
internal sealed class NativeMediaEnginePlayer : IDisposable
{
	private IMFMediaEngine? _engine;

	private MediaEngineNotify? _notify;

	private bool _runtimeAcquired;

	private bool _disposed;

	internal bool IsReady { get; private set; }

	internal uint NativeWidth { get; private set; }

	internal uint NativeHeight { get; private set; }

	internal Task InitializeAsync(nint playbackWindow, string sourcePath, bool isMuted, double volume, CancellationToken cancellationToken)
	{
		return InitializeAsync(playbackWindow, sourcePath, isMuted, volume, 0.0, cancellationToken);
	}

	internal async Task InitializeAsync(nint playbackWindow, string sourcePath, bool isMuted, double volume, double initialPositionSeconds, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (playbackWindow == IntPtr.Zero || !NativeMethods.IsWindow(playbackWindow))
		{
			throw new InvalidOperationException("Media Foundation 영상 출력 창이 준비되지 않았습니다.");
		}
		MediaFoundationRuntime.Acquire();
		_runtimeAcquired = true;
		IMFAttributes attributes = null;
		IMFMediaEngineClassFactory factory = null;
		try
		{
			ThrowIfFailed(MediaFoundationNative.MFCreateAttributes(out attributes, 2u));
			_notify = new MediaEngineNotify();
			Guid key = MediaFoundationNative.MediaEngineCallback;
			ThrowIfFailed(attributes.SetUnknown(ref key, _notify));
			Guid key2 = MediaFoundationNative.MediaEnginePlaybackHwnd;
			ThrowIfFailed(attributes.SetUINT64(ref key2, (ulong)((IntPtr)playbackWindow).ToInt64()));
			Type type = Type.GetTypeFromCLSID(MediaFoundationNative.MediaEngineClassFactory, throwOnError: true) ?? throw new InvalidOperationException("Media Foundation Media Engine 팩터리를 찾지 못했습니다.");
			factory = (IMFMediaEngineClassFactory)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("Media Foundation Media Engine 팩터리를 만들지 못했습니다."));
			ThrowIfFailed(factory.CreateInstance(0u, attributes, out _engine));
			ThrowIfFailed(_engine.SetLoop(loop: true));
			ThrowIfFailed(_engine.SetAutoPlay(autoPlay: false));
			ThrowIfFailed(_engine.SetMuted(isMuted));
			ThrowIfFailed(_engine.SetVolume(Math.Clamp(volume, 0.0, 1.0)));
			ThrowIfFailed(_engine.SetSource(System.IO.Path.GetFullPath(sourcePath)));
			ThrowIfFailed(_engine.Load());
			await _notify.WaitUntilMetadataReadyAsync(cancellationToken);
			ThrowIfFailed(_engine.GetNativeVideoSize(out var width, out var height));
			NativeWidth = width;
			NativeHeight = height;
			if (NativeWidth == 0 || NativeHeight == 0)
			{
				throw new InvalidOperationException("Media Foundation이 영상 크기를 확인하지 못했습니다.");
			}
			if (double.IsFinite(initialPositionSeconds) && initialPositionSeconds > 0.1)
			{
				double duration = _engine.GetDuration();
				double currentTime = ((double.IsFinite(duration) && duration > 0.0) ? (initialPositionSeconds % duration) : initialPositionSeconds);
				ThrowIfFailed(_engine.SetCurrentTime(currentTime));
			}
			ThrowIfFailed(_engine.Play());
			await _notify.WaitUntilFirstFrameReadyAsync(cancellationToken);
			IsReady = true;
		}
		catch
		{
			Dispose();
			throw;
		}
		finally
		{
			ReleaseComObject(factory);
			ReleaseComObject(attributes);
		}
	}

	internal Task PauseAsync()
	{
		if (_engine != null)
		{
			ThrowIfFailed(_engine.Pause());
		}
		return Task.CompletedTask;
	}

	internal Task ResumeAsync()
	{
		if (_engine != null)
		{
			ThrowIfFailed(_engine.Play());
		}
		return Task.CompletedTask;
	}

	internal Task UpdateAudioAsync(bool isMuted, double volume)
	{
		if (_engine != null)
		{
			ThrowIfFailed(_engine.SetMuted(isMuted));
			ThrowIfFailed(_engine.SetVolume(Math.Clamp(volume, 0.0, 1.0)));
		}
		return Task.CompletedTask;
	}

	internal double GetCurrentTimeSeconds()
	{
		if (_engine == null || _disposed)
		{
			return 0.0;
		}
		try
		{
			double currentTime = _engine.GetCurrentTime();
			return (double.IsFinite(currentTime) && currentTime > 0.0) ? currentTime : 0.0;
		}
		catch
		{
			return 0.0;
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		IsReady = false;
		_notify?.Cancel();
		_notify = null;
		if (_engine != null)
		{
			try
			{
				_engine.Pause();
				_engine.Shutdown();
			}
			catch
			{
			}
		}
		ReleaseComObject(_engine);
		_engine = null;
		if (_runtimeAcquired)
		{
			_runtimeAcquired = false;
			MediaFoundationRuntime.Release();
		}
	}

	private static void ThrowIfFailed(int result)
	{
		if (result < 0)
		{
			Marshal.ThrowExceptionForHR(result);
		}
	}

	private static void ReleaseComObject(object? value)
	{
		if (value != null && Marshal.IsComObject(value))
		{
			Marshal.FinalReleaseComObject(value);
		}
	}
}
internal static class MediaFoundationRuntime
{
	private static readonly object Sync = new object();

	private static int _referenceCount;

	internal static void Acquire()
	{
		lock (Sync)
		{
			if (_referenceCount == 0)
			{
				int num = MediaFoundationNative.MFStartup(131184, 0);
				if (num < 0)
				{
					Marshal.ThrowExceptionForHR(num);
				}
			}
			_referenceCount++;
		}
	}

	internal static void Release()
	{
		lock (Sync)
		{
			if (_referenceCount != 0)
			{
				_referenceCount--;
				if (_referenceCount == 0)
				{
					MediaFoundationNative.MFShutdown();
				}
			}
		}
	}
}
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class MediaEngineNotify : IMFMediaEngineNotify
{
	private readonly TaskCompletionSource<bool> _metadataReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly TaskCompletionSource<bool> _firstFrameReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

	public int EventNotify(uint eventId, nuint parameterOne, uint parameterTwo)
	{
		switch (eventId)
		{
		case 11u:
		case 14u:
			_metadataReady.TrySetResult(result: true);
			break;
		case 1009u:
			_firstFrameReady.TrySetResult(result: true);
			break;
		case 5u:
		case 1014u:
		{
			InvalidOperationException exception = new InvalidOperationException("Media Foundation이 영상을 디코딩하지 못했습니다 " + $"(0x{parameterOne:X}, 0x{parameterTwo:X8}).");
			_metadataReady.TrySetException(exception);
			_firstFrameReady.TrySetException(exception);
			break;
		}
		}
		return 0;
	}

	internal async Task WaitUntilMetadataReadyAsync(CancellationToken cancellationToken)
	{
		try
		{
			await _metadataReady.Task.WaitAsync(TimeSpan.FromSeconds(8.0), cancellationToken);
		}
		catch (TimeoutException innerException)
		{
			throw new TimeoutException("Media Foundation 영상이 8초 안에 준비되지 않았습니다.", innerException);
		}
	}

	internal async Task WaitUntilFirstFrameReadyAsync(CancellationToken cancellationToken)
	{
		try
		{
			await _firstFrameReady.Task.WaitAsync(TimeSpan.FromSeconds(8.0), cancellationToken);
		}
		catch (TimeoutException innerException)
		{
			throw new TimeoutException("Media Foundation의 첫 영상 프레임이 8초 안에 표시되지 않았습니다.", innerException);
		}
	}

	internal void Cancel()
	{
		_metadataReady.TrySetCanceled();
		_firstFrameReady.TrySetCanceled();
	}
}
internal static class MediaFoundationNative
{
	internal const int Version = 131184;

	internal const uint EventError = 5u;

	internal const uint EventLoadedData = 11u;

	internal const uint EventCanPlay = 14u;

	internal const uint EventFirstFrameReady = 1009u;

	internal const uint EventStreamRenderingError = 1014u;

	internal static readonly Guid MediaEngineClassFactory = new Guid("B44392DA-499B-446B-A4CB-005FEAD0E6D5");

	internal static readonly Guid MediaEngineCallback = new Guid("C60381B8-83A4-41F8-A3D0-DE05076849A9");

	internal static readonly Guid MediaEnginePlaybackHwnd = new Guid("D988879B-67C9-4D92-BAA7-6EADD446039D");

	[DllImport("mfplat.dll", ExactSpelling = true)]
	internal static extern int MFStartup(int version, int flags);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	internal static extern int MFShutdown();

	[DllImport("mfplat.dll", ExactSpelling = true)]
	internal static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
}
[ComImport]
[Guid("FEE7C112-E776-42B5-9BBF-0048524E2BD5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngineNotify
{
	[PreserveSig]
	int EventNotify(uint eventId, nuint parameterOne, uint parameterTwo);
}
[ComImport]
[Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngineClassFactory
{
	[PreserveSig]
	int CreateInstance(uint flags, IMFAttributes attributes, out IMFMediaEngine mediaEngine);

	[PreserveSig]
	int CreateTimeRange(out nint timeRange);

	[PreserveSig]
	int CreateError(out nint error);
}
[ComImport]
[Guid("98A1B0BB-03EB-4935-AE7C-93C1FA0E1C93")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngine
{
	[PreserveSig]
	int GetError(out nint error);

	[PreserveSig]
	int SetErrorCode(int error);

	[PreserveSig]
	int SetSourceElements(nint sourceElements);

	[PreserveSig]
	int SetSource([MarshalAs(UnmanagedType.BStr)] string source);

	[PreserveSig]
	int GetCurrentSource([MarshalAs(UnmanagedType.BStr)] out string source);

	[PreserveSig]
	ushort GetNetworkState();

	[PreserveSig]
	int GetPreload();

	[PreserveSig]
	int SetPreload(int preload);

	[PreserveSig]
	int GetBuffered(out nint buffered);

	[PreserveSig]
	int Load();

	[PreserveSig]
	int CanPlayType([MarshalAs(UnmanagedType.BStr)] string type, out int answer);

	[PreserveSig]
	ushort GetReadyState();

	[PreserveSig]
	[return: MarshalAs(UnmanagedType.Bool)]
	bool IsSeeking();

	[PreserveSig]
	double GetCurrentTime();

	[PreserveSig]
	int SetCurrentTime(double seekTime);

	[PreserveSig]
	double GetStartTime();

	[PreserveSig]
	double GetDuration();

	[PreserveSig]
	[return: MarshalAs(UnmanagedType.Bool)]
	bool IsPaused();

	[PreserveSig]
	double GetDefaultPlaybackRate();

	[PreserveSig]
	int SetDefaultPlaybackRate(double rate);

	[PreserveSig]
	double GetPlaybackRate();

	[PreserveSig]
	int SetPlaybackRate(double rate);

	[PreserveSig]
	int GetPlayed(out nint played);

	[PreserveSig]
	int GetSeekable(out nint seekable);

	[PreserveSig]
	[return: MarshalAs(UnmanagedType.Bool)]
	bool IsEnded();

	[PreserveSig]
	[return: MarshalAs(UnmanagedType.Bool)]
	bool GetAutoPlay();

	[PreserveSig]
	int SetAutoPlay([MarshalAs(UnmanagedType.Bool)] bool autoPlay);

	[PreserveSig]
	[return: MarshalAs(UnmanagedType.Bool)]
	bool GetLoop();

	[PreserveSig]
	int SetLoop([MarshalAs(UnmanagedType.Bool)] bool loop);

	[PreserveSig]
	int Play();

	[PreserveSig]
	int Pause();

	[PreserveSig]
	[return: MarshalAs(UnmanagedType.Bool)]
	bool GetMuted();

	[PreserveSig]
	int SetMuted([MarshalAs(UnmanagedType.Bool)] bool muted);

	[PreserveSig]
	double GetVolume();

	[PreserveSig]
	int SetVolume(double volume);

	[PreserveSig]
	[return: MarshalAs(UnmanagedType.Bool)]
	bool HasVideo();

	[PreserveSig]
	[return: MarshalAs(UnmanagedType.Bool)]
	bool HasAudio();

	[PreserveSig]
	int GetNativeVideoSize(out uint width, out uint height);

	[PreserveSig]
	int GetVideoAspectRatio(out uint width, out uint height);

	[PreserveSig]
	int Shutdown();

	[PreserveSig]
	int TransferVideoFrame(nint destinationSurface, nint sourceRectangle, nint destinationRectangle, nint borderColor);

	[PreserveSig]
	int OnVideoStreamTick(out long presentationTime);
}
[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
	[PreserveSig]
	int GetItem(ref Guid key, nint value);

	[PreserveSig]
	int GetItemType(ref Guid key, out int type);

	[PreserveSig]
	int CompareItem(ref Guid key, nint value, [MarshalAs(UnmanagedType.Bool)] out bool result);

	[PreserveSig]
	int Compare(IMFAttributes attributes, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);

	[PreserveSig]
	int GetUINT32(ref Guid key, out uint value);

	[PreserveSig]
	int GetUINT64(ref Guid key, out ulong value);

	[PreserveSig]
	int GetDouble(ref Guid key, out double value);

	[PreserveSig]
	int GetGUID(ref Guid key, out Guid value);

	[PreserveSig]
	int GetStringLength(ref Guid key, out uint length);

	[PreserveSig]
	int GetString(ref Guid key, nint value, uint bufferSize, nint length);

	[PreserveSig]
	int GetAllocatedString(ref Guid key, out nint value, out uint length);

	[PreserveSig]
	int GetBlobSize(ref Guid key, out uint size);

	[PreserveSig]
	int GetBlob(ref Guid key, nint buffer, uint bufferSize, nint blobSize);

	[PreserveSig]
	int GetAllocatedBlob(ref Guid key, out nint buffer, out uint size);

	[PreserveSig]
	int GetUnknown(ref Guid key, ref Guid interfaceId, out nint value);

	[PreserveSig]
	int SetItem(ref Guid key, nint value);

	[PreserveSig]
	int DeleteItem(ref Guid key);

	[PreserveSig]
	int DeleteAllItems();

	[PreserveSig]
	int SetUINT32(ref Guid key, uint value);

	[PreserveSig]
	int SetUINT64(ref Guid key, ulong value);

	[PreserveSig]
	int SetDouble(ref Guid key, double value);

	[PreserveSig]
	int SetGUID(ref Guid key, ref Guid value);

	[PreserveSig]
	int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);

	[PreserveSig]
	int SetBlob(ref Guid key, nint buffer, uint bufferSize);

	[PreserveSig]
	int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);

	[PreserveSig]
	int LockStore();

	[PreserveSig]
	int UnlockStore();

	[PreserveSig]
	int GetCount(out uint itemCount);

	[PreserveSig]
	int GetItemByIndex(uint index, out Guid key, nint value);

	[PreserveSig]
	int CopyAllItems(IMFAttributes destination);
}
internal static class NativeMethods
{
	internal delegate bool EnumWindowsProc(nint windowHandle, nint parameter);

	internal delegate bool MonitorEnumProc(nint monitorHandle, nint deviceContext, nint monitorRectangle, nint parameter);

	internal struct Rect
	{
		internal int Left;

		internal int Top;

		internal int Right;

		internal int Bottom;
	}

	internal struct NativePoint
	{
		internal int X;

		internal int Y;
	}

	internal struct NativeSize
	{
		internal int Width;

		internal int Height;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	internal struct BlendFunction
	{
		internal byte BlendOp;

		internal byte BlendFlags;

		internal byte SourceConstantAlpha;

		internal byte AlphaFormat;
	}

	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	internal delegate nint WindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	internal struct WindowClassEx
	{
		internal uint Size;

		internal uint Style;

		internal nint WindowProcedure;

		internal int ClassExtra;

		internal int WindowExtra;

		internal nint Instance;

		internal nint Icon;

		internal nint Cursor;

		internal nint BackgroundBrush;

		[MarshalAs(UnmanagedType.LPWStr)]
		internal string? MenuName;

		[MarshalAs(UnmanagedType.LPWStr)]
		internal string ClassName;

		internal nint SmallIcon;
	}

	internal struct BitmapInfoHeader
	{
		internal uint Size;

		internal int Width;

		internal int Height;

		internal ushort Planes;

		internal ushort BitCount;

		internal uint Compression;

		internal uint SizeImage;

		internal int XPelsPerMeter;

		internal int YPelsPerMeter;

		internal uint ColorsUsed;

		internal uint ColorsImportant;
	}

	internal struct BitmapInfo
	{
		internal BitmapInfoHeader Header;

		internal uint Colors;
	}

	internal struct Point
	{
		internal int X;

		internal int Y;

		internal Point(int x, int y)
		{
			X = x;
			Y = y;
		}
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	internal struct StockIconInfo
	{
		internal uint Size;

		internal nint IconHandle;

		internal int SystemImageIndex;

		internal int IconIndex;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		internal string Path;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	internal struct MonitorInfoEx
	{
		internal int Size;

		internal Rect Monitor;

		internal Rect WorkArea;

		internal uint Flags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		internal string DeviceName;
	}

	internal const uint SpawnWorkerWMessage = 1324u;

	internal const uint SmtoNormal = 0u;

	internal const int GwlStyle = -16;

	internal const int GwlExtendedStyle = -20;

	internal const long WsChild = 1073741824L;

	internal const long WsPopup = 2147483648L;

	internal const long WsVisible = 268435456L;

	internal const long WsClipChildren = 33554432L;

	internal const long WsClipSiblings = 67108864L;

	internal const long WsExLayered = 524288L;

	internal const long WsExTransparent = 32L;

	internal const long WsExNoActivate = 134217728L;

	internal const long WsExToolWindow = 128L;

	internal const long WsExNoRedirectionBitmap = 2097152L;

	internal const uint LwaColorKey = 1u;

	internal const uint LwaAlpha = 2u;

	internal const uint UlwAlpha = 2u;

	internal const byte AcSrcOver = 0;

	internal const byte AcSrcAlpha = 1;

	internal const uint DibRgbColors = 0u;

	internal const uint BiRgb = 0u;

	internal const uint SwpNoSize = 1u;

	internal const uint SwpNoMove = 2u;

	internal const uint SwpNoZOrder = 4u;

	internal const uint SwpNoActivate = 16u;

	internal const uint SwpShowWindow = 64u;

	internal const uint SwpFrameChanged = 32u;

	internal const int SwHide = 0;

	internal const int SwShowNoActivate = 4;

	internal const long SsBitmap = 14L;

	internal const long SsBlackRect = 4L;

	internal const uint StmSetImage = 370u;

	internal const nuint ImageBitmap = 0u;

	internal const uint MonitorInfoPrimary = 1u;

	internal const uint MonitorDefaultToNearest = 2u;

	internal const uint GwHwndNext = 2u;

	internal const uint GwHwndPrevious = 3u;

	internal const uint CwpSkipInvisible = 1u;

	internal const uint CwpSkipDisabled = 2u;

	internal const uint CwpSkipTransparent = 4u;

	internal const uint WmMouseMove = 512u;

	internal const uint WmLeftButtonDown = 513u;

	internal const uint WmLeftButtonUp = 514u;

	internal const uint WmEraseBackground = 20u;

	internal const uint WmPrintClient = 792u;

	internal const nint PrfClient = 4;

	internal const uint MkLeftButton = 1u;

	internal const uint CsVRedraw = 1u;

	internal const uint CsHRedraw = 2u;

	internal const int AlternateFillMode = 1;

	internal const int RegionOr = 2;

	internal const uint StockIconLocation = 0u;

	internal const uint StockIconRecycleBin = 31u;

	internal const uint StockIconRecycleBinFull = 32u;

	internal const uint StockIconDesktopPc = 94u;

	internal static readonly nint HwndBottom = new IntPtr(1);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parentHandle, nint menuHandle, nint instanceHandle, nint parameter);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool DestroyWindow(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool ShowWindow(nint windowHandle, int command);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern nint GetModuleHandleW(string? moduleName);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern nint FindWindowW(string? className, string? windowName);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern nint FindWindowExW(nint parentHandle, nint childAfter, string? className, string? windowName);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern int GetClassNameW(nint windowHandle, StringBuilder className, int maximumCount);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern nint SendMessageTimeoutW(nint windowHandle, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	internal static extern nint SendMessageW(nint windowHandle, uint message, nuint wParam, nint lParam);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern nint SetParent(nint childHandle, nint newParentHandle);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern nint GetParent(nint childHandle);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern nint GetWindow(nint windowHandle, uint command);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern nint ChildWindowFromPointEx(nint parentHandle, Point point, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern int MapWindowPoints(nint fromHandle, nint toHandle, ref Point point, uint pointCount);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetCursorPos(out Point point);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool PostMessageW(nint windowHandle, uint message, nint wParam, nint lParam);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern nint CreatePolygonRgn([In] Point[] points, int pointCount, int fillMode);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern nint CreateEllipticRgn(int left, int top, int right, int bottom);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
	internal static extern uint PrivateExtractIcons(string fileName, int iconIndex, int iconWidth, int iconHeight, [Out] nint[] iconHandles, [Out] uint[] iconIdentifiers, uint iconCount, uint flags);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	internal static extern int SHGetStockIconInfo(uint stockIconId, uint flags, ref StockIconInfo stockIconInfo);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool DestroyIcon(nint iconHandle);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern int CombineRgn(nint destination, nint sourceOne, nint sourceTwo, int combineMode);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern int SetWindowRgn(nint windowHandle, nint regionHandle, [MarshalAs(UnmanagedType.Bool)] bool redraw);

	[DllImport("gdi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool DeleteObject(nint objectHandle);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern nint CreateCompatibleDC(nint deviceContext);

	[DllImport("gdi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool DeleteDC(nint deviceContext);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern nint SelectObject(nint deviceContext, nint graphicsObject);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern nint CreateDIBSection(nint deviceContext, ref BitmapInfo bitmapInfo, uint usage, out nint bitmapBits, nint section, uint offset);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool UpdateLayeredWindow(nint windowHandle, nint destinationDeviceContext, nint destinationPoint, ref NativeSize size, nint sourceDeviceContext, ref NativePoint sourcePoint, uint colorKey, ref BlendFunction blend, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool IsWindow(nint windowHandle);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool IsWindowVisible(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool IsIconic(nint windowHandle);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

	[DllImport("user32.dll")]
	internal static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool IsZoomed(nint windowHandle);

	[DllImport("user32.dll")]
	internal static extern nint MonitorFromWindow(nint windowHandle, uint flags);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
	internal static extern nint GetWindowLongPtr(nint windowHandle, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	internal static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool SetLayeredWindowAttributes(nint windowHandle, uint colorKey, byte alpha, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetLayeredWindowAttributes(nint windowHandle, out uint colorKey, out byte alpha, out uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	internal static extern nint DefWindowProcW(nint windowHandle, uint message, nint wParam, nint lParam);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern nint CreateSolidBrush(uint color);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetClientRect(nint windowHandle, out Rect rectangle);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern int FillRect(nint deviceContext, ref Rect rectangle, nint brush);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern nint GetDC(nint windowHandle);

	[DllImport("user32.dll")]
	internal static extern int ReleaseDC(nint windowHandle, nint deviceContext);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetWindowRect(nint windowHandle, out Rect rectangle);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool EnumDisplayMonitors(nint deviceContext, nint clipRectangle, MonitorEnumProc callback, nint parameter);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetMonitorInfoW(nint monitorHandle, ref MonitorInfoEx monitorInfo);
}
internal sealed class NativeQuickSlotGroupWindow
{
	private sealed record SlotVisualState(Ellipse Running, Border Attention);

	private readonly string _groupId;

	private readonly WindowsIconService _iconService = new WindowsIconService();

	private readonly Dictionary<int, SlotVisualState> _slotVisuals = new Dictionary<int, SlotVisualState>();

	private NativeWpfChildHost? _nativeHost;

	private HwndSource? _source;

	private nint _interactiveHostHandle;

	private int _relativeX;

	private int _relativeY;

	private int _displayOriginX;

	private int _displayOriginY;

	private int _displayWidth;

	private int _displayHeight;

	private int _width;

	private int _height;

	private bool _snapToGrid;

	private int _gridSize = 40;

	private QuickSlotGroup _group = new QuickSlotGroup();

	private IReadOnlyList<QuickSlotItem> _items = Array.Empty<QuickSlotItem>();

	private ImageSource? _baseTexture;

	private bool _dragging;

	private bool _isWindowVisible = true;

	private NativeMethods.Point _dragStartCursor;

	private int _dragStartX;

	private int _dragStartY;

	private bool _disposed;

	internal nint Handle => _nativeHost?.Handle ?? IntPtr.Zero;

	internal bool IsReady
	{
		get
		{
			if (!_disposed)
			{
				return _nativeHost?.IsReady ?? false;
			}
			return false;
		}
	}

	internal event EventHandler<int>? SlotInvoked;

	internal event EventHandler<int>? SlotSecondaryInvoked;

	internal event EventHandler<QuickSlotLayoutChangedEventArgs>? LayoutChanged;

	internal NativeQuickSlotGroupWindow(string groupId)
	{
		_groupId = groupId;
		_baseTexture = LoadBaseTexture();
	}

	internal void AttachTo(nint interactiveHostHandle, DisplayInfo display, QuickSlotGroup group, int itemCount, bool snapToGrid, int gridSize)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!NativeMethods.GetWindowRect(interactiveHostHandle, out var rectangle))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 상호작용 데스크톱 영역을 읽지 못했습니다.");
		}
		_interactiveHostHandle = interactiveHostHandle;
		_group = group.Copy();
		int num = Math.Clamp(group.Columns, 1, 10);
		int num2 = Math.Max(1, (int)Math.Ceiling((double)itemCount / (double)num));
		_width = 16 + num * 68 + (num - 1) * 7;
		_height = 46 + num2 * 68 + (num2 - 1) * 7;
		_displayOriginX = display.X - rectangle.Left;
		_displayOriginY = display.Y - rectangle.Top;
		_displayWidth = display.Width;
		_displayHeight = display.Height;
		_snapToGrid = snapToGrid;
		_gridSize = Math.Clamp(gridSize, 10, 200);
		int value = (_snapToGrid ? SnapCoordinate(group.X, _gridSize) : group.X);
		int value2 = (_snapToGrid ? SnapCoordinate(group.Y, _gridSize) : group.Y);
		_relativeX = _displayOriginX + Math.Clamp(value, 0, Math.Max(0, _displayWidth - _width));
		_relativeY = _displayOriginY + Math.Clamp(value2, 0, Math.Max(0, _displayHeight - _height));
		if (_nativeHost != null)
		{
			_nativeHost.AttachTo(interactiveHostHandle, _relativeX, _relativeY, _width, _height);
			BuildVisualTree();
			ApplyInteractiveRegion();
			ApplyWindowVisibility();
		}
	}

	internal Task InitializeAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellationToken.ThrowIfCancellationRequested();
		if (_source != null)
		{
			return Task.CompletedTask;
		}
		_nativeHost = new NativeWpfChildHost("Vellume Native Quick Slot " + _groupId, _interactiveHostHandle, _relativeX, _relativeY, _width, _height, clickThrough: false);
		_source = _nativeHost.Source;
		BuildVisualTree();
		ApplyInteractiveRegion();
		ApplyWindowVisibility();
		return Task.CompletedTask;
	}

	internal void SetWindowVisible(bool visible)
	{
		if (!_disposed && _isWindowVisible != visible)
		{
			_isWindowVisible = visible;
			ApplyWindowVisibility();
		}
	}

	internal Task RenderAsync(QuickSlotGroup group, IReadOnlyList<QuickSlotItem> items, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_group = group.Copy();
		_items = items.Select((QuickSlotItem item) => item.Copy()).ToArray();
		BuildVisualTree();
		ApplyInteractiveRegion();
		return Task.CompletedTask;
	}

	internal bool IsAttachedTo(nint interactiveHostHandle)
	{
		return _nativeHost?.IsAttachedTo(interactiveHostHandle) ?? false;
	}

	internal Task UpdateRuntimeStatesAsync(IReadOnlyList<QuickSlotItem> items, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (_disposed)
		{
			return Task.CompletedTask;
		}
		foreach (QuickSlotItem item in items)
		{
			if (_slotVisuals.TryGetValue(item.Slot, out SlotVisualState value))
			{
				ApplyRuntimeState(value, item.IsRunning, item.HasAttention);
			}
		}
		return Task.CompletedTask;
	}

	internal string GetAttachmentDiagnostics()
	{
		nint handle = Handle;
		nint parent = NativeMethods.GetParent(handle);
		nint window = NativeMethods.GetWindow(handle, 3u);
		nint window2 = NativeMethods.GetWindow(handle, 2u);
		long value = ((IntPtr)NativeMethods.GetWindowLongPtr(handle, -20)).ToInt64();
		NativeMethods.GetWindowRect(handle, out var rectangle);
		return $"backend=native-wpf, group={_groupId}, handle=0x{handle:X}, parent=0x{parent:X}, above=0x{window:X}({GetWindowClass(window)}), below=0x{window2:X}({GetWindowClass(window2)}), ex-style=0x{value:X8}, rect={rectangle.Left},{rectangle.Top}-{rectangle.Right},{rectangle.Bottom}";
	}

	internal Task CloseSafelyAsync()
	{
		if (_disposed)
		{
			return Task.CompletedTask;
		}
		_disposed = true;
		_nativeHost?.Dispose();
		_nativeHost = null;
		_source = null;
		_slotVisuals.Clear();
		return Task.CompletedTask;
	}

	private void BuildVisualTree()
	{
		if (_source != null)
		{
			Canvas canvas = new Canvas
			{
				Width = _width,
				Height = _height,
				Background = System.Windows.Media.Brushes.Transparent,
				SnapsToDevicePixels = true
			};
			canvas.Children.Add(CreateHeader());
			_slotVisuals.Clear();
			int num = Math.Clamp(_group.Columns, 1, 10);
			for (int i = 0; i < _items.Count; i++)
			{
				QuickSlotItem item = _items[i];
				int num2 = i % num;
				int num3 = i / num;
				FrameworkElement element = CreateSlot(item);
				Canvas.SetLeft(element, 8 + num2 * 75);
				Canvas.SetTop(element, 38 + num3 * 75);
				canvas.Children.Add(element);
			}
			_source.RootVisual = canvas;
		}
	}

	private void ApplyWindowVisibility()
	{
		nint handle = Handle;
		if (handle != IntPtr.Zero && NativeMethods.IsWindow(handle))
		{
			NativeMethods.ShowWindow(handle, _isWindowVisible ? 4 : 0);
		}
	}

	private FrameworkElement CreateHeader()
	{
		int num = CalculateHeaderWidth(_group.Name, _width);
		TextBlock child = new TextBlock
		{
			Text = _group.Name,
			Margin = new Thickness(12.0, 0.0, 12.0, 0.0),
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(222, byte.MaxValue, byte.MaxValue)),
			FontFamily = new System.Windows.Media.FontFamily("Pretendard, Malgun Gothic"),
			FontSize = 11.0,
			FontWeight = FontWeights.Bold,
			VerticalAlignment = VerticalAlignment.Center
		};
		Border border = new Border();
		border.Width = num - 8;
		border.Height = 28.0;
		border.Margin = new Thickness(8.0, 1.0, 0.0, 0.0);
		border.CornerRadius = new CornerRadius(3.0, 9.0, 3.0, 9.0);
		border.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(225, 7, 38, 58));
		border.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 85, 235, 247));
		border.BorderThickness = new Thickness(0.0, 0.0, 1.0, 0.0);
		border.Child = child;
		border.Cursor = Cursors.SizeAll;
		border.MouseLeftButtonDown += HeaderOnMouseLeftButtonDown;
		border.MouseMove += HeaderOnMouseMove;
		border.MouseLeftButtonUp += HeaderOnMouseLeftButtonUp;
		return border;
	}

	private FrameworkElement CreateSlot(QuickSlotItem item)
	{
		QuickSlotItem item2 = item;
		Grid root = new Grid
		{
			Width = 68.0,
			Height = 68.0,
			Background = System.Windows.Media.Brushes.Transparent,
			Cursor = Cursors.Hand,
			ToolTip = (string.IsNullOrWhiteSpace(item2.Name) ? System.IO.Path.GetFileNameWithoutExtension(item2.TargetPath) : item2.Name)
		};
		ImageSource icon = _iconService.GetIcon(item2.TargetPath, item2.IconPath, item2.SystemKind, item2.IsDynamicStateActive);
		if (_baseTexture != null)
		{
			root.Children.Add(new System.Windows.Controls.Image
			{
				Source = _baseTexture,
				Stretch = Stretch.Fill,
				IsHitTestVisible = false
			});
		}
		Grid grid = new Grid
		{
			Margin = new Thickness(7.0),
			Clip = new EllipseGeometry(new System.Windows.Point(27.0, 27.0), 27.0, 27.0),
			IsHitTestVisible = false
		};
		if (icon != null)
		{
			System.Windows.Controls.Image image = new System.Windows.Controls.Image
			{
				Source = icon,
				Stretch = Stretch.UniformToFill
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			grid.Children.Add(image);
		}
		else
		{
			grid.Children.Add(new Border
			{
				CornerRadius = new CornerRadius(27.0),
				Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 80, 104)),
				Child = new TextBlock
				{
					Text = GetMonogram(item2.Name),
					Foreground = System.Windows.Media.Brushes.White,
					FontWeight = FontWeights.Bold,
					FontSize = 18.0,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				}
			});
		}
		root.Children.Add(grid);
		Ellipse ellipse = new Ellipse
		{
			Width = 8.0,
			Height = 8.0,
			Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 252, byte.MaxValue)),
			Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(6, 47, 65)),
			StrokeThickness = 1.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			IsHitTestVisible = false
		};
		Border border = new Border
		{
			Width = 16.0,
			Height = 16.0,
			CornerRadius = new CornerRadius(8.0),
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(byte.MaxValue, 73, 121)),
			BorderBrush = System.Windows.Media.Brushes.White,
			BorderThickness = new Thickness(1.0),
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			Child = new TextBlock
			{
				Text = "!",
				Foreground = System.Windows.Media.Brushes.White,
				FontWeight = FontWeights.Bold,
				FontSize = 10.0,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				TextAlignment = TextAlignment.Center
			},
			IsHitTestVisible = false
		};
		root.Children.Add(ellipse);
		root.Children.Add(border);
		SlotVisualState slotVisualState = new SlotVisualState(ellipse, border);
		_slotVisuals[item2.Slot] = slotVisualState;
		ApplyRuntimeState(slotVisualState, item2.IsRunning, item2.HasAttention);
		root.MouseLeftButtonUp += delegate(object _, MouseButtonEventArgs eventArgs)
		{
			eventArgs.Handled = true;
			this.SlotInvoked?.Invoke(this, item2.Slot);
		};
		root.MouseRightButtonUp += delegate(object _, MouseButtonEventArgs eventArgs)
		{
			eventArgs.Handled = true;
			this.SlotSecondaryInvoked?.Invoke(this, item2.Slot);
		};
		root.MouseEnter += delegate
		{
			root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
			root.RenderTransform = new ScaleTransform(1.06, 1.06);
		};
		root.MouseLeave += delegate
		{
			root.RenderTransform = Transform.Identity;
		};
		return root;
	}

	private void HeaderOnMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
	{
		if (NativeMethods.GetCursorPos(out _dragStartCursor))
		{
			_dragging = true;
			_dragStartX = _relativeX;
			_dragStartY = _relativeY;
			((UIElement)sender).CaptureMouse();
			eventArgs.Handled = true;
		}
	}

	private void HeaderOnMouseMove(object sender, MouseEventArgs eventArgs)
	{
		if (_dragging && eventArgs.LeftButton == MouseButtonState.Pressed && NativeMethods.GetCursorPos(out var point))
		{
			MoveTo(_dragStartX + point.X - _dragStartCursor.X, _dragStartY + point.Y - _dragStartCursor.Y);
			eventArgs.Handled = true;
		}
	}

	private void HeaderOnMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
	{
		if (_dragging)
		{
			_dragging = false;
			((UIElement)sender).ReleaseMouseCapture();
			SnapToPlacementGrid();
			this.LayoutChanged?.Invoke(this, new QuickSlotLayoutChangedEventArgs(_groupId, _relativeX - _displayOriginX, _relativeY - _displayOriginY));
			eventArgs.Handled = true;
		}
	}

	private void MoveTo(int x, int y)
	{
		int max = _displayOriginX + Math.Max(0, _displayWidth - _width);
		int max2 = _displayOriginY + Math.Max(0, _displayHeight - _height);
		_relativeX = Math.Clamp(x, _displayOriginX, max);
		_relativeY = Math.Clamp(y, _displayOriginY, max2);
		PositionWindow();
	}

	private void SnapToPlacementGrid()
	{
		if (_snapToGrid)
		{
			int value = _relativeX - _displayOriginX;
			int value2 = _relativeY - _displayOriginY;
			MoveTo(_displayOriginX + SnapCoordinate(value, _gridSize), _displayOriginY + SnapCoordinate(value2, _gridSize));
		}
	}

	private void PositionWindow()
	{
		if (_nativeHost == null)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 네이티브 퀵슬롯 위치를 지정하지 못했습니다.");
		}
		_nativeHost.Position(_relativeX, _relativeY, _width, _height);
	}

	private void ApplyInteractiveRegion()
	{
		if (_source == null)
		{
			return;
		}
		nint num = NativeMethods.CreateRectRgn(0, 0, 0, 0);
		if (num == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 퀵슬롯 영역을 만들지 못했습니다.");
		}
		try
		{
			int num2 = CalculateHeaderWidth(_group.Name, _width);
			AddPolygon(num, new NativeMethods.Point[6]
			{
				new NativeMethods.Point(8, 1),
				new NativeMethods.Point(num2 - 8, 1),
				new NativeMethods.Point(num2, 8),
				new NativeMethods.Point(num2, 29),
				new NativeMethods.Point(16, 29),
				new NativeMethods.Point(8, 21)
			});
			int num3 = Math.Clamp(_group.Columns, 1, 10);
			for (int i = 0; i < _items.Count; i++)
			{
				int num4 = i % num3;
				int num5 = i / num3;
				int num6 = 8 + num4 * 75;
				int num7 = 38 + num5 * 75;
				AddEllipse(num, num6 - 3, num7 - 3, num6 + 71, num7 + 71);
			}
			if (NativeMethods.SetWindowRgn(_nativeHost.Handle, num, redraw: true) == 0)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 퀵슬롯 영역을 적용하지 못했습니다.");
			}
			num = IntPtr.Zero;
		}
		finally
		{
			if (num != IntPtr.Zero)
			{
				NativeMethods.DeleteObject(num);
			}
		}
	}

	private static void AddPolygon(nint destination, NativeMethods.Point[] points)
	{
		nint num = NativeMethods.CreatePolygonRgn(points, points.Length, 1);
		try
		{
			if (num == IntPtr.Zero || NativeMethods.CombineRgn(destination, destination, num, 2) == 0)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 퀵슬롯 헤더 영역을 합치지 못했습니다.");
			}
		}
		finally
		{
			if (num != IntPtr.Zero)
			{
				NativeMethods.DeleteObject(num);
			}
		}
	}

	private static void AddEllipse(nint destination, int left, int top, int right, int bottom)
	{
		nint num = NativeMethods.CreateEllipticRgn(left, top, right, bottom);
		try
		{
			if (num == IntPtr.Zero || NativeMethods.CombineRgn(destination, destination, num, 2) == 0)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 퀵슬롯 아이콘 영역을 합치지 못했습니다.");
			}
		}
		finally
		{
			if (num != IntPtr.Zero)
			{
				NativeMethods.DeleteObject(num);
			}
		}
	}

	private ImageSource? LoadBaseTexture()
	{
		try
		{
			string text = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Launcher", "quick-slot-base-1024.png");
			if (!File.Exists(text))
			{
				return null;
			}
			BitmapImage bitmapImage = new BitmapImage();
			bitmapImage.BeginInit();
			bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
			bitmapImage.UriSource = new Uri(text, UriKind.Absolute);
			bitmapImage.EndInit();
			bitmapImage.Freeze();
			return bitmapImage;
		}
		catch
		{
			return null;
		}
	}

	private static void ApplyRuntimeState(SlotVisualState visual, bool isRunning, bool hasAttention)
	{
		visual.Running.Visibility = ((!isRunning || hasAttention) ? Visibility.Collapsed : Visibility.Visible);
		visual.Attention.Visibility = ((!hasAttention) ? Visibility.Collapsed : Visibility.Visible);
	}

	private static int CalculateHeaderWidth(string name, int windowWidth)
	{
		return Math.Min(windowWidth, Math.Clamp(32 + name.Trim().Length * 9, 88, 180));
	}

	internal static int SnapCoordinate(int value, int gridSize)
	{
		return Math.Max(0, (int)Math.Round((double)value / (double)gridSize, MidpointRounding.AwayFromZero) * gridSize);
	}

	private static string GetMonogram(string name)
	{
		return string.Concat((string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()).Take(2)).ToUpperInvariant();
	}

	private static string GetWindowClass(nint windowHandle)
	{
		if (windowHandle == IntPtr.Zero)
		{
			return "none";
		}
		StringBuilder stringBuilder = new StringBuilder(128);
		if (NativeMethods.GetClassNameW(windowHandle, stringBuilder, stringBuilder.Capacity) <= 0)
		{
			return "unknown";
		}
		return stringBuilder.ToString();
	}
}
internal sealed class NativeRedirectBridgeHost : IDisposable
{
	private bool _disposed;

	internal nint Handle { get; private set; }

	internal bool IsReady
	{
		get
		{
			if (!_disposed && Handle != IntPtr.Zero)
			{
				return NativeMethods.IsWindow(Handle);
			}
			return false;
		}
	}

	internal NativeRedirectBridgeHost(string name, nint parentHandle, int x, int y, int width, int height)
	{
		string className = NativeRedirectBridgeWindowClass.EnsureRegistered();
		Handle = NativeMethods.CreateWindowExW(134742176u, className, name, 1174405120u, x, y, width, height, parentHandle, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
		if (Handle == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 위젯 리디렉션 브리지를 만들지 못했습니다.");
		}
		if (!NativeMethods.SetLayeredWindowAttributes(Handle, 197121u, byte.MaxValue, 3u))
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			NativeMethods.DestroyWindow(Handle);
			Handle = IntPtr.Zero;
			throw new Win32Exception(lastWin32Error, "Windows에서 위젯 리디렉션 브리지를 투명하게 만들지 못했습니다.");
		}
		if (!NativeRedirectBridgeWindowClass.FillBackground(Handle))
		{
			int lastWin32Error2 = Marshal.GetLastWin32Error();
			NativeMethods.DestroyWindow(Handle);
			Handle = IntPtr.Zero;
			throw new Win32Exception(lastWin32Error2, "Windows에서 위젯 투명 배경을 초기화하지 못했습니다.");
		}
	}

	internal bool IsAttachedTo(nint parentHandle)
	{
		if (IsReady && NativeMethods.IsWindow(parentHandle))
		{
			return NativeMethods.GetParent(Handle) == parentHandle;
		}
		return false;
	}

	internal void AttachTo(nint parentHandle, int x, int y, int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		NativeMethods.SetParent(Handle, parentHandle);
		Position(x, y, width, height);
		if (!IsAttachedTo(parentHandle))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows가 위젯 리디렉션 브리지를 다시 연결하지 못했습니다.");
		}
	}

	internal void Position(int x, int y, int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!NativeMethods.SetWindowPos(Handle, IntPtr.Zero, x, y, width, height, 52u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows가 위젯 리디렉션 브리지 위치를 갱신하지 못했습니다.");
		}
		if (!NativeRedirectBridgeWindowClass.FillBackground(Handle))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 위젯 투명 배경을 다시 그리지 못했습니다.");
		}
	}

	internal void SetWindowVisible(bool visible)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (IsReady)
		{
			NativeMethods.ShowWindow(Handle, visible ? 4 : 0);
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			if (Handle != IntPtr.Zero && NativeMethods.IsWindow(Handle))
			{
				NativeMethods.DestroyWindow(Handle);
			}
			Handle = IntPtr.Zero;
		}
	}
}
internal static class NativeRedirectBridgeWindowClass
{
	internal const string ClassName = "Vellume.NativeRedirectBridge.v1";

	private static readonly object Sync = new object();

	private static readonly NativeMethods.WindowProcedure WindowProcedureCallback = WindowProcedure;

	private static ushort _classAtom;

	private static nint _backgroundBrush;

	internal const uint TransparencyColorKey = 197121u;

	internal static string EnsureRegistered()
	{
		lock (Sync)
		{
			if (_classAtom != 0)
			{
				return "Vellume.NativeRedirectBridge.v1";
			}
			_backgroundBrush = NativeMethods.CreateSolidBrush(197121u);
			if (_backgroundBrush == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 투명 위젯 브리지 브러시를 만들지 못했습니다.");
			}
			NativeMethods.WindowClassEx windowClassEx = default(NativeMethods.WindowClassEx);
			windowClassEx.Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>();
			windowClassEx.WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedureCallback);
			windowClassEx.Style = 3u;
			windowClassEx.Instance = NativeMethods.GetModuleHandleW(null);
			windowClassEx.BackgroundBrush = _backgroundBrush;
			windowClassEx.ClassName = "Vellume.NativeRedirectBridge.v1";
			NativeMethods.WindowClassEx windowClass = windowClassEx;
			_classAtom = NativeMethods.RegisterClassExW(ref windowClass);
			if (_classAtom == 0)
			{
				int lastWin32Error = Marshal.GetLastWin32Error();
				NativeMethods.DeleteObject(_backgroundBrush);
				_backgroundBrush = IntPtr.Zero;
				throw new Win32Exception(lastWin32Error, "Windows에서 투명 위젯 브리지 클래스를 등록하지 못했습니다.");
			}
			return "Vellume.NativeRedirectBridge.v1";
		}
	}

	internal static bool FillBackground(nint windowHandle)
	{
		if (windowHandle == IntPtr.Zero || _backgroundBrush == IntPtr.Zero)
		{
			return false;
		}
		nint dC = NativeMethods.GetDC(windowHandle);
		if (dC == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			return FillBackground(windowHandle, dC);
		}
		finally
		{
			NativeMethods.ReleaseDC(windowHandle, dC);
		}
	}

	private static bool FillBackground(nint windowHandle, nint deviceContext)
	{
		if (deviceContext == IntPtr.Zero || _backgroundBrush == IntPtr.Zero || !NativeMethods.GetClientRect(windowHandle, out var rectangle))
		{
			return false;
		}
		return NativeMethods.FillRect(deviceContext, ref rectangle, _backgroundBrush) != 0;
	}

	private static nint WindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam)
	{
		try
		{
			if ((message == 20 || message == 792) && FillBackground(windowHandle, wParam))
			{
				return new IntPtr(1);
			}
		}
		catch
		{
		}
		return NativeMethods.DefWindowProcW(windowHandle, message, wParam, lParam);
	}
}
internal static class NativeWallpaperFallbackRenderer
{
	[ComImport]
	[Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItemImageFactory
	{
		[PreserveSig]
		int GetImage(ShellSize size, uint flags, out nint bitmapHandle);
	}

	private struct ShellSize
	{
		internal int Width;

		internal int Height;
	}

	private const uint BiggerSizeOk = 1u;

	private const uint ThumbnailOnly = 8u;

	private const uint WideThumbnails = 64u;

	private const uint ScaleUp = 256u;

	internal static nint Render(string sourcePath, int targetWidth, int targetHeight, PlaybackOptions options)
	{
		string extension = System.IO.Path.GetExtension(sourcePath);
		if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
		{
			return NativeWallpaperImageRenderer.Render(sourcePath, targetWidth, targetHeight, options);
		}
		nint num = IntPtr.Zero;
		try
		{
			num = ExtractShellThumbnail(sourcePath, targetWidth, targetHeight);
			using Bitmap source = System.Drawing.Image.FromHbitmap(num);
			return NativeWallpaperImageRenderer.Render(source, targetWidth, targetHeight, options);
		}
		catch
		{
			return RenderNeutralFallback(targetWidth, targetHeight);
		}
		finally
		{
			if (num != IntPtr.Zero)
			{
				NativeMethods.DeleteObject(num);
			}
		}
	}

	private static nint ExtractShellThumbnail(string sourcePath, int targetWidth, int targetHeight)
	{
		Guid interfaceId = typeof(IShellItemImageFactory).GUID;
		int num = SHCreateItemFromParsingName(System.IO.Path.GetFullPath(sourcePath), IntPtr.Zero, ref interfaceId, out IShellItemImageFactory imageFactory);
		if (num < 0 || imageFactory == null)
		{
			Marshal.ThrowExceptionForHR(num);
			throw new InvalidOperationException("Windows Shell did not return a thumbnail provider.");
		}
		try
		{
			ShellSize shellSize = default(ShellSize);
			shellSize.Width = Math.Clamp(targetWidth, 256, 1920);
			shellSize.Height = Math.Clamp(targetHeight, 256, 1080);
			ShellSize size = shellSize;
			num = imageFactory.GetImage(size, 329u, out var bitmapHandle);
			if (num < 0 || bitmapHandle == IntPtr.Zero)
			{
				Marshal.ThrowExceptionForHR(num);
				throw new InvalidOperationException("Windows Shell did not return a thumbnail bitmap.");
			}
			return bitmapHandle;
		}
		finally
		{
			if (Marshal.IsComObject(imageFactory))
			{
				Marshal.FinalReleaseComObject(imageFactory);
			}
		}
	}

	private static nint RenderNeutralFallback(int targetWidth, int targetHeight)
	{
		using Bitmap bitmap = new Bitmap(targetWidth, targetHeight, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
		using Graphics graphics = Graphics.FromImage(bitmap);
		graphics.SmoothingMode = SmoothingMode.HighQuality;
		using System.Drawing.Drawing2D.LinearGradientBrush brush = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, targetWidth, targetHeight), System.Drawing.Color.FromArgb(18, 40, 58), System.Drawing.Color.FromArgb(36, 122, 127), 28f);
		graphics.FillRectangle(brush, 0, 0, targetWidth, targetHeight);
		using SolidBrush brush2 = new SolidBrush(System.Drawing.Color.FromArgb(45, 134, 206, 203));
		graphics.FillEllipse(brush2, targetWidth / 2 - targetHeight / 2, -targetHeight / 3, targetHeight, targetHeight);
		graphics.Flush();
		return bitmap.GetHbitmap(System.Drawing.Color.Black);
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? imageFactory);
}
internal static class NativeWallpaperImageRenderer
{
	internal static nint Render(string sourcePath, int targetWidth, int targetHeight, PlaybackOptions options)
	{
		if (targetWidth <= 0 || targetHeight <= 0)
		{
			throw new ArgumentOutOfRangeException("targetWidth", "네이티브 이미지 출력 크기는 0보다 커야 합니다.");
		}
		using System.Drawing.Image original = System.Drawing.Image.FromFile(sourcePath);
		using Bitmap source = new Bitmap(original);
		return Render(source, targetWidth, targetHeight, options);
	}

	internal static nint Render(System.Drawing.Image source, int targetWidth, int targetHeight, PlaybackOptions options)
	{
		if (targetWidth <= 0 || targetHeight <= 0)
		{
			throw new ArgumentOutOfRangeException("targetWidth", "The wallpaper output size must be greater than zero.");
		}
		using Bitmap bitmap = new Bitmap(targetWidth, targetHeight, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
		using Graphics graphics = Graphics.FromImage(bitmap);
		graphics.Clear(System.Drawing.Color.Black);
		graphics.CompositingMode = CompositingMode.SourceOver;
		graphics.CompositingQuality = CompositingQuality.HighQuality;
		graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
		graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
		graphics.SmoothingMode = SmoothingMode.HighQuality;
		System.Drawing.Rectangle destRect = WallpaperLayout.Calculate(source.Width, source.Height, targetWidth, targetHeight, options.Fit);
		using ImageAttributes imageAttributes = new ImageAttributes();
		imageAttributes.SetWrapMode(WrapMode.TileFlipXY);
		imageAttributes.SetColorMatrix(BuildColorMatrix(options), ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
		graphics.DrawImage(source, destRect, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, imageAttributes);
		graphics.Flush();
		return bitmap.GetHbitmap(System.Drawing.Color.Black);
	}

	private static ColorMatrix BuildColorMatrix(PlaybackOptions options)
	{
		float num = (float)Math.Clamp(options.Brightness, 0.25, 1.25);
		float num2 = (float)Math.Clamp(options.Contrast, 0.5, 1.5);
		float num3 = (float)Math.Clamp(options.Saturation, 0.0, 1.5);
		float num4 = 1f - num3;
		float num5 = 0.3086f * num4;
		float num6 = 0.6094f * num4;
		float num7 = 0.082f * num4;
		float num8 = num * num2;
		float num9 = 0.5f * (1f - num2);
		return new ColorMatrix(new float[5][]
		{
			new float[5]
			{
				(num5 + num3) * num8,
				num5 * num8,
				num5 * num8,
				0f,
				0f
			},
			new float[5]
			{
				num6 * num8,
				(num6 + num3) * num8,
				num6 * num8,
				0f,
				0f
			},
			new float[5]
			{
				num7 * num8,
				num7 * num8,
				(num7 + num3) * num8,
				0f,
				0f
			},
			new float[5] { 0f, 0f, 0f, 1f, 0f },
			new float[5] { num9, num9, num9, 0f, 1f }
		});
	}
}
internal static class WallpaperLayout
{
	internal static System.Drawing.Rectangle Calculate(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, WallpaperFit fit)
	{
		if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
		{
			return System.Drawing.Rectangle.Empty;
		}
		if (fit == WallpaperFit.Stretch)
		{
			return new System.Drawing.Rectangle(0, 0, targetWidth, targetHeight);
		}
		double val = (double)targetWidth / (double)sourceWidth;
		double val2 = (double)targetHeight / (double)sourceHeight;
		double num = ((fit == WallpaperFit.Fit) ? Math.Min(val, val2) : Math.Max(val, val2));
		int num2 = Math.Max(1, (int)Math.Round((double)sourceWidth * num));
		int num3 = Math.Max(1, (int)Math.Round((double)sourceHeight * num));
		return new System.Drawing.Rectangle((targetWidth - num2) / 2, (targetHeight - num3) / 2, num2, num3);
	}
}
internal sealed class NativeWidgetRendererWindow
{
	private sealed record GaugeVisual(System.Windows.Shapes.Path Arc, VectorTextBlock Value, VectorTextBlock Detail);

	private sealed record LayeredPanelVisual(FrameworkElement Visual, int Width, int Height);

	[CompilerGenerated]
	private sealed class <EnumeratePanelHosts>d__130 : IEnumerable<BoundedDesktopPanelHost>, IEnumerable, IEnumerator<BoundedDesktopPanelHost>, IEnumerator, IDisposable
	{
		private int <>1__state;

		private BoundedDesktopPanelHost <>2__current;

		private int <>l__initialThreadId;

		public NativeWidgetRendererWindow <>4__this;

		BoundedDesktopPanelHost IEnumerator<BoundedDesktopPanelHost>.Current
		{
			[DebuggerHidden]
			get
			{
				return <>2__current;
			}
		}

		object IEnumerator.Current
		{
			[DebuggerHidden]
			get
			{
				return <>2__current;
			}
		}

		[DebuggerHidden]
		public <EnumeratePanelHosts>d__130(int <>1__state)
		{
			this.<>1__state = <>1__state;
			<>l__initialThreadId = Environment.CurrentManagedThreadId;
		}

		[DebuggerHidden]
		void IDisposable.Dispose()
		{
			<>1__state = -2;
		}

		private bool MoveNext()
		{
			int num = <>1__state;
			NativeWidgetRendererWindow nativeWidgetRendererWindow = <>4__this;
			switch (num)
			{
			default:
				return false;
			case 0:
				<>1__state = -1;
				if (nativeWidgetRendererWindow._clockPanelHost != null)
				{
					<>2__current = nativeWidgetRendererWindow._clockPanelHost;
					<>1__state = 1;
					return true;
				}
				goto IL_0055;
			case 1:
				<>1__state = -1;
				goto IL_0055;
			case 2:
				<>1__state = -1;
				goto IL_0079;
			case 3:
				<>1__state = -1;
				goto IL_009d;
			case 4:
				{
					<>1__state = -1;
					break;
				}
				IL_0055:
				if (nativeWidgetRendererWindow._systemPanelHost != null)
				{
					<>2__current = nativeWidgetRendererWindow._systemPanelHost;
					<>1__state = 2;
					return true;
				}
				goto IL_0079;
				IL_009d:
				if (nativeWidgetRendererWindow._codexPanelHost != null)
				{
					<>2__current = nativeWidgetRendererWindow._codexPanelHost;
					<>1__state = 4;
					return true;
				}
				break;
				IL_0079:
				if (nativeWidgetRendererWindow._mediaPanelHost != null)
				{
					<>2__current = nativeWidgetRendererWindow._mediaPanelHost;
					<>1__state = 3;
					return true;
				}
				goto IL_009d;
			}
			return false;
		}

		bool IEnumerator.MoveNext()
		{
			//ILSpy generated this explicit interface implementation from .override directive in MoveNext
			return this.MoveNext();
		}

		[DebuggerHidden]
		void IEnumerator.Reset()
		{
			throw new NotSupportedException();
		}

		[DebuggerHidden]
		IEnumerator<BoundedDesktopPanelHost> IEnumerable<BoundedDesktopPanelHost>.GetEnumerator()
		{
			<EnumeratePanelHosts>d__130 result;
			if (<>1__state == -2 && <>l__initialThreadId == Environment.CurrentManagedThreadId)
			{
				<>1__state = 0;
				result = this;
			}
			else
			{
				result = new <EnumeratePanelHosts>d__130(0)
				{
					<>4__this = <>4__this
				};
			}
			return result;
		}

		[DebuggerHidden]
		IEnumerator IEnumerable.GetEnumerator()
		{
			return ((IEnumerable<BoundedDesktopPanelHost>)this).GetEnumerator();
		}
	}

	private const double MediaFrameIntervalMilliseconds = 83.33333333333333;

	private static readonly System.Windows.Media.Color MintAccent = MikuPalette.Mint;

	private static readonly System.Windows.Media.Color MintPanelStrong = MikuPalette.WithAlpha(MikuPalette.Mint, 190);

	private static readonly System.Windows.Media.Color MintPanelMid = MikuPalette.WithAlpha(MikuPalette.Teal, 170);

	private static readonly System.Windows.Media.Color MintPanelSoft = MikuPalette.WithAlpha(MikuPalette.Mint, 58);

	private static readonly System.Windows.Media.Color ReferenceNavy = System.Windows.Media.Color.FromRgb(38, 54, 79);

	private static readonly System.Windows.Media.Color ReferenceCyan = System.Windows.Media.Color.FromRgb(67, 231, 242);

	private static readonly System.Windows.Media.Color ReferenceBlue = System.Windows.Media.Color.FromRgb(36, 201, 223);

	private static readonly System.Windows.Media.Color ReferenceDateBand = System.Windows.Media.Color.FromArgb(178, 43, 132, 168);

	private static readonly System.Windows.Media.Color ReferenceMonthBand = System.Windows.Media.Color.FromArgb(190, 61, 210, 224);

	private static readonly System.Windows.Media.Color ReferenceTimeBand = System.Windows.Media.Color.FromArgb(160, 42, 116, 153);

	private static readonly System.Windows.Media.Color ReferenceYearBand = System.Windows.Media.Color.FromArgb(158, 43, 132, 168);

	private static readonly System.Windows.Media.FontFamily ReferenceFontFamily = new System.Windows.Media.FontFamily("Bahnschrift, Pretendard, Malgun Gothic");

	private readonly DispatcherTimer _clockTimer;

	private readonly DispatcherTimer _mediaRenderTimer;

	private readonly Dictionary<string, GaugeVisual> _gauges = new Dictionary<string, GaugeVisual>(StringComparer.Ordinal);

	private BoundedDesktopPanelHost? _clockPanelHost;

	private BoundedDesktopPanelHost? _systemPanelHost;

	private BoundedDesktopPanelHost? _mediaPanelHost;

	private BoundedDesktopPanelHost? _codexPanelHost;

	private FrameworkElement? _clockPanelVisual;

	private FrameworkElement? _systemPanelVisual;

	private FrameworkElement? _mediaPanelVisual;

	private FrameworkElement? _codexPanelVisual;

	private int _clockPanelX;

	private int _clockPanelY;

	private int _systemPanelX;

	private int _systemPanelY;

	private int _mediaPanelX;

	private int _mediaPanelY;

	private int _codexPanelX;

	private int _codexPanelY;

	private WidgetSettings _settings = new WidgetSettings();

	private nint _hostHandle;

	private nint _shellViewHandle;

	private nint _backgroundWorkerHandle;

	private int _relativeX;

	private int _relativeY;

	private int _width;

	private int _height;

	private bool _isRaisedDesktop;

	private bool _isPaused;

	private bool _isWindowVisible = true;

	private bool _initialized;

	private bool _disposed;

	private bool _mediaRenderPending;

	private long _lastMediaRenderTick;

	private VectorTextBlock? _clockYear;

	private VectorTextBlock? _clockWeekday;

	private VectorTextBlock? _clockDay;

	private VectorTextBlock? _clockMonth;

	private VectorTextBlock? _clockTime;

	private System.Windows.Controls.Image? _mediaArtwork;

	private TextBlock? _mediaArtworkPlaceholder;

	private TextBlock? _mediaTitle;

	private TextBlock? _mediaArtist;

	private Border? _mediaProgress;

	private System.Windows.Shapes.Path? _mediaToggleGlyph;

	private Border[] _spectrumBars = Array.Empty<Border>();

	private string _lastArtworkDataUri = string.Empty;

	private MediaSessionSnapshot? _appliedMedia;

	private TextBlock? _codexHeadline;

	private TextBlock? _codexDetail;

	private TextBlock? _codexMeta;

	private Ellipse? _codexPulse;

	private TranslateTransform? _codexTranslate;

	private string _lastCodexState = string.Empty;

	private SystemMetricsSnapshot? _lastMetrics;

	private MediaSessionSnapshot _lastMedia = new MediaSessionSnapshot(IsAvailable: false, IsPlaying: false, "재생 중인 미디어 없음", "미디어 앱에서 음악을 재생하세요", string.Empty, 0.0, string.Empty);

	private float[] _lastSpectrum = new float[24];

	private CodexCompanionSnapshot _lastCodex = CodexCompanionSnapshot.Disconnected();

	internal nint Handle => EnumeratePanelHosts().FirstOrDefault((BoundedDesktopPanelHost host) => host.IsReady)?.Handle ?? IntPtr.Zero;

	internal IReadOnlyList<nint> PanelHandles => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host.Handle).ToArray();

	internal IReadOnlyList<nint> PanelSurfaceHandles => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host.SurfaceHandle).ToArray();

	internal IReadOnlyList<long> PanelPresentationGenerations => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host.PresentationGeneration).ToArray();

	internal IReadOnlyList<int> PanelNonTransparentPixelCounts => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host.LastNonTransparentPixelCount).ToArray();

	internal bool AllPanelsPresented => (from host in EnumeratePanelHosts()
		where host.IsReady
		select host).All((BoundedDesktopPanelHost host) => host.LastPresentSucceeded);

	internal bool UsesFullDisplayCompositionSurface => false;

	internal bool IsReady
	{
		get
		{
			if (!_disposed && _initialized && _hostHandle != IntPtr.Zero && NativeMethods.IsWindow(_hostHandle))
			{
				if (_settings.IsEnabled && _settings.ShowClock)
				{
					BoundedDesktopPanelHost? clockPanelHost = _clockPanelHost;
					if (clockPanelHost == null || !clockPanelHost.IsReady)
					{
						goto IL_00f1;
					}
				}
				if (_settings.IsEnabled && _settings.ShowSystemInfo)
				{
					BoundedDesktopPanelHost? systemPanelHost = _systemPanelHost;
					if (systemPanelHost == null || !systemPanelHost.IsReady)
					{
						goto IL_00f1;
					}
				}
				if (_settings.IsEnabled && _settings.ShowMediaStatus)
				{
					BoundedDesktopPanelHost? mediaPanelHost = _mediaPanelHost;
					if (mediaPanelHost == null || !mediaPanelHost.IsReady)
					{
						goto IL_00f1;
					}
				}
				if (_settings.IsEnabled && _settings.ShowCodexCompanion)
				{
					return _codexPanelHost?.IsReady ?? false;
				}
				return true;
			}
			goto IL_00f1;
			IL_00f1:
			return false;
		}
	}

	internal NativeWidgetRendererWindow()
	{
		_clockTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_clockTimer.Tick += ClockTimerOnTick;
		_mediaRenderTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(83.33333333333333)
		};
		_mediaRenderTimer.Tick += MediaRenderTimerOnTick;
	}

	internal void AttachTo(nint hostHandle, DisplayInfo display, bool isRaisedDesktop, nint shellViewHandle, nint backgroundWorkerHandle)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!NativeMethods.GetWindowRect(hostHandle, out var rectangle))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 네이티브 위젯 영역을 읽지 못했습니다.");
		}
		if (isRaisedDesktop && (shellViewHandle == IntPtr.Zero || backgroundWorkerHandle == IntPtr.Zero))
		{
			throw new InvalidOperationException("확장 데스크톱에 ShellView 또는 WorkerW 레이어가 없습니다.");
		}
		bool flag = _width != display.Width || _height != display.Height;
		int num = display.X - rectangle.Left;
		int num2 = display.Y - rectangle.Top;
		bool flag2 = !IsAttachedTo(hostHandle) || _relativeX != num || _relativeY != num2;
		_hostHandle = hostHandle;
		_shellViewHandle = shellViewHandle;
		_backgroundWorkerHandle = backgroundWorkerHandle;
		_isRaisedDesktop = isRaisedDesktop;
		_relativeX = num;
		_relativeY = num2;
		_width = display.Width;
		_height = display.Height;
		if (_initialized)
		{
			if (flag)
			{
				RebuildAlphaPanels();
				UpdateClockAndCalendar();
				ApplyLastMetrics();
				ApplyLastAddOns();
			}
			else if (flag2)
			{
				ReattachAlphaPanels();
			}
			ApplyLayerOrder();
			ApplyWindowVisibility();
		}
	}

	internal Task InitializeAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellationToken.ThrowIfCancellationRequested();
		if (_initialized)
		{
			return Task.CompletedTask;
		}
		if (_hostHandle == IntPtr.Zero || !NativeMethods.IsWindow(_hostHandle))
		{
			throw new InvalidOperationException("네이티브 위젯 호스트가 연결되지 않았습니다.");
		}
		_initialized = true;
		RebuildAlphaPanels();
		ApplyLayerOrder();
		ApplyWindowVisibility();
		UpdateClockAndCalendar();
		return Task.CompletedTask;
	}

	internal Task ApplySettingsAsync(WidgetSettings widgets, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		ObjectDisposedException.ThrowIf(_disposed, this);
		WidgetSettings widgetSettings = widgets.Copy();
		widgetSettings.ShowCalendar = false;
		bool num = !HasSameInformationPanelLayout(_settings, widgetSettings);
		_settings = widgetSettings;
		if (num)
		{
			RebuildAlphaPanels();
		}
		UpdateClockAndCalendar();
		ApplyLastMetrics();
		ApplyLastAddOns();
		UpdateClockTimer();
		return Task.CompletedTask;
	}

	internal Task UpdateSystemMetricsAsync(SystemMetricsSnapshot metrics)
	{
		if (_disposed)
		{
			return Task.CompletedTask;
		}
		_lastMetrics = metrics;
		ApplyLastMetrics();
		return Task.CompletedTask;
	}

	private static bool HasSameInformationPanelLayout(WidgetSettings current, WidgetSettings next)
	{
		if (current.IsEnabled == next.IsEnabled && current.ShowClock == next.ShowClock && current.ShowSystemInfo == next.ShowSystemInfo && current.ShowMediaStatus == next.ShowMediaStatus && current.ShowCodexCompanion == next.ShowCodexCompanion && current.ClockSide == next.ClockSide && current.SystemInfoSide == next.SystemInfoSide && current.MediaStatusSide == next.MediaStatusSide)
		{
			return current.CodexCompanionSide == next.CodexCompanionSide;
		}
		return false;
	}

	internal Task UpdateAddOnsAsync(MediaSessionSnapshot media, IReadOnlyList<float> spectrum, bool audioCaptureAvailable, CodexCompanionSnapshot codex)
	{
		if (_disposed)
		{
			return Task.CompletedTask;
		}
		_lastMedia = media;
		_lastSpectrum = spectrum.Take(24).Concat(Enumerable.Repeat(0f, 24)).Take(24)
			.ToArray();
		_lastCodex = codex;
		ApplyLastAddOns();
		return Task.CompletedTask;
	}

	internal Task PauseAsync()
	{
		_isPaused = true;
		_clockTimer.Stop();
		_mediaRenderTimer.Stop();
		StopCodexAnimation();
		Border[] spectrumBars = _spectrumBars;
		foreach (Border obj in spectrumBars)
		{
			obj.Height = 2.0;
			obj.Opacity = 0.25;
		}
		return Task.CompletedTask;
	}

	internal Task ResumeAsync()
	{
		_isPaused = false;
		UpdateClockAndCalendar();
		ApplySpectrum();
		RequestMediaPanelRender(immediate: true);
		RenderCodexPanel();
		UpdateClockTimer();
		return Task.CompletedTask;
	}

	internal void SetWindowVisible(bool visible)
	{
		if (!_disposed && _isWindowVisible != visible)
		{
			_isWindowVisible = visible;
			if (!visible)
			{
				_mediaRenderTimer.Stop();
			}
			ApplyWindowVisibility();
			if (visible && _mediaRenderPending)
			{
				ApplySpectrum(requestRender: false);
				RequestMediaPanelRender(immediate: true);
			}
		}
	}

	internal bool IsAttachedTo(nint hostHandle)
	{
		if (_initialized && NativeMethods.IsWindow(hostHandle) && _hostHandle == hostHandle && (_clockPanelHost == null || _clockPanelHost.IsAttachedTo(hostHandle)) && (_systemPanelHost == null || _systemPanelHost.IsAttachedTo(hostHandle)) && (_mediaPanelHost == null || _mediaPanelHost.IsAttachedTo(hostHandle)))
		{
			if (_codexPanelHost != null)
			{
				return _codexPanelHost.IsAttachedTo(hostHandle);
			}
			return true;
		}
		return false;
	}

	internal string GetAttachmentDiagnostics()
	{
		IEnumerable<string> values = (from host in EnumeratePanelHosts()
			where host.IsReady
			select host).Select(delegate(BoundedDesktopPanelHost host)
		{
			nint parent = NativeMethods.GetParent(host.Handle);
			nint window = NativeMethods.GetWindow(host.Handle, 3u);
			nint window2 = NativeMethods.GetWindow(host.Handle, 2u);
			return $"0x{host.Handle:X}[parent=0x{parent:X},surface=0x{host.SurfaceHandle:X},surfaceParent=0x{NativeMethods.GetParent(host.SurfaceHandle):X},above=0x{window:X},below=0x{window2:X},size={host.Width}x{host.Height},frame={host.PresentationGeneration},alphaPixels={host.LastNonTransparentPixelCount},presented={host.LastPresentSucceeded},visible={NativeMethods.IsWindowVisible(host.Handle)}]";
		});
		return $"backend=native-bounded-redirected-skins, desktop=0x{_hostHandle:X}, fullDisplaySurface=false, panels={string.Join(';', values)}";
	}

	internal Task CloseSafelyAsync()
	{
		if (_disposed)
		{
			return Task.CompletedTask;
		}
		_disposed = true;
		_initialized = false;
		_clockTimer.Stop();
		_clockTimer.Tick -= ClockTimerOnTick;
		_mediaRenderTimer.Stop();
		_mediaRenderTimer.Tick -= MediaRenderTimerOnTick;
		StopCodexAnimation();
		_clockPanelHost?.Dispose();
		_systemPanelHost?.Dispose();
		_mediaPanelHost?.Dispose();
		_codexPanelHost?.Dispose();
		_clockPanelHost = null;
		_systemPanelHost = null;
		_mediaPanelHost = null;
		_codexPanelHost = null;
		_clockPanelVisual = null;
		_systemPanelVisual = null;
		_mediaPanelVisual = null;
		_codexPanelVisual = null;
		_hostHandle = IntPtr.Zero;
		_shellViewHandle = IntPtr.Zero;
		_backgroundWorkerHandle = IntPtr.Zero;
		_gauges.Clear();
		_spectrumBars = Array.Empty<Border>();
		_mediaRenderPending = false;
		return Task.CompletedTask;
	}

	private void ApplyWindowVisibility()
	{
		foreach (BoundedDesktopPanelHost item in EnumeratePanelHosts())
		{
			item.SetWindowVisible(_isWindowVisible);
		}
	}

	private void RebuildAlphaPanels()
	{
		_mediaRenderTimer.Stop();
		_mediaRenderPending = false;
		_lastMediaRenderTick = 0L;
		_clockPanelHost?.Dispose();
		_systemPanelHost?.Dispose();
		_mediaPanelHost?.Dispose();
		_codexPanelHost?.Dispose();
		_clockPanelHost = null;
		_systemPanelHost = null;
		_mediaPanelHost = null;
		_codexPanelHost = null;
		_clockPanelVisual = null;
		_systemPanelVisual = null;
		_mediaPanelVisual = null;
		_codexPanelVisual = null;
		ResetVisualReferences();
		if (_settings.IsEnabled && _initialized && _hostHandle != IntPtr.Zero && NativeMethods.IsWindow(_hostHandle))
		{
			if (_settings.ShowClock)
			{
				double scale = ((_width <= 1450) ? 0.82 : 1.0);
				LayeredPanelVisual layeredPanelVisual = PrepareLayeredPanel(CreateClock(_settings.ClockSide), scale);
				_clockPanelX = CalculatePanelX(_settings.ClockSide, 25.0, layeredPanelVisual.Width);
				_clockPanelY = 28;
				_clockPanelVisual = layeredPanelVisual.Visual;
				_clockPanelHost = new BoundedDesktopPanelHost("Vellume Alpha Clock", _hostHandle, _relativeX + _clockPanelX, _relativeY + _clockPanelY, layeredPanelVisual.Width, layeredPanelVisual.Height);
				_clockPanelHost.Render(_clockPanelVisual);
			}
			if (_settings.ShowSystemInfo)
			{
				double scale2 = ((_width <= 1450) ? 0.92 : 1.0);
				LayeredPanelVisual layeredPanelVisual2 = PrepareLayeredPanel(CreateSystemPanel(_settings.SystemInfoSide), scale2);
				_systemPanelX = CalculatePanelX(_settings.SystemInfoSide, 25.0, layeredPanelVisual2.Width);
				_systemPanelY = GetSystemPanelTop();
				_systemPanelVisual = layeredPanelVisual2.Visual;
				_systemPanelHost = new BoundedDesktopPanelHost("Vellume Alpha System", _hostHandle, _relativeX + _systemPanelX, _relativeY + _systemPanelY, layeredPanelVisual2.Width, layeredPanelVisual2.Height);
				_systemPanelHost.Render(_systemPanelVisual);
			}
			if (_settings.ShowMediaStatus)
			{
				WidgetSide mediaStatusSide = _settings.MediaStatusSide;
				double scale3 = ((_width <= 1450) ? 0.82 : 1.0);
				LayeredPanelVisual layeredPanelVisual3 = PrepareLayeredPanel(CreateMediaPanel(mediaStatusSide), scale3);
				_mediaPanelX = CalculatePanelX(mediaStatusSide, (mediaStatusSide == WidgetSide.Left) ? 30 : 42, layeredPanelVisual3.Width);
				_mediaPanelY = ((mediaStatusSide == WidgetSide.Left) ? 28 : 188);
				_mediaPanelVisual = layeredPanelVisual3.Visual;
				_mediaPanelHost = new BoundedDesktopPanelHost("Vellume Alpha Media", _hostHandle, _relativeX + _mediaPanelX, _relativeY + _mediaPanelY, layeredPanelVisual3.Width, layeredPanelVisual3.Height);
				_mediaPanelHost.Render(_mediaPanelVisual);
			}
			if (_settings.ShowCodexCompanion)
			{
				WidgetSide codexCompanionSide = _settings.CodexCompanionSide;
				double scale4 = ((_width <= 1450) ? 0.84 : 1.0);
				LayeredPanelVisual layeredPanelVisual4 = PrepareLayeredPanel(CreateCodexPanel(codexCompanionSide), scale4);
				_codexPanelX = CalculatePanelX(codexCompanionSide, 28.0, layeredPanelVisual4.Width);
				_codexPanelY = _height - 26 - layeredPanelVisual4.Height;
				_codexPanelVisual = layeredPanelVisual4.Visual;
				_codexPanelHost = new BoundedDesktopPanelHost("Vellume Alpha Codex", _hostHandle, _relativeX + _codexPanelX, _relativeY + _codexPanelY, layeredPanelVisual4.Width, layeredPanelVisual4.Height);
				_codexPanelHost.Render(_codexPanelVisual);
			}
			ApplyLayerOrder();
			ApplyWindowVisibility();
		}
	}

	private void ReattachAlphaPanels()
	{
		if (_clockPanelHost != null)
		{
			_clockPanelHost.AttachTo(_hostHandle, _relativeX + _clockPanelX, _relativeY + _clockPanelY, _clockPanelHost.Width, _clockPanelHost.Height);
		}
		if (_systemPanelHost != null)
		{
			_systemPanelHost.AttachTo(_hostHandle, _relativeX + _systemPanelX, _relativeY + _systemPanelY, _systemPanelHost.Width, _systemPanelHost.Height);
		}
		if (_mediaPanelHost != null)
		{
			_mediaPanelHost.AttachTo(_hostHandle, _relativeX + _mediaPanelX, _relativeY + _mediaPanelY, _mediaPanelHost.Width, _mediaPanelHost.Height);
		}
		if (_codexPanelHost != null)
		{
			_codexPanelHost.AttachTo(_hostHandle, _relativeX + _codexPanelX, _relativeY + _codexPanelY, _codexPanelHost.Width, _codexPanelHost.Height);
		}
	}

	private int CalculatePanelX(WidgetSide side, double margin, int panelWidth)
	{
		if (side != 0)
		{
			return _width - (int)Math.Round(margin) - panelWidth;
		}
		return (int)Math.Round(margin);
	}

	private int GetSystemPanelTop()
	{
		bool flag = _settings.ShowClock && _settings.ClockSide == _settings.SystemInfoSide;
		if (_width > 1450)
		{
			if (!flag)
			{
				return 330;
			}
			return 370;
		}
		if (!flag)
		{
			return 285;
		}
		return 330;
	}

	private static LayeredPanelVisual PrepareLayeredPanel(FrameworkElement element, double scale)
	{
		element.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
		double num = (double.IsNaN(element.Width) ? element.DesiredSize.Width : element.Width);
		double num2 = (double.IsNaN(element.Height) ? element.DesiredSize.Height : element.Height);
		int num3 = Math.Max(1, (int)Math.Ceiling(num * scale));
		int num4 = Math.Max(1, (int)Math.Ceiling(num2 * scale));
		if (Math.Abs(scale - 1.0) < 0.001)
		{
			return new LayeredPanelVisual(element, num3, num4);
		}
		return new LayeredPanelVisual(new Viewbox
		{
			Width = num3,
			Height = num4,
			Stretch = Stretch.Fill,
			Child = element
		}, num3, num4);
	}

	private void RenderClockPanel()
	{
		BoundedDesktopPanelHost? clockPanelHost = _clockPanelHost;
		if (clockPanelHost != null && clockPanelHost.IsReady && _clockPanelVisual != null)
		{
			_clockPanelHost.Render(_clockPanelVisual);
		}
	}

	private void RenderSystemPanel()
	{
		BoundedDesktopPanelHost? systemPanelHost = _systemPanelHost;
		if (systemPanelHost != null && systemPanelHost.IsReady && _systemPanelVisual != null)
		{
			_systemPanelHost.Render(_systemPanelVisual);
		}
	}

	private void RenderMediaPanel()
	{
		RequestMediaPanelRender(immediate: true);
	}

	private void CommitMediaPanelRender()
	{
		_mediaRenderTimer.Stop();
		if (!_disposed && !_isPaused && _isWindowVisible)
		{
			BoundedDesktopPanelHost? mediaPanelHost = _mediaPanelHost;
			if (mediaPanelHost != null && mediaPanelHost.IsReady && _mediaPanelVisual != null && _mediaPanelHost.IsReady && _mediaPanelVisual != null)
			{
				_mediaPanelHost.Render(_mediaPanelVisual);
				_mediaRenderPending = false;
				_lastMediaRenderTick = Environment.TickCount64;
			}
		}
	}

	private void RequestMediaPanelRender(bool immediate)
	{
		_mediaRenderPending = true;
		if (!_disposed && !_isPaused && _isWindowVisible)
		{
			BoundedDesktopPanelHost? mediaPanelHost = _mediaPanelHost;
			if (mediaPanelHost != null && mediaPanelHost.IsReady && _mediaPanelVisual != null)
			{
				double num = ((_lastMediaRenderTick == 0L) ? double.MaxValue : ((double)(Environment.TickCount64 - _lastMediaRenderTick)));
				if (num >= 83.33333333333333)
				{
					CommitMediaPanelRender();
					return;
				}
				double value = Math.Max(1.0, 83.33333333333333 - num);
				_mediaRenderTimer.Stop();
				_mediaRenderTimer.Interval = TimeSpan.FromMilliseconds(value);
				_mediaRenderTimer.Start();
				return;
			}
		}
		_mediaRenderTimer.Stop();
	}

	private void RenderCodexPanel()
	{
		BoundedDesktopPanelHost? codexPanelHost = _codexPanelHost;
		if (codexPanelHost != null && codexPanelHost.IsReady && _codexPanelVisual != null)
		{
			_codexPanelHost.Render(_codexPanelVisual);
		}
	}

	internal FrameworkElement CreateClock(WidgetSide side)
	{
		bool flag = side == WidgetSide.Left;
		TextAlignment textAlignment = ((!flag) ? TextAlignment.Right : TextAlignment.Left);
		Canvas obj = new Canvas
		{
			Width = 500.0,
			Height = 340.0,
			ClipToBounds = false
		};
		Border element = new Border
		{
			Width = 340.0,
			Height = 2.0,
			Background = new SolidColorBrush(ReferenceCyan)
		};
		Canvas.SetLeft(element, (!flag) ? 160 : 0);
		Canvas.SetTop(element, 5.0);
		obj.Children.Add(element);
		Canvas canvas = new Canvas
		{
			Width = 430.0,
			Height = 300.0,
			RenderTransformOrigin = new System.Windows.Point((!flag) ? 1 : 0, 0.5),
			RenderTransform = new RotateTransform(flag ? 11 : (-11)),
			ClipToBounds = false
		};
		Canvas.SetLeft(canvas, 30.0);
		Canvas.SetTop(canvas, 15.0);
		obj.Children.Add(canvas);
		Border border = new Border
		{
			Width = 350.0,
			Height = 58.0,
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			Background = new SolidColorBrush(ReferenceYearBand),
			BorderBrush = new SolidColorBrush(MikuPalette.WithAlpha(Colors.White, 210)),
			BorderThickness = new Thickness(0.0, 1.0, 0.0, 1.0)
		};
		_clockYear = CreateOutlinedVectorText(52.0, FontWeights.SemiBold, ReferenceNavy, 1.05);
		_clockYear.Width = 322.0;
		_clockYear.TextAlignment = textAlignment;
		_clockYear.VerticalAlignment = VerticalAlignment.Center;
		border.Child = _clockYear;
		Canvas.SetLeft(border, (!flag) ? 80 : 0);
		Canvas.SetTop(border, 0.0);
		canvas.Children.Add(border);
		Border border2 = new Border
		{
			Width = 420.0,
			Height = 62.0,
			Background = new SolidColorBrush(ReferenceDateBand),
			BorderBrush = new SolidColorBrush(ReferenceCyan),
			BorderThickness = (flag ? new Thickness(7.0, 0.0, 0.0, 0.0) : new Thickness(0.0, 0.0, 7.0, 0.0))
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = ((!flag) ? HorizontalAlignment.Right : HorizontalAlignment.Left),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(18.0, 0.0, 18.0, 0.0)
		};
		_clockWeekday = CreateOutlinedVectorText(34.0, FontWeights.SemiBold, ReferenceCyan, 0.85);
		_clockDay = CreateOutlinedVectorText(50.0, FontWeights.Bold, Colors.White, 0.9, ReferenceNavy);
		if (flag)
		{
			stackPanel.Children.Add(_clockDay);
			stackPanel.Children.Add(CreateSpacer(18.0));
			stackPanel.Children.Add(_clockWeekday);
		}
		else
		{
			stackPanel.Children.Add(_clockWeekday);
			stackPanel.Children.Add(CreateSpacer(18.0));
			stackPanel.Children.Add(_clockDay);
		}
		border2.Child = stackPanel;
		Canvas.SetTop(border2, 58.0);
		canvas.Children.Add(border2);
		Border border3 = new Border
		{
			Width = 330.0,
			Height = 40.0,
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			Background = new SolidColorBrush(ReferenceMonthBand),
			BorderThickness = new Thickness(0.0)
		};
		_clockMonth = CreateOutlinedVectorText(25.0, FontWeights.SemiBold, ReferenceNavy, 0.8);
		_clockMonth.Width = 300.0;
		_clockMonth.TextAlignment = textAlignment;
		_clockMonth.VerticalAlignment = VerticalAlignment.Center;
		border3.Child = _clockMonth;
		Canvas.SetLeft(border3, (!flag) ? 90 : 0);
		Canvas.SetTop(border3, 120.0);
		canvas.Children.Add(border3);
		Border border4 = new Border
		{
			Width = 300.0,
			Height = 66.0,
			Padding = new Thickness(15.0, 0.0, 15.0, 0.0),
			Background = new SolidColorBrush(ReferenceTimeBand),
			BorderThickness = new Thickness(0.0)
		};
		_clockTime = CreateOutlinedVectorText(58.0, FontWeights.Light, ReferenceCyan, 0.85);
		_clockTime.Width = 270.0;
		_clockTime.TextAlignment = textAlignment;
		_clockTime.HorizontalAlignment = ((!flag) ? HorizontalAlignment.Right : HorizontalAlignment.Left);
		_clockTime.VerticalAlignment = VerticalAlignment.Center;
		border4.Child = _clockTime;
		Canvas.SetLeft(border4, (!flag) ? 120 : 0);
		Canvas.SetTop(border4, 160.0);
		canvas.Children.Add(border4);
		return obj;
	}

	internal FrameworkElement CreateSystemPanel(WidgetSide side)
	{
		return new StackPanel
		{
			Width = 255.0,
			Children = 
			{
				(UIElement)CreateGauge("cpu", ReferenceCyan, "CPU 사용량", "실시간 프로세서", side),
				(UIElement)CreateGauge("gpu", ReferenceCyan, "GPU 사용량", "GPU 엔진", side),
				(UIElement)CreateGauge("memory", ReferenceCyan, "메모리", "--", side),
				(UIElement)CreateGauge("driveC", ReferenceCyan, "C:\\ 드라이브", "--", side),
				(UIElement)CreateGauge("driveD", ReferenceCyan, "D:\\ 드라이브", "--", side)
			}
		};
	}

	private FrameworkElement CreateGauge(string key, System.Windows.Media.Color accent, string title, string detail, WidgetSide side)
	{
		bool flag = side == WidgetSide.Left;
		Canvas canvas = new Canvas
		{
			Width = 255.0,
			Height = 54.0,
			Background = System.Windows.Media.Brushes.Transparent
		};
		int num = (flag ? 54 : 0);
		Border element = new Border
		{
			Width = 201.0,
			Height = 3.0,
			Background = new SolidColorBrush(MikuPalette.WithAlpha(Colors.White, 230))
		};
		Canvas.SetLeft(element, num);
		Canvas.SetTop(element, 29.0);
		canvas.Children.Add(element);
		Border element2 = new Border
		{
			Width = 201.0,
			Height = 1.0,
			Background = new SolidColorBrush(MikuPalette.WithAlpha(ReferenceNavy, 195))
		};
		Canvas.SetLeft(element2, num);
		Canvas.SetTop(element2, 30.0);
		canvas.Children.Add(element2);
		Grid grid = new Grid
		{
			Width = 54.0,
			Height = 54.0
		};
		grid.Children.Add(new Ellipse
		{
			Fill = new SolidColorBrush(MikuPalette.WithAlpha(ReferenceBlue, 48)),
			Stroke = new SolidColorBrush(ReferenceNavy),
			StrokeThickness = 2.0
		});
		grid.Children.Add(new Ellipse
		{
			Margin = new Thickness(7.0),
			Stroke = new SolidColorBrush(MikuPalette.WithAlpha(ReferenceCyan, 155)),
			StrokeThickness = 1.0
		});
		System.Windows.Shapes.Path path = new System.Windows.Shapes.Path
		{
			Stroke = new SolidColorBrush(accent),
			StrokeThickness = 4.0,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round
		};
		grid.Children.Add(path);
		Canvas.SetLeft(grid, (!flag) ? 201 : 0);
		Canvas.SetTop(grid, 0.0);
		canvas.Children.Add(grid);
		VectorTextBlock vectorTextBlock = CreateOutlinedVectorText(18.0, FontWeights.Bold, ReferenceNavy, 0.45);
		vectorTextBlock.Text = "--%";
		vectorTextBlock.Margin = new Thickness(5.0, 0.0, 0.0, 0.0);
		Grid grid2 = new Grid
		{
			Width = 190.0,
			Height = 54.0
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = ((!flag) ? HorizontalAlignment.Right : HorizontalAlignment.Left)
		};
		VectorTextBlock vectorTextBlock2 = CreateOutlinedVectorText(18.0, FontWeights.Bold, ReferenceNavy, 0.45);
		vectorTextBlock2.Text = title;
		if (flag)
		{
			stackPanel.Children.Add(vectorTextBlock);
			stackPanel.Children.Add(CreateSpacer(5.0));
			stackPanel.Children.Add(vectorTextBlock2);
		}
		else
		{
			stackPanel.Children.Add(vectorTextBlock2);
			stackPanel.Children.Add(vectorTextBlock);
		}
		VectorTextBlock vectorTextBlock3 = CreateOutlinedVectorText(11.0, FontWeights.Bold, ReferenceNavy, 0.35);
		vectorTextBlock3.Text = detail;
		vectorTextBlock3.TextAlignment = ((!flag) ? TextAlignment.Right : TextAlignment.Left);
		vectorTextBlock3.HorizontalAlignment = HorizontalAlignment.Stretch;
		vectorTextBlock3.VerticalAlignment = VerticalAlignment.Bottom;
		vectorTextBlock3.Margin = new Thickness(0.0, 0.0, 0.0, 1.0);
		stackPanel.VerticalAlignment = VerticalAlignment.Top;
		grid2.Children.Add(stackPanel);
		grid2.Children.Add(vectorTextBlock3);
		Canvas.SetLeft(grid2, flag ? 65 : 0);
		Canvas.SetTop(grid2, 0.0);
		canvas.Children.Add(grid2);
		Border result = new Border
		{
			Width = 255.0,
			Height = 54.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 3.0),
			Background = System.Windows.Media.Brushes.Transparent,
			Child = canvas
		};
		_gauges[key] = new GaugeVisual(path, vectorTextBlock, vectorTextBlock3);
		return result;
	}

	internal FrameworkElement CreateMediaPanel(WidgetSide side)
	{
		Canvas canvas = new Canvas
		{
			Width = 430.0,
			Height = 146.0
		};
		Border element = new Border
		{
			Width = 430.0,
			Height = 101.0,
			CornerRadius = new CornerRadius(5.0),
			Background = CreateGradient(side == WidgetSide.Left, MikuPalette.WithAlpha(ReferenceDateBand, 222), MikuPalette.WithAlpha(ReferenceTimeBand, 205)),
			BorderBrush = new SolidColorBrush(ReferenceCyan),
			BorderThickness = ((side == WidgetSide.Left) ? new Thickness(3.0, 0.0, 0.0, 0.0) : new Thickness(0.0, 0.0, 3.0, 0.0))
		};
		Canvas.SetLeft(element, 0.0);
		Canvas.SetTop(element, 0.0);
		canvas.Children.Add(element);
		Grid grid = new Grid();
		_mediaArtwork = new System.Windows.Controls.Image
		{
			Stretch = Stretch.UniformToFill,
			Visibility = Visibility.Collapsed,
			SnapsToDevicePixels = false,
			UseLayoutRounding = false
		};
		RenderOptions.SetBitmapScalingMode(_mediaArtwork, BitmapScalingMode.HighQuality);
		_mediaArtworkPlaceholder = CreateText(31.0, FontWeights.Bold, MikuPalette.Mint);
		_mediaArtworkPlaceholder.Text = "♫";
		_mediaArtworkPlaceholder.TextAlignment = TextAlignment.Center;
		_mediaArtworkPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
		_mediaArtworkPlaceholder.VerticalAlignment = VerticalAlignment.Center;
		grid.Children.Add(new Border
		{
			Background = CreateRadialBackground(),
			Child = _mediaArtworkPlaceholder
		});
		grid.Children.Add(_mediaArtwork);
		Border element2 = new Border
		{
			Width = 80.0,
			Height = 80.0,
			BorderBrush = new SolidColorBrush(ReferenceCyan),
			BorderThickness = new Thickness(2.0),
			Child = grid
		};
		Canvas.SetLeft(element2, 5.0);
		Canvas.SetTop(element2, 8.0);
		canvas.Children.Add(element2);
		TextAlignment textAlignment = ((side != 0) ? TextAlignment.Right : TextAlignment.Left);
		_mediaTitle = CreateText(18.0, FontWeights.Bold, Colors.White);
		_mediaTitle.Width = 324.0;
		_mediaTitle.TextTrimming = TextTrimming.CharacterEllipsis;
		_mediaTitle.TextAlignment = textAlignment;
		Canvas.SetLeft(_mediaTitle, 96.0);
		Canvas.SetTop(_mediaTitle, 4.0);
		canvas.Children.Add(_mediaTitle);
		_mediaArtist = CreateText(12.0, FontWeights.Medium, Colors.White);
		_mediaArtist.Width = 324.0;
		_mediaArtist.TextTrimming = TextTrimming.CharacterEllipsis;
		_mediaArtist.TextAlignment = textAlignment;
		Canvas.SetLeft(_mediaArtist, 96.0);
		Canvas.SetTop(_mediaArtist, 31.0);
		canvas.Children.Add(_mediaArtist);
		System.Windows.Shapes.Path glyph;
		FrameworkElement element3 = CreateMediaVisualButton(MediaPlaybackCommand.Previous, isPlaying: false, out glyph);
		FrameworkElement element4 = CreateMediaVisualButton(MediaPlaybackCommand.Toggle, _lastMedia.IsPlaying, out _mediaToggleGlyph);
		FrameworkElement element5 = CreateMediaVisualButton(MediaPlaybackCommand.Next, isPlaying: false, out glyph);
		Canvas.SetLeft(element3, 96.0);
		Canvas.SetTop(element3, 56.0);
		Canvas.SetLeft(element4, 133.0);
		Canvas.SetTop(element4, 56.0);
		Canvas.SetLeft(element5, 170.0);
		Canvas.SetTop(element5, 56.0);
		canvas.Children.Add(element3);
		canvas.Children.Add(element4);
		canvas.Children.Add(element5);
		Grid grid2 = new Grid
		{
			Width = 419.0,
			Height = 35.0
		};
		_spectrumBars = new Border[24];
		for (int i = 0; i < 24; i++)
		{
			grid2.ColumnDefinitions.Add(new ColumnDefinition());
			Border border = new Border
			{
				Height = 2.0,
				Margin = new Thickness(1.4, 0.0, 1.4, 0.0),
				VerticalAlignment = VerticalAlignment.Bottom,
				Background = CreateGradient(leftToRight: false, Colors.White, ReferenceCyan)
			};
			Grid.SetColumn(border, i);
			grid2.Children.Add(border);
			_spectrumBars[i] = border;
		}
		Canvas.SetLeft(grid2, 5.0);
		Canvas.SetTop(grid2, 105.0);
		canvas.Children.Add(grid2);
		Border element6 = new Border
		{
			Width = 419.0,
			Height = 2.0,
			Background = new SolidColorBrush(MikuPalette.WithAlpha(Colors.White, 225))
		};
		Canvas.SetLeft(element6, 5.0);
		Canvas.SetTop(element6, 141.0);
		canvas.Children.Add(element6);
		Border border2 = new Border
		{
			Width = 324.0,
			Height = 3.0,
			Background = Brush(150, byte.MaxValue, byte.MaxValue, byte.MaxValue)
		};
		_mediaProgress = new Border
		{
			Width = 0.0,
			Height = 3.0,
			HorizontalAlignment = HorizontalAlignment.Left,
			Background = new SolidColorBrush(ReferenceCyan)
		};
		border2.Child = _mediaProgress;
		Canvas.SetLeft(border2, 96.0);
		Canvas.SetTop(border2, 94.0);
		canvas.Children.Add(border2);
		return canvas;
	}

	private static FrameworkElement CreateMediaVisualButton(MediaPlaybackCommand command, bool isPlaying, out System.Windows.Shapes.Path glyph)
	{
		glyph = new System.Windows.Shapes.Path
		{
			Data = NativeMediaControlsWindow.CreateGlyphGeometry(command, isPlaying),
			Fill = System.Windows.Media.Brushes.White,
			Stretch = Stretch.None,
			Width = 16.0,
			Height = 16.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			SnapsToDevicePixels = false,
			UseLayoutRounding = false
		};
		RadialGradientBrush radialGradientBrush = new RadialGradientBrush
		{
			Center = new System.Windows.Point(0.38, 0.3),
			GradientOrigin = new System.Windows.Point(0.38, 0.3),
			RadiusX = 0.8,
			RadiusY = 0.8
		};
		radialGradientBrush.GradientStops.Add(new GradientStop(ReferenceCyan, 0.0));
		radialGradientBrush.GradientStops.Add(new GradientStop(ReferenceBlue, 0.74));
		return new Border
		{
			Width = 30.0,
			Height = 30.0,
			CornerRadius = new CornerRadius(15.0),
			BorderThickness = new Thickness(1.2),
			BorderBrush = new SolidColorBrush(Colors.White),
			Background = radialGradientBrush,
			Child = glyph
		};
	}

	private FrameworkElement CreateCodexPanel(WidgetSide side)
	{
		Canvas obj = new Canvas
		{
			Width = 360.0,
			Height = 280.0
		};
		Border border = new Border
		{
			Width = 202.0,
			MinHeight = 92.0,
			Padding = new Thickness(15.0, 13.0, 15.0, 13.0),
			Background = CreateGradient(leftToRight: true, MikuPalette.WithAlpha(ReferenceDateBand, 230), MikuPalette.WithAlpha(ReferenceTimeBand, 215)),
			BorderBrush = new SolidColorBrush(ReferenceCyan),
			BorderThickness = ((side == WidgetSide.Left) ? new Thickness(0.0, 0.0, 3.0, 0.0) : new Thickness(3.0, 0.0, 0.0, 0.0))
		};
		StackPanel stackPanel = new StackPanel();
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		_codexPulse = new Ellipse
		{
			Width = 7.0,
			Height = 7.0,
			Margin = new Thickness(0.0, 3.0, 7.0, 0.0),
			Fill = new SolidColorBrush(ReferenceCyan)
		};
		_codexHeadline = CreateText(12.0, FontWeights.Bold, Colors.White);
		stackPanel2.Children.Add(_codexPulse);
		stackPanel2.Children.Add(_codexHeadline);
		_codexDetail = CreateText(11.0, FontWeights.Normal, Colors.White);
		_codexDetail.TextWrapping = TextWrapping.Wrap;
		_codexDetail.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
		_codexMeta = CreateText(8.0, FontWeights.SemiBold, MikuPalette.WithAlpha(Colors.White, 225));
		_codexMeta.TextWrapping = TextWrapping.Wrap;
		_codexMeta.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
		stackPanel.Children.Add(stackPanel2);
		stackPanel.Children.Add(_codexDetail);
		stackPanel.Children.Add(_codexMeta);
		border.Child = stackPanel;
		Canvas.SetLeft(border, (side == WidgetSide.Left) ? 158 : 0);
		Canvas.SetTop(border, 39.0);
		obj.Children.Add(border);
		System.Windows.Controls.Image image = new System.Windows.Controls.Image
		{
			Width = 235.0,
			Height = 255.0,
			Stretch = Stretch.Uniform,
			Source = LoadCompanionImage(),
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
			SnapsToDevicePixels = false,
			UseLayoutRounding = false
		};
		RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
		_codexTranslate = new TranslateTransform();
		TransformGroup transformGroup = new TransformGroup();
		if (side == WidgetSide.Left)
		{
			transformGroup.Children.Add(new ScaleTransform(-1.0, 1.0));
		}
		transformGroup.Children.Add(_codexTranslate);
		image.RenderTransform = transformGroup;
		Canvas.SetLeft(image, (side != 0) ? 125 : 0);
		Canvas.SetTop(image, 25.0);
		obj.Children.Add(image);
		return obj;
	}

	internal void UpdateClockAndCalendar()
	{
		if (_disposed)
		{
			return;
		}
		DateTime now = DateTime.Now;
		if (_clockTime != null)
		{
			string text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
			string text2 = now.Year.ToString(CultureInfo.InvariantCulture);
			string text3 = now.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant();
			string text4 = now.Day.ToString("00", CultureInfo.InvariantCulture);
			string text5 = now.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
			if (!string.Equals(_clockTime.Text, text, StringComparison.Ordinal) || !string.Equals(_clockYear.Text, text2, StringComparison.Ordinal) || !string.Equals(_clockWeekday.Text, text3, StringComparison.Ordinal) || !string.Equals(_clockDay.Text, text4, StringComparison.Ordinal) || !string.Equals(_clockMonth.Text, text5, StringComparison.Ordinal))
			{
				_clockTime.Text = text;
				_clockYear.Text = text2;
				_clockWeekday.Text = text3;
				_clockDay.Text = text4;
				_clockMonth.Text = text5;
				RenderClockPanel();
			}
		}
	}

	private void ApplyLastMetrics()
	{
		SystemMetricsSnapshot lastMetrics = _lastMetrics;
		if ((object)lastMetrics != null)
		{
			double num = Math.Round(lastMetrics.CpuPercent);
			double num2 = ((!lastMetrics.GpuPercent.HasValue) ? 0.0 : Math.Round(lastMetrics.GpuPercent.Value));
			double num3 = Math.Round(lastMetrics.MemoryPercent);
			double num4 = Math.Round(lastMetrics.DriveC.Percent);
			double num5 = Math.Round(lastMetrics.DriveD.Percent);
			if (SetGauge("cpu", num, $"{num}%", "실시간 프로세서") | SetGauge("gpu", num2, (!lastMetrics.GpuPercent.HasValue) ? "N/A" : $"{num2}%", (!lastMetrics.GpuPercent.HasValue) ? "측정 불가" : "GPU 엔진") | SetGauge("memory", num3, $"{num3}%", FormatBytes(lastMetrics.MemoryUsedBytes) + " / " + FormatBytes(lastMetrics.MemoryTotalBytes)) | SetGauge("driveC", num4, lastMetrics.DriveC.IsAvailable ? $"{num4}%" : "N/A", FormatDrive(lastMetrics.DriveC)) | SetGauge("driveD", num5, lastMetrics.DriveD.IsAvailable ? $"{num5}%" : "N/A", FormatDrive(lastMetrics.DriveD)))
			{
				RenderSystemPanel();
			}
		}
	}

	private void ApplyLastAddOns()
	{
		if (_mediaTitle != null)
		{
			bool flag = !object.Equals(_appliedMedia, _lastMedia);
			if (flag)
			{
				_mediaTitle.Text = _lastMedia.Title;
				_mediaArtist.Text = _lastMedia.Artist;
				if (_mediaToggleGlyph != null)
				{
					_mediaToggleGlyph.Data = NativeMediaControlsWindow.CreateGlyphGeometry(MediaPlaybackCommand.Toggle, _lastMedia.IsPlaying);
				}
				_mediaProgress.Width = 324.0 * Math.Clamp(_lastMedia.Progress, 0.0, 1.0);
				UpdateArtwork(_lastMedia.ArtworkDataUri);
				_appliedMedia = _lastMedia;
			}
			ApplySpectrum(!flag);
			if (flag)
			{
				RequestMediaPanelRender(immediate: true);
			}
		}
		if (_codexHeadline != null)
		{
			string text = string.Join(" // ", new string[2] { _lastCodex.Workspace, _lastCodex.Model }.Where((string value) => !string.IsNullOrWhiteSpace(value)));
			if (string.IsNullOrWhiteSpace(text))
			{
				text = "로컬 상태 전용";
			}
			bool flag2 = !string.Equals(_codexHeadline.Text, _lastCodex.Headline, StringComparison.Ordinal) || !string.Equals(_codexDetail.Text, _lastCodex.Detail, StringComparison.Ordinal) || !string.Equals(_codexMeta.Text, text, StringComparison.Ordinal) || !string.Equals(_lastCodexState, _lastCodex.State, StringComparison.Ordinal);
			_codexHeadline.Text = _lastCodex.Headline;
			_codexDetail.Text = _lastCodex.Detail;
			_codexMeta.Text = text;
			System.Windows.Media.Color color;
			switch (_lastCodex.State)
			{
			case "complete":
				color = MikuPalette.Mint;
				break;
			case "offline":
			case "disconnected":
				color = MikuPalette.Silver;
				break;
			default:
				color = MikuPalette.Pink;
				break;
			}
			System.Windows.Media.Color color2 = color;
			_codexPulse.Fill = new SolidColorBrush(color2);
			if (!string.Equals(_lastCodexState, _lastCodex.State, StringComparison.Ordinal))
			{
				_lastCodexState = _lastCodex.State;
			}
			if (flag2)
			{
				RenderCodexPanel();
			}
		}
	}

	private void ApplySpectrum(bool requestRender = true)
	{
		if (_isPaused || !_isWindowVisible)
		{
			return;
		}
		bool flag = false;
		for (int i = 0; i < _spectrumBars.Length; i++)
		{
			double num = Math.Pow(Math.Clamp(_lastSpectrum.ElementAtOrDefault(i) * 2.35f, 0f, 1f), 0.55);
			double num2 = Math.Max(2.0, num * 35.0);
			double num3 = (_lastMedia.IsPlaying ? 1.0 : 0.26);
			Border border = _spectrumBars[i];
			if (!(Math.Abs(border.Height - num2) < 0.01) || !(Math.Abs(border.Opacity - num3) < 0.01))
			{
				border.Height = num2;
				border.Opacity = num3;
				flag = true;
			}
		}
		if (flag && requestRender)
		{
			RequestMediaPanelRender(immediate: false);
		}
	}

	private void UpdateArtwork(string dataUri)
	{
		if (_mediaArtwork != null && !string.Equals(dataUri, _lastArtworkDataUri, StringComparison.Ordinal))
		{
			_lastArtworkDataUri = dataUri;
			ImageSource imageSource = DecodeDataUri(dataUri);
			_mediaArtwork.Source = imageSource;
			_mediaArtwork.Visibility = ((imageSource == null) ? Visibility.Collapsed : Visibility.Visible);
			if (_mediaArtworkPlaceholder != null)
			{
				_mediaArtworkPlaceholder.Visibility = ((imageSource != null) ? Visibility.Collapsed : Visibility.Visible);
			}
		}
	}

	private bool SetGauge(string key, double percent, string value, string detail)
	{
		if (!_gauges.TryGetValue(key, out GaugeVisual value2))
		{
			return false;
		}
		if (string.Equals(value2.Value.Text, value, StringComparison.Ordinal) && string.Equals(value2.Detail.Text, detail, StringComparison.Ordinal))
		{
			return false;
		}
		value2.Value.Text = value;
		value2.Detail.Text = detail;
		value2.Arc.Data = CreateArcGeometry(percent);
		return true;
	}

	private static Geometry CreateArcGeometry(double percent)
	{
		double num = Math.Clamp(percent, 0.0, 100.0);
		if (num <= 0.0)
		{
			return Geometry.Empty;
		}
		double num2 = ((num >= 100.0) ? 359.999 : (num * 3.6));
		double num3 = (num2 - 90.0) * Math.PI / 180.0;
		System.Windows.Point point = new System.Windows.Point(27.0 + 23.0 * Math.Cos(num3), 27.0 + 23.0 * Math.Sin(num3));
		return new PathGeometry(new <>z__ReadOnlySingleElementList<PathFigure>(new PathFigure
		{
			StartPoint = new System.Windows.Point(27.0, 4.0),
			Segments = { (PathSegment)new ArcSegment
			{
				Point = point,
				Size = new System.Windows.Size(23.0, 23.0),
				IsLargeArc = (num2 > 180.0),
				SweepDirection = SweepDirection.Clockwise
			} }
		}));
	}

	private void StartCodexAnimation()
	{
		RenderCodexPanel();
	}

	private void StopCodexAnimation()
	{
		if (_codexTranslate != null)
		{
			_codexTranslate.BeginAnimation(TranslateTransform.YProperty, null);
			_codexTranslate.Y = 0.0;
		}
	}

	private void AddPositioned(Canvas canvas, FrameworkElement element, WidgetSide side, double margin, double top, double scale)
	{
		element.RenderTransformOrigin = new System.Windows.Point((side != WidgetSide.Left) ? 1 : 0, 0.0);
		element.RenderTransform = new ScaleTransform(scale, scale);
		Canvas.SetLeft(element, (side == WidgetSide.Left) ? margin : ((double)_width - margin - element.Width));
		Canvas.SetTop(element, top);
		canvas.Children.Add(element);
	}

	private void ApplyLayerOrder()
	{
		if (!_initialized)
		{
			return;
		}
		uint flags = 19u;
		ApplyBoundedPanelLayerOrder(flags);
		if (!UsesFullDisplayCompositionSurface)
		{
			return;
		}
		if (_isRaisedDesktop)
		{
			if (!NativeMethods.SetWindowPos(Handle, _shellViewHandle, 0, 0, 0, 0, flags))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 네이티브 위젯 Z 순서를 설정하지 못했습니다.");
			}
		}
		else if (!NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 네이티브 위젯을 월페이퍼 위에 배치하지 못했습니다.");
		}
		BoundedDesktopPanelHost? clockPanelHost = _clockPanelHost;
		if (clockPanelHost != null && clockPanelHost.IsReady && !NativeMethods.SetWindowPos(_clockPanelHost.Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "시계 알파 패널의 데스크톱 순서를 설정하지 못했습니다.");
		}
		BoundedDesktopPanelHost? systemPanelHost = _systemPanelHost;
		if (systemPanelHost != null && systemPanelHost.IsReady && !NativeMethods.SetWindowPos(_systemPanelHost.Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "시스템 알파 패널의 데스크톱 순서를 설정하지 못했습니다.");
		}
		BoundedDesktopPanelHost? mediaPanelHost = _mediaPanelHost;
		if (mediaPanelHost != null && mediaPanelHost.IsReady && !NativeMethods.SetWindowPos(_mediaPanelHost.Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "미디어 알파 패널의 데스크톱 순서를 설정하지 못했습니다.");
		}
		BoundedDesktopPanelHost? codexPanelHost = _codexPanelHost;
		if (codexPanelHost == null || !codexPanelHost.IsReady || NativeMethods.SetWindowPos(_codexPanelHost.Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
		{
			return;
		}
		throw new Win32Exception(Marshal.GetLastWin32Error(), "Codex 알파 패널의 데스크톱 순서를 설정하지 못했습니다.");
	}

	private void ApplyBoundedPanelLayerOrder(uint flags)
	{
		foreach (BoundedDesktopPanelHost item in EnumeratePanelHosts())
		{
			if (item.IsReady)
			{
				nint insertAfter = (_isRaisedDesktop ? _shellViewHandle : IntPtr.Zero);
				if (!NativeMethods.SetWindowPos(item.Handle, insertAfter, 0, 0, 0, 0, flags))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 독립 위젯 창의 바탕화면 Z 순서를 설정하지 못했습니다.");
				}
			}
		}
	}

	[IteratorStateMachine(typeof(<EnumeratePanelHosts>d__130))]
	private IEnumerable<BoundedDesktopPanelHost> EnumeratePanelHosts()
	{
		//yield-return decompiler failed: Unexpected instruction in Iterator.Dispose()
		return new <EnumeratePanelHosts>d__130(-2)
		{
			<>4__this = this
		};
	}

	private void ClockTimerOnTick(object? sender, EventArgs eventArgs)
	{
		_clockTimer.Stop();
		UpdateClockAndCalendar();
		UpdateClockTimer();
	}

	private void MediaRenderTimerOnTick(object? sender, EventArgs eventArgs)
	{
		_mediaRenderTimer.Stop();
		if (_mediaRenderPending)
		{
			CommitMediaPanelRender();
		}
	}

	private void UpdateClockTimer()
	{
		if (!_isPaused && _settings.IsEnabled && _settings.ShowClock)
		{
			_clockTimer.Stop();
			DateTime now = DateTime.Now;
			DateTime dateTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1.0);
			_clockTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100.0, (dateTime - now).TotalMilliseconds + 25.0));
			_clockTimer.Start();
		}
		else
		{
			_clockTimer.Stop();
		}
	}

	private void ResetVisualReferences()
	{
		StopCodexAnimation();
		_gauges.Clear();
		_clockYear = null;
		_clockWeekday = null;
		_clockDay = null;
		_clockMonth = null;
		_clockTime = null;
		_mediaArtwork = null;
		_mediaArtworkPlaceholder = null;
		_mediaTitle = null;
		_mediaArtist = null;
		_mediaProgress = null;
		_mediaToggleGlyph = null;
		_spectrumBars = Array.Empty<Border>();
		_codexHeadline = null;
		_codexDetail = null;
		_codexMeta = null;
		_codexPulse = null;
		_codexTranslate = null;
		_lastCodexState = string.Empty;
		_lastArtworkDataUri = string.Empty;
		_appliedMedia = null;
	}

	private static TextBlock CreateText(double fontSize, FontWeight fontWeight, System.Windows.Media.Color color)
	{
		return new TextBlock
		{
			FontFamily = new System.Windows.Media.FontFamily("Pretendard, Malgun Gothic, Segoe UI"),
			FontSize = fontSize,
			FontWeight = fontWeight,
			Foreground = new SolidColorBrush(color),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
	}

	private static VectorTextBlock CreateVectorText(double fontSize, FontWeight fontWeight, System.Windows.Media.Color color)
	{
		return new VectorTextBlock
		{
			FontFamily = ReferenceFontFamily,
			FontSize = fontSize,
			FontWeight = fontWeight,
			FillColor = color,
			SnapsToDevicePixels = false,
			UseLayoutRounding = false,
			ClipToBounds = false
		};
	}

	private static VectorTextBlock CreateOutlinedVectorText(double fontSize, FontWeight fontWeight, System.Windows.Media.Color color, double outlineThickness, System.Windows.Media.Color? outlineColor = null)
	{
		VectorTextBlock vectorTextBlock = CreateVectorText(fontSize, fontWeight, color);
		vectorTextBlock.OutlineColor = outlineColor ?? Colors.White;
		vectorTextBlock.OutlineThickness = outlineThickness;
		return vectorTextBlock;
	}

	private static FrameworkElement CreateSpacer(double width)
	{
		return new Border
		{
			Width = width
		};
	}

	private static System.Windows.Media.LinearGradientBrush CreateGradient(bool leftToRight, System.Windows.Media.Color start, System.Windows.Media.Color end)
	{
		return new System.Windows.Media.LinearGradientBrush
		{
			StartPoint = (leftToRight ? new System.Windows.Point(0.0, 0.5) : new System.Windows.Point(1.0, 0.5)),
			EndPoint = (leftToRight ? new System.Windows.Point(1.0, 0.5) : new System.Windows.Point(0.0, 0.5)),
			GradientStops = 
			{
				new GradientStop(start, 0.0),
				new GradientStop(end, 1.0)
			}
		};
	}

	private static RadialGradientBrush CreateRadialBackground()
	{
		return new RadialGradientBrush
		{
			Center = new System.Windows.Point(0.45, 0.4),
			GradientOrigin = new System.Windows.Point(0.45, 0.4),
			RadiusX = 0.75,
			RadiusY = 0.75,
			GradientStops = 
			{
				new GradientStop(MikuPalette.Teal, 0.0),
				new GradientStop(MikuPalette.Graphite, 0.72)
			}
		};
	}

	private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
	{
		return new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, red, green, blue));
	}

	private static ImageSource? DecodeDataUri(string dataUri)
	{
		if (string.IsNullOrWhiteSpace(dataUri))
		{
			return null;
		}
		try
		{
			int num = dataUri.IndexOf(',');
			if (num < 0)
			{
				return null;
			}
			int num2 = num + 1;
			using MemoryStream streamSource = new MemoryStream(Convert.FromBase64String(dataUri.Substring(num2, dataUri.Length - num2)));
			BitmapImage bitmapImage = new BitmapImage();
			bitmapImage.BeginInit();
			bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
			bitmapImage.StreamSource = streamSource;
			bitmapImage.EndInit();
			bitmapImage.Freeze();
			return bitmapImage;
		}
		catch
		{
			return null;
		}
	}

	private static ImageSource? LoadCompanionImage()
	{
		try
		{
			string text = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Companion", "vellume-companion.png");
			if (!File.Exists(text))
			{
				return null;
			}
			BitmapImage bitmapImage = new BitmapImage();
			bitmapImage.BeginInit();
			bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
			bitmapImage.UriSource = new Uri(text, UriKind.Absolute);
			bitmapImage.EndInit();
			bitmapImage.Freeze();
			return bitmapImage;
		}
		catch
		{
			return null;
		}
	}

	private static string FormatBytes(ulong bytes)
	{
		return $"{(double)bytes / 1073741824.0:0.0} GB";
	}

	private static string FormatDrive(DriveMetricsSnapshot drive)
	{
		if (!drive.IsAvailable)
		{
			return "사용할 수 없음";
		}
		return FormatBytes(drive.UsedBytes) + " / " + FormatBytes(drive.TotalBytes);
	}
}
internal sealed class NativeWpfChildHost : IDisposable
{
	private nint _parentHandle;

	private bool _disposed;

	internal nint Handle { get; private set; }

	internal HwndSource Source { get; }

	internal bool IsReady
	{
		get
		{
			if (!_disposed && Handle != IntPtr.Zero && NativeMethods.IsWindow(Handle) && Source.Handle != IntPtr.Zero)
			{
				return NativeMethods.IsWindow(Source.Handle);
			}
			return false;
		}
	}

	internal NativeWpfChildHost(string name, nint parentHandle, int x, int y, int width, int height, bool clickThrough)
	{
		_parentHandle = parentHandle;
		long num = 134742144L;
		if (clickThrough)
		{
			num |= 0x20;
		}
		Handle = NativeMethods.CreateWindowExW((uint)num, "Static", name, 1442840580u, x, y, width, height, parentHandle, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
		if (Handle == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 WPF 데스크톱 브리지 창을 만들지 못했습니다.");
		}
		try
		{
			if (!NativeMethods.SetLayeredWindowAttributes(Handle, 0u, byte.MaxValue, 3u))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 WPF 데스크톱 브리지 투명도를 설정하지 못했습니다.");
			}
			HwndSourceParameters parameters = new HwndSourceParameters(name)
			{
				ParentWindow = Handle,
				PositionX = 0,
				PositionY = 0,
				Width = width,
				Height = height,
				WindowStyle = 1442840576,
				ExtendedWindowStyle = 134217856,
				UsesPerPixelOpacity = true
			};
			Source = new HwndSource(parameters);
			Source.CompositionTarget.BackgroundColor = Colors.Transparent;
		}
		catch
		{
			NativeMethods.DestroyWindow(Handle);
			Handle = IntPtr.Zero;
			throw;
		}
	}

	internal void AttachTo(nint parentHandle, int x, int y, int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_parentHandle = parentHandle;
		NativeMethods.SetParent(Handle, parentHandle);
		Position(x, y, width, height);
		if (NativeMethods.GetParent(Handle) != parentHandle)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows가 WPF 데스크톱 브리지 창을 다시 연결하지 못했습니다.");
		}
	}

	internal void Position(int x, int y, int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!NativeMethods.SetWindowPos(Handle, IntPtr.Zero, x, y, width, height, 116u) || !NativeMethods.SetWindowPos(Source.Handle, IntPtr.Zero, 0, 0, width, height, 116u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 WPF 데스크톱 브리지 위치를 갱신하지 못했습니다.");
		}
	}

	internal bool IsAttachedTo(nint parentHandle)
	{
		if (IsReady && NativeMethods.IsWindow(parentHandle) && NativeMethods.GetParent(Handle) == parentHandle)
		{
			return NativeMethods.GetParent(Source.Handle) == Handle;
		}
		return false;
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			Source.Dispose();
			if (Handle != IntPtr.Zero && NativeMethods.IsWindow(Handle))
			{
				NativeMethods.DestroyWindow(Handle);
			}
			Handle = IntPtr.Zero;
			_parentHandle = IntPtr.Zero;
		}
	}
}
internal static class PackagedAppLauncher
{
	public static async Task<bool> TryLaunchAsync(string targetPath)
	{
		Package package = FindPackage(targetPath);
		if ((object)package == null)
		{
			return false;
		}
		try
		{
			foreach (AppListEntry item in await package.GetAppListEntriesAsync())
			{
				if (await item.LaunchAsync())
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static Package? FindPackage(string targetPath)
	{
		string fullPath;
		try
		{
			fullPath = System.IO.Path.GetFullPath(targetPath);
		}
		catch
		{
			return null;
		}
		try
		{
			foreach (Package package in new PackageManager().FindPackagesForUser(string.Empty))
			{
				string text = null;
				try
				{
					text = package.InstalledLocation.Path;
				}
				catch
				{
				}
				if (!string.IsNullOrWhiteSpace(text))
				{
					ReadOnlySpan<char> readOnlySpan = text;
					char reference = System.IO.Path.DirectorySeparatorChar;
					if (fullPath.StartsWith(string.Concat(readOnlySpan, new ReadOnlySpan<char>(ref reference)), StringComparison.OrdinalIgnoreCase))
					{
						return package;
					}
				}
				if (fullPath.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault((string segment) => segment.StartsWith(package.Id.Name + "_", StringComparison.OrdinalIgnoreCase)) != null)
				{
					return package;
				}
			}
		}
		catch
		{
		}
		return null;
	}
}
public static class SessionRecoveryGuard
{
	private const string ExclusiveGuardMutexName = "Local\\Vellume.Desktop.ExclusiveGpuGuard";

	public static void MarkActive(string markerPath, int ownerProcessId)
	{
		string directoryName = System.IO.Path.GetDirectoryName(markerPath);
		if (!string.IsNullOrWhiteSpace(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		File.WriteAllText(markerPath, ownerProcessId.ToString(CultureInfo.InvariantCulture));
	}

	public static bool MarkCleanExit(string markerPath, int ownerProcessId)
	{
		if (!MarkerBelongsTo(markerPath, ownerProcessId))
		{
			return false;
		}
		try
		{
			File.Delete(markerPath);
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static Process? Start(int ownerProcessId, string markerPath, int recoveryAttempt)
	{
		string processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
		{
			return null;
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = processPath,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		processStartInfo.ArgumentList.Add("--session-guard");
		processStartInfo.ArgumentList.Add(ownerProcessId.ToString(CultureInfo.InvariantCulture));
		processStartInfo.ArgumentList.Add("--session-marker");
		processStartInfo.ArgumentList.Add(markerPath);
		processStartInfo.ArgumentList.Add("--recovery-attempt");
		processStartInfo.ArgumentList.Add(recoveryAttempt.ToString(CultureInfo.InvariantCulture));
		try
		{
			return Process.Start(processStartInfo);
		}
		catch
		{
			return null;
		}
	}

	public static bool Run(int ownerProcessId, string markerPath, int recoveryAttempt)
	{
		try
		{
			using Process process = Process.GetProcessById(ownerProcessId);
			process.WaitForExit();
		}
		catch
		{
		}
		TaskbarVisibilityController.RestoreAllTaskbars();
		if (!ConsumeMarker(markerPath, ownerProcessId) || recoveryAttempt > 0)
		{
			return false;
		}
		string processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
		{
			return false;
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = processPath,
			UseShellExecute = false,
			CreateNoWindow = false,
			WindowStyle = ProcessWindowStyle.Normal
		};
		processStartInfo.ArgumentList.Add("--recovered-session");
		try
		{
			return Process.Start(processStartInfo) != null;
		}
		catch
		{
			return false;
		}
	}

	public static Process? StartExclusiveGpuGuard(int ownerProcessId)
	{
		string processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
		{
			return null;
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = processPath,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		processStartInfo.ArgumentList.Add("--exclusive-gpu-guard");
		processStartInfo.ArgumentList.Add(ownerProcessId.ToString(CultureInfo.InvariantCulture));
		try
		{
			return Process.Start(processStartInfo);
		}
		catch
		{
			return null;
		}
	}

	public static bool RunExclusiveGpuGuard(int ownerProcessId)
	{
		bool createdNew;
		using (new Mutex(initiallyOwned: true, "Local\\Vellume.Desktop.ExclusiveGpuGuard", out createdNew))
		{
			if (!createdNew)
			{
				return false;
			}
			WaitForProcessExit(ownerProcessId);
			WaitForExclusiveGpuApplicationsToExit(RequiresExclusiveGpuIsolation, delegate(TimeSpan delay)
			{
				Thread.Sleep(delay);
			}, 2, TimeSpan.FromMilliseconds(100.0));
			string processPath = Environment.ProcessPath;
			if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
			{
				return false;
			}
			ProcessStartInfo processStartInfo = new ProcessStartInfo
			{
				FileName = processPath,
				UseShellExecute = false,
				CreateNoWindow = false,
				WindowStyle = ProcessWindowStyle.Normal
			};
			processStartInfo.ArgumentList.Add("--resumed-after-exclusive-gpu");
			try
			{
				return Process.Start(processStartInfo) != null;
			}
			catch
			{
				return false;
			}
		}
	}

	internal static void WaitForExclusiveGpuApplicationsToExit(Func<bool> isRunning, Action<TimeSpan> delay, int requiredClearSamples, TimeSpan sampleInterval)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(requiredClearSamples, 1, "requiredClearSamples");
		int num = 0;
		while (num < requiredClearSamples)
		{
			num = ((!isRunning()) ? (num + 1) : 0);
			if (num < requiredClearSamples)
			{
				delay(sampleInterval);
			}
		}
	}

	private static void WaitForProcessExit(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			process.WaitForExit();
		}
		catch
		{
		}
	}

	private static bool RequiresExclusiveGpuIsolation()
	{
		return new WindowsPerformanceModeDetector().RequiresExclusiveGpuIsolation();
	}

	private static bool ConsumeMarker(string markerPath, int ownerProcessId)
	{
		if (!MarkerBelongsTo(markerPath, ownerProcessId))
		{
			return false;
		}
		try
		{
			File.Delete(markerPath);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool MarkerBelongsTo(string markerPath, int ownerProcessId)
	{
		try
		{
			int result;
			return File.Exists(markerPath) && int.TryParse(File.ReadAllText(markerPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result == ownerProcessId;
		}
		catch
		{
			return false;
		}
	}
}
internal sealed class SystemMetricsSampler : IDisposable
{
	private struct FileTime
	{
		internal uint Low;

		internal uint High;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	private struct MemoryStatusEx
	{
		internal uint Length;

		internal uint MemoryLoad;

		internal ulong TotalPhysical;

		internal ulong AvailablePhysical;

		internal ulong TotalPageFile;

		internal ulong AvailablePageFile;

		internal ulong TotalVirtual;

		internal ulong AvailableVirtual;

		internal ulong AvailableExtendedVirtual;
	}

	private struct PdhFormattedCounterValue
	{
		internal uint Status;

		internal double DoubleValue;
	}

	private struct PdhFormattedCounterValueItem
	{
		internal nint Name;

		internal PdhFormattedCounterValue Value;
	}

	private const uint PdhFormatDouble = 512u;

	private const uint PdhMoreData = 2147485650u;

	private ulong _previousIdle;

	private ulong _previousKernel;

	private ulong _previousUser;

	private bool _hasCpuBaseline;

	private readonly object _gpuSync = new object();

	private nint _gpuQuery;

	private nint _gpuCounter;

	private bool _gpuInitializationAttempted;

	public SystemMetricsSnapshot Sample()
	{
		double value = SampleCpuUsage();
		double? num = SampleGpuUsage();
		MemoryStatusEx buffer = new MemoryStatusEx
		{
			Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
		};
		if (!GlobalMemoryStatusEx(ref buffer))
		{
			buffer.TotalPhysical = 1uL;
		}
		ulong num2 = buffer.TotalPhysical - buffer.AvailablePhysical;
		return new SystemMetricsSnapshot(MemoryPercent: Math.Clamp((buffer.TotalPhysical == 0L) ? 0.0 : ((double)num2 * 100.0 / (double)buffer.TotalPhysical), 0.0, 100.0), CpuPercent: Math.Clamp(value, 0.0, 100.0), GpuPercent: (!num.HasValue) ? null : new double?(Math.Clamp(num.Value, 0.0, 100.0)), MemoryUsedBytes: num2, MemoryTotalBytes: buffer.TotalPhysical, DriveC: SampleDrive("C"), DriveD: SampleDrive("D"), Uptime: TimeSpan.FromMilliseconds(Environment.TickCount64));
	}

	public void Dispose()
	{
		lock (_gpuSync)
		{
			if (_gpuQuery != IntPtr.Zero)
			{
				PdhCloseQuery(_gpuQuery);
				_gpuQuery = IntPtr.Zero;
				_gpuCounter = IntPtr.Zero;
			}
		}
	}

	private double SampleCpuUsage()
	{
		if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
		{
			return 0.0;
		}
		ulong num = ToUInt64(idleTime);
		ulong num2 = ToUInt64(kernelTime);
		ulong num3 = ToUInt64(userTime);
		if (!_hasCpuBaseline)
		{
			_previousIdle = num;
			_previousKernel = num2;
			_previousUser = num3;
			_hasCpuBaseline = true;
			return 0.0;
		}
		ulong num4 = num - _previousIdle;
		ulong num5 = num2 - _previousKernel;
		ulong num6 = num3 - _previousUser;
		_previousIdle = num;
		_previousKernel = num2;
		_previousUser = num3;
		ulong num7 = num5 + num6;
		if (num7 != 0L)
		{
			return (double)(num7 - num4) * 100.0 / (double)num7;
		}
		return 0.0;
	}

	private double? SampleGpuUsage()
	{
		lock (_gpuSync)
		{
			return SampleGpuUsageCore();
		}
	}

	private double? SampleGpuUsageCore()
	{
		try
		{
			if (!_gpuInitializationAttempted)
			{
				_gpuInitializationAttempted = true;
				if (PdhOpenQueryW(null, IntPtr.Zero, out _gpuQuery) != 0 || PdhAddEnglishCounterW(_gpuQuery, "\\GPU Engine(*)\\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
				{
					Dispose();
					return null;
				}
				PdhCollectQueryData(_gpuQuery);
				return null;
			}
			if (_gpuQuery == IntPtr.Zero || _gpuCounter == IntPtr.Zero || PdhCollectQueryData(_gpuQuery) != 0)
			{
				return null;
			}
			uint bufferSize = 0u;
			uint itemCount = 0u;
			if (PdhGetFormattedCounterArrayW(_gpuCounter, 512u, ref bufferSize, ref itemCount, IntPtr.Zero) != 2147485650u || bufferSize == 0 || itemCount == 0)
			{
				return null;
			}
			nint num = Marshal.AllocHGlobal((int)bufferSize);
			try
			{
				if (PdhGetFormattedCounterArrayW(_gpuCounter, 512u, ref bufferSize, ref itemCount, num) != 0)
				{
					return null;
				}
				int num2 = Marshal.SizeOf<PdhFormattedCounterValueItem>();
				double num3 = 0.0;
				bool flag = false;
				for (int i = 0; i < itemCount; i++)
				{
					PdhFormattedCounterValueItem pdhFormattedCounterValueItem = Marshal.PtrToStructure<PdhFormattedCounterValueItem>(num + i * num2);
					uint status = pdhFormattedCounterValueItem.Value.Status;
					if ((status == 0 || status == 1) && !double.IsNaN(pdhFormattedCounterValueItem.Value.DoubleValue) && !double.IsInfinity(pdhFormattedCounterValueItem.Value.DoubleValue))
					{
						flag = true;
						num3 = Math.Max(num3, pdhFormattedCounterValueItem.Value.DoubleValue);
					}
				}
				return flag ? new double?(num3) : null;
			}
			finally
			{
				Marshal.FreeHGlobal(num);
			}
		}
		catch (DllNotFoundException)
		{
			Dispose();
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			Dispose();
			return null;
		}
	}

	private static DriveMetricsSnapshot SampleDrive(string driveLetter)
	{
		string driveLetter2 = driveLetter;
		try
		{
			DriveInfo driveInfo = DriveInfo.GetDrives().FirstOrDefault((DriveInfo candidate) => candidate.Name.StartsWith(driveLetter2 + ":", StringComparison.OrdinalIgnoreCase));
			if (driveInfo == null || !driveInfo.IsReady)
			{
				return DriveMetricsSnapshot.Unavailable(driveLetter2);
			}
			ulong num = (ulong)Math.Max(0L, driveInfo.TotalSize);
			ulong num2 = (ulong)Math.Max(0L, driveInfo.AvailableFreeSpace);
			ulong num3 = ((num >= num2) ? (num - num2) : 0);
			double value = ((num == 0L) ? 0.0 : ((double)num3 * 100.0 / (double)num));
			return new DriveMetricsSnapshot(driveLetter2, IsAvailable: true, Math.Clamp(value, 0.0, 100.0), num3, num);
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
		{
			return DriveMetricsSnapshot.Unavailable(driveLetter2);
		}
	}

	private static ulong ToUInt64(FileTime value)
	{
		return ((ulong)value.High << 32) | value.Low;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

	[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
	private static extern uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

	[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
	private static extern uint PdhAddEnglishCounterW(nint query, string counterPath, nint userData, out nint counter);

	[DllImport("pdh.dll")]
	private static extern uint PdhCollectQueryData(nint query);

	[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
	private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, ref uint itemCount, nint itemBuffer);

	[DllImport("pdh.dll")]
	private static extern uint PdhCloseQuery(nint query);
}
internal sealed record SystemMetricsSnapshot(double CpuPercent, double? GpuPercent, double MemoryPercent, ulong MemoryUsedBytes, ulong MemoryTotalBytes, DriveMetricsSnapshot DriveC, DriveMetricsSnapshot DriveD, TimeSpan Uptime)
{
	internal double DiskPercent => DriveC.Percent;

	internal ulong DiskUsedBytes => DriveC.UsedBytes;

	internal ulong DiskTotalBytes => DriveC.TotalBytes;

	[CompilerGenerated]
	private bool PrintMembers(StringBuilder builder)
	{
		RuntimeHelpers.EnsureSufficientExecutionStack();
		builder.Append("CpuPercent = ");
		builder.Append(CpuPercent.ToString());
		builder.Append(", GpuPercent = ");
		builder.Append(GpuPercent.ToString());
		builder.Append(", MemoryPercent = ");
		builder.Append(MemoryPercent.ToString());
		builder.Append(", MemoryUsedBytes = ");
		builder.Append(MemoryUsedBytes.ToString());
		builder.Append(", MemoryTotalBytes = ");
		builder.Append(MemoryTotalBytes.ToString());
		builder.Append(", DriveC = ");
		builder.Append(DriveC);
		builder.Append(", DriveD = ");
		builder.Append(DriveD);
		builder.Append(", Uptime = ");
		builder.Append(Uptime.ToString());
		return true;
	}
}
internal sealed record DriveMetricsSnapshot(string Letter, bool IsAvailable, double Percent, ulong UsedBytes, ulong TotalBytes)
{
	internal static DriveMetricsSnapshot Unavailable(string letter)
	{
		return new DriveMetricsSnapshot(letter, IsAvailable: false, 0.0, 0uL, 0uL);
	}
}
public sealed class TaskbarVisibilityController : IDisposable
{
	private delegate bool EnumWindowsCallback(nint window, nint parameter);

	private const int HideWindow = 0;

	private const int ShowWithoutActivation = 8;

	private bool _hidden;

	private bool _disposed;

	public bool IsHidden => _hidden;

	public void SetHidden(bool hidden)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (hidden)
		{
			Hide();
		}
		else
		{
			Restore();
		}
	}

	public void Maintain()
	{
		if (!_disposed && _hidden)
		{
			Apply(visible: false);
		}
	}

	public void Restore()
	{
		if (!_disposed)
		{
			Apply(visible: true);
			_hidden = false;
		}
	}

	public static void RestoreAllTaskbars()
	{
		Apply(visible: true);
	}

	public static Process? StartRestoreGuard(int ownerProcessId)
	{
		string processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
		{
			return null;
		}
		return Process.Start(new ProcessStartInfo
		{
			FileName = processPath,
			Arguments = $"--taskbar-guard {ownerProcessId}",
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		});
	}

	public static void RunRestoreGuard(int ownerProcessId)
	{
		try
		{
			using Process process = Process.GetProcessById(ownerProcessId);
			process.WaitForExit();
		}
		catch
		{
		}
		RestoreAllTaskbars();
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			Restore();
			_disposed = true;
		}
	}

	private void Hide()
	{
		Apply(visible: false);
		_hidden = true;
	}

	private static void Apply(bool visible)
	{
		int command = (visible ? 8 : 0);
		EnumWindows(delegate(nint window, nint _)
		{
			string windowClass = GetWindowClass(window);
			if ((windowClass == "Shell_TrayWnd" || windowClass == "Shell_SecondaryTrayWnd") ? true : false)
			{
				ShowWindowAsync(window, command);
			}
			return true;
		}, IntPtr.Zero);
	}

	private static string GetWindowClass(nint window)
	{
		StringBuilder stringBuilder = new StringBuilder(64);
		if (GetClassNameW(window, stringBuilder, stringBuilder.Capacity) <= 0)
		{
			return string.Empty;
		}
		return stringBuilder.ToString();
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(nint window, StringBuilder className, int maximumCount);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindowAsync(nint window, int command);
}
internal sealed class VectorTextBlock : FrameworkElement
{
	private string _text = string.Empty;

	internal string Text
	{
		get
		{
			return _text;
		}
		set
		{
			string text = value ?? string.Empty;
			if (!string.Equals(_text, text, StringComparison.Ordinal))
			{
				_text = text;
				InvalidateMeasure();
				InvalidateVisual();
			}
		}
	}

	internal System.Windows.Media.FontFamily FontFamily { get; set; } = new System.Windows.Media.FontFamily("Pretendard, Malgun Gothic, Segoe UI");


	internal double FontSize { get; set; } = 12.0;


	internal FontWeight FontWeight { get; set; } = FontWeights.Normal;


	internal System.Windows.Media.Color FillColor { get; set; } = Colors.White;


	internal System.Windows.Media.Color? OutlineColor { get; set; }

	internal double OutlineThickness { get; set; }

	internal TextAlignment TextAlignment { get; set; }

	protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
	{
		FormattedText formattedText = CreateFormattedText();
		double num = Math.Ceiling(formattedText.WidthIncludingTrailingWhitespace + OutlineThickness * 2.0);
		double num2 = Math.Ceiling(formattedText.Height + OutlineThickness * 2.0);
		if (!double.IsInfinity(availableSize.Width))
		{
			num = Math.Min(num, availableSize.Width);
		}
		if (!double.IsInfinity(availableSize.Height))
		{
			num2 = Math.Min(num2, availableSize.Height);
		}
		return new System.Windows.Size(Math.Max(0.0, num), Math.Max(0.0, num2));
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		if (string.IsNullOrEmpty(Text) || base.RenderSize.Width <= 0.0 || base.RenderSize.Height <= 0.0)
		{
			return;
		}
		FormattedText formattedText = CreateFormattedText();
		double widthIncludingTrailingWhitespace = formattedText.WidthIncludingTrailingWhitespace;
		Geometry geometry = formattedText.BuildGeometry(new System.Windows.Point(TextAlignment switch
		{
			TextAlignment.Center => Math.Max(0.0, (base.RenderSize.Width - widthIncludingTrailingWhitespace) / 2.0), 
			TextAlignment.Right => Math.Max(0.0, base.RenderSize.Width - widthIncludingTrailingWhitespace), 
			_ => 0.0, 
		}, 0.0));
		SolidColorBrush solidColorBrush = new SolidColorBrush(FillColor);
		solidColorBrush.Freeze();
		System.Windows.Media.Pen pen = null;
		System.Windows.Media.Color? outlineColor = OutlineColor;
		if (outlineColor.HasValue)
		{
			System.Windows.Media.Color valueOrDefault = outlineColor.GetValueOrDefault();
			if (OutlineThickness > 0.0)
			{
				SolidColorBrush solidColorBrush2 = new SolidColorBrush(valueOrDefault);
				solidColorBrush2.Freeze();
				pen = new System.Windows.Media.Pen(solidColorBrush2, OutlineThickness)
				{
					LineJoin = PenLineJoin.Round
				};
				pen.Freeze();
			}
		}
		drawingContext.DrawGeometry(solidColorBrush, pen, geometry);
	}

	private FormattedText CreateFormattedText()
	{
		double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		return new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, System.Windows.Media.Brushes.White, pixelsPerDip);
	}
}
public sealed class WallpaperController : IWallpaperController, IAsyncDisposable
{
	private readonly DesktopHostLocator _hostLocator = new DesktopHostLocator();

	private readonly string _webViewDataDirectory;

	private readonly string _gpuSafetyDirectory;

	private readonly IAppLogger _logger;

	private readonly ISystemShortcutService _systemShortcutService;

	private readonly DispatcherTimer _reconnectTimer;

	private readonly DispatcherTimer _systemMetricsTimer;

	private readonly DispatcherTimer _recycleBinTimer;

	private readonly DispatcherTimer _addOnTimer;

	private readonly DispatcherTimer _foregroundAppTimer;

	private readonly ForegroundAppPauseDetector _foregroundAppPauseDetector = new ForegroundAppPauseDetector();

	private readonly WindowsPerformanceModeDetector _performanceModeDetector = new WindowsPerformanceModeDetector();

	private readonly ExclusiveGpuIsolationStabilizer _exclusiveGpuIsolationStabilizer = new ExclusiveGpuIsolationStabilizer(TimeSpan.FromMilliseconds(450.0));

	private readonly WindowsSystemEventMonitor _systemEventMonitor;

	private readonly Dispatcher _dispatcher;

	private readonly SystemMetricsSampler _metricsSampler = new SystemMetricsSampler();

	private readonly WindowsMediaSessionService _mediaSessionService = new WindowsMediaSessionService();

	private readonly CodexCompanionStatusReader _codexCompanionStatusReader;

	private readonly CodexCompanionPipeServer _codexCompanionPipeServer;

	private readonly CodexCompanionBridgeInstaller _codexCompanionBridgeInstaller;

	private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);

	private readonly Dictionary<string, NativeQuickSlotGroupWindow> _quickSlotWindows = new Dictionary<string, NativeQuickSlotGroupWindow>(StringComparer.OrdinalIgnoreCase);

	private IWallpaperRenderer? _renderer;

	private NativeWidgetRendererWindow? _widgetRenderer;

	private NativeMediaControlsWindow? _mediaControlsWindow;

	private DisplayInfo? _display;

	private DisplayInfo? _widgetDisplay;

	private WallpaperItem? _wallpaper;

	private PlaybackOptions? _playbackOptions;

	private WidgetSettings _widgetSettings = new WidgetSettings();

	private nint _hostHandle;

	private nint _interactiveHostHandle;

	private bool? _lastRecycleBinHasItems;

	private AudioSpectrumSampler? _audioSpectrumSampler;

	private MediaSessionSnapshot _lastMediaSnapshot = new MediaSessionSnapshot(IsAvailable: false, IsPlaying: false, "재생 중인 미디어 없음", "브라우저의 미디어 키 연동을 확인하세요", "WINDOWS 미디어", 0.0, string.Empty);

	private string _lastMediaDiagnosticKey = string.Empty;

	private CodexCompanionSnapshot _lastCodexSnapshot = CodexCompanionSnapshot.Disconnected();

	private int _mediaStatusDirty = 1;

	private int _codexStatusDirty = 1;

	private int _spectrumDirty = 1;

	private int _addOnRefreshQueued;

	private bool _manualPauseRequested;

	private ApplicationPlaybackAction _automaticAction;

	private CancellationTokenSource? _foregroundEventDebounce;

	private CancellationTokenSource? _exclusiveGpuReleaseDebounce;

	private bool _foregroundCheckInProgress;

	private bool _foregroundCheckQueued;

	private bool? _desktopOverlaysVisible;

	private bool _exclusiveGpuCompositionIsolationActive;

	private bool _auxiliaryCompositionSurfacesReleased;

	private bool _desktopWallpaperRecoveryPending;

	private bool _disposed;

	private bool _automaticPauseActive
	{
		get
		{
			return _automaticAction != ApplicationPlaybackAction.KeepRunning;
		}
		set
		{
			_automaticAction = (value ? ApplicationPlaybackAction.Pause : ApplicationPlaybackAction.KeepRunning);
		}
	}

	public WallpaperPlaybackState State { get; private set; }

	private bool IsRenderingPaused
	{
		get
		{
			if (!_manualPauseRequested)
			{
				return _automaticAction != ApplicationPlaybackAction.KeepRunning;
			}
			return true;
		}
	}

	public event EventHandler<WallpaperStateChangedEventArgs>? StateChanged;

	public event EventHandler<ExclusiveGpuIsolationChangedEventArgs>? ExclusiveGpuIsolationChanged;

	public event EventHandler<int>? QuickSlotInvoked;

	public event EventHandler<int>? QuickSlotSecondaryInvoked;

	public event EventHandler<QuickSlotLayoutChangedEventArgs>? QuickSlotLayoutChanged;

	public WallpaperController(string webViewDataDirectory, IAppLogger logger, ISystemShortcutService systemShortcutService, WindowsSystemEventMonitor systemEventMonitor)
	{
		_webViewDataDirectory = webViewDataDirectory;
		_logger = logger;
		_systemShortcutService = systemShortcutService;
		_systemEventMonitor = systemEventMonitor;
		_dispatcher = Dispatcher.CurrentDispatcher;
		string path = System.IO.Path.GetDirectoryName(_webViewDataDirectory) ?? _webViewDataDirectory;
		_gpuSafetyDirectory = System.IO.Path.Combine(path, "GpuSafety");
		_desktopWallpaperRecoveryPending = WindowsDesktopWallpaperLease.HasPendingRecovery(_gpuSafetyDirectory);
		string text = System.IO.Path.Combine(path, "Codex");
		string statusPath = System.IO.Path.Combine(text, "status.json");
		_codexCompanionStatusReader = new CodexCompanionStatusReader(statusPath);
		_codexCompanionPipeServer = new CodexCompanionPipeServer(statusPath, _logger);
		_codexCompanionBridgeInstaller = new CodexCompanionBridgeInstaller(text, statusPath, "Vellume.Codex.Status.v1");
		_reconnectTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(30.0)
		};
		_reconnectTimer.Tick += ReconnectTimerOnTick;
		_systemMetricsTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_systemMetricsTimer.Tick += SystemMetricsTimerOnTick;
		_recycleBinTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(30.0)
		};
		_recycleBinTimer.Tick += RecycleBinTimerOnTick;
		_addOnTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(30.0)
		};
		_addOnTimer.Tick += AddOnTimerOnTick;
		_foregroundAppTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(10.0)
		};
		_foregroundAppTimer.Tick += ForegroundAppTimerOnTick;
		_systemEventMonitor.ForegroundWindowChanged += SystemEventMonitor_ForegroundWindowChanged;
		_systemEventMonitor.DesktopShellChanged += SystemEventMonitor_DesktopShellChanged;
		_systemEventMonitor.RecycleBinChanged += SystemEventMonitor_RecycleBinChanged;
		_mediaSessionService.Changed += MediaSessionService_Changed;
		_codexCompanionStatusReader.Changed += CodexCompanionStatusReader_Changed;
	}

	public async Task ApplyAsync(WallpaperItem wallpaper, DisplayInfo display, PlaybackOptions options, WidgetSettings widgets, CancellationToken cancellationToken = default(CancellationToken))
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			WallpaperValidator.DetectKind(wallpaper.SourcePath);
			nint hostHandle = FindWallpaperHostOrThrow();
			IWallpaperRenderer renderer = _renderer;
			if (renderer == null || !renderer.IsReady)
			{
				await CloseWallpaperCoreAsync();
				renderer = (_renderer = new WallpaperRenderHostClient(_webViewDataDirectory));
			}
			await AttachWallpaperRendererAsync(renderer, hostHandle, display, cancellationToken);
			bool keepDesktopHandoffUntilReady = _desktopWallpaperRecoveryPending;
			if (keepDesktopHandoffUntilReady)
			{
				await renderer.SetWindowVisibleAsync(visible: false, cancellationToken);
			}
			bool captureDiagnostics = Environment.GetEnvironmentVariable("VELLUME_DIAGNOSTIC_CAPTURE") == "1";
			string diagnosticDirectory = System.IO.Path.GetDirectoryName(_webViewDataDirectory) ?? _webViewDataDirectory;
			await _logger.InfoAsync("Desktop attachment (" + (_hostLocator.IsRaisedDesktop ? "raised" : "classic") + "): " + renderer.GetAttachmentDiagnostics());
			await renderer.InitializeAndNavigateAsync(wallpaper, options, cancellationToken);
			_hostHandle = hostHandle;
			_interactiveHostHandle = _hostLocator.InteractiveHostHandle;
			_display = display;
			_wallpaper = wallpaper;
			_playbackOptions = options.Copy();
			_widgetSettings = widgets.Copy();
			_widgetDisplay = display;
			_manualPauseRequested = false;
			_automaticAction = ApplicationPlaybackAction.KeepRunning;
			_auxiliaryCompositionSurfacesReleased = false;
			_exclusiveGpuIsolationStabilizer.Reset();
			if (!HasInformationWidgets(_widgetSettings))
			{
				await CloseWidgetCoreAsync(clearDisplay: false);
			}
			else
			{
				await EnsureWidgetRendererAsync(hostHandle, display, _widgetSettings, cancellationToken);
				NativeWidgetRendererWindow widgetRenderer = _widgetRenderer ?? throw new InvalidOperationException("위젯 렌더러가 준비되지 않았습니다.");
				await FinalizeDesktopLayerOrderAsync(renderer, widgetRenderer, hostHandle, display, cancellationToken);
				await _logger.InfoAsync("Final desktop layer order: wallpaper=(" + renderer.GetAttachmentDiagnostics() + "), widgets=(" + widgetRenderer.GetAttachmentDiagnostics() + ").");
			}
			await EnsureQuickSlotWindowsAsync(_interactiveHostHandle, display, _widgetSettings, cancellationToken);
			await EnsureMediaControlsWindowAsync(_interactiveHostHandle, display, _widgetSettings, cancellationToken);
			if (keepDesktopHandoffUntilReady)
			{
				SetDesktopOverlayVisibility(visible: false);
				await renderer.SetWindowVisibleAsync(visible: true, cancellationToken);
				await Task.Delay(TimeSpan.FromMilliseconds(40.0), cancellationToken);
				if (!(await CompleteDesktopWallpaperHandoffCoreAsync()))
				{
					SetDesktopOverlayVisibility(visible: true);
				}
			}
			if (captureDiagnostics)
			{
				await Task.Delay(250, cancellationToken);
				string afterPath = System.IO.Path.Combine(diagnosticDirectory, "renderer-after-attach.png");
				await renderer.CapturePreviewAsync(afterPath, cancellationToken);
				await _logger.InfoAsync("Captured post-attachment renderer preview to '" + afterPath + "'.");
				await _logger.InfoAsync("Post-capture attachment: " + renderer.GetAttachmentDiagnostics());
			}
			UpdateTimers();
			SetState(WallpaperPlaybackState.Playing, wallpaper.Name + " 재생 중 · " + renderer.BackendDisplayName);
			await _logger.InfoAsync($"Applied '{wallpaper.SourcePath}' to {display.Id}.");
			if (_playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				_dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
				{
					ForegroundAppTimerOnTick(this, EventArgs.Empty);
				});
			}
		}
		catch (Exception ex)
		{
			Exception exception = ex;
			_manualPauseRequested = false;
			_automaticAction = ApplicationPlaybackAction.KeepRunning;
			CancelExclusiveGpuReleaseRecheck();
			_exclusiveGpuIsolationStabilizer.Reset();
			await CloseWallpaperCoreAsync();
			UpdateTimers();
			SetState(WallpaperPlaybackState.Error, exception.Message);
			await _logger.ErrorAsync("월페이퍼 적용에 실패했습니다.", exception);
			ExceptionDispatchInfo.Capture((ex as Exception) ?? throw ex).Throw();
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			_manualPauseRequested = false;
			_automaticAction = ApplicationPlaybackAction.KeepRunning;
			await CloseWallpaperCoreAsync();
			if (_auxiliaryCompositionSurfacesReleased)
			{
				await RestoreAuxiliaryCompositionSurfacesAsync();
			}
			SetDesktopOverlayVisibility(visible: true);
			SetExclusiveGpuCompositionIsolation(active: false);
			UpdateTimers();
			string text = ((_widgetRenderer == null && _quickSlotWindows.Count == 0) ? "월페이퍼를 중지했습니다" : "월페이퍼를 중지했습니다. 데스크톱 위젯은 유지됩니다");
			SetState(WallpaperPlaybackState.Stopped, text);
			await _logger.InfoAsync(text + ".");
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task PauseAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			if (_renderer != null)
			{
				_manualPauseRequested = true;
				await _renderer.PauseAsync();
				if (_widgetRenderer != null)
				{
					await _widgetRenderer.PauseAsync();
				}
				UpdateTimers();
				SetState(WallpaperPlaybackState.Paused, "월페이퍼 일시정지");
			}
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task ResumeAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			if (_renderer == null)
			{
				return;
			}
			_manualPauseRequested = false;
			if (_automaticAction != 0)
			{
				SetState(WallpaperPlaybackState.Paused, "집중 작업 앱 감지 · 렌더링 절전");
				return;
			}
			if (!_renderer.IsStaticPerformanceMode)
			{
				await _renderer.ResumeAsync();
			}
			else
			{
				await EnsureWallpaperCompositionWindowAsync(cancellationToken);
				await _renderer.ExitStaticPerformanceModeAsync();
			}
			SetDesktopOverlayVisibility(visible: true);
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.ResumeAsync();
			}
			if (_auxiliaryCompositionSurfacesReleased)
			{
				await RestoreAuxiliaryCompositionSurfacesAsync();
			}
			UpdateTimers();
			SetState(WallpaperPlaybackState.Playing, "월페이퍼 재생 중");
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task UpdatePlaybackAsync(PlaybackOptions options, CancellationToken cancellationToken = default(CancellationToken))
	{
		bool evaluateRulesAfterUpdate = false;
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			_playbackOptions = options.Copy();
			if (_renderer != null)
			{
				await _renderer.UpdatePlaybackAsync(_playbackOptions);
			}
			if (!_playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				if (_automaticAction != 0)
				{
					await SetAutomaticPlaybackCoreAsync(ApplicationPlaybackAction.KeepRunning);
				}
				else
				{
					UpdateTimers();
				}
			}
			else
			{
				UpdateTimers();
				evaluateRulesAfterUpdate = _renderer != null;
			}
		}
		finally
		{
			_operationLock.Release();
		}
		if (evaluateRulesAfterUpdate)
		{
			_dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				ForegroundAppTimerOnTick(this, EventArgs.Empty);
			});
		}
	}

	public async Task UpdateWidgetsAsync(WidgetSettings widgets, DisplayInfo? display = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			_widgetSettings = widgets.Copy();
			_widgetDisplay = display ?? _widgetDisplay ?? _display;
			if (!_widgetSettings.IsEnabled)
			{
				await CloseWidgetCoreAsync(clearDisplay: false);
				await CloseQuickSlotWindowsAsync();
				UpdateTimers();
			}
			else
			{
				if (_widgetDisplay == null)
				{
					return;
				}
				nint hostHandle = _hostHandle;
				if (hostHandle == IntPtr.Zero || !NativeMethods.IsWindow(hostHandle))
				{
					hostHandle = FindWallpaperHostOrThrow();
					_interactiveHostHandle = _hostLocator.InteractiveHostHandle;
				}
				if (!HasInformationWidgets(_widgetSettings))
				{
					await CloseWidgetCoreAsync(clearDisplay: false);
				}
				else
				{
					await EnsureWidgetRendererAsync(hostHandle, _widgetDisplay, _widgetSettings, cancellationToken);
					if (_renderer != null && _display != null && _display.Id.Equals(_widgetDisplay.Id, StringComparison.OrdinalIgnoreCase))
					{
						await FinalizeDesktopLayerOrderAsync(_renderer, _widgetRenderer, hostHandle, _display, cancellationToken);
					}
				}
				await EnsureQuickSlotWindowsAsync(_interactiveHostHandle, _widgetDisplay, _widgetSettings, cancellationToken);
				await EnsureMediaControlsWindowAsync(_interactiveHostHandle, _widgetDisplay, _widgetSettings, cancellationToken);
				if (_desktopWallpaperRecoveryPending)
				{
					SetDesktopOverlayVisibility(visible: false);
				}
				_hostHandle = hostHandle;
				UpdateTimers();
			}
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task UpdateQuickSlotRuntimeAsync(WidgetSettings widgets, CancellationToken cancellationToken = default(CancellationToken))
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			_widgetSettings = widgets.Copy();
			if (_quickSlotWindows.Count == 0)
			{
				return;
			}
			IReadOnlyList<QuickSlotItem> desktopItems = _widgetSettings.GetDesktopQuickSlots(_lastRecycleBinHasItems.GetValueOrDefault());
			foreach (KeyValuePair<string, NativeQuickSlotGroupWindow> quickSlotWindow in _quickSlotWindows)
			{
				quickSlotWindow.Deconstruct(out var key, out var value);
				string groupId = key;
				NativeQuickSlotGroupWindow nativeQuickSlotGroupWindow = value;
				QuickSlotItem[] items = (from item in desktopItems
					where item.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase)
					orderby item.Slot
					select item).ToArray();
				await nativeQuickSlotGroupWindow.UpdateRuntimeStatesAsync(items, cancellationToken);
			}
		}
		finally
		{
			_operationLock.Release();
		}
	}

	public async Task<bool> ToggleMediaPlaybackAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		bool result = default(bool);
		object obj;
		int num2;
		try
		{
			bool num = await _mediaSessionService.TogglePlaybackAsync();
			QueueAddOnRefresh(refreshMediaStatus: true, refreshCodexStatus: false, refreshSpectrum: false);
			result = num;
			return result;
		}
		catch (Exception ex)
		{
			obj = ex;
			num2 = 1;
		}
		if (num2 != 1)
		{
			return result;
		}
		Exception exception = (Exception)obj;
		await _logger.ErrorAsync("Windows 미디어 재생/일시정지 명령에 실패했습니다.", exception);
		return false;
	}

	public async Task<bool> SkipToNextMediaAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		bool result = default(bool);
		object obj;
		int num2;
		try
		{
			bool num = await _mediaSessionService.SkipNextAsync();
			QueueAddOnRefresh(refreshMediaStatus: true, refreshCodexStatus: false, refreshSpectrum: false);
			result = num;
			return result;
		}
		catch (Exception ex)
		{
			obj = ex;
			num2 = 1;
		}
		if (num2 != 1)
		{
			return result;
		}
		Exception exception = (Exception)obj;
		await _logger.ErrorAsync("Windows 미디어 다음 항목 명령에 실패했습니다.", exception);
		return false;
	}

	public async Task<bool> SkipToPreviousMediaAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		bool result = default(bool);
		object obj;
		int num2;
		try
		{
			bool num = await _mediaSessionService.SkipPreviousAsync();
			QueueAddOnRefresh(refreshMediaStatus: true, refreshCodexStatus: false, refreshSpectrum: false);
			result = num;
			return result;
		}
		catch (Exception ex)
		{
			obj = ex;
			num2 = 1;
		}
		if (num2 != 1)
		{
			return result;
		}
		Exception exception = (Exception)obj;
		await _logger.ErrorAsync("Windows 미디어 이전 항목 명령에 실패했습니다.", exception);
		return false;
	}

	public Task<string> InstallCodexCompanionBridgeAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		_codexCompanionPipeServer.Start();
		return _codexCompanionBridgeInstaller.InstallAsync(cancellationToken);
	}

	public async Task CompleteDesktopWallpaperHandoffAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		await _operationLock.WaitAsync(cancellationToken);
		try
		{
			await CompleteDesktopWallpaperHandoffCoreAsync();
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private async Task<bool> CompleteDesktopWallpaperHandoffCoreAsync()
	{
		if (!_desktopWallpaperRecoveryPending)
		{
			return false;
		}
		if (!WindowsDesktopWallpaperLease.TryRecover(_gpuSafetyDirectory))
		{
			return false;
		}
		_desktopWallpaperRecoveryPending = false;
		SetDesktopOverlayVisibility(visible: true);
		await _logger.InfoAsync("새 월페이퍼 렌더러가 준비된 뒤 Windows 정지 배경 인계를 완료했습니다.");
		return true;
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_foregroundEventDebounce?.Cancel();
		_foregroundEventDebounce?.Dispose();
		_foregroundEventDebounce = null;
		CancelExclusiveGpuReleaseRecheck();
		_systemEventMonitor.ForegroundWindowChanged -= SystemEventMonitor_ForegroundWindowChanged;
		_systemEventMonitor.DesktopShellChanged -= SystemEventMonitor_DesktopShellChanged;
		_systemEventMonitor.RecycleBinChanged -= SystemEventMonitor_RecycleBinChanged;
		_mediaSessionService.Changed -= MediaSessionService_Changed;
		_codexCompanionStatusReader.Changed -= CodexCompanionStatusReader_Changed;
		_reconnectTimer.Stop();
		_reconnectTimer.Tick -= ReconnectTimerOnTick;
		_systemMetricsTimer.Stop();
		_systemMetricsTimer.Tick -= SystemMetricsTimerOnTick;
		_recycleBinTimer.Stop();
		_recycleBinTimer.Tick -= RecycleBinTimerOnTick;
		_addOnTimer.Stop();
		_addOnTimer.Tick -= AddOnTimerOnTick;
		_foregroundAppTimer.Stop();
		_foregroundAppTimer.Tick -= ForegroundAppTimerOnTick;
		await _codexCompanionPipeServer.DisposeAsync();
		_codexCompanionStatusReader.Dispose();
		_mediaSessionService.Dispose();
		await _operationLock.WaitAsync();
		try
		{
			await CloseWallpaperCoreAsync();
			await CloseWidgetCoreAsync(clearDisplay: true);
			await CloseQuickSlotWindowsAsync();
			_hostHandle = IntPtr.Zero;
			_interactiveHostHandle = IntPtr.Zero;
		}
		finally
		{
			if (_audioSpectrumSampler != null)
			{
				_audioSpectrumSampler.SpectrumChanged -= AudioSpectrumSampler_SpectrumChanged;
				_audioSpectrumSampler.Dispose();
			}
			_audioSpectrumSampler = null;
			_metricsSampler.Dispose();
			_operationLock.Release();
			_operationLock.Dispose();
		}
	}

	private async Task EnsureWidgetRendererAsync(nint hostHandle, DisplayInfo display, WidgetSettings settings, CancellationToken cancellationToken)
	{
		try
		{
			if (_widgetRenderer == null || !_widgetRenderer.IsReady)
			{
				if (_widgetRenderer != null)
				{
					await _widgetRenderer.CloseSafelyAsync();
				}
				_widgetRenderer = new NativeWidgetRendererWindow();
				AttachWidgetRenderer(_widgetRenderer, hostHandle, display);
				await _widgetRenderer.InitializeAsync(cancellationToken);
			}
			else
			{
				AttachWidgetRenderer(_widgetRenderer, hostHandle, display);
			}
			await _widgetRenderer.ApplySettingsAsync(settings, cancellationToken);
			if (settings.ShowSystemInfo)
			{
				SystemMetricsSnapshot metrics = await Task.Run((Func<SystemMetricsSnapshot>)_metricsSampler.Sample, cancellationToken);
				await _widgetRenderer.UpdateSystemMetricsAsync(metrics);
			}
			await UpdateAddOnsCoreAsync(settings, refreshMediaStatus: true, refreshCodexStatus: true, cancellationToken);
			if (IsRenderingPaused)
			{
				await _widgetRenderer.PauseAsync();
			}
			_widgetRenderer.SetWindowVisible(_desktopOverlaysVisible.GetValueOrDefault(true));
			_widgetDisplay = display;
			await _logger.InfoAsync("Independent widget attachment: " + _widgetRenderer.GetAttachmentDiagnostics());
		}
		catch
		{
			await CloseWidgetCoreAsync(clearDisplay: false);
			throw;
		}
	}

	private async Task EnsureQuickSlotWindowsAsync(nint interactiveHostHandle, DisplayInfo display, WidgetSettings settings, CancellationToken cancellationToken)
	{
		bool flag = settings.IsEnabled && settings.ShowQuickSlots && settings.SystemShortcuts.Any((SystemShortcutSetting item) => item.IsVisible && item.Kind == SystemShortcutKind.RecycleBin);
		bool flag2 = flag && _systemShortcutService.RecycleBinHasItems();
		_lastRecycleBinHasItems = (flag ? new bool?(flag2) : null);
		IReadOnlyList<QuickSlotItem> desktopItems = settings.GetDesktopQuickSlots(flag2);
		if (!settings.IsEnabled || !settings.ShowQuickSlots || desktopItems.Count == 0 || interactiveHostHandle == IntPtr.Zero)
		{
			await CloseQuickSlotWindowsAsync();
			return;
		}
		Dictionary<string, QuickSlotGroup> groups = settings.QuickSlotGroups.ToDictionary<QuickSlotGroup, string>((QuickSlotGroup group) => group.Id, StringComparer.OrdinalIgnoreCase);
		HashSet<string> desiredGroupIds = desktopItems.Select((QuickSlotItem item) => item.GroupId).Where(groups.ContainsKey).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		string[] array = _quickSlotWindows.Keys.Where((string id) => !desiredGroupIds.Contains(id)).ToArray();
		foreach (string groupId2 in array)
		{
			await CloseQuickSlotWindowAsync(groupId2);
		}
		foreach (string groupId in desiredGroupIds)
		{
			QuickSlotGroup group2 = groups[groupId];
			QuickSlotItem[] items = (from item in desktopItems
				where item.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase)
				orderby item.Slot
				select item).ToArray();
			if (!_quickSlotWindows.TryGetValue(groupId, out NativeQuickSlotGroupWindow renderer) || !renderer.IsReady)
			{
				if (renderer != null)
				{
					await CloseQuickSlotWindowAsync(groupId);
				}
				renderer = new NativeQuickSlotGroupWindow(groupId);
				renderer.SlotInvoked += QuickSlotWindowOnSlotInvoked;
				renderer.SlotSecondaryInvoked += QuickSlotWindowOnSlotSecondaryInvoked;
				renderer.LayoutChanged += QuickSlotWindowOnLayoutChanged;
				_quickSlotWindows[groupId] = renderer;
				renderer.AttachTo(interactiveHostHandle, display, group2, items.Length, settings.SnapQuickSlotsToGrid, settings.QuickSlotGridSize);
				await renderer.InitializeAsync(cancellationToken);
			}
			else
			{
				renderer.AttachTo(interactiveHostHandle, display, group2, items.Length, settings.SnapQuickSlotsToGrid, settings.QuickSlotGridSize);
			}
			await renderer.RenderAsync(group2, items, cancellationToken);
			renderer.SetWindowVisible(_desktopOverlaysVisible.GetValueOrDefault(true));
			await _logger.InfoAsync("Quick-slot attachment: " + renderer.GetAttachmentDiagnostics());
			renderer = null;
		}
		_interactiveHostHandle = interactiveHostHandle;
		_widgetDisplay = display;
		await _logger.InfoAsync($"Attached {_quickSlotWindows.Count} interactive quick-slot group(s).");
	}

	private async Task EnsureMediaControlsWindowAsync(nint interactiveHostHandle, DisplayInfo display, WidgetSettings settings, CancellationToken cancellationToken)
	{
		if (!settings.IsEnabled || !settings.ShowMediaStatus || interactiveHostHandle == IntPtr.Zero)
		{
			await CloseMediaControlsWindowAsync();
			return;
		}
		NativeMediaControlsWindow controls = _mediaControlsWindow;
		if (controls == null || !controls.IsReady)
		{
			await CloseMediaControlsWindowAsync();
			controls = new NativeMediaControlsWindow();
			controls.CommandRequested += MediaControlsOnCommandRequested;
			_mediaControlsWindow = controls;
			controls.AttachTo(interactiveHostHandle, display, settings.MediaStatusSide);
			await controls.InitializeAsync(cancellationToken);
		}
		else
		{
			controls.AttachTo(interactiveHostHandle, display, settings.MediaStatusSide);
		}
		await controls.UpdateAsync(_lastMediaSnapshot.IsAvailable, _lastMediaSnapshot.IsPlaying, cancellationToken);
		controls.SetWindowVisible(_desktopOverlaysVisible.GetValueOrDefault(true));
	}

	private async Task CloseMediaControlsWindowAsync()
	{
		NativeMediaControlsWindow mediaControlsWindow = _mediaControlsWindow;
		_mediaControlsWindow = null;
		if (mediaControlsWindow != null)
		{
			mediaControlsWindow.CommandRequested -= MediaControlsOnCommandRequested;
			await mediaControlsWindow.CloseSafelyAsync();
		}
	}

	private async void MediaControlsOnCommandRequested(object? sender, MediaPlaybackCommand command)
	{
		switch (command)
		{
		case MediaPlaybackCommand.Previous:
			await SkipToPreviousMediaAsync();
			break;
		case MediaPlaybackCommand.Toggle:
			await ToggleMediaPlaybackAsync();
			break;
		case MediaPlaybackCommand.Next:
			await SkipToNextMediaAsync();
			break;
		}
	}

	private async Task CloseQuickSlotWindowsAsync()
	{
		string[] array = _quickSlotWindows.Keys.ToArray();
		foreach (string groupId in array)
		{
			await CloseQuickSlotWindowAsync(groupId);
		}
	}

	private async Task CloseQuickSlotWindowAsync(string groupId)
	{
		if (_quickSlotWindows.Remove(groupId, out NativeQuickSlotGroupWindow value))
		{
			value.SlotInvoked -= QuickSlotWindowOnSlotInvoked;
			value.SlotSecondaryInvoked -= QuickSlotWindowOnSlotSecondaryInvoked;
			value.LayoutChanged -= QuickSlotWindowOnLayoutChanged;
			await value.CloseSafelyAsync();
		}
	}

	private void QuickSlotWindowOnSlotInvoked(object? sender, int slot)
	{
		this.QuickSlotInvoked?.Invoke(this, slot);
	}

	private void QuickSlotWindowOnSlotSecondaryInvoked(object? sender, int slot)
	{
		this.QuickSlotSecondaryInvoked?.Invoke(this, slot);
	}

	private void QuickSlotWindowOnLayoutChanged(object? sender, QuickSlotLayoutChangedEventArgs eventArgs)
	{
		QuickSlotLayoutChangedEventArgs eventArgs2 = eventArgs;
		QuickSlotGroup quickSlotGroup = _widgetSettings.QuickSlotGroups.FirstOrDefault((QuickSlotGroup candidate) => candidate.Id.Equals(eventArgs2.GroupId, StringComparison.OrdinalIgnoreCase));
		if (quickSlotGroup != null)
		{
			quickSlotGroup.X = eventArgs2.X;
			quickSlotGroup.Y = eventArgs2.Y;
		}
		this.QuickSlotLayoutChanged?.Invoke(this, eventArgs2);
	}

	private async Task CloseWallpaperCoreAsync()
	{
		IWallpaperRenderer renderer = _renderer;
		_renderer = null;
		_display = null;
		_wallpaper = null;
		_playbackOptions = null;
		if (renderer != null)
		{
			await renderer.CloseSafelyAsync();
		}
	}

	private async Task CloseWidgetCoreAsync(bool clearDisplay)
	{
		_systemMetricsTimer.Stop();
		_addOnTimer.Stop();
		await CloseMediaControlsWindowAsync();
		NativeWidgetRendererWindow widgetRenderer = _widgetRenderer;
		_widgetRenderer = null;
		if (clearDisplay)
		{
			_widgetDisplay = null;
		}
		if (widgetRenderer != null)
		{
			await widgetRenderer.CloseSafelyAsync();
		}
	}

	private async void ReconnectTimerOnTick(object? sender, EventArgs eventArgs)
	{
		bool wallpaperNeedsReconnect = _renderer != null && (!_renderer.IsStaticPerformanceMode || _renderer.HasCompositionWindow) && !_renderer.IsAttachedTo(_hostHandle);
		bool widgetNeedsReconnect = _widgetRenderer != null && !_widgetRenderer.IsAttachedTo(_hostHandle);
		bool mediaControlsNeedReconnect2 = _mediaControlsWindow != null && !_mediaControlsWindow.IsAttachedTo(_interactiveHostHandle);
		bool quickSlotsNeedReconnect2 = _quickSlotWindows.Values.Any((NativeQuickSlotGroupWindow window) => !window.IsAttachedTo(_interactiveHostHandle));
		bool flag = !wallpaperNeedsReconnect && !widgetNeedsReconnect && !mediaControlsNeedReconnect2 && !quickSlotsNeedReconnect2;
		if (!flag)
		{
			flag = !(await _operationLock.WaitAsync(0));
		}
		if (flag)
		{
			return;
		}
		try
		{
			wallpaperNeedsReconnect = _renderer != null && (!_renderer.IsStaticPerformanceMode || _renderer.HasCompositionWindow) && !_renderer.IsAttachedTo(_hostHandle);
			widgetNeedsReconnect = _widgetRenderer != null && !_widgetRenderer.IsAttachedTo(_hostHandle);
			mediaControlsNeedReconnect2 = _mediaControlsWindow != null && !_mediaControlsWindow.IsAttachedTo(_interactiveHostHandle);
			quickSlotsNeedReconnect2 = _quickSlotWindows.Values.Any((NativeQuickSlotGroupWindow window) => !window.IsAttachedTo(_interactiveHostHandle));
			if (!wallpaperNeedsReconnect && !widgetNeedsReconnect && !mediaControlsNeedReconnect2 && !quickSlotsNeedReconnect2)
			{
				return;
			}
			nint replacementHost = _hostLocator.FindWallpaperHost();
			if (replacementHost == IntPtr.Zero)
			{
				return;
			}
			if (wallpaperNeedsReconnect && _renderer != null && _display != null && _wallpaper != null && _playbackOptions != null)
			{
				bool wasPaused = State == WallpaperPlaybackState.Paused;
				if (_renderer.IsReady)
				{
					await AttachWallpaperRendererAsync(_renderer, replacementHost, _display, CancellationToken.None);
				}
				else
				{
					await _renderer.CloseSafelyAsync();
					_renderer = new WallpaperRenderHostClient(_webViewDataDirectory);
					await AttachWallpaperRendererAsync(_renderer, replacementHost, _display, CancellationToken.None);
					await _renderer.InitializeAndNavigateAsync(_wallpaper, _playbackOptions, CancellationToken.None);
					if (wasPaused)
					{
						await _renderer.PauseAsync();
					}
				}
			}
			if (_widgetRenderer != null && _widgetDisplay != null && HasInformationWidgets(_widgetSettings) && (widgetNeedsReconnect || wallpaperNeedsReconnect))
			{
				await EnsureWidgetRendererAsync(replacementHost, _widgetDisplay, _widgetSettings, CancellationToken.None);
			}
			if (_renderer != null && _widgetRenderer != null && _display != null && _widgetDisplay != null && _display.Id.Equals(_widgetDisplay.Id, StringComparison.OrdinalIgnoreCase))
			{
				await FinalizeDesktopLayerOrderAsync(_renderer, _widgetRenderer, replacementHost, _display, CancellationToken.None);
			}
			_hostHandle = replacementHost;
			_interactiveHostHandle = _hostLocator.InteractiveHostHandle;
			if (_widgetDisplay != null && (quickSlotsNeedReconnect2 || mediaControlsNeedReconnect2 || wallpaperNeedsReconnect || widgetNeedsReconnect))
			{
				await EnsureQuickSlotWindowsAsync(_interactiveHostHandle, _widgetDisplay, _widgetSettings, CancellationToken.None);
				await EnsureMediaControlsWindowAsync(_interactiveHostHandle, _widgetDisplay, _widgetSettings, CancellationToken.None);
			}
			await _logger.InfoAsync("Reattached independent desktop layers after WorkerW recreation.");
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("Desktop layer reattachment failed.", exception);
			SetState(WallpaperPlaybackState.Error, "Explorer changed and Vellume could not reattach. Use Apply to retry.");
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private void SystemEventMonitor_DesktopShellChanged(object? sender, EventArgs eventArgs)
	{
		ReconnectTimerOnTick(sender, eventArgs);
	}

	private void SystemEventMonitor_RecycleBinChanged(object? sender, EventArgs eventArgs)
	{
		RecycleBinTimerOnTick(sender, eventArgs);
	}

	private async void RecycleBinTimerOnTick(object? sender, EventArgs eventArgs)
	{
		bool flag = _widgetDisplay == null || !_widgetSettings.IsEnabled || !_widgetSettings.ShowQuickSlots || !_widgetSettings.SystemShortcuts.Any((SystemShortcutSetting item) => item.IsVisible && item.Kind == SystemShortcutKind.RecycleBin);
		if (!flag)
		{
			flag = !(await _operationLock.WaitAsync(0));
		}
		if (flag)
		{
			return;
		}
		try
		{
			bool currentState = await Task.Run((Func<bool>)_systemShortcutService.RecycleBinHasItems);
			bool num = !_lastRecycleBinHasItems.HasValue || _lastRecycleBinHasItems.Value != currentState;
			_lastRecycleBinHasItems = currentState;
			if (!num)
			{
				return;
			}
			await EnsureQuickSlotWindowsAsync(_interactiveHostHandle, _widgetDisplay, _widgetSettings, CancellationToken.None);
			await _logger.InfoAsync("Recycle Bin icon changed to " + (currentState ? "full" : "empty") + ".");
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("Recycle Bin icon refresh failed.", exception);
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private async void SystemMetricsTimerOnTick(object? sender, EventArgs eventArgs)
	{
		bool flag = _widgetRenderer == null || !_widgetSettings.IsEnabled || !_widgetSettings.ShowSystemInfo;
		if (!flag)
		{
			flag = !(await _operationLock.WaitAsync(0));
		}
		if (flag)
		{
			return;
		}
		try
		{
			if (_widgetRenderer != null && _widgetSettings.IsEnabled && _widgetSettings.ShowSystemInfo)
			{
				SystemMetricsSnapshot metrics = await Task.Run((Func<SystemMetricsSnapshot>)_metricsSampler.Sample);
				await _widgetRenderer.UpdateSystemMetricsAsync(metrics);
				if (Environment.GetEnvironmentVariable("VELLUME_DIAGNOSTIC_CAPTURE") == "1")
				{
					await _logger.InfoAsync($"Diagnostic metrics: CPU={metrics.CpuPercent:0.0}%, GPU={((!metrics.GpuPercent.HasValue) ? "N/A" : $"{metrics.GpuPercent:0.0}%")}, C:={(metrics.DriveC.IsAvailable ? $"{metrics.DriveC.Percent:0.0}%" : "N/A")}, D:={(metrics.DriveD.IsAvailable ? $"{metrics.DriveD.Percent:0.0}%" : "N/A")}.");
				}
			}
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("System widget metrics update failed.", exception);
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private async void AddOnTimerOnTick(object? sender, EventArgs eventArgs)
	{
		bool flag = _widgetRenderer == null || !_widgetSettings.IsEnabled || (!_widgetSettings.ShowMediaStatus && !_widgetSettings.ShowCodexCompanion);
		if (!flag)
		{
			flag = !(await _operationLock.WaitAsync(0));
		}
		if (flag)
		{
			return;
		}
		try
		{
			await UpdateAddOnsCoreAsync(_widgetSettings, refreshMediaStatus: true, refreshCodexStatus: true, CancellationToken.None);
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("Desktop add-on status update failed.", exception);
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private void MediaSessionService_Changed(object? sender, EventArgs eventArgs)
	{
		QueueAddOnRefresh(refreshMediaStatus: true, refreshCodexStatus: false, refreshSpectrum: false);
	}

	private void CodexCompanionStatusReader_Changed(object? sender, EventArgs eventArgs)
	{
		QueueAddOnRefresh(refreshMediaStatus: false, refreshCodexStatus: true, refreshSpectrum: false);
	}

	private void AudioSpectrumSampler_SpectrumChanged(object? sender, EventArgs eventArgs)
	{
		QueueAddOnRefresh(refreshMediaStatus: false, refreshCodexStatus: false, refreshSpectrum: true);
	}

	private void QueueAddOnRefresh(bool refreshMediaStatus, bool refreshCodexStatus, bool refreshSpectrum)
	{
		if (_disposed)
		{
			return;
		}
		if (refreshMediaStatus)
		{
			Interlocked.Exchange(ref _mediaStatusDirty, 1);
		}
		if (refreshCodexStatus)
		{
			Interlocked.Exchange(ref _codexStatusDirty, 1);
		}
		if (refreshSpectrum)
		{
			Interlocked.Exchange(ref _spectrumDirty, 1);
		}
		if (Interlocked.Exchange(ref _addOnRefreshQueued, 1) == 0)
		{
			_dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				RefreshAddOnsFromEventsAsync();
			});
		}
	}

	private async Task RefreshAddOnsFromEventsAsync()
	{
		_ = 2;
		try
		{
			bool refreshMediaStatus = Interlocked.Exchange(ref _mediaStatusDirty, 0) != 0;
			bool refreshCodexStatus = Interlocked.Exchange(ref _codexStatusDirty, 0) != 0;
			Interlocked.Exchange(ref _spectrumDirty, 0);
			if (_disposed || IsRenderingPaused || _widgetRenderer == null || !_widgetSettings.IsEnabled || (!_widgetSettings.ShowMediaStatus && !_widgetSettings.ShowCodexCompanion))
			{
				return;
			}
			await _operationLock.WaitAsync();
			try
			{
				await UpdateAddOnsCoreAsync(_widgetSettings, refreshMediaStatus, refreshCodexStatus, CancellationToken.None);
			}
			finally
			{
				_operationLock.Release();
			}
		}
		catch (ObjectDisposedException) when (_disposed)
		{
		}
		catch (Exception exception)
		{
			await _logger.ErrorAsync("이벤트 기반 데스크톱 애드온 갱신에 실패했습니다.", exception);
		}
		finally
		{
			Interlocked.Exchange(ref _addOnRefreshQueued, 0);
			if (!_disposed && (Volatile.Read(ref _mediaStatusDirty) != 0 || Volatile.Read(ref _codexStatusDirty) != 0 || Volatile.Read(ref _spectrumDirty) != 0))
			{
				QueueAddOnRefresh(refreshMediaStatus: false, refreshCodexStatus: false, refreshSpectrum: false);
			}
		}
	}

	private async void ForegroundAppTimerOnTick(object? sender, EventArgs eventArgs)
	{
		if (!_disposed && _renderer != null)
		{
			PlaybackOptions? playbackOptions = _playbackOptions;
			if (playbackOptions != null && playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				_foregroundCheckQueued = true;
				if (_foregroundCheckInProgress)
				{
					return;
				}
				_foregroundCheckInProgress = true;
				try
				{
					try
					{
						do
						{
							_foregroundCheckQueued = false;
							await EvaluateForegroundPauseAsync();
						}
						while (_foregroundCheckQueued && !_disposed);
					}
					catch (Exception exception)
					{
						await _logger.ErrorAsync("전경 앱의 창 상태를 확인하지 못했습니다.", exception);
					}
					return;
				}
				finally
				{
					_foregroundCheckInProgress = false;
				}
			}
		}
		_foregroundCheckQueued = false;
	}

	private void SystemEventMonitor_ForegroundWindowChanged(object? sender, EventArgs eventArgs)
	{
		if (!_disposed && _renderer != null)
		{
			PlaybackOptions? playbackOptions = _playbackOptions;
			if (playbackOptions != null && playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				_performanceModeDetector.Invalidate();
				_foregroundEventDebounce?.Cancel();
				_foregroundEventDebounce?.Dispose();
				_foregroundEventDebounce = new CancellationTokenSource();
				EvaluateForegroundAfterEventAsync(_foregroundEventDebounce.Token);
			}
		}
	}

	private async Task EvaluateForegroundAfterEventAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(120, cancellationToken);
			ForegroundAppTimerOnTick(this, EventArgs.Empty);
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task EvaluateForegroundPauseAsync()
	{
		PlaybackOptions playbackOptions = _playbackOptions;
		if (_renderer == null || playbackOptions == null || !playbackOptions.PauseWhenForegroundAppIsMaximized)
		{
			return;
		}
		ForegroundWindowSnapshot foregroundSnapshot = _foregroundAppPauseDetector.Capture();
		ExclusiveGpuProcessSnapshot exclusiveGpuProcessSnapshot = _performanceModeDetector.CaptureExclusiveGpuApplication(playbackOptions.ApplicationRules);
		ApplicationPlaybackAction applicationPlaybackAction = ResolveRunningRuleAction(exclusiveGpuProcessSnapshot, playbackOptions);
		ExclusiveGpuProcessSnapshot rawSnapshot = ((applicationPlaybackAction == ApplicationPlaybackAction.StopFreeMemory) ? exclusiveGpuProcessSnapshot : default(ExclusiveGpuProcessSnapshot));
		ExclusiveGpuIsolationDecision exclusiveGpuIsolationDecision = _exclusiveGpuIsolationStabilizer.Update(rawSnapshot, _renderer.IsStaticPerformanceMode, DateTimeOffset.UtcNow);
		ScheduleExclusiveGpuReleaseRecheck(exclusiveGpuIsolationDecision.RecheckAfter);
		ExclusiveGpuProcessSnapshot stopProcess = exclusiveGpuIsolationDecision.Snapshot;
		if (stopProcess.IsRunning)
		{
			applicationPlaybackAction = ApplicationPlaybackAction.StopFreeMemory;
		}
		ApplicationPlaybackAction val = ForegroundAppPausePolicy.ResolveAction(foregroundSnapshot, playbackOptions);
		ApplicationPlaybackAction requestedAction = (ApplicationPlaybackAction)Math.Max((int)applicationPlaybackAction, (int)val);
		if (requestedAction == ApplicationPlaybackAction.StopFreeMemory && !stopProcess.IsRunning && foregroundSnapshot.IsValid)
		{
			stopProcess = new ExclusiveGpuProcessSnapshot(IsRunning: true, checked((int)foregroundSnapshot.ProcessId), foregroundSnapshot.ProcessName);
		}
		bool flag = requestedAction == ApplicationPlaybackAction.StopFreeMemory;
		bool flag2 = flag != _renderer.IsStaticPerformanceMode;
		bool flag3 = (flag && (!_auxiliaryCompositionSurfacesReleased || _renderer.HasCompositionWindow)) || (!flag && _auxiliaryCompositionSurfacesReleased);
		if (requestedAction == _automaticAction && !flag2 && !flag3)
		{
			return;
		}
		await _operationLock.WaitAsync();
		try
		{
			if (_renderer != null)
			{
				PlaybackOptions? playbackOptions2 = _playbackOptions;
				if (playbackOptions2 != null && playbackOptions2.PauseWhenForegroundAppIsMaximized)
				{
					await SetAutomaticPlaybackCoreAsync(requestedAction, foregroundSnapshot, stopProcess);
				}
			}
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private async Task SetAutomaticPlaybackCoreAsync(ApplicationPlaybackAction requestedAction, ForegroundWindowSnapshot? trigger = null, ExclusiveGpuProcessSnapshot stopProcess = default(ExclusiveGpuProcessSnapshot))
	{
		if (requestedAction == ApplicationPlaybackAction.StopFreeMemory)
		{
			if (_renderer != null && !_renderer.IsStaticPerformanceMode)
			{
				await _renderer.EnterStaticPerformanceModeAsync();
			}
			await ReleaseAuxiliaryCompositionSurfacesAsync();
			_automaticAction = ApplicationPlaybackAction.StopFreeMemory;
			SetExclusiveGpuCompositionIsolation(active: true, stopProcess);
			UpdateTimers();
			SetState(WallpaperPlaybackState.Paused, (_renderer?.IsDesktopWallpaperIsolationActive ?? false) ? "앱 규칙 · 완전 정지(메모리 해제)" : "앱 규칙 · GPU 안전 정적 화면");
			await _logger.InfoAsync("애플리케이션 규칙에 따라 정적 프레임을 Windows 배경으로 넘기고 RenderHost를 종료했습니다." + FormatForegroundPauseTrigger(trigger, stopProcess));
			return;
		}
		if (_renderer?.IsStaticPerformanceMode ?? false)
		{
			await EnsureWallpaperCompositionWindowAsync(CancellationToken.None);
			await _renderer.ExitStaticPerformanceModeAsync();
		}
		if (_auxiliaryCompositionSurfacesReleased)
		{
			await RestoreAuxiliaryCompositionSurfacesAsync();
		}
		SetDesktopOverlayVisibility(visible: true);
		SetExclusiveGpuCompositionIsolation(active: false);
		_automaticAction = requestedAction;
		if (requestedAction == ApplicationPlaybackAction.Pause)
		{
			if (_renderer != null)
			{
				await _renderer.PauseAsync();
			}
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.PauseAsync();
			}
			UpdateTimers();
			SetState(WallpaperPlaybackState.Paused, "집중 작업 감지 · 월페이퍼 일시정지");
			await _logger.InfoAsync("전경 애플리케이션에 따라 월페이퍼를 일시정지했습니다." + FormatForegroundPauseTrigger(trigger));
			return;
		}
		if (!_manualPauseRequested)
		{
			if (_renderer != null)
			{
				await _renderer.ResumeAsync();
			}
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.ResumeAsync();
			}
			if (_renderer != null && _widgetRenderer != null && _display != null && _widgetDisplay != null && _display.Id.Equals(_widgetDisplay.Id, StringComparison.OrdinalIgnoreCase))
			{
				await FinalizeDesktopLayerOrderAsync(_renderer, _widgetRenderer, _hostHandle, _display, CancellationToken.None);
			}
			SetState(WallpaperPlaybackState.Playing, "월페이퍼 재생 중");
		}
		else
		{
			if (_renderer != null)
			{
				await _renderer.PauseAsync();
			}
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.PauseAsync();
			}
			SetState(WallpaperPlaybackState.Paused, "수동 일시정지 유지");
		}
		UpdateTimers();
		await _logger.InfoAsync(_manualPauseRequested ? "자동 규칙은 해제됐지만 수동 일시정지를 유지합니다." : "자동 규칙이 해제되어 RenderHost와 월페이퍼를 복원했습니다.");
	}

	private static ApplicationPlaybackAction ResolveRunningRuleAction(ExclusiveGpuProcessSnapshot process, PlaybackOptions options)
	{
		if (!process.IsRunning)
		{
			return ApplicationPlaybackAction.KeepRunning;
		}
		string processName = WindowsPerformanceModeDetector.NormalizeProcessName(process.ProcessName);
		return (from rule in options.ApplicationRules ?? new List<ApplicationPlaybackRule>()
			where rule != null && rule.Condition == ApplicationRuleCondition.IsRunning && WindowsPerformanceModeDetector.NormalizeProcessName(rule.ProcessName).Equals(processName, StringComparison.OrdinalIgnoreCase)
			select rule.Action).DefaultIfEmpty(ApplicationPlaybackAction.KeepRunning).Max();
	}

	private async Task EvaluateForegroundPauseLegacyAsync()
	{
		ForegroundWindowSnapshot foregroundSnapshot = _foregroundAppPauseDetector.Capture();
		ExclusiveGpuProcessSnapshot rawSnapshot = _performanceModeDetector.CaptureExclusiveGpuApplication();
		ExclusiveGpuIsolationDecision exclusiveGpuIsolationDecision = _exclusiveGpuIsolationStabilizer.Update(rawSnapshot, _renderer?.IsStaticPerformanceMode ?? false, DateTimeOffset.UtcNow);
		ScheduleExclusiveGpuReleaseRecheck(exclusiveGpuIsolationDecision.RecheckAfter);
		ExclusiveGpuProcessSnapshot exclusiveGpuProcess = exclusiveGpuIsolationDecision.Snapshot;
		bool flag = _performanceModeDetector.IsExclusiveGpuApplicationForeground();
		bool flag2 = !rawSnapshot.IsRunning && exclusiveGpuProcess.IsRunning;
		bool isolateDesktopComposition = flag || flag2;
		SetDesktopOverlayVisibility(!isolateDesktopComposition);
		SetExclusiveGpuCompositionIsolation(isolateDesktopComposition, exclusiveGpuProcess);
		bool shouldPause = exclusiveGpuProcess.IsRunning || ForegroundAppPausePolicy.ShouldPause(foregroundSnapshot);
		bool flag3 = _renderer != null && ((exclusiveGpuProcess.IsRunning && !_renderer.IsStaticPerformanceMode) || (!shouldPause && _renderer.IsStaticPerformanceMode));
		bool flag4 = (isolateDesktopComposition && (!_auxiliaryCompositionSurfacesReleased || (_renderer?.HasCompositionWindow ?? false))) || (!isolateDesktopComposition && _auxiliaryCompositionSurfacesReleased);
		bool flag5 = shouldPause == _automaticPauseActive && !flag3 && !flag4;
		if (!flag5)
		{
			flag5 = !(await _operationLock.WaitAsync(0));
		}
		if (flag5)
		{
			return;
		}
		try
		{
			if (_renderer != null)
			{
				PlaybackOptions? playbackOptions = _playbackOptions;
				if (playbackOptions != null && playbackOptions.PauseWhenForegroundAppIsMaximized)
				{
					await SetAutomaticPauseCoreAsync(shouldPause, foregroundSnapshot, exclusiveGpuProcess, isolateDesktopComposition);
				}
			}
		}
		finally
		{
			_operationLock.Release();
		}
	}

	private async Task SetAutomaticPauseCoreAsync(bool shouldPause, ForegroundWindowSnapshot? trigger = null, ExclusiveGpuProcessSnapshot exclusiveGpuProcess = default(ExclusiveGpuProcessSnapshot), bool isolateDesktopComposition = false)
	{
		if (isolateDesktopComposition && exclusiveGpuProcess.IsRunning && _renderer != null && !_renderer.IsStaticPerformanceMode)
		{
			await _renderer.EnterStaticPerformanceModeAsync();
		}
		if (isolateDesktopComposition)
		{
			await ReleaseAuxiliaryCompositionSurfacesAsync();
		}
		else if (shouldPause && _auxiliaryCompositionSurfacesReleased)
		{
			await RestoreAuxiliaryCompositionSurfacesAsync();
		}
		if (_automaticPauseActive == shouldPause)
		{
			if (shouldPause && _renderer != null && exclusiveGpuProcess.IsRunning && !_renderer.IsStaticPerformanceMode)
			{
				await _renderer.EnterStaticPerformanceModeAsync();
				bool isDesktopWallpaperIsolationActive = _renderer.IsDesktopWallpaperIsolationActive;
				SetState(WallpaperPlaybackState.Paused, isDesktopWallpaperIsolationActive ? "Unreal 감지 · Windows 배경 격리" : "Unreal 감지 · GPU 안전 정적 프레임");
				await _logger.InfoAsync(isDesktopWallpaperIsolationActive ? "Unreal 완전 격리 모드로 전환했습니다. 정적 프레임을 Windows 시스템 배경으로 넘기고 Vellume의 전체 화면 합성 창을 숨겼습니다." : "Unreal GPU 안전 모드로 전환했지만 Windows 시스템 배경 전환에 실패해 GDI 정적 프레임 폴백을 사용합니다.");
			}
			return;
		}
		_automaticPauseActive = shouldPause;
		if (shouldPause)
		{
			if (_renderer != null)
			{
				if (!exclusiveGpuProcess.IsRunning)
				{
					await _renderer.PauseAsync();
				}
				else
				{
					await _renderer.EnterStaticPerformanceModeAsync();
				}
			}
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.PauseAsync();
			}
			UpdateTimers();
			if (_renderer != null)
			{
				SetState(WallpaperPlaybackState.Paused, (!exclusiveGpuProcess.IsRunning) ? "집중 작업 앱 감지 · 렌더링 절전" : (_renderer.IsDesktopWallpaperIsolationActive ? "Unreal 감지 · Windows 배경 격리" : "Unreal 감지 · GPU 안전 정적 프레임"));
			}
			await _logger.InfoAsync(((exclusiveGpuProcess.IsRunning && (_renderer?.IsDesktopWallpaperIsolationActive ?? false)) ? "Unreal을 감지해 정지 프레임을 Windows 시스템 배경으로 넘기고 Vellume의 전체 화면 창을 합성에서 제거했습니다." : "최대화·전체화면 또는 고부하 전경 앱을 감지해 데스크톱 렌더링을 일시정지했습니다.") + FormatForegroundPauseTrigger(trigger, exclusiveGpuProcess));
			return;
		}
		SetDesktopOverlayVisibility(visible: true);
		if (!_manualPauseRequested)
		{
			if (_renderer != null)
			{
				if (!_renderer.IsStaticPerformanceMode)
				{
					await _renderer.ResumeAsync();
				}
				else
				{
					await EnsureWallpaperCompositionWindowAsync(CancellationToken.None);
					await _renderer.ExitStaticPerformanceModeAsync();
				}
			}
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.ResumeAsync();
			}
			if (_auxiliaryCompositionSurfacesReleased)
			{
				await RestoreAuxiliaryCompositionSurfacesAsync();
			}
			if (_renderer != null && _widgetRenderer != null && _display != null && _widgetDisplay != null && _display.Id.Equals(_widgetDisplay.Id, StringComparison.OrdinalIgnoreCase))
			{
				await FinalizeDesktopLayerOrderAsync(_renderer, _widgetRenderer, _hostHandle, _display, CancellationToken.None);
			}
			if (_renderer != null)
			{
				SetState(WallpaperPlaybackState.Playing, "월페이퍼 재생 중");
			}
		}
		UpdateTimers();
		await _logger.InfoAsync(_manualPauseRequested ? "집중 작업 앱 절전이 끝났지만 수동 일시정지 상태를 유지합니다." : "집중 작업 앱 절전이 끝나 데스크톱 렌더링을 재개했습니다.");
	}

	private async Task ReleaseAuxiliaryCompositionSurfacesAsync()
	{
		if (_auxiliaryCompositionSurfacesReleased)
		{
			return;
		}
		IWallpaperRenderer? renderer = _renderer;
		if (renderer != null && renderer.IsDesktopWallpaperIsolationActive)
		{
			if (_widgetRenderer != null)
			{
				await _widgetRenderer.PauseAsync();
				_widgetRenderer.SetWindowVisible(!_widgetRenderer.UsesFullDisplayCompositionSurface);
			}
			await CloseQuickSlotWindowsAsync();
			await CloseMediaControlsWindowAsync();
			if (!(await _renderer.DestroyCompositionWindowForIsolationAsync(CancellationToken.None)))
			{
				throw new InvalidOperationException("Windows 안전 배경을 유지한 채 RenderHost를 종료하지 못했습니다.");
			}
			_auxiliaryCompositionSurfacesReleased = true;
			UpdateTimers();
			await _logger.InfoAsync("완전 정지 규칙이 GPU 월페이퍼와 상호작용 오버레이를 해제했습니다. bounded 소프트웨어 위젯 HWND는 숨김·정지 상태로 유지합니다.");
		}
	}

	private async Task RestoreAuxiliaryCompositionSurfacesAsync()
	{
		if (!_auxiliaryCompositionSurfacesReleased)
		{
			return;
		}
		_auxiliaryCompositionSurfacesReleased = false;
		if (_widgetDisplay != null)
		{
			nint hostHandle = (_hostHandle = FindWallpaperHostOrThrow());
			_interactiveHostHandle = _hostLocator.InteractiveHostHandle;
			if (HasInformationWidgets(_widgetSettings) && _widgetRenderer != null && _widgetRenderer.IsReady)
			{
				AttachWidgetRenderer(_widgetRenderer, hostHandle, _widgetDisplay);
				await _widgetRenderer.ResumeAsync();
			}
			else if (HasInformationWidgets(_widgetSettings))
			{
				await EnsureWidgetRendererAsync(hostHandle, _widgetDisplay, _widgetSettings, CancellationToken.None);
			}
			await EnsureQuickSlotWindowsAsync(_interactiveHostHandle, _widgetDisplay, _widgetSettings, CancellationToken.None);
			await EnsureMediaControlsWindowAsync(_interactiveHostHandle, _widgetDisplay, _widgetSettings, CancellationToken.None);
			UpdateTimers();
		}
	}

	private async Task EnsureWallpaperCompositionWindowAsync(CancellationToken cancellationToken)
	{
		if (_renderer != null && !_renderer.HasCompositionWindow && _display != null)
		{
			nint hostHandle = (_hostHandle = FindWallpaperHostOrThrow());
			_interactiveHostHandle = _hostLocator.InteractiveHostHandle;
			await AttachWallpaperRendererAsync(_renderer, hostHandle, _display, cancellationToken);
		}
	}

	private void SetDesktopOverlayVisibility(bool visible)
	{
		if (_desktopOverlaysVisible == visible)
		{
			return;
		}
		_desktopOverlaysVisible = visible;
		_widgetRenderer?.SetWindowVisible(visible);
		_mediaControlsWindow?.SetWindowVisible(visible);
		foreach (NativeQuickSlotGroupWindow value in _quickSlotWindows.Values)
		{
			value.SetWindowVisible(visible);
		}
	}

	private void SetExclusiveGpuCompositionIsolation(bool active, ExclusiveGpuProcessSnapshot process = default(ExclusiveGpuProcessSnapshot))
	{
		if (_exclusiveGpuCompositionIsolationActive != active)
		{
			_exclusiveGpuCompositionIsolationActive = active;
			this.ExclusiveGpuIsolationChanged?.Invoke(this, new ExclusiveGpuIsolationChangedEventArgs(active, active ? process.ProcessId : 0, active ? process.ProcessName : string.Empty));
		}
	}

	private void ScheduleExclusiveGpuReleaseRecheck(TimeSpan? delay)
	{
		CancelExclusiveGpuReleaseRecheck();
		if (delay.HasValue && !(delay <= TimeSpan.Zero) && !_disposed)
		{
			_exclusiveGpuReleaseDebounce = new CancellationTokenSource();
			RecheckExclusiveGpuReleaseAsync(delay.Value, _exclusiveGpuReleaseDebounce.Token);
		}
	}

	private async Task RecheckExclusiveGpuReleaseAsync(TimeSpan delay, CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(delay, cancellationToken);
			ForegroundAppTimerOnTick(this, EventArgs.Empty);
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void CancelExclusiveGpuReleaseRecheck()
	{
		_exclusiveGpuReleaseDebounce?.Cancel();
		_exclusiveGpuReleaseDebounce?.Dispose();
		_exclusiveGpuReleaseDebounce = null;
	}

	private static string FormatForegroundPauseTrigger(ForegroundWindowSnapshot? trigger, ExclusiveGpuProcessSnapshot exclusiveGpuProcess = default(ExclusiveGpuProcessSnapshot))
	{
		string text = (exclusiveGpuProcess.IsRunning ? $" exclusiveProcess={exclusiveGpuProcess.ProcessName}, exclusivePid={exclusiveGpuProcess.ProcessId}," : string.Empty);
		if (trigger.HasValue)
		{
			ForegroundWindowSnapshot valueOrDefault = trigger.GetValueOrDefault();
			if (valueOrDefault.IsValid)
			{
				return text + " process=" + valueOrDefault.ProcessName + ", " + $"pid={valueOrDefault.ProcessId}, " + "class=" + valueOrDefault.WindowClass + ", " + $"maximized={valueOrDefault.IsMaximized}, " + $"window={valueOrDefault.WindowBounds.Left}," + $"{valueOrDefault.WindowBounds.Top}," + $"{valueOrDefault.WindowBounds.Right}," + $"{valueOrDefault.WindowBounds.Bottom}, " + $"workArea={valueOrDefault.WorkAreaBounds.Left}," + $"{valueOrDefault.WorkAreaBounds.Top}," + $"{valueOrDefault.WorkAreaBounds.Right}," + $"{valueOrDefault.WorkAreaBounds.Bottom}";
			}
		}
		return text;
	}

	private async Task UpdateAddOnsCoreAsync(WidgetSettings settings, bool refreshMediaStatus, bool refreshCodexStatus, CancellationToken cancellationToken)
	{
		if (_widgetRenderer == null)
		{
			return;
		}
		if (settings.ShowMediaStatus && refreshMediaStatus)
		{
			_lastMediaSnapshot = await _mediaSessionService.GetSnapshotAsync();
			string text = $"{_mediaSessionService.LastSessionCount}|{_mediaSessionService.LastError}";
			if (!string.Equals(text, _lastMediaDiagnosticKey, StringComparison.Ordinal))
			{
				_lastMediaDiagnosticKey = text;
				string value = ((!string.IsNullOrWhiteSpace(_mediaSessionService.LastError)) ? _mediaSessionService.LastError : ((_mediaSessionService.LastSessionCount > 0) ? "정상" : "활성 세션 없음"));
				await _logger.InfoAsync("미디어 세션 진단: " + $"{_mediaSessionService.LastSessionCount}개, {value}.");
			}
		}
		if (settings.ShowCodexCompanion && refreshCodexStatus)
		{
			_codexCompanionPipeServer.Start();
			_lastCodexSnapshot = await _codexCompanionStatusReader.ReadAsync(cancellationToken);
		}
		float[] spectrum = ((!settings.ShowMediaStatus) ? new float[24] : (_audioSpectrumSampler?.GetBands() ?? new float[24]));
		if (_widgetRenderer != null)
		{
			await _widgetRenderer.UpdateAddOnsAsync(_lastMediaSnapshot, spectrum, _audioSpectrumSampler?.IsAvailable ?? false, _lastCodexSnapshot);
		}
		if (_mediaControlsWindow != null && settings.ShowMediaStatus)
		{
			await _mediaControlsWindow.UpdateAsync(_lastMediaSnapshot.IsAvailable, _lastMediaSnapshot.IsPlaying, cancellationToken);
		}
	}

	private void UpdateTimers()
	{
		if (_renderer != null || _widgetRenderer != null || _mediaControlsWindow != null || _quickSlotWindows.Count > 0)
		{
			_reconnectTimer.Start();
		}
		else
		{
			_reconnectTimer.Stop();
			_hostHandle = IntPtr.Zero;
			_interactiveHostHandle = IntPtr.Zero;
		}
		if (!IsRenderingPaused && _widgetRenderer != null && _widgetSettings.IsEnabled && _widgetSettings.ShowSystemInfo)
		{
			_systemMetricsTimer.Start();
		}
		else
		{
			_systemMetricsTimer.Stop();
		}
		if (!IsRenderingPaused && _quickSlotWindows.Count > 0 && _widgetSettings.IsEnabled && _widgetSettings.ShowQuickSlots && _widgetSettings.SystemShortcuts.Any((SystemShortcutSetting item) => item.IsVisible && item.Kind == SystemShortcutKind.RecycleBin))
		{
			_recycleBinTimer.Start();
		}
		else
		{
			_recycleBinTimer.Stop();
			_lastRecycleBinHasItems = null;
		}
		bool num = !IsRenderingPaused && _widgetRenderer != null && _widgetSettings.IsEnabled && (_widgetSettings.ShowMediaStatus || _widgetSettings.ShowCodexCompanion);
		bool isEnabled = _addOnTimer.IsEnabled;
		if (num)
		{
			_addOnTimer.Start();
			if (!isEnabled)
			{
				QueueAddOnRefresh(refreshMediaStatus: true, refreshCodexStatus: true, refreshSpectrum: true);
			}
		}
		else
		{
			_addOnTimer.Stop();
		}
		if (!IsRenderingPaused && _widgetRenderer != null && _widgetSettings.IsEnabled && _widgetSettings.ShowMediaStatus)
		{
			if (_audioSpectrumSampler == null)
			{
				_audioSpectrumSampler = new AudioSpectrumSampler();
				_audioSpectrumSampler.SpectrumChanged += AudioSpectrumSampler_SpectrumChanged;
				_audioSpectrumSampler.Start();
			}
		}
		else
		{
			if (_audioSpectrumSampler != null)
			{
				_audioSpectrumSampler.SpectrumChanged -= AudioSpectrumSampler_SpectrumChanged;
				_audioSpectrumSampler.Dispose();
			}
			_audioSpectrumSampler = null;
		}
		if (_renderer != null)
		{
			PlaybackOptions? playbackOptions = _playbackOptions;
			if (playbackOptions != null && playbackOptions.PauseWhenForegroundAppIsMaximized)
			{
				_foregroundAppTimer.Start();
				return;
			}
		}
		_foregroundAppTimer.Stop();
		_foregroundEventDebounce?.Cancel();
	}

	private nint FindWallpaperHostOrThrow()
	{
		nint num = _hostLocator.FindWallpaperHost();
		if (num == IntPtr.Zero)
		{
			throw new InvalidOperationException("Explorer의 WorkerW 월페이퍼 호스트를 찾지 못했습니다. Vellume은 Explorer를 재시작하지 않았습니다. Explorer가 준비된 뒤 다시 적용해 주세요.");
		}
		return num;
	}

	private static bool HasInformationWidgets(WidgetSettings settings)
	{
		if (settings.IsEnabled)
		{
			if (!settings.ShowClock && !settings.ShowSystemInfo && !settings.ShowMediaStatus)
			{
				return settings.ShowCodexCompanion;
			}
			return true;
		}
		return false;
	}

	private async Task AttachWallpaperRendererAsync(IWallpaperRenderer renderer, nint hostHandle, DisplayInfo display, CancellationToken cancellationToken)
	{
		await renderer.AttachToAsync(hostHandle, display, _hostLocator.IsRaisedDesktop, _hostLocator.ShellViewHandle, _hostLocator.BackgroundWorkerHandle, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
	}

	private void AttachWidgetRenderer(NativeWidgetRendererWindow renderer, nint hostHandle, DisplayInfo display)
	{
		renderer.AttachTo(hostHandle, display, _hostLocator.IsRaisedDesktop, _hostLocator.ShellViewHandle, _hostLocator.BackgroundWorkerHandle);
	}

	private async Task FinalizeDesktopLayerOrderAsync(IWallpaperRenderer wallpaper, NativeWidgetRendererWindow? widgets, nint hostHandle, DisplayInfo display, CancellationToken cancellationToken)
	{
		if (!wallpaper.IsAttachedTo(hostHandle))
		{
			await AttachWallpaperRendererAsync(wallpaper, hostHandle, display, cancellationToken);
		}
		if (widgets != null)
		{
			AttachWidgetRenderer(widgets, hostHandle, display);
		}
	}

	private void SetState(WallpaperPlaybackState state, string message)
	{
		State = state;
		this.StateChanged?.Invoke(this, new WallpaperStateChangedEventArgs(state, message));
	}
}
internal sealed class WallpaperRendererWindow : IWallpaperRenderer
{
	private sealed record MediaReadyProbe(bool Found, int ReadyState, bool Paused, int ErrorCode, int Width);

	private const string VirtualHostName = "vellume.local";

	private readonly string _webViewDataDirectory;

	private readonly string _gpuSafetyDirectory;

	private readonly Func<System.Drawing.Rectangle, nint, WindowsDesktopWallpaperLease?> _desktopWallpaperLeaseFactory;

	private CoreWebView2Controller? _controller;

	private CoreWebView2? _core;

	private NativeMediaEnginePlayer? _nativeVideoPlayer;

	private WindowsDesktopWallpaperLease? _desktopWallpaperLease;

	private nint _handle;

	private nint _nativeVideoHandle;

	private nint _nativeBitmapHandle;

	private int _width;

	private int _height;

	private int _displayX;

	private int _displayY;

	private string _nativeSourcePath = string.Empty;

	private PlaybackOptions _playbackOptions = new PlaybackOptions();

	private WallpaperItem? _wallpaper;

	private string _nativeFallbackReason = string.Empty;

	private WallpaperRenderBackend _backend;

	private double _resumePositionSeconds;

	private bool _webViewFailed;

	private bool _coreConfigured;

	private bool _staticPerformanceMode;

	private bool _windowVisibleRequested = true;

	private bool _disposed;

	public nint Handle => _handle;

	public string BackendDisplayName => _backend switch
	{
		WallpaperRenderBackend.NativeImage => "네이티브 이미지", 
		WallpaperRenderBackend.NativeVideo => "Media Foundation", 
		WallpaperRenderBackend.WebView => "WebView2", 
		WallpaperRenderBackend.StaticFallback => (_desktopWallpaperLease != null) ? "Windows 배경 격리" : "GPU 안전 정적 프레임", 
		_ => "준비 중", 
	};

	public bool IsStaticPerformanceMode => _staticPerformanceMode;

	public bool IsDesktopWallpaperIsolationActive => _desktopWallpaperLease != null;

	public bool HasCompositionWindow
	{
		get
		{
			if (_handle != IntPtr.Zero)
			{
				return NativeMethods.IsWindow(_handle);
			}
			return false;
		}
	}

	internal double ResumePositionSeconds => _resumePositionSeconds;

	public bool IsReady
	{
		get
		{
			bool flag = !_disposed && _handle != IntPtr.Zero && NativeMethods.IsWindow(_handle);
			if (flag)
			{
				bool flag2;
				switch (_backend)
				{
				case WallpaperRenderBackend.NativeImage:
					flag2 = _nativeBitmapHandle != IntPtr.Zero;
					break;
				case WallpaperRenderBackend.StaticFallback:
					flag2 = _nativeBitmapHandle != IntPtr.Zero;
					break;
				case WallpaperRenderBackend.NativeVideo:
				{
					NativeMediaEnginePlayer? nativeVideoPlayer = _nativeVideoPlayer;
					flag2 = nativeVideoPlayer != null && nativeVideoPlayer.IsReady && _nativeVideoHandle != IntPtr.Zero && NativeMethods.IsWindow(_nativeVideoHandle);
					break;
				}
				case WallpaperRenderBackend.WebView:
					flag2 = !_webViewFailed && _controller != null && _core != null;
					break;
				default:
					flag2 = false;
					break;
				}
				flag = flag2;
			}
			return flag;
		}
	}

	public WallpaperRendererWindow(string webViewDataDirectory, Func<System.Drawing.Rectangle, nint, WindowsDesktopWallpaperLease?>? desktopWallpaperLeaseFactory = null)
	{
		_webViewDataDirectory = webViewDataDirectory;
		string path = System.IO.Path.GetDirectoryName(webViewDataDirectory) ?? webViewDataDirectory;
		_gpuSafetyDirectory = System.IO.Path.Combine(path, "GpuSafety");
		_desktopWallpaperLeaseFactory = desktopWallpaperLeaseFactory ?? ((Func<System.Drawing.Rectangle, nint, WindowsDesktopWallpaperLease>)((System.Drawing.Rectangle bounds, nint bitmapHandle) => WindowsDesktopWallpaperLease.TryActivate(_gpuSafetyDirectory, bounds, bitmapHandle)));
	}

	public string GetAttachmentDiagnostics()
	{
		nint parent = NativeMethods.GetParent(_handle);
		nint window = NativeMethods.GetWindow(_handle, 3u);
		nint window2 = NativeMethods.GetWindow(_handle, 2u);
		long value = ((IntPtr)NativeMethods.GetWindowLongPtr(_handle, -20)).ToInt64();
		NativeMethods.GetWindowRect(_handle, out var rectangle);
		uint colorKey;
		byte alpha;
		uint flags;
		bool layeredWindowAttributes = NativeMethods.GetLayeredWindowAttributes(_handle, out colorKey, out alpha, out flags);
		return $"backend={_backend}{(string.IsNullOrWhiteSpace(_nativeFallbackReason) ? string.Empty : (" (fallback=" + _nativeFallbackReason + ")"))}, parent=0x{parent:X}, renderer=0x{_handle:X}, class={DescribeWindow(_handle)}, above={DescribeWindow(window)}, below={DescribeWindow(window2)}, visible={NativeMethods.IsWindowVisible(_handle)}, rect={rectangle.Left},{rectangle.Top}-{rectangle.Right},{rectangle.Bottom}, ex-style=0x{value:X8}, layered-alpha={(layeredWindowAttributes ? alpha : (-1))}, layered-flags=0x{flags:X}";
	}

	public void SetWindowVisible(bool visible)
	{
		_windowVisibleRequested = visible;
		if (_handle != IntPtr.Zero && NativeMethods.IsWindow(_handle))
		{
			if (NativeMethods.IsWindowVisible(_handle) != visible)
			{
				NativeMethods.ShowWindow(_handle, visible ? 4 : 0);
			}
			if (_controller != null && _controller.IsVisible != visible)
			{
				_controller.IsVisible = visible;
			}
		}
	}

	Task IWallpaperRenderer.SetWindowVisibleAsync(bool visible, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		SetWindowVisible(visible);
		return Task.CompletedTask;
	}

	public void AttachTo(nint hostHandle, DisplayInfo display, bool isRaisedDesktop, nint shellViewHandle, nint backgroundWorkerHandle)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!NativeMethods.GetWindowRect(hostHandle, out var rectangle))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 데스크톱 호스트 영역을 읽지 못했습니다.");
		}
		if (isRaisedDesktop && (shellViewHandle == IntPtr.Zero || backgroundWorkerHandle == IntPtr.Zero))
		{
			throw new InvalidOperationException("확장 데스크톱에 ShellView 또는 WorkerW 레이어가 없습니다.");
		}
		int x = display.X - rectangle.Left;
		int y = display.Y - rectangle.Top;
		_width = display.Width;
		_height = display.Height;
		_displayX = display.X;
		_displayY = display.Y;
		if (_handle == IntPtr.Zero || !NativeMethods.IsWindow(_handle))
		{
			long num = 0x4600000EL | (long)(_windowVisibleRequested ? 268435456 : 0);
			long num2 = 0x8000080L | (long)(isRaisedDesktop ? 524288 : 0);
			_handle = NativeMethods.CreateWindowExW((uint)num2, "Static", "Vellume Wallpaper Renderer", (uint)num, x, y, _width, _height, hostHandle, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
			if (_handle == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 네이티브 월페이퍼 호스트를 만들지 못했습니다.");
			}
		}
		else
		{
			NativeMethods.SetParent(_handle, hostHandle);
		}
		if (NativeMethods.GetParent(_handle) != hostHandle)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows가 데스크톱 렌더러 연결을 거부했습니다.");
		}
		if (isRaisedDesktop)
		{
			long num3 = ((IntPtr)NativeMethods.GetWindowLongPtr(_handle, -20)).ToInt64();
			if ((num3 & 0x80000) == 0L)
			{
				NativeMethods.SetWindowLongPtr(_handle, -20, new IntPtr(num3 | 0x80000));
			}
			if (!NativeMethods.SetLayeredWindowAttributes(_handle, 0u, byte.MaxValue, 2u))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 확장 데스크톱 합성을 켜지 못했습니다.");
			}
			uint flags = 19u;
			if (!NativeMethods.SetWindowPos(_handle, shellViewHandle, 0, 0, 0, 0, flags))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 확장 데스크톱의 Z 순서를 설정하지 못했습니다.");
			}
		}
		if (!NativeMethods.SetWindowPos(_handle, IntPtr.Zero, x, y, _width, _height, 52u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 월페이퍼 렌더러 위치를 지정하지 못했습니다.");
		}
		if (_controller != null)
		{
			_controller.ParentWindow = _handle;
			_controller.Bounds = new System.Drawing.Rectangle(0, 0, _width, _height);
			_controller.IsVisible = _windowVisibleRequested;
		}
		if (_backend == WallpaperRenderBackend.NativeVideo)
		{
			LayoutNativeVideo();
		}
		if (_desktopWallpaperLease != null || !_windowVisibleRequested)
		{
			NativeMethods.ShowWindow(_handle, 0);
		}
		else
		{
			NativeMethods.ShowWindow(_handle, 4);
		}
	}

	Task IWallpaperRenderer.AttachToAsync(nint hostHandle, DisplayInfo display, bool raisedDesktop, nint shellViewHandle, nint backgroundWorkerHandle, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		AttachTo(hostHandle, display, raisedDesktop, shellViewHandle, backgroundWorkerHandle);
		return Task.CompletedTask;
	}

	public async Task InitializeAndNavigateAsync(WallpaperItem wallpaper, PlaybackOptions options, CancellationToken cancellationToken)
	{
		if (_handle == IntPtr.Zero || !NativeMethods.IsWindow(_handle))
		{
			throw new InvalidOperationException("네이티브 월페이퍼 호스트가 연결되지 않았습니다.");
		}
		_wallpaper = new WallpaperItem
		{
			Id = wallpaper.Id,
			Name = wallpaper.Name,
			SourcePath = wallpaper.SourcePath,
			Kind = wallpaper.Kind,
			IsBuiltIn = wallpaper.IsBuiltIn
		};
		_playbackOptions = options.Copy();
		_nativeSourcePath = wallpaper.SourcePath;
		_nativeFallbackReason = string.Empty;
		_staticPerformanceMode = false;
		if (wallpaper.Kind == WallpaperKind.Image)
		{
			try
			{
				await CloseWebCoreAsync();
				CloseNativeVideoCore();
				SetNativeBitmap(NativeWallpaperImageRenderer.Render(wallpaper.SourcePath, _width, _height, _playbackOptions));
				_backend = WallpaperRenderBackend.NativeImage;
				return;
			}
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				_nativeFallbackReason = "native image: " + ex.GetType().Name;
				CloseNativeImageCore();
			}
		}
		if (WallpaperRendererBackendPolicy.ShouldUseNativeVideo(wallpaper, options))
		{
			try
			{
				await CloseWebCoreAsync();
				PrepareNativeVideoSurface();
				_nativeVideoPlayer = new NativeMediaEnginePlayer();
				await _nativeVideoPlayer.InitializeAsync(_nativeVideoHandle, wallpaper.SourcePath, options.IsMuted, options.Volume, _resumePositionSeconds, cancellationToken);
				_backend = WallpaperRenderBackend.NativeVideo;
				LayoutNativeVideo();
				_resumePositionSeconds = 0.0;
				CloseNativeImageCore();
				return;
			}
			catch (Exception ex2) when (!(ex2 is OperationCanceledException))
			{
				_nativeFallbackReason = "Media Foundation: " + ex2.GetType().Name;
				CloseNativeVideoCore();
			}
		}
		CloseNativeVideoCore();
		bool keepSafetyFrameUntilReady = _nativeBitmapHandle != IntPtr.Zero;
		try
		{
			if (_controller == null || _core == null)
			{
				_controller = await (await WebViewEnvironmentProvider.GetAsync(_webViewDataDirectory)).CreateCoreWebView2ControllerAsync(_handle);
				_controller.DefaultBackgroundColor = System.Drawing.Color.Black;
				_core = _controller.CoreWebView2;
				_core.ProcessFailed += CoreOnProcessFailed;
			}
			_webViewFailed = false;
			_backend = WallpaperRenderBackend.WebView;
			_controller.ParentWindow = _handle;
			_controller.Bounds = new System.Drawing.Rectangle(0, 0, _width, _height);
			_controller.IsVisible = !keepSafetyFrameUntilReady;
		}
		catch (Exception innerException)
		{
			throw new InvalidOperationException("WebView2를 시작하지 못했습니다. Microsoft Edge WebView2 Runtime을 설치하거나 복구하세요.", innerException);
		}
		CoreWebView2 core = _core;
		if (!_coreConfigured)
		{
			core.Settings.AreDefaultContextMenusEnabled = false;
			core.Settings.AreDevToolsEnabled = false;
			core.Settings.IsStatusBarEnabled = false;
			core.Settings.AreBrowserAcceleratorKeysEnabled = false;
			core.NewWindowRequested += delegate(object? _, CoreWebView2NewWindowRequestedEventArgs eventArgs)
			{
				eventArgs.Handled = true;
			};
			_coreConfigured = true;
		}
		string path = ((wallpaper.Kind == WallpaperKind.Web) ? WallpaperValidator.ResolveWebEntryPoint(wallpaper.SourcePath) : wallpaper.SourcePath);
		string folderPath = System.IO.Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The wallpaper source has no parent folder.");
		core.ClearVirtualHostNameToFolderMapping("vellume.local");
		core.SetVirtualHostNameToFolderMapping("vellume.local", folderPath, CoreWebView2HostResourceAccessKind.DenyCors);
		string text = Uri.EscapeDataString(System.IO.Path.GetFileName(path));
		string text2 = "https://vellume.local/" + text;
		Task<CoreWebView2NavigationCompletedEventArgs> task = WaitForNavigationAsync(cancellationToken);
		if (wallpaper.Kind == WallpaperKind.Web)
		{
			core.Navigate(text2);
		}
		else
		{
			core.NavigateToString(BuildMediaDocument(wallpaper, text2, options));
		}
		CoreWebView2NavigationCompletedEventArgs coreWebView2NavigationCompletedEventArgs = await task;
		if (!coreWebView2NavigationCompletedEventArgs.IsSuccess)
		{
			throw new InvalidOperationException($"WebView2가 월페이퍼를 불러오지 못했습니다 ({coreWebView2NavigationCompletedEventArgs.WebErrorStatus}).");
		}
		await EnsureMediaReadyAsync(wallpaper.Kind, cancellationToken);
		await RestoreWebPlaybackPositionAsync(wallpaper.Kind, cancellationToken);
		await UpdateWebPlaybackAsync(options);
		await ResumeAsync();
		if (_controller != null)
		{
			_controller.IsVisible = _windowVisibleRequested;
		}
		CloseNativeImageCore();
	}

	public async Task CapturePreviewAsync(string outputPath, CancellationToken cancellationToken)
	{
		if (_backend == WallpaperRenderBackend.NativeImage && _nativeBitmapHandle != IntPtr.Zero)
		{
			using (Bitmap bitmap = System.Drawing.Image.FromHbitmap(_nativeBitmapHandle))
			{
				bitmap.Save(outputPath, ImageFormat.Png);
				return;
			}
		}
		if (_backend == WallpaperRenderBackend.NativeVideo && NativeMethods.GetWindowRect(_handle, out var rectangle))
		{
			cancellationToken.ThrowIfCancellationRequested();
			using Bitmap bitmap2 = new Bitmap(_width, _height);
			using Graphics graphics = Graphics.FromImage(bitmap2);
			graphics.CopyFromScreen(rectangle.Left, rectangle.Top, 0, 0, new System.Drawing.Size(_width, _height));
			bitmap2.Save(outputPath, ImageFormat.Png);
			return;
		}
		if (_core == null)
		{
			throw new InvalidOperationException("월페이퍼 렌더러가 미리보기 캡처 준비 상태가 아닙니다.");
		}
		FileStream output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
		try
		{
			await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
			await output.FlushAsync(cancellationToken);
		}
		finally
		{
			if (output != null)
			{
				await output.DisposeAsync();
			}
		}
	}

	public bool IsAttachedTo(nint hostHandle)
	{
		if (IsReady && NativeMethods.IsWindow(hostHandle))
		{
			return NativeMethods.GetParent(_handle) == hostHandle;
		}
		return false;
	}

	public async Task PauseAsync()
	{
		if (_backend == WallpaperRenderBackend.NativeVideo && _nativeVideoPlayer != null)
		{
			await _nativeVideoPlayer.PauseAsync();
		}
		else
		{
			if (_backend != WallpaperRenderBackend.WebView)
			{
				return;
			}
			await ExecuteScriptAsync("document.querySelectorAll('video,audio').forEach(x=>x.pause());document.getAnimations().forEach(x=>x.pause());");
			if (_core != null && !_core.IsSuspended)
			{
				try
				{
					await _core.TrySuspendAsync();
				}
				catch
				{
				}
			}
		}
	}

	public async Task ResumeAsync()
	{
		if (_backend == WallpaperRenderBackend.NativeVideo && _nativeVideoPlayer != null)
		{
			await _nativeVideoPlayer.ResumeAsync();
		}
		else if (_backend == WallpaperRenderBackend.WebView)
		{
			if (_core?.IsSuspended ?? false)
			{
				_core.Resume();
			}
			await ExecuteScriptAsync("document.querySelectorAll('video,audio').forEach(x=>x.play().catch(()=>{}));document.getAnimations().forEach(x=>x.play());");
		}
	}

	public async Task EnterStaticPerformanceModeAsync()
	{
		if (!_staticPerformanceMode && _wallpaper != null && !_disposed)
		{
			nint bitmapHandle = await CreateStaticFallbackFrameAsync();
			_resumePositionSeconds = await CapturePlaybackPositionAsync();
			await PauseAsync();
			SetNativeBitmap(bitmapHandle);
			_desktopWallpaperLease = _desktopWallpaperLeaseFactory(new System.Drawing.Rectangle(_displayX, _displayY, _width, _height), bitmapHandle);
			if (_controller != null)
			{
				_controller.IsVisible = false;
			}
			if (_nativeVideoHandle != IntPtr.Zero && NativeMethods.IsWindow(_nativeVideoHandle))
			{
				NativeMethods.ShowWindow(_nativeVideoHandle, 0);
			}
			await CloseWebCoreAsync();
			CloseNativeVideoCore();
			_backend = WallpaperRenderBackend.StaticFallback;
			_staticPerformanceMode = true;
			if (_desktopWallpaperLease != null)
			{
				NativeMethods.ShowWindow(_handle, 0);
				_nativeFallbackReason = "Windows desktop wallpaper isolation";
			}
			else
			{
				_nativeFallbackReason = "exclusive GPU application";
			}
		}
	}

	public async Task ExitStaticPerformanceModeAsync()
	{
		if (!_staticPerformanceMode || _wallpaper == null || _disposed)
		{
			return;
		}
		WallpaperItem wallpaper = new WallpaperItem
		{
			Id = _wallpaper.Id,
			Name = _wallpaper.Name,
			SourcePath = _wallpaper.SourcePath,
			Kind = _wallpaper.Kind,
			IsBuiltIn = _wallpaper.IsBuiltIn
		};
		PlaybackOptions options = _playbackOptions.Copy();
		WindowsDesktopWallpaperLease desktopWallpaperLease = _desktopWallpaperLease;
		try
		{
			_staticPerformanceMode = false;
			await InitializeAndNavigateAsync(wallpaper, options, CancellationToken.None);
			if (desktopWallpaperLease != null)
			{
				NativeMethods.ShowWindow(_handle, _windowVisibleRequested ? 4 : 0);
				desktopWallpaperLease.Dispose();
				_desktopWallpaperLease = null;
			}
		}
		catch
		{
			_staticPerformanceMode = true;
			_backend = WallpaperRenderBackend.StaticFallback;
			if (desktopWallpaperLease != null)
			{
				NativeMethods.ShowWindow(_handle, 0);
			}
			throw;
		}
	}

	public bool DestroyCompositionWindowForIsolation()
	{
		if (!_staticPerformanceMode || _desktopWallpaperLease == null || _disposed)
		{
			return false;
		}
		CloseNativeVideoCore();
		if (_handle != IntPtr.Zero && NativeMethods.IsWindow(_handle))
		{
			NativeMethods.DestroyWindow(_handle);
		}
		_handle = IntPtr.Zero;
		if (_nativeBitmapHandle != IntPtr.Zero)
		{
			NativeMethods.DeleteObject(_nativeBitmapHandle);
			_nativeBitmapHandle = IntPtr.Zero;
		}
		_backend = WallpaperRenderBackend.StaticFallback;
		_nativeFallbackReason = "Windows desktop wallpaper with renderer HWND released";
		return true;
	}

	Task<bool> IWallpaperRenderer.DestroyCompositionWindowForIsolationAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(DestroyCompositionWindowForIsolation());
	}

	internal bool DetachDesktopWallpaperLeaseForProcessExit()
	{
		if (_desktopWallpaperLease == null)
		{
			return false;
		}
		_desktopWallpaperLease.DetachForProcessExit();
		_desktopWallpaperLease = null;
		return true;
	}

	internal void SetResumePositionForRestore(double positionSeconds)
	{
		_resumePositionSeconds = ((double.IsFinite(positionSeconds) && positionSeconds > 0.0) ? positionSeconds : 0.0);
	}

	public async Task UpdatePlaybackAsync(PlaybackOptions options)
	{
		_playbackOptions = options.Copy();
		if (_staticPerformanceMode && _wallpaper != null)
		{
			if (HasCompositionWindow)
			{
				SetNativeBitmap(await Task.Run(() => NativeWallpaperFallbackRenderer.Render(_wallpaper.SourcePath, _width, _height, _playbackOptions)));
				_backend = WallpaperRenderBackend.StaticFallback;
			}
		}
		else if (_backend == WallpaperRenderBackend.NativeImage && !string.IsNullOrWhiteSpace(_nativeSourcePath))
		{
			SetNativeBitmap(NativeWallpaperImageRenderer.Render(_nativeSourcePath, _width, _height, _playbackOptions));
		}
		else if (_backend != WallpaperRenderBackend.NativeVideo || _nativeVideoPlayer == null)
		{
			await UpdateWebPlaybackAsync(_playbackOptions);
		}
		else if (_wallpaper != null && !WallpaperRendererBackendPolicy.ShouldUseNativeVideo(_wallpaper, _playbackOptions))
		{
			await InitializeAndNavigateAsync(_wallpaper, _playbackOptions, CancellationToken.None);
		}
		else
		{
			await _nativeVideoPlayer.UpdateAudioAsync(_playbackOptions.IsMuted, _playbackOptions.Volume);
			LayoutNativeVideo();
		}
	}

	private Task UpdateWebPlaybackAsync(PlaybackOptions options)
	{
		string value = Math.Clamp(options.Volume, 0.0, 1.0).ToString(CultureInfo.InvariantCulture);
		string value2 = (options.IsMuted ? "true" : "false");
		string value3 = JsonSerializer.Serialize(BuildCssFilter(options));
		string value4 = options.Fit switch
		{
			WallpaperFit.Fit => "contain", 
			WallpaperFit.Stretch => "fill", 
			_ => "cover", 
		};
		return ExecuteScriptAsync($"(()=>{{document.querySelectorAll('video,audio').forEach(x=>{{x.muted={value2};x.volume={value};}});const vellumeMedia=[...document.querySelectorAll('[data-vellume-media]')];vellumeMedia.forEach(x=>{{x.style.objectFit={JsonSerializer.Serialize(value4)};x.style.setProperty('filter',{value3},'important');}});if(vellumeMedia.length){{document.documentElement.style.removeProperty('filter');}}else{{document.documentElement.style.setProperty('filter',{value3},'important');}}}})();");
	}

	public Task ApplyWidgetsAsync(WidgetSettings widgets)
	{
		if (!widgets.IsEnabled || (!widgets.ShowClock && !widgets.ShowSystemInfo && !widgets.ShowQuickSlots))
		{
			return ExecuteScriptAsync("window.clearInterval(window.__vellumeClockTimer);document.getElementById('vellume-widget-layer')?.remove();delete window.__vellumeWidgetUpdate;");
		}
		string[] array = (from item in widgets.QuickSlots.Where(delegate(QuickSlotItem item)
			{
				int slot = item.Slot;
				return slot >= 1 && slot <= 9;
			})
			orderby item.Slot
			select item).Select(delegate(QuickSlotItem item)
		{
			string value5 = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(item.Name) ? System.IO.Path.GetFileNameWithoutExtension(item.TargetPath) : item.Name);
			return $"<div class=\"slot\"><span>{item.Slot}</span><strong>{value5}</strong></div>";
		}).ToArray();
		string value = ((array.Length == 0) ? "<div class=\"empty\">Add files in Vellume to create quick slots.</div>" : string.Join(string.Empty, array));
		string value2 = (widgets.ShowClock ? "grid" : "none");
		string value3 = (widgets.ShowSystemInfo ? "block" : "none");
		string value4 = (widgets.ShowQuickSlots ? "flex" : "none");
		return ExecuteScriptAsync($"(() => {{\n  const id = 'vellume-widget-layer';\n  let host = document.getElementById(id);\n  if (!host) {{\n    host = document.createElement('div');\n    host.id = id;\n    (document.body || document.documentElement).appendChild(host);\n  }}\n  host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none;';\n  const root = host.shadowRoot || host.attachShadow({{ mode: 'open' }});\n  root.innerHTML = `\n    <style>\n      :host {{ all: initial; }}\n      * {{ box-sizing: border-box; }}\n      .layer {{\n        position: fixed; inset: 0; pointer-events: none;\n        color: #d9ffff;\n        font-family: \"Segoe UI Variable\", \"Segoe UI\", sans-serif;\n        text-shadow: 0 0 16px rgba(54, 238, 255, .48);\n      }}\n      .panel {{\n        position: absolute;\n        background: linear-gradient(135deg, rgba(2,12,23,.88), rgba(8,26,40,.62));\n        border: 1px solid rgba(89, 229, 255, .72);\n        box-shadow: 0 0 24px rgba(24, 181, 255, .16), inset 0 0 28px rgba(23, 114, 166, .12);\n        backdrop-filter: blur(7px);\n        clip-path: polygon(0 0, calc(100% - 16px) 0, 100% 16px, 100% 100%, 16px 100%, 0 calc(100% - 16px));\n      }}\n      .eyebrow {{\n        color: #6ef6ff; font-size: 11px; font-weight: 700;\n        letter-spacing: .2em; text-transform: uppercase;\n      }}\n      .clock {{\n        left: 42px; bottom: 54px; width: 340px;\n        display: {value2}; grid-template-columns: 1fr; padding: 20px 24px 18px;\n      }}\n      .clock time {{\n        margin-top: 4px; color: #e4ffff; font-size: 72px;\n        font-weight: 250; line-height: .95; letter-spacing: .02em;\n      }}\n      .clock .date {{\n        margin-top: 10px; color: rgba(213,251,255,.72);\n        font-size: 13px; letter-spacing: .14em; text-transform: uppercase;\n      }}\n      .system {{\n        display: {value3}; right: 42px; top: 42px;\n        width: 330px; padding: 19px 20px 18px;\n      }}\n      .system h2 {{\n        margin: 5px 0 16px; font-size: 18px; font-weight: 650;\n        letter-spacing: .08em;\n      }}\n      .metric {{ margin-top: 12px; }}\n      .metric-line {{\n        display: flex; justify-content: space-between;\n        color: rgba(225,253,255,.82); font-size: 12px;\n      }}\n      .track {{\n        height: 5px; margin-top: 6px; overflow: hidden;\n        background: rgba(82,147,170,.22);\n      }}\n      .fill {{\n        width: 0; height: 100%;\n        background: linear-gradient(90deg, #32d7ff, #7affd5);\n        box-shadow: 0 0 12px #39e5ff;\n        transition: width .5s ease;\n      }}\n      .uptime {{\n        margin-top: 15px; padding-top: 11px;\n        border-top: 1px solid rgba(91,218,255,.25);\n        color: rgba(174,235,241,.6); font-size: 10px;\n        letter-spacing: .12em;\n      }}\n      .slots {{\n        display: {value4}; gap: 8px; align-items: stretch;\n        left: 50%; bottom: 52px; transform: translateX(-50%);\n        min-width: 240px; max-width: 720px; padding: 11px 13px;\n      }}\n      .slot {{\n        width: 72px; min-height: 58px; padding: 7px 6px;\n        display: grid; align-content: center; justify-items: center;\n        border: 1px solid rgba(102,237,255,.42);\n        background: rgba(7,25,38,.72);\n        clip-path: polygon(11px 0, 100% 0, 100% calc(100% - 11px), calc(100% - 11px) 100%, 0 100%, 0 11px);\n      }}\n      .slot span {{\n        color: #60f4ff; font-size: 10px; font-weight: 800;\n      }}\n      .slot strong {{\n        width: 100%; margin-top: 4px; overflow: hidden;\n        color: rgba(232,255,255,.88); font-size: 10px;\n        font-weight: 600; text-align: center;\n        text-overflow: ellipsis; white-space: nowrap;\n      }}\n      .empty {{\n        padding: 8px 12px; color: rgba(195,239,245,.58);\n        font-size: 11px; letter-spacing: .04em;\n      }}\n    </style>\n    <div class=\"layer\">\n      <section class=\"panel clock\">\n        <span class=\"eyebrow\">Local time</span>\n        <time data-clock>--:--</time>\n        <span class=\"date\" data-date></span>\n      </section>\n      <section class=\"panel system\">\n        <span class=\"eyebrow\">System telemetry</span>\n        <h2>VELLUME // STATUS</h2>\n        <div class=\"metric\">\n          <div class=\"metric-line\"><span>CPU</span><b data-cpu-label>--%</b></div>\n          <div class=\"track\"><div class=\"fill\" data-cpu-bar></div></div>\n        </div>\n        <div class=\"metric\">\n          <div class=\"metric-line\"><span>MEMORY</span><b data-memory-label>--</b></div>\n          <div class=\"track\"><div class=\"fill\" data-memory-bar></div></div>\n        </div>\n        <div class=\"metric\">\n          <div class=\"metric-line\"><span>SYSTEM DISK</span><b data-disk-label>--</b></div>\n          <div class=\"track\"><div class=\"fill\" data-disk-bar></div></div>\n        </div>\n        <div class=\"uptime\" data-uptime>UPTIME --</div>\n      </section>\n      <section class=\"panel slots\">{value}</section>\n    </div>`;\n\n  const updateClock = () => {{\n    const now = new Date();\n    const clock = root.querySelector('[data-clock]');\n    const date = root.querySelector('[data-date]');\n    if (clock) {{\n      clock.textContent = new Intl.DateTimeFormat([], {{\n        hour: '2-digit', minute: '2-digit', hour12: false\n      }}).format(now);\n    }}\n    if (date) {{\n      date.textContent = new Intl.DateTimeFormat([], {{\n        weekday: 'short', year: 'numeric', month: 'long', day: '2-digit'\n      }}).format(now);\n    }}\n  }};\n  window.clearInterval(window.__vellumeClockTimer);\n  updateClock();\n  window.__vellumeClockTimer = window.setInterval(updateClock, 1000);\n\n  window.__vellumeWidgetUpdate = metrics => {{\n    const setText = (selector, value) => {{\n      const node = root.querySelector(selector);\n      if (node) node.textContent = value;\n    }};\n    const setBar = (selector, value) => {{\n      const node = root.querySelector(selector);\n      if (node) node.style.width = `${{Math.max(0, Math.min(100, value))}}%`;\n    }};\n    setText('[data-cpu-label]', `${{Math.round(metrics.cpu)}}%`);\n    setText('[data-memory-label]', metrics.memoryLabel);\n    setText('[data-disk-label]', metrics.diskLabel);\n    setText('[data-uptime]', `UPTIME ${{metrics.uptime}}`);\n    setBar('[data-cpu-bar]', metrics.cpu);\n    setBar('[data-memory-bar]', metrics.memory);\n    setBar('[data-disk-bar]', metrics.disk);\n  }};\n}})();");
	}

	public Task UpdateSystemMetricsAsync(SystemMetricsSnapshot metrics)
	{
		string text = JsonSerializer.Serialize(new
		{
			cpu = metrics.CpuPercent,
			memory = metrics.MemoryPercent,
			disk = metrics.DiskPercent,
			memoryLabel = FormatBytes(metrics.MemoryUsedBytes) + " / " + FormatBytes(metrics.MemoryTotalBytes),
			diskLabel = FormatBytes(metrics.DiskUsedBytes) + " / " + FormatBytes(metrics.DiskTotalBytes),
			uptime = ((metrics.Uptime.TotalDays >= 1.0) ? $"{(int)metrics.Uptime.TotalDays}D {metrics.Uptime.Hours:00}H" : $"{metrics.Uptime.Hours:00}H {metrics.Uptime.Minutes:00}M")
		});
		return ExecuteScriptAsync("window.__vellumeWidgetUpdate?.(" + text + ");");
	}

	public async Task CloseSafelyAsync()
	{
		if (!_disposed)
		{
			_disposed = true;
			_staticPerformanceMode = false;
			_resumePositionSeconds = 0.0;
			_desktopWallpaperLease?.Dispose();
			_desktopWallpaperLease = null;
			await CloseWebCoreAsync();
			CloseNativeVideoCore();
			CloseNativeImageCore();
			if (_handle != IntPtr.Zero && NativeMethods.IsWindow(_handle))
			{
				NativeMethods.DestroyWindow(_handle);
			}
			_handle = IntPtr.Zero;
			_backend = WallpaperRenderBackend.None;
		}
	}

	private async Task CloseWebCoreAsync()
	{
		CoreWebView2 core = _core;
		_core = null;
		if (core != null)
		{
			core.ProcessFailed -= CoreOnProcessFailed;
			try
			{
				core.Navigate("about:blank");
				await Task.Yield();
			}
			catch
			{
			}
		}
		_controller?.Close();
		_controller = null;
		_coreConfigured = false;
		_webViewFailed = false;
		if (_backend == WallpaperRenderBackend.WebView)
		{
			_backend = WallpaperRenderBackend.None;
		}
	}

	private async Task<nint> CreateStaticFallbackFrameAsync()
	{
		if (_backend == WallpaperRenderBackend.WebView && _core != null)
		{
			try
			{
				MemoryStream stream = new MemoryStream();
				nint result;
				try
				{
					await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
					stream.Position = 0L;
					using System.Drawing.Image source = System.Drawing.Image.FromStream(stream);
					result = NativeWallpaperImageRenderer.Render(source, _width, _height, new PlaybackOptions
					{
						Fit = WallpaperFit.Stretch,
						Brightness = 1.0,
						Contrast = 1.0,
						Saturation = 1.0
					});
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
			catch
			{
			}
		}
		string sourcePath = _wallpaper?.SourcePath ?? _nativeSourcePath;
		return await Task.Run(() => NativeWallpaperFallbackRenderer.Render(sourcePath, _width, _height, _playbackOptions));
	}

	private void PrepareNativeVideoSurface()
	{
		CloseNativeVideoCore();
		_nativeVideoHandle = NativeMethods.CreateWindowExW(134217856u, "Static", "Vellume Media Foundation Video", 1174405124u, 0, 0, _width, _height, _handle, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
		if (_nativeVideoHandle == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Media Foundation 영상 출력 창을 만들지 못했습니다.");
		}
	}

	private void LayoutNativeVideo()
	{
		if (_nativeVideoHandle != IntPtr.Zero && NativeMethods.IsWindow(_nativeVideoHandle))
		{
			int sourceWidth = ((int?)_nativeVideoPlayer?.NativeWidth) ?? _width;
			int sourceHeight = ((int?)_nativeVideoPlayer?.NativeHeight) ?? _height;
			System.Drawing.Rectangle rectangle = WallpaperLayout.Calculate(sourceWidth, sourceHeight, _width, _height, _playbackOptions.Fit);
			NativeMethods.SetWindowPos(_nativeVideoHandle, IntPtr.Zero, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, 112u);
		}
	}

	private void SetNativeBitmap(nint bitmapHandle)
	{
		if (bitmapHandle == IntPtr.Zero)
		{
			throw new InvalidOperationException("네이티브 월페이퍼 비트맵을 만들지 못했습니다.");
		}
		nint num = NativeMethods.SendMessageW(_handle, 370u, 0u, bitmapHandle);
		if (num != IntPtr.Zero && num != bitmapHandle)
		{
			NativeMethods.DeleteObject(num);
		}
		if (_nativeBitmapHandle != IntPtr.Zero && _nativeBitmapHandle != num && _nativeBitmapHandle != bitmapHandle)
		{
			NativeMethods.DeleteObject(_nativeBitmapHandle);
		}
		_nativeBitmapHandle = bitmapHandle;
	}

	private void CloseNativeImageCore()
	{
		if (_nativeBitmapHandle != IntPtr.Zero)
		{
			NativeMethods.SendMessageW(_handle, 370u, 0u, IntPtr.Zero);
			NativeMethods.DeleteObject(_nativeBitmapHandle);
			_nativeBitmapHandle = IntPtr.Zero;
			WallpaperRenderBackend backend = _backend;
			if ((backend == WallpaperRenderBackend.NativeImage || backend == WallpaperRenderBackend.StaticFallback) ? true : false)
			{
				_backend = WallpaperRenderBackend.None;
			}
		}
	}

	private void CloseNativeVideoCore()
	{
		_nativeVideoPlayer?.Dispose();
		_nativeVideoPlayer = null;
		if (_nativeVideoHandle != IntPtr.Zero && NativeMethods.IsWindow(_nativeVideoHandle))
		{
			NativeMethods.DestroyWindow(_nativeVideoHandle);
		}
		_nativeVideoHandle = IntPtr.Zero;
		if (_backend == WallpaperRenderBackend.NativeVideo)
		{
			_backend = WallpaperRenderBackend.None;
		}
	}

	private void CoreOnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs eventArgs)
	{
		if (!_disposed)
		{
			_webViewFailed = true;
		}
	}

	private Task<CoreWebView2NavigationCompletedEventArgs> WaitForNavigationAsync(CancellationToken cancellationToken)
	{
		TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs> completion = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
		_core.NavigationCompleted += Handler;
		cancellationToken.Register(delegate
		{
			if (_core != null)
			{
				_core.NavigationCompleted -= Handler;
			}
			completion.TrySetCanceled(cancellationToken);
		});
		return completion.Task;
		void Handler(object? _, CoreWebView2NavigationCompletedEventArgs eventArgs)
		{
			if (_core != null)
			{
				_core.NavigationCompleted -= Handler;
			}
			completion.TrySetResult(eventArgs);
		}
	}

	private async Task ExecuteScriptAsync(string script)
	{
		CoreWebView2 core = _core;
		if (_disposed || core == null)
		{
			return;
		}
		try
		{
			await core.ExecuteScriptAsync(script);
		}
		catch (InvalidOperationException) when (_disposed || _core != core)
		{
		}
		catch (InvalidOperationException)
		{
			_webViewFailed = true;
		}
	}

	private async Task<double> CapturePlaybackPositionAsync()
	{
		WallpaperItem? wallpaper = _wallpaper;
		if (wallpaper == null || wallpaper.Kind != WallpaperKind.Video)
		{
			return 0.0;
		}
		if (_backend == WallpaperRenderBackend.NativeVideo)
		{
			return _nativeVideoPlayer?.GetCurrentTimeSeconds() ?? 0.0;
		}
		CoreWebView2 core = _core;
		if (_backend != WallpaperRenderBackend.WebView || core == null)
		{
			return 0.0;
		}
		try
		{
			double num = JsonSerializer.Deserialize<double>(await core.ExecuteScriptAsync("(() => {\n  const media = document.querySelector('video,audio');\n  const value = Number(media?.currentTime || 0);\n  return Number.isFinite(value) && value > 0 ? value : 0;\n})()"));
			return (double.IsFinite(num) && num > 0.0) ? num : 0.0;
		}
		catch
		{
			return 0.0;
		}
	}

	private async Task RestoreWebPlaybackPositionAsync(WallpaperKind kind, CancellationToken cancellationToken)
	{
		double resumePositionSeconds = _resumePositionSeconds;
		if (kind != WallpaperKind.Video || resumePositionSeconds <= 0.1 || _core == null)
		{
			_resumePositionSeconds = 0.0;
			return;
		}
		string text = JsonSerializer.Serialize(resumePositionSeconds);
		await ExecuteScriptAsync("(() => {\n  const media = document.querySelector('video,audio');\n  if (!media) return;\n  const requested = " + text + ";\n  const duration = Number(media.duration);\n  media.currentTime =\n    Number.isFinite(duration) && duration > 0\n      ? requested % duration\n      : requested;\n})()");
		for (int attempt = 0; attempt < 40; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (JsonSerializer.Deserialize<bool>(await _core.ExecuteScriptAsync("(() => {\n  const media = document.querySelector('video,audio');\n  return !!media && media.readyState >= 2 && !media.seeking;\n})()")))
			{
				break;
			}
			await Task.Delay(25, cancellationToken);
		}
		_resumePositionSeconds = 0.0;
	}

	private async Task EnsureMediaReadyAsync(WallpaperKind kind, CancellationToken cancellationToken)
	{
		if (kind == WallpaperKind.Web || _core == null)
		{
			return;
		}
		bool isVideo = kind == WallpaperKind.Video;
		int attempts = (isVideo ? 80 : 40);
		string script = (isVideo ? "(() => {\n  const media = document.querySelector('video');\n  if (!media) return JSON.stringify({found:false});\n  media.muted = true;\n  media.loop = true;\n  if (media.readyState === 0) media.load();\n  if (media.paused) media.play().catch(() => {});\n  return JSON.stringify({\n    found:true,\n    readyState:media.readyState,\n    paused:media.paused,\n    errorCode:media.error?.code || 0,\n    width:media.videoWidth || 0\n  });\n})()" : "(() => {\n  const media = document.querySelector('img');\n  if (!media) return JSON.stringify({found:false});\n  return JSON.stringify({\n    found:true,\n    readyState:media.complete ? 4 : 0,\n    paused:false,\n    errorCode:media.complete && media.naturalWidth === 0 ? 4 : 0,\n    width:media.naturalWidth || 0\n  });\n})()");
		for (int attempt = 0; attempt < attempts; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string json = await _core.ExecuteScriptAsync(script);
			MediaReadyProbe mediaReadyProbe;
			try
			{
				string text = JsonSerializer.Deserialize<string>(json);
				mediaReadyProbe = (string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<MediaReadyProbe>(text, new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				}));
			}
			catch (JsonException)
			{
				mediaReadyProbe = null;
			}
			if ((object)mediaReadyProbe != null && mediaReadyProbe.ErrorCode > 0)
			{
				throw new InvalidOperationException($"미디어 파일을 디코딩하지 못했습니다 (브라우저 오류 코드 {mediaReadyProbe.ErrorCode}).");
			}
			if ((object)mediaReadyProbe != null && mediaReadyProbe.Found && mediaReadyProbe.ReadyState >= 2 && mediaReadyProbe.Width > 0)
			{
				return;
			}
			await Task.Delay(100, cancellationToken);
		}
		throw new TimeoutException(isVideo ? "영상이 8초 안에 재생 준비 상태가 되지 않았습니다." : "이미지가 4초 안에 로드되지 않았습니다.");
	}

	private static string DescribeWindow(nint handle)
	{
		if (handle == IntPtr.Zero)
		{
			return "none";
		}
		StringBuilder stringBuilder = new StringBuilder(128);
		NativeMethods.GetClassNameW(handle, stringBuilder, stringBuilder.Capacity);
		return $"0x{handle:X}({stringBuilder})";
	}

	private static string FormatBytes(ulong bytes)
	{
		return $"{(double)bytes / 1073741824.0:0.0} GB";
	}

	private static string BuildMediaDocument(WallpaperItem wallpaper, string virtualUri, PlaybackOptions options)
	{
		string value = options.Fit switch
		{
			WallpaperFit.Fit => "contain", 
			WallpaperFit.Stretch => "fill", 
			_ => "cover", 
		};
		string value2 = WebUtility.HtmlEncode(wallpaper.Name);
		string text = WebUtility.HtmlEncode(virtualUri);
		string value3 = BuildCssFilter(options);
		string value4 = ((wallpaper.Kind == WallpaperKind.Video) ? ("<video data-vellume-media src=\"" + text + "\" autoplay muted loop playsinline preload=\"auto\"></video>") : $"<img data-vellume-media src=\"{text}\" alt=\"{value2}\" loading=\"eager\">");
		return $"<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n  <title>{value2}</title>\n  <style>\n    html,body {{ width:100%; height:100%; margin:0; overflow:hidden; background:#05070b; }}\n    [data-vellume-media] {{\n      display:block; width:100%; height:100%; object-fit:{value};\n      filter:{value3};\n      user-select:none; pointer-events:none;\n    }}\n  </style>\n</head>\n<body>{value4}</body>\n</html>";
	}

	private static string BuildCssFilter(PlaybackOptions options)
	{
		double num = Math.Clamp(options.Brightness, 0.25, 1.25);
		double num2 = Math.Clamp(options.Contrast, 0.5, 1.5);
		double num3 = Math.Clamp(options.Saturation, 0.0, 1.5);
		CultureInfo invariantCulture = CultureInfo.InvariantCulture;
		return $"brightness({num.ToString("0.###", invariantCulture)}) contrast({num2.ToString("0.###", invariantCulture)}) saturate({num3.ToString("0.###", invariantCulture)})";
	}
}
internal enum WallpaperRenderBackend
{
	None,
	NativeImage,
	NativeVideo,
	WebView,
	StaticFallback
}
internal static class WallpaperRendererBackendPolicy
{
	internal static bool ShouldUseNativeVideo(WallpaperItem wallpaper, PlaybackOptions options)
	{
		if (wallpaper.Kind == WallpaperKind.Video && System.IO.Path.GetExtension(wallpaper.SourcePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && options.Fit != WallpaperFit.Stretch && Math.Abs(options.Brightness - 1.0) < 0.001 && Math.Abs(options.Contrast - 1.0) < 0.001)
		{
			return Math.Abs(options.Saturation - 1.0) < 0.001;
		}
		return false;
	}
}
internal sealed class WallpaperRenderHostClient : IWallpaperRenderer
{
	private readonly string _webViewDataDirectory;

	private readonly string _gpuSafetyDirectory;

	private readonly SemaphoreSlim _requestLock = new SemaphoreSlim(1, 1);

	private NamedPipeServerStream? _pipe;

	private Process? _process;

	private RenderHostAttachment? _attachment;

	private WallpaperItem? _wallpaper;

	private PlaybackOptions? _playbackOptions;

	private RenderHostState _state = RenderHostState.Empty;

	private double _resumePositionSeconds;

	private bool _visible = true;

	private bool _hostAttached;

	private bool _stoppedForIsolation;

	private bool _closed;

	public nint Handle => new IntPtr(_state.Handle);

	public string BackendDisplayName
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(_state.BackendDisplayName))
			{
				return _state.BackendDisplayName;
			}
			return "렌더 호스트 준비 중";
		}
	}

	public bool IsStaticPerformanceMode
	{
		get
		{
			if (!_stoppedForIsolation)
			{
				return _state.IsStaticPerformanceMode;
			}
			return true;
		}
	}

	public bool IsDesktopWallpaperIsolationActive
	{
		get
		{
			if (!_stoppedForIsolation)
			{
				return _state.IsDesktopWallpaperIsolationActive;
			}
			return true;
		}
	}

	public bool HasCompositionWindow
	{
		get
		{
			if (!_stoppedForIsolation)
			{
				return _state.HasCompositionWindow;
			}
			return false;
		}
	}

	public bool IsReady
	{
		get
		{
			if (!_stoppedForIsolation)
			{
				Process process = _process;
				if (process != null && !process.HasExited)
				{
					return _state.IsReady;
				}
			}
			return false;
		}
	}

	internal WallpaperRenderHostClient(string webViewDataDirectory)
	{
		_webViewDataDirectory = webViewDataDirectory;
		string path = System.IO.Path.GetDirectoryName(webViewDataDirectory) ?? webViewDataDirectory;
		_gpuSafetyDirectory = System.IO.Path.Combine(path, "GpuSafety");
	}

	public string GetAttachmentDiagnostics()
	{
		if (!_stoppedForIsolation)
		{
			if (!string.IsNullOrWhiteSpace(_state.AttachmentDiagnostics))
			{
				return _state.AttachmentDiagnostics;
			}
			return "render-host=starting";
		}
		return "backend=Windows wallpaper, render-host=stopped";
	}

	public async Task AttachToAsync(nint hostHandle, DisplayInfo display, bool raisedDesktop, nint shellViewHandle, nint backgroundWorkerHandle, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_closed, this);
		cancellationToken.ThrowIfCancellationRequested();
		_attachment = new RenderHostAttachment
		{
			HostHandle = ((IntPtr)hostHandle).ToInt64(),
			Display = display,
			RaisedDesktop = raisedDesktop,
			ShellViewHandle = ((IntPtr)shellViewHandle).ToInt64(),
			BackgroundWorkerHandle = ((IntPtr)backgroundWorkerHandle).ToInt64()
		};
		_hostAttached = false;
		if (_stoppedForIsolation)
		{
			return;
		}
		NamedPipeServerStream? pipe = _pipe;
		if (pipe != null && pipe.IsConnected)
		{
			Process process = _process;
			if (process != null && !process.HasExited)
			{
				await SendAttachAsync(new RenderHostRequest
				{
					Command = "attach",
					Attachment = _attachment,
					Visible = _visible
				}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				_hostAttached = true;
			}
		}
	}

	public async Task InitializeAndNavigateAsync(WallpaperItem wallpaper, PlaybackOptions options, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_closed, this);
		_wallpaper = CopyWallpaper(wallpaper);
		_playbackOptions = options.Copy();
		_stoppedForIsolation = false;
		bool revealAfterInitialization = ShouldAttachHiddenForInitialization(_hostAttached, _visible);
		await EnsureAttachedAsync(cancellationToken, revealAfterInitialization ? new bool?(false) : null);
		await SendAsync(new RenderHostRequest
		{
			Command = "initialize",
			Wallpaper = _wallpaper,
			Playback = _playbackOptions,
			ResumePositionSeconds = _resumePositionSeconds
		}, cancellationToken);
		if (revealAfterInitialization)
		{
			await SetRenderHostVisibilityAsync(visible: true, cancellationToken);
		}
		_resumePositionSeconds = 0.0;
	}

	public async Task CapturePreviewAsync(string outputPath, CancellationToken cancellationToken)
	{
		await SendAsync(new RenderHostRequest
		{
			Command = "capture",
			OutputPath = outputPath
		}, cancellationToken);
	}

	public bool IsAttachedTo(nint hostHandle)
	{
		if (!_stoppedForIsolation && _state.HasCompositionWindow)
		{
			return _state.ParentHandle == ((IntPtr)hostHandle).ToInt64();
		}
		return false;
	}

	public async Task SetWindowVisibleAsync(bool visible, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_visible = visible;
		if (_stoppedForIsolation || _closed || !_hostAttached)
		{
			return;
		}
		NamedPipeServerStream? pipe = _pipe;
		if (pipe != null && pipe.IsConnected)
		{
			Process process = _process;
			if (process != null && !process.HasExited)
			{
				await SendAsync(new RenderHostRequest
				{
					Command = "set-visible",
					Visible = visible
				}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	public Task PauseAsync()
	{
		if (!_stoppedForIsolation)
		{
			return SendAsync(new RenderHostRequest
			{
				Command = "pause"
			}, CancellationToken.None);
		}
		return Task.CompletedTask;
	}

	public Task ResumeAsync()
	{
		if (!_stoppedForIsolation)
		{
			return SendAsync(new RenderHostRequest
			{
				Command = "resume"
			}, CancellationToken.None);
		}
		return Task.CompletedTask;
	}

	public Task EnterStaticPerformanceModeAsync()
	{
		if (!_stoppedForIsolation)
		{
			return SendAsync(new RenderHostRequest
			{
				Command = "enter-static"
			}, CancellationToken.None);
		}
		return Task.CompletedTask;
	}

	public async Task ExitStaticPerformanceModeAsync()
	{
		if (!_stoppedForIsolation)
		{
			await SendAsync(new RenderHostRequest
			{
				Command = "exit-static"
			}, CancellationToken.None);
			return;
		}
		if (_attachment == null || _wallpaper == null || _playbackOptions == null)
		{
			throw new InvalidOperationException("중지된 월페이퍼 렌더 호스트를 복원할 정보가 없습니다.");
		}
		_stoppedForIsolation = false;
		try
		{
			await EnsureAttachedAsync(CancellationToken.None, false);
			await SendAsync(new RenderHostRequest
			{
				Command = "initialize",
				Wallpaper = _wallpaper,
				Playback = _playbackOptions,
				ResumePositionSeconds = _resumePositionSeconds
			}, CancellationToken.None);
			if (_visible)
			{
				await SetRenderHostVisibilityAsync(visible: true, CancellationToken.None);
			}
			WindowsDesktopWallpaperLease.TryRecover(_gpuSafetyDirectory);
			_resumePositionSeconds = 0.0;
		}
		catch
		{
			_stoppedForIsolation = true;
			await CloseHostConnectionAsync(requestCleanClose: true);
			throw;
		}
	}

	public async Task<bool> DestroyCompositionWindowForIsolationAsync(CancellationToken cancellationToken)
	{
		if (_stoppedForIsolation)
		{
			return true;
		}
		RenderHostResponse renderHostResponse = await SendAsync(new RenderHostRequest
		{
			Command = "stop-for-isolation"
		}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (!renderHostResponse.BooleanResult)
		{
			return false;
		}
		_resumePositionSeconds = renderHostResponse.State?.ResumePositionSeconds ?? 0.0;
		_stoppedForIsolation = true;
		_state = renderHostResponse.State ?? new RenderHostState
		{
			IsStaticPerformanceMode = true,
			IsDesktopWallpaperIsolationActive = true
		};
		await WaitForRenderHostExitAsync().ConfigureAwait(continueOnCapturedContext: false);
		DisposePipeAndProcess();
		return true;
	}

	public async Task UpdatePlaybackAsync(PlaybackOptions options)
	{
		_playbackOptions = options.Copy();
		if (!_stoppedForIsolation)
		{
			await SendAsync(new RenderHostRequest
			{
				Command = "update-playback",
				Playback = _playbackOptions
			}, CancellationToken.None);
		}
	}

	public async Task CloseSafelyAsync()
	{
		if (_closed)
		{
			return;
		}
		_closed = true;
		await _requestLock.WaitAsync();
		try
		{
			if (_stoppedForIsolation)
			{
				WindowsDesktopWallpaperLease.TryRecover(_gpuSafetyDirectory);
			}
			else
			{
				await CloseHostConnectionAsync(requestCleanClose: true);
			}
		}
		finally
		{
			_stoppedForIsolation = false;
			_state = RenderHostState.Empty;
			_requestLock.Release();
			_requestLock.Dispose();
		}
	}

	internal static bool ShouldAttachHiddenForInitialization(bool hostAttached, bool desiredVisibility)
	{
		return !hostAttached && desiredVisibility;
	}

	private async Task EnsureAttachedAsync(CancellationToken cancellationToken, bool? initialVisibility = null)
	{
		if (!_hostAttached)
		{
			RenderHostAttachment attachment = _attachment ?? throw new InvalidOperationException("월페이퍼 RenderHost 연결 정보가 없습니다.");
			await SendAttachAsync(new RenderHostRequest
			{
				Command = "attach",
				Attachment = attachment,
				Visible = (initialVisibility ?? _visible)
			}, cancellationToken);
			_hostAttached = true;
		}
	}

	private async Task SetRenderHostVisibilityAsync(bool visible, CancellationToken cancellationToken)
	{
		await SendAsync(new RenderHostRequest
		{
			Command = "set-visible",
			Visible = visible
		}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task<RenderHostResponse> SendAttachAsync(RenderHostRequest request, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(5.0));
		try
		{
			return await SendAsync(request, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			DisposePipeAndProcess();
			throw new TimeoutException("The wallpaper render host did not attach within 5 seconds.");
		}
	}

	private async Task<RenderHostResponse> SendAsync(RenderHostRequest request, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_closed, this);
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(GetCommandTimeout(request.Command));
		CancellationToken commandToken = timeout.Token;
		bool lockTaken = false;
		try
		{
			await _requestLock.WaitAsync(commandToken).ConfigureAwait(continueOnCapturedContext: false);
			lockTaken = true;
			await EnsureHostAsync(commandToken).ConfigureAwait(continueOnCapturedContext: false);
			NamedPipeServerStream pipe = _pipe ?? throw new InvalidOperationException("월페이퍼 렌더 호스트 파이프가 연결되지 않았습니다.");
			await RenderHostWire.WriteAsync(pipe, request, commandToken).ConfigureAwait(continueOnCapturedContext: false);
			RenderHostResponse renderHostResponse = (await RenderHostWire.ReadAsync<RenderHostResponse>(pipe, commandToken).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new EndOfStreamException("월페이퍼 렌더 호스트가 응답 전에 종료되었습니다.");
			if (!renderHostResponse.Success)
			{
				throw new InvalidOperationException(string.IsNullOrWhiteSpace(renderHostResponse.Error) ? "월페이퍼 렌더 호스트 명령이 실패했습니다." : renderHostResponse.Error);
			}
			if (renderHostResponse.State != null)
			{
				_state = renderHostResponse.State;
			}
			return renderHostResponse;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			if (lockTaken)
			{
				DisposePipeAndProcess();
			}
			throw new TimeoutException("The wallpaper render host command '" + request.Command + "' timed out.");
		}
		catch (OperationCanceledException)
		{
			if (lockTaken)
			{
				DisposePipeAndProcess();
			}
			throw;
		}
		catch
		{
			if (_process?.HasExited ?? true)
			{
				DisposePipeAndProcess();
			}
			throw;
		}
		finally
		{
			if (lockTaken)
			{
				_requestLock.Release();
			}
		}
	}

	private static TimeSpan GetCommandTimeout(string command)
	{
		return command switch
		{
			"attach" => TimeSpan.FromSeconds(5.0), 
			"initialize" => TimeSpan.FromSeconds(30.0), 
			"capture" => TimeSpan.FromSeconds(20.0), 
			_ => TimeSpan.FromSeconds(10.0), 
		};
	}

	private async Task EnsureHostAsync(CancellationToken cancellationToken)
	{
		if (_pipe?.IsConnected ?? false)
		{
			Process process = _process;
			if (process != null && !process.HasExited)
			{
				return;
			}
		}
		DisposePipeAndProcess();
		string text = Environment.GetEnvironmentVariable("VELLUME_RENDER_HOST_EXECUTABLE");
		if (string.IsNullOrWhiteSpace(text))
		{
			text = Environment.ProcessPath;
		}
		if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
		{
			throw new FileNotFoundException("Vellume 렌더 호스트 실행 파일을 찾을 수 없습니다.", text);
		}
		string text2 = $"vellume-render-{Environment.ProcessId}-" + Guid.NewGuid().ToString("N");
		_pipe = new NamedPipeServerStream(text2, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = text,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = AppContext.BaseDirectory
		};
		processStartInfo.ArgumentList.Add("--wallpaper-render-host");
		processStartInfo.ArgumentList.Add("--render-pipe");
		processStartInfo.ArgumentList.Add(text2);
		processStartInfo.ArgumentList.Add("--render-owner");
		processStartInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
		processStartInfo.ArgumentList.Add("--render-data");
		processStartInfo.ArgumentList.Add(_webViewDataDirectory);
		_process = Process.Start(processStartInfo) ?? throw new InvalidOperationException("월페이퍼 렌더 호스트 프로세스를 시작하지 못했습니다.");
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(8.0));
		try
		{
			await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
			if (_process == null || !RenderHostPipeSecurity.IsExpectedClient(_pipe, _process.Id))
			{
				throw new UnauthorizedAccessException("예상한 RenderHost 프로세스가 파이프에 연결하지 않았습니다.");
			}
		}
		catch
		{
			DisposePipeAndProcess();
			throw new TimeoutException("월페이퍼 렌더 호스트가 8초 안에 연결되지 않았습니다.");
		}
	}

	private async Task CloseHostConnectionAsync(bool requestCleanClose)
	{
		NamedPipeServerStream pipe = _pipe;
		if (requestCleanClose && (pipe?.IsConnected ?? false))
		{
			Process process = _process;
			if (process != null && !process.HasExited)
			{
				try
				{
					await RenderHostWire.WriteAsync(pipe, new RenderHostRequest
					{
						Command = "close"
					}, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
					await RenderHostWire.ReadAsync<RenderHostResponse>(pipe, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3.0)).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch
				{
				}
			}
		}
		await WaitForRenderHostExitAsync().ConfigureAwait(continueOnCapturedContext: false);
		DisposePipeAndProcess();
	}

	private async Task WaitForRenderHostExitAsync()
	{
		try
		{
			Process process = _process;
			if (process != null && !process.HasExited)
			{
				await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3.0)).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch
		{
		}
	}

	private void DisposePipeAndProcess()
	{
		_pipe?.Dispose();
		_pipe = null;
		_process?.Dispose();
		_process = null;
		_hostAttached = false;
	}

	private static WallpaperItem CopyWallpaper(WallpaperItem wallpaper)
	{
		return new WallpaperItem
		{
			Id = wallpaper.Id,
			Name = wallpaper.Name,
			SourcePath = wallpaper.SourcePath,
			Kind = wallpaper.Kind,
			IsBuiltIn = wallpaper.IsBuiltIn
		};
	}
}
public static class WallpaperRenderHostServer
{
	public static async Task RunAsync(string pipeName, string webViewDataDirectory, int ownerProcessId, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(pipeName))
		{
			throw new ArgumentException("렌더 호스트 파이프 이름이 비어 있습니다.", "pipeName");
		}
		using NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		connectTimeout.CancelAfter(TimeSpan.FromSeconds(8.0));
		await pipe.ConnectAsync(connectTimeout.Token);
		if (!RenderHostPipeSecurity.IsExpectedServer(pipe, ownerProcessId))
		{
			throw new UnauthorizedAccessException("예상한 Vellume 제어 프로세스의 파이프가 아닙니다.");
		}
		WallpaperRendererWindow renderer = null;
		RenderHostAttachment attachment = null;
		bool shutdownRequested = false;
		try
		{
			while (!shutdownRequested && !cancellationToken.IsCancellationRequested)
			{
				RenderHostRequest renderHostRequest = await RenderHostWire.ReadAsync<RenderHostRequest>(pipe, cancellationToken);
				if (renderHostRequest == null)
				{
					break;
				}
				RenderHostResponse value;
				try
				{
					if (renderer == null)
					{
						renderer = new WallpaperRendererWindow(webViewDataDirectory);
					}
					bool booleanResult2 = true;
					switch (renderHostRequest.Command)
					{
					case "attach":
						attachment = renderHostRequest.Attachment ?? throw new InvalidOperationException("렌더 호스트 연결 정보가 없습니다.");
						renderer.SetWindowVisible(renderHostRequest.Visible);
						Attach(renderer, attachment);
						renderer.SetWindowVisible(renderHostRequest.Visible);
						break;
					case "initialize":
					{
						WallpaperItem wallpaper = renderHostRequest.Wallpaper ?? throw new InvalidOperationException("월페이퍼 정보가 없습니다.");
						PlaybackOptions options = renderHostRequest.Playback ?? new PlaybackOptions();
						renderer.SetResumePositionForRestore(renderHostRequest.ResumePositionSeconds);
						await renderer.InitializeAndNavigateAsync(wallpaper, options, cancellationToken);
						break;
					}
					case "capture":
						await renderer.CapturePreviewAsync(renderHostRequest.OutputPath ?? throw new InvalidOperationException("캡처 출력 경로가 없습니다."), cancellationToken);
						break;
					case "set-visible":
						renderer.SetWindowVisible(renderHostRequest.Visible);
						break;
					case "pause":
						await renderer.PauseAsync();
						break;
					case "resume":
						await renderer.ResumeAsync();
						break;
					case "enter-static":
						await renderer.EnterStaticPerformanceModeAsync();
						break;
					case "exit-static":
						await renderer.ExitStaticPerformanceModeAsync();
						break;
					case "update-playback":
						await renderer.UpdatePlaybackAsync(renderHostRequest.Playback ?? new PlaybackOptions());
						break;
					case "stop-for-isolation":
					{
						booleanResult2 = renderer.DestroyCompositionWindowForIsolation();
						RenderHostState state = CaptureState(renderer, attachment);
						if (booleanResult2)
						{
							renderer.DetachDesktopWallpaperLeaseForProcessExit();
						}
						value = RenderHostResponse.Ok(state, booleanResult2);
						await RenderHostWire.WriteAsync(pipe, value, cancellationToken);
						if (booleanResult2)
						{
							await renderer.CloseSafelyAsync();
							renderer = null;
							shutdownRequested = true;
						}
						continue;
					}
					case "close":
						await renderer.CloseSafelyAsync();
						renderer = null;
						shutdownRequested = true;
						break;
					default:
						throw new InvalidOperationException("알 수 없는 렌더 호스트 명령: " + renderHostRequest.Command);
					}
					value = RenderHostResponse.Ok(CaptureState(renderer, attachment), booleanResult2);
				}
				catch (Exception ex)
				{
					value = RenderHostResponse.Failed(ex.Message, CaptureState(renderer, attachment));
				}
				await RenderHostWire.WriteAsync(pipe, value, cancellationToken);
			}
		}
		finally
		{
			if (renderer != null)
			{
				await renderer.CloseSafelyAsync();
			}
		}
	}

	private static void Attach(WallpaperRendererWindow renderer, RenderHostAttachment attachment)
	{
		DisplayInfo display = attachment.Display ?? throw new InvalidOperationException("렌더 호스트 모니터 정보가 없습니다.");
		renderer.AttachTo(new IntPtr(attachment.HostHandle), display, attachment.RaisedDesktop, new IntPtr(attachment.ShellViewHandle), new IntPtr(attachment.BackgroundWorkerHandle));
	}

	private static RenderHostState CaptureState(WallpaperRendererWindow? renderer, RenderHostAttachment? attachment)
	{
		if (renderer != null)
		{
			return new RenderHostState
			{
				Handle = ((IntPtr)renderer.Handle).ToInt64(),
				ParentHandle = (attachment?.HostHandle ?? 0),
				BackendDisplayName = renderer.BackendDisplayName,
				IsStaticPerformanceMode = renderer.IsStaticPerformanceMode,
				IsDesktopWallpaperIsolationActive = renderer.IsDesktopWallpaperIsolationActive,
				HasCompositionWindow = renderer.HasCompositionWindow,
				IsReady = renderer.IsReady,
				AttachmentDiagnostics = renderer.GetAttachmentDiagnostics(),
				ResumePositionSeconds = renderer.ResumePositionSeconds
			};
		}
		return RenderHostState.Empty;
	}
}
internal static class RenderHostCommands
{
	internal const string Attach = "attach";

	internal const string Initialize = "initialize";

	internal const string Capture = "capture";

	internal const string SetVisible = "set-visible";

	internal const string Pause = "pause";

	internal const string Resume = "resume";

	internal const string EnterStatic = "enter-static";

	internal const string ExitStatic = "exit-static";

	internal const string UpdatePlayback = "update-playback";

	internal const string StopForIsolation = "stop-for-isolation";

	internal const string Close = "close";
}
internal static class RenderHostPipeSecurity
{
	internal static bool IsExpectedClient(NamedPipeServerStream pipe, int expectedProcessId)
	{
		if (expectedProcessId > 0 && GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
		{
			return clientProcessId == (uint)expectedProcessId;
		}
		return false;
	}

	internal static bool IsExpectedServer(NamedPipeClientStream pipe, int expectedProcessId)
	{
		if (expectedProcessId > 0 && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId))
		{
			return serverProcessId == (uint)expectedProcessId;
		}
		return false;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
internal sealed class RenderHostRequest
{
	public string Command { get; set; } = string.Empty;


	public RenderHostAttachment? Attachment { get; set; }

	public WallpaperItem? Wallpaper { get; set; }

	public PlaybackOptions? Playback { get; set; }

	public string? OutputPath { get; set; }

	public bool Visible { get; set; }

	public double ResumePositionSeconds { get; set; }
}
internal sealed class RenderHostAttachment
{
	public long HostHandle { get; set; }

	public DisplayInfo? Display { get; set; }

	public bool RaisedDesktop { get; set; }

	public long ShellViewHandle { get; set; }

	public long BackgroundWorkerHandle { get; set; }
}
internal sealed class RenderHostState
{
	internal static RenderHostState Empty => new RenderHostState();

	public long Handle { get; set; }

	public long ParentHandle { get; set; }

	public string BackendDisplayName { get; set; } = string.Empty;


	public bool IsStaticPerformanceMode { get; set; }

	public bool IsDesktopWallpaperIsolationActive { get; set; }

	public bool HasCompositionWindow { get; set; }

	public bool IsReady { get; set; }

	public string AttachmentDiagnostics { get; set; } = string.Empty;


	public double ResumePositionSeconds { get; set; }
}
internal sealed class RenderHostResponse
{
	public bool Success { get; set; }

	public string Error { get; set; } = string.Empty;


	public bool BooleanResult { get; set; }

	public RenderHostState? State { get; set; }

	internal static RenderHostResponse Ok(RenderHostState state, bool booleanResult)
	{
		return new RenderHostResponse
		{
			Success = true,
			BooleanResult = booleanResult,
			State = state
		};
	}

	internal static RenderHostResponse Failed(string error, RenderHostState state)
	{
		return new RenderHostResponse
		{
			Success = false,
			Error = error,
			State = state
		};
	}
}
internal static class RenderHostWire
{
	private const int MaxMessageBytes = 16777216;

	private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	};

	internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
	{
		byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
		if (payload.Length > 16777216)
		{
			throw new InvalidDataException("렌더 호스트 메시지가 허용 크기를 초과했습니다.");
		}
		byte[] bytes = BitConverter.GetBytes(payload.Length);
		await stream.WriteAsync(bytes, cancellationToken);
		await stream.WriteAsync(payload, cancellationToken);
		await stream.FlushAsync(cancellationToken);
	}

	internal static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
	{
		byte[] header = new byte[4];
		if (!(await ReadExactlyOrEofAsync(stream, header, cancellationToken)))
		{
			return default(T);
		}
		int num = BitConverter.ToInt32(header, 0);
		if (num <= 0 || num > 16777216)
		{
			throw new InvalidDataException("렌더 호스트 메시지 길이가 잘못되었습니다.");
		}
		byte[] payload = new byte[num];
		if (!(await ReadExactlyOrEofAsync(stream, payload, cancellationToken)))
		{
			throw new EndOfStreamException("렌더 호스트 메시지가 중간에 끊겼습니다.");
		}
		return JsonSerializer.Deserialize<T>(payload, Options);
	}

	private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
	{
		int num;
		for (int offset = 0; offset < buffer.Length; offset += num)
		{
			num = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
			if (num == 0)
			{
				if (offset != 0)
				{
					throw new EndOfStreamException();
				}
				return false;
			}
		}
		return true;
	}
}
internal static class WebViewEnvironmentProvider
{
	private static readonly ConcurrentDictionary<string, Lazy<Task<CoreWebView2Environment>>> Environments = new ConcurrentDictionary<string, Lazy<Task<CoreWebView2Environment>>>(StringComparer.OrdinalIgnoreCase);

	public static async Task<CoreWebView2Environment> GetAsync(string userDataDirectory)
	{
		string normalizedDirectory = System.IO.Path.GetFullPath(userDataDirectory);
		Lazy<Task<CoreWebView2Environment>> lazyEnvironment = Environments.GetOrAdd(normalizedDirectory, delegate(string directory)
		{
			string directory2 = directory;
			return new Lazy<Task<CoreWebView2Environment>>(() => CoreWebView2Environment.CreateAsync(null, directory2), LazyThreadSafetyMode.ExecutionAndPublication);
		});
		try
		{
			return await lazyEnvironment.Value;
		}
		catch
		{
			Environments.TryRemove(new KeyValuePair<string, Lazy<Task<CoreWebView2Environment>>>(normalizedDirectory, lazyEnvironment));
			throw;
		}
	}
}
internal readonly record struct DesktopWallpaperMonitor(string Id, System.Drawing.Rectangle Bounds);
internal interface IDesktopWallpaperPlatform : IDisposable
{
	IReadOnlyList<DesktopWallpaperMonitor> GetMonitors();

	string GetWallpaper(string monitorId);

	void SetWallpaper(string monitorId, string wallpaperPath);
}
internal sealed class WindowsDesktopWallpaperLease : IDisposable
{
	private sealed record WallpaperLeaseMarker(string MonitorId, string OriginalWallpaperPath, string FramePath);

	private const string MarkerFileName = "active-wallpaper-lease.json";

	private readonly IDesktopWallpaperPlatform _platform;

	private readonly string _safetyDirectory;

	private readonly string _markerPath;

	private readonly WallpaperLeaseMarker _marker;

	private bool _restored;

	private WindowsDesktopWallpaperLease(IDesktopWallpaperPlatform platform, string safetyDirectory, WallpaperLeaseMarker marker)
	{
		_platform = platform;
		_safetyDirectory = safetyDirectory;
		_markerPath = System.IO.Path.Combine(safetyDirectory, "active-wallpaper-lease.json");
		_marker = marker;
	}

	internal static WindowsDesktopWallpaperLease? TryActivate(string safetyDirectory, System.Drawing.Rectangle targetBounds, nint bitmapHandle, IDesktopWallpaperPlatform? platform = null)
	{
		if (bitmapHandle == IntPtr.Zero)
		{
			return null;
		}
		bool flag = platform == null;
		string text = null;
		bool flag2 = false;
		try
		{
			Directory.CreateDirectory(safetyDirectory);
			string text2 = System.IO.Path.Combine(safetyDirectory, "active-wallpaper-lease.json");
			if (File.Exists(text2))
			{
				return null;
			}
			if (platform == null)
			{
				platform = new WindowsDesktopWallpaperPlatform();
			}
			DesktopWallpaperMonitor desktopWallpaperMonitor = FindTargetMonitor(platform.GetMonitors(), targetBounds);
			if (string.IsNullOrWhiteSpace(desktopWallpaperMonitor.Id))
			{
				throw new InvalidOperationException("The target Windows monitor could not be resolved.");
			}
			text = System.IO.Path.Combine(safetyDirectory, $"unreal-safety-{Guid.NewGuid():N}.bmp");
			SaveBitmap(bitmapHandle, text);
			WallpaperLeaseMarker marker = new WallpaperLeaseMarker(desktopWallpaperMonitor.Id, platform.GetWallpaper(desktopWallpaperMonitor.Id), text);
			WriteMarkerAtomically(safetyDirectory, text2, marker);
			flag2 = true;
			platform.SetWallpaper(desktopWallpaperMonitor.Id, text);
			return new WindowsDesktopWallpaperLease(platform, safetyDirectory, marker);
		}
		catch
		{
			DeleteSafetyFile(safetyDirectory, text);
			if (flag2)
			{
				DeleteMarkerIfOwned(safetyDirectory, System.IO.Path.Combine(safetyDirectory, "active-wallpaper-lease.json"), text);
			}
			if (flag)
			{
				platform?.Dispose();
			}
			return null;
		}
	}

	internal static bool TryRecover(string safetyDirectory, IDesktopWallpaperPlatform? platform = null)
	{
		string path = System.IO.Path.Combine(safetyDirectory, "active-wallpaper-lease.json");
		if (!File.Exists(path))
		{
			return false;
		}
		bool flag = platform == null;
		try
		{
			WallpaperLeaseMarker wallpaperLeaseMarker = JsonSerializer.Deserialize<WallpaperLeaseMarker>(File.ReadAllText(path));
			if ((object)wallpaperLeaseMarker == null || string.IsNullOrWhiteSpace(wallpaperLeaseMarker.MonitorId))
			{
				return false;
			}
			if (platform == null)
			{
				platform = new WindowsDesktopWallpaperPlatform();
			}
			platform.SetWallpaper(wallpaperLeaseMarker.MonitorId, wallpaperLeaseMarker.OriginalWallpaperPath);
			DeleteSafetyFile(safetyDirectory, wallpaperLeaseMarker.FramePath);
			DeleteSafetyFile(safetyDirectory, path);
			return true;
		}
		catch
		{
			return false;
		}
		finally
		{
			if (flag)
			{
				platform?.Dispose();
			}
		}
	}

	internal static bool HasPendingRecovery(string safetyDirectory)
	{
		return File.Exists(System.IO.Path.Combine(safetyDirectory, "active-wallpaper-lease.json"));
	}

	public void Dispose()
	{
		if (_restored)
		{
			return;
		}
		try
		{
			_platform.SetWallpaper(_marker.MonitorId, _marker.OriginalWallpaperPath);
			DeleteSafetyFile(_safetyDirectory, _marker.FramePath);
			DeleteSafetyFile(_safetyDirectory, _markerPath);
		}
		catch
		{
		}
		finally
		{
			_restored = true;
			_platform.Dispose();
		}
	}

	internal void DetachForProcessExit()
	{
		if (!_restored)
		{
			_restored = true;
			_platform.Dispose();
		}
	}

	private static DesktopWallpaperMonitor FindTargetMonitor(IReadOnlyList<DesktopWallpaperMonitor> monitors, System.Drawing.Rectangle targetBounds)
	{
		DesktopWallpaperMonitor result = monitors.FirstOrDefault((DesktopWallpaperMonitor monitor) => monitor.Bounds == targetBounds);
		if (!string.IsNullOrWhiteSpace(result.Id))
		{
			return result;
		}
		var anon = (from monitor in monitors
			where monitor.Bounds.Width > 0 && monitor.Bounds.Height > 0
			select new
			{
				Monitor = monitor,
				Intersection = IntersectionArea(monitor.Bounds, targetBounds),
				ContainsCenter = ContainsCenter(monitor.Bounds, targetBounds)
			} into candidate
			orderby candidate.Intersection descending, candidate.ContainsCenter descending
			select candidate).ToArray().FirstOrDefault();
		if (anon == null || (anon.Intersection <= 0 && !anon.ContainsCenter))
		{
			return default(DesktopWallpaperMonitor);
		}
		return anon.Monitor;
	}

	private static long IntersectionArea(System.Drawing.Rectangle first, System.Drawing.Rectangle second)
	{
		System.Drawing.Rectangle rectangle = System.Drawing.Rectangle.Intersect(first, second);
		return Math.Max(0L, rectangle.Width) * Math.Max(0L, rectangle.Height);
	}

	private static bool ContainsCenter(System.Drawing.Rectangle monitorBounds, System.Drawing.Rectangle targetBounds)
	{
		long num = targetBounds.Left + (long)targetBounds.Width / 2L;
		long num2 = targetBounds.Top + (long)targetBounds.Height / 2L;
		if (num >= monitorBounds.Left && num < monitorBounds.Right && num2 >= monitorBounds.Top)
		{
			return num2 < monitorBounds.Bottom;
		}
		return false;
	}

	private static void SaveBitmap(nint bitmapHandle, string outputPath)
	{
		using Bitmap original = System.Drawing.Image.FromHbitmap(bitmapHandle);
		using Bitmap bitmap = new Bitmap(original);
		bitmap.Save(outputPath, ImageFormat.Bmp);
	}

	private static void WriteMarkerAtomically(string safetyDirectory, string markerPath, WallpaperLeaseMarker marker)
	{
		string text = System.IO.Path.Combine(safetyDirectory, $".{"active-wallpaper-lease.json"}.{Guid.NewGuid():N}.tmp");
		try
		{
			byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(marker));
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			{
				fileStream.Write(bytes);
				fileStream.Flush(flushToDisk: true);
			}
			File.Move(text, markerPath, overwrite: false);
		}
		finally
		{
			DeleteSafetyFile(safetyDirectory, text);
		}
	}

	private static void DeleteMarkerIfOwned(string safetyDirectory, string markerPath, string? framePath)
	{
		if (string.IsNullOrWhiteSpace(framePath))
		{
			return;
		}
		try
		{
			WallpaperLeaseMarker wallpaperLeaseMarker = JsonSerializer.Deserialize<WallpaperLeaseMarker>(File.ReadAllText(markerPath));
			if ((object)wallpaperLeaseMarker != null && System.IO.Path.GetFullPath(wallpaperLeaseMarker.FramePath).Equals(System.IO.Path.GetFullPath(framePath), StringComparison.OrdinalIgnoreCase))
			{
				DeleteSafetyFile(safetyDirectory, markerPath);
			}
		}
		catch
		{
		}
	}

	private static void DeleteSafetyFile(string safetyDirectory, string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}
		try
		{
			ReadOnlySpan<char> readOnlySpan = System.IO.Path.GetFullPath(safetyDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
			char reference = System.IO.Path.DirectorySeparatorChar;
			string value = string.Concat(readOnlySpan, new ReadOnlySpan<char>(ref reference));
			string fullPath = System.IO.Path.GetFullPath(path);
			if (fullPath.StartsWith(value, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
			{
				File.Delete(fullPath);
			}
		}
		catch
		{
		}
	}
}
internal sealed class WindowsDesktopWallpaperPlatform : IDesktopWallpaperPlatform, IDisposable
{
	[ComImport]
	[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDesktopWallpaperNative
	{
		void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaperPath);

		void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaperPath);

		void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);

		void GetMonitorDevicePathCount(out uint count);

		void GetMonitorRect([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out NativeRectangle rectangle);
	}

	private struct NativeRectangle
	{
		internal int Left;

		internal int Top;

		internal int Right;

		internal int Bottom;
	}

	private static readonly Guid DesktopWallpaperClassId = new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

	private object? _comObject;

	private IDesktopWallpaperNative? _native;

	internal WindowsDesktopWallpaperPlatform()
	{
		Type type = Type.GetTypeFromCLSID(DesktopWallpaperClassId, throwOnError: true) ?? throw new InvalidOperationException("Windows DesktopWallpaper COM class is unavailable.");
		_comObject = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows DesktopWallpaper COM class could not start.");
		_native = (IDesktopWallpaperNative)_comObject;
	}

	public IReadOnlyList<DesktopWallpaperMonitor> GetMonitors()
	{
		IDesktopWallpaperNative native = GetNative();
		native.GetMonitorDevicePathCount(out var count);
		List<DesktopWallpaperMonitor> list = new List<DesktopWallpaperMonitor>(checked((int)count));
		for (uint num = 0u; num < count; num++)
		{
			native.GetMonitorDevicePathAt(num, out string monitorId);
			native.GetMonitorRect(monitorId, out var rectangle);
			list.Add(new DesktopWallpaperMonitor(monitorId, System.Drawing.Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom)));
		}
		return list;
	}

	public string GetWallpaper(string monitorId)
	{
		GetNative().GetWallpaper(monitorId, out string wallpaperPath);
		return wallpaperPath ?? string.Empty;
	}

	public void SetWallpaper(string monitorId, string wallpaperPath)
	{
		GetNative().SetWallpaper(monitorId, wallpaperPath);
	}

	public void Dispose()
	{
		_native = null;
		if (_comObject != null && Marshal.IsComObject(_comObject))
		{
			Marshal.FinalReleaseComObject(_comObject);
		}
		_comObject = null;
	}

	private IDesktopWallpaperNative GetNative()
	{
		return _native ?? throw new ObjectDisposedException("WindowsDesktopWallpaperPlatform");
	}
}
public sealed record DockWindowInfo(nint Handle, string Title);
public sealed record DockApplicationSnapshot(string Key, string Name, string ExecutablePath, IReadOnlyList<DockWindowInfo> Windows, bool IsForeground, bool HasAttention);
public sealed class WindowsDockRuntimeService : IDisposable
{
	private delegate bool EnumWindowsCallback(nint window, nint parameter);

	private static readonly HashSet<string> ShellHostProcessNames = new HashSet<string>(new string[6] { "ApplicationFrameHost", "SystemSettings", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost" }, StringComparer.OrdinalIgnoreCase);

	private const int ShellWindowActivated = 4;

	private const int ShellRudeAppActivated = 32772;

	private const int ShellFlash = 32774;

	private const int ShowRestore = 9;

	private const int ShowMinimized = 2;

	private const int GwOwner = 4;

	private const int GwlExStyle = -20;

	private const long WsExToolWindow = 128L;

	private readonly int _vellumeProcessId = Environment.ProcessId;

	private readonly Dictionary<string, int> _activationIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> _attentionProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly object _attentionLock = new object();

	private nint _messageWindow;

	public int ShellHookMessage { get; }

	public event EventHandler? StateChanged;

	public WindowsDockRuntimeService()
	{
		ShellHookMessage = RegisterWindowMessageW("SHELLHOOK");
	}

	public void Attach(nint messageWindow)
	{
		Detach();
		if (messageWindow != IntPtr.Zero && RegisterShellHookWindow(messageWindow))
		{
			_messageWindow = messageWindow;
		}
	}

	public bool TryHandleShellMessage(int message, nint wParam, nint lParam)
	{
		if (message != ShellHookMessage || ShellHookMessage == 0)
		{
			return false;
		}
		this.StateChanged?.Invoke(this, EventArgs.Empty);
		string text = TryGetProcessNameForWindow(lParam);
		if (string.IsNullOrWhiteSpace(text))
		{
			return true;
		}
		lock (_attentionLock)
		{
			bool flag;
			switch ((int)((IntPtr)wParam).ToInt64())
			{
			case 32774:
				_attentionProcessNames.Add(text);
				goto end_IL_0044;
			case 4:
			case 32772:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				_attentionProcessNames.Remove(text);
			}
			end_IL_0044:;
		}
		return true;
	}

	public bool HasAttention(string executablePath)
	{
		string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(executablePath);
		lock (_attentionLock)
		{
			return !string.IsNullOrWhiteSpace(fileNameWithoutExtension) && _attentionProcessNames.Contains(fileNameWithoutExtension);
		}
	}

	public IReadOnlyList<DockApplicationSnapshot> Capture()
	{
		nint foreground = GetForegroundWindow();
		List<(nint Handle, uint ProcessId, string Title)> windows = new List<(nint, uint, string)>();
		EnumWindows(delegate(nint window, nint _)
		{
			if (!IsTaskWindow(window))
			{
				return true;
			}
			GetWindowThreadProcessId(window, out var processId);
			if (processId == 0 || processId == _vellumeProcessId)
			{
				return true;
			}
			string windowTitle = GetWindowTitle(window);
			if (string.IsNullOrWhiteSpace(windowTitle))
			{
				return true;
			}
			windows.Add((window, processId, windowTitle));
			return true;
		}, IntPtr.Zero);
		List<DockApplicationSnapshot> list = new List<DockApplicationSnapshot>();
		foreach (IGrouping<uint, (nint, uint, string)> item in from item in windows
			group item by item.ProcessId)
		{
			string text;
			string processName;
			try
			{
				using Process process = Process.GetProcessById((int)item.Key);
				text = process.MainModule?.FileName;
				processName = process.ProcessName;
			}
			catch
			{
				continue;
			}
			if (!string.IsNullOrWhiteSpace(text) && !IsShellHostProcessName(processName))
			{
				string fullPath = System.IO.Path.GetFullPath(text);
				DockWindowInfo[] array = item.Select<(nint, uint, string), DockWindowInfo>(((nint Handle, uint ProcessId, string Title) item) => new DockWindowInfo(item.Handle, item.Title)).ToArray();
				list.Add(new DockApplicationSnapshot(fullPath, GetFriendlyName(fullPath, processName), fullPath, array, array.Any((DockWindowInfo item) => item.Handle == foreground), HasAttention(fullPath)));
			}
		}
		return list.GroupBy<DockApplicationSnapshot, string>((DockApplicationSnapshot item) => item.Key, StringComparer.OrdinalIgnoreCase).Select(delegate(IGrouping<string, DockApplicationSnapshot> group)
		{
			DockApplicationSnapshot dockApplicationSnapshot = group.First();
			DockWindowInfo[] windows2 = group.SelectMany((DockApplicationSnapshot item) => item.Windows).DistinctBy((DockWindowInfo item) => item.Handle).ToArray();
			return dockApplicationSnapshot with
			{
				Windows = windows2,
				IsForeground = group.Any((DockApplicationSnapshot item) => item.IsForeground),
				HasAttention = group.Any((DockApplicationSnapshot item) => item.HasAttention)
			};
		}).OrderBy<DockApplicationSnapshot, string>((DockApplicationSnapshot item) => item.Name, StringComparer.CurrentCultureIgnoreCase)
			.ToArray();
	}

	public bool Activate(DockApplicationSnapshot application, bool minimizeWhenForeground = true)
	{
		DockWindowInfo[] array = application.Windows.Where((DockWindowInfo item) => IsWindow(item.Handle)).ToArray();
		if (array.Length == 0)
		{
			return false;
		}
		nint foregroundWindow = GetForegroundWindow();
		if (minimizeWhenForeground && array.Length == 1 && array[0].Handle == foregroundWindow)
		{
			ShowWindowAsync(foregroundWindow, 2);
			return true;
		}
		_activationIndexes.TryGetValue(application.Key, out var value);
		int num = ((application.IsForeground && array.Length > 1) ? ((value + 1) % array.Length) : Math.Clamp(value, 0, array.Length - 1));
		_activationIndexes[application.Key] = num;
		bool num2 = ActivateWindow(array[num].Handle);
		if (num2)
		{
			ClearAttention(application.ExecutablePath);
		}
		return num2;
	}

	public bool ActivateWindow(nint window)
	{
		if (!IsWindow(window))
		{
			return false;
		}
		if (IsIconic(window))
		{
			ShowWindowAsync(window, 9);
		}
		return SetForegroundWindow(window);
	}

	public void LaunchNew(string executablePath)
	{
		if (!File.Exists(executablePath))
		{
			throw new FileNotFoundException("Dock 실행 파일을 찾을 수 없습니다.", executablePath);
		}
		if (IsShellHostProcessName(System.IO.Path.GetFileNameWithoutExtension(executablePath)))
		{
			throw new InvalidOperationException("Windows 셸 호스트는 Dock에서 새로 실행할 수 없습니다.");
		}
		Process.Start(new ProcessStartInfo
		{
			FileName = executablePath,
			UseShellExecute = true
		});
	}

	internal static bool IsShellHostProcessName(string? processName)
	{
		if (!string.IsNullOrWhiteSpace(processName))
		{
			return ShellHostProcessNames.Contains(processName);
		}
		return true;
	}

	public void Dispose()
	{
		Detach();
		lock (_attentionLock)
		{
			_attentionProcessNames.Clear();
		}
	}

	private static bool IsTaskWindow(nint window)
	{
		if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero)
		{
			return false;
		}
		if ((((IntPtr)GetWindowLongPtr(window, -20)).ToInt64() & 0x80) != 0L)
		{
			return false;
		}
		bool flag;
		switch (GetWindowClass(window))
		{
		case "Shell_TrayWnd":
		case "Shell_SecondaryTrayWnd":
		case "Progman":
		case "WorkerW":
		case "Windows.UI.Core.CoreWindow":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		return !flag;
	}

	private static string GetWindowTitle(nint window)
	{
		int windowTextLengthW = GetWindowTextLengthW(window);
		if (windowTextLengthW <= 0)
		{
			return string.Empty;
		}
		StringBuilder stringBuilder = new StringBuilder(windowTextLengthW + 1);
		GetWindowTextW(window, stringBuilder, stringBuilder.Capacity);
		return stringBuilder.ToString().Trim();
	}

	private static string GetWindowClass(nint window)
	{
		StringBuilder stringBuilder = new StringBuilder(128);
		if (GetClassNameW(window, stringBuilder, stringBuilder.Capacity) <= 0)
		{
			return string.Empty;
		}
		return stringBuilder.ToString();
	}

	private static string GetFriendlyName(string executablePath, string fallback)
	{
		try
		{
			string fileDescription = FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
			return string.IsNullOrWhiteSpace(fileDescription) ? fallback : fileDescription.Trim();
		}
		catch
		{
			return fallback;
		}
	}

	private static string? TryGetProcessNameForWindow(nint window)
	{
		if (window == IntPtr.Zero)
		{
			return null;
		}
		GetWindowThreadProcessId(window, out var processId);
		if (processId == 0)
		{
			return null;
		}
		try
		{
			using Process process = Process.GetProcessById((int)processId);
			return process.ProcessName;
		}
		catch
		{
			return null;
		}
	}

	private void ClearAttention(string executablePath)
	{
		string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(executablePath);
		lock (_attentionLock)
		{
			if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension))
			{
				_attentionProcessNames.Remove(fileNameWithoutExtension);
			}
		}
	}

	private void Detach()
	{
		if (_messageWindow != IntPtr.Zero)
		{
			DeregisterShellHookWindow(_messageWindow);
			_messageWindow = IntPtr.Zero;
		}
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterShellHookWindow(nint window);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeregisterShellHookWindow(nint window);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int RegisterWindowMessageW(string messageName);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindow(nint window);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextLengthW(nint window);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextW(nint window, StringBuilder text, int maximumCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(nint window, StringBuilder className, int maximumCount);

	[DllImport("user32.dll")]
	private static extern nint GetWindow(nint window, int command);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern nint GetWindowLongPtr(nint window, int index);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsIconic(nint window);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindowAsync(nint window, int command);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(nint window);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();
}
public sealed class WindowsIconService
{
	private const int MaximumCachedIcons = 256;

	private readonly object _cacheGate = new object();

	private readonly Dictionary<string, ImageSource?> _cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	public ImageSource? GetIcon(string targetPath, string customIconPath = "", SystemShortcutKind? systemKind = null, bool dynamicState = false)
	{
		try
		{
			bool flag = false;
			if (!string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
			{
				string extension = System.IO.Path.GetExtension(customIconPath);
				if (!extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
				{
					targetPath = customIconPath;
					flag = true;
				}
				else
				{
					targetPath = customIconPath;
				}
			}
			(string, int)? tuple = systemKind switch
			{
				SystemShortcutKind.ThisPc => TryGetStockLocation(94u), 
				SystemShortcutKind.RecycleBin => (!dynamicState) ? TryGetStockLocation(31u) : TryGetStockLocation(32u), 
				_ => null, 
			};
			string path = tuple?.Item1 ?? targetPath;
			int num = tuple?.Item2 ?? 0;
			string key = $"{System.IO.Path.GetFullPath(path)}|{GetFileStamp(path)}|{num}|{systemKind}|{dynamicState}|{flag}";
			lock (_cacheGate)
			{
				if (_cache.TryGetValue(key, out ImageSource value))
				{
					return value;
				}
			}
			ImageSource imageSource = (flag ? LoadBitmap(path) : ExtractLargeIcon(path, num));
			lock (_cacheGate)
			{
				if (_cache.Count >= 256)
				{
					_cache.Clear();
				}
				_cache[key] = imageSource;
			}
			return imageSource;
		}
		catch
		{
			return null;
		}
	}

	private static ImageSource? LoadBitmap(string path)
	{
		BitmapImage bitmapImage = new BitmapImage();
		bitmapImage.BeginInit();
		bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
		bitmapImage.UriSource = new Uri(path, UriKind.Absolute);
		bitmapImage.EndInit();
		bitmapImage.Freeze();
		return bitmapImage;
	}

	private static ImageSource? ExtractLargeIcon(string path, int iconIndex)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		nint[] array = new nint[1];
		uint[] iconIdentifiers = new uint[1];
		if (NativeMethods.PrivateExtractIcons(path, iconIndex, 256, 256, array, iconIdentifiers, 1u, 0u) == 0 || array[0] == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHIcon(array[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(256, 256));
			bitmapSource.Freeze();
			return bitmapSource;
		}
		finally
		{
			NativeMethods.DestroyIcon(array[0]);
		}
	}

	private static string GetFileStamp(string path)
	{
		if (!File.Exists(path))
		{
			return "missing";
		}
		FileInfo fileInfo = new FileInfo(path);
		return $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
	}

	private static (string Path, int Index)? TryGetStockLocation(uint stockIconId)
	{
		NativeMethods.StockIconInfo stockIconInfo = default(NativeMethods.StockIconInfo);
		stockIconInfo.Size = (uint)Marshal.SizeOf<NativeMethods.StockIconInfo>();
		NativeMethods.StockIconInfo stockIconInfo2 = stockIconInfo;
		if (NativeMethods.SHGetStockIconInfo(stockIconId, 0u, ref stockIconInfo2) < 0 || string.IsNullOrWhiteSpace(stockIconInfo2.Path))
		{
			return null;
		}
		return (stockIconInfo2.Path, stockIconInfo2.IconIndex);
	}
}
internal sealed record MediaSessionSnapshot(bool IsAvailable, bool IsPlaying, string Title, string Artist, string Source, double Progress, string ArtworkDataUri);
internal sealed class WindowsMediaSessionService : IDisposable
{
	private readonly object _subscriptionSync = new object();

	private readonly SemaphoreSlim _managerInitialization = new SemaphoreSlim(1, 1);

	private GlobalSystemMediaTransportControlsSessionManager? _manager;

	private readonly HashSet<GlobalSystemMediaTransportControlsSession> _subscribedSessions = new HashSet<GlobalSystemMediaTransportControlsSession>(ReferenceEqualityComparer.Instance);

	private string _lastArtworkKey = string.Empty;

	private string _lastArtworkDataUri = string.Empty;

	private bool _disposed;

	public string? LastError { get; private set; }

	public int LastSessionCount { get; private set; }

	public event EventHandler? Changed;

	public async Task<MediaSessionSnapshot> GetSnapshotAsync()
	{
		_ = 2;
		try
		{
			GlobalSystemMediaTransportControlsSessionManager globalSystemMediaTransportControlsSessionManager = await GetManagerAsync();
			GlobalSystemMediaTransportControlsSession current = globalSystemMediaTransportControlsSessionManager.GetCurrentSession();
			List<GlobalSystemMediaTransportControlsSession> list = globalSystemMediaTransportControlsSessionManager.GetSessions().ToList();
			if ((object)current != null && !list.Any((GlobalSystemMediaTransportControlsSession session) => (object)session == current))
			{
				list.Insert(0, current);
			}
			LastSessionCount = list.Count;
			LastError = null;
			if (list.Count == 0)
			{
				return Empty();
			}
			var array = list.Select((GlobalSystemMediaTransportControlsSession session) => new
			{
				Session = session,
				Playback = TryGetPlaybackInfo(session),
				IsCurrent = ((object)session == current)
			}).OrderByDescending(candidate =>
			{
				GlobalSystemMediaTransportControlsSessionPlaybackInfo playback2 = candidate.Playback;
				return (object)playback2 != null && playback2.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
			}).ThenByDescending(candidate => candidate.IsCurrent)
				.ToArray();
			var array2 = array;
			foreach (var candidate2 in array2)
			{
				try
				{
					GlobalSystemMediaTransportControlsSessionMediaProperties globalSystemMediaTransportControlsSessionMediaProperties = await candidate2.Session.TryGetMediaPropertiesAsync();
					GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = candidate2.Playback ?? candidate2.Session.GetPlaybackInfo();
					GlobalSystemMediaTransportControlsSessionTimelineProperties timelineProperties = candidate2.Session.GetTimelineProperties();
					TimeSpan timeSpan = timelineProperties.EndTime - timelineProperties.StartTime;
					TimeSpan timeSpan2 = timelineProperties.Position - timelineProperties.StartTime;
					double progress = ((timeSpan > TimeSpan.Zero) ? Math.Clamp(timeSpan2.TotalMilliseconds / timeSpan.TotalMilliseconds, 0.0, 1.0) : 0.0);
					string title = (string.IsNullOrWhiteSpace(globalSystemMediaTransportControlsSessionMediaProperties.Title) ? "제목 없는 미디어" : globalSystemMediaTransportControlsSessionMediaProperties.Title.Trim());
					string artist = (string.IsNullOrWhiteSpace(globalSystemMediaTransportControlsSessionMediaProperties.Artist) ? "아티스트 정보 없음" : globalSystemMediaTransportControlsSessionMediaProperties.Artist.Trim());
					string source = FormatSource(candidate2.Session.SourceAppUserModelId);
					string artworkDataUri = await GetArtworkDataUriAsync(globalSystemMediaTransportControlsSessionMediaProperties.Thumbnail, $"{candidate2.Session.SourceAppUserModelId}|{title}|{artist}");
					return new MediaSessionSnapshot(IsAvailable: true, playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, title, artist, source, progress, artworkDataUri);
				}
				catch
				{
				}
			}
			LastError = "Windows 미디어 세션은 있지만 메타데이터를 읽지 못했습니다.";
			return Empty();
		}
		catch (Exception ex)
		{
			LastError = ex.Message;
			LastSessionCount = 0;
			ResetManager();
			return Empty();
		}
	}

	public async Task<bool> TogglePlaybackAsync()
	{
		GlobalSystemMediaTransportControlsSession globalSystemMediaTransportControlsSession = await GetBestSessionAsync();
		bool flag = (object)globalSystemMediaTransportControlsSession != null;
		if (flag)
		{
			flag = await globalSystemMediaTransportControlsSession.TryTogglePlayPauseAsync();
		}
		return flag;
	}

	public async Task<bool> SkipNextAsync()
	{
		GlobalSystemMediaTransportControlsSession globalSystemMediaTransportControlsSession = await GetBestSessionAsync();
		bool flag = (object)globalSystemMediaTransportControlsSession != null;
		if (flag)
		{
			flag = await globalSystemMediaTransportControlsSession.TrySkipNextAsync();
		}
		return flag;
	}

	public async Task<bool> SkipPreviousAsync()
	{
		GlobalSystemMediaTransportControlsSession globalSystemMediaTransportControlsSession = await GetBestSessionAsync();
		bool flag = (object)globalSystemMediaTransportControlsSession != null;
		if (flag)
		{
			flag = await globalSystemMediaTransportControlsSession.TrySkipPreviousAsync();
		}
		return flag;
	}

	private async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
	{
		await _managerInitialization.WaitAsync();
		try
		{
			lock (_subscriptionSync)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if ((object)_manager != null)
				{
					return _manager;
				}
			}
			GlobalSystemMediaTransportControlsSessionManager globalSystemMediaTransportControlsSessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
			lock (_subscriptionSync)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if ((object)_manager != null)
				{
					return _manager;
				}
				_manager = globalSystemMediaTransportControlsSessionManager;
				try
				{
					globalSystemMediaTransportControlsSessionManager.CurrentSessionChanged += ManagerOnCurrentSessionChanged;
					globalSystemMediaTransportControlsSessionManager.SessionsChanged += ManagerOnSessionsChanged;
				}
				catch
				{
					_manager = null;
					TryDetachManagerEvents(globalSystemMediaTransportControlsSessionManager);
					throw;
				}
			}
			RewireSessionEvents(globalSystemMediaTransportControlsSessionManager);
			lock (_subscriptionSync)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if ((object)_manager != globalSystemMediaTransportControlsSessionManager)
				{
					throw new InvalidOperationException("Windows 미디어 세션 관리자가 초기화 중 재설정되었습니다.");
				}
				return globalSystemMediaTransportControlsSessionManager;
			}
		}
		finally
		{
			_managerInitialization.Release();
		}
	}

	private async Task<GlobalSystemMediaTransportControlsSession?> GetBestSessionAsync()
	{
		GlobalSystemMediaTransportControlsSessionManager obj = await GetManagerAsync();
		GlobalSystemMediaTransportControlsSession currentSession = obj.GetCurrentSession();
		return obj.GetSessions().OrderByDescending(delegate(GlobalSystemMediaTransportControlsSession session)
		{
			GlobalSystemMediaTransportControlsSessionPlaybackInfo? globalSystemMediaTransportControlsSessionPlaybackInfo = TryGetPlaybackInfo(session);
			return (object)globalSystemMediaTransportControlsSessionPlaybackInfo != null && globalSystemMediaTransportControlsSessionPlaybackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
		}).FirstOrDefault() ?? currentSession;
	}

	private static GlobalSystemMediaTransportControlsSessionPlaybackInfo? TryGetPlaybackInfo(GlobalSystemMediaTransportControlsSession session)
	{
		try
		{
			return session.GetPlaybackInfo();
		}
		catch
		{
			return null;
		}
	}

	private static MediaSessionSnapshot Empty()
	{
		return new MediaSessionSnapshot(IsAvailable: false, IsPlaying: false, "재생 중인 미디어 없음", "브라우저의 미디어 키 연동을 확인하세요", "WINDOWS 미디어", 0.0, string.Empty);
	}

	private async Task<string> GetArtworkDataUriAsync(IRandomAccessStreamReference? thumbnail, string artworkKey)
	{
		if (artworkKey.Equals(_lastArtworkKey, StringComparison.Ordinal))
		{
			return _lastArtworkDataUri;
		}
		string artwork = string.Empty;
		try
		{
			if (thumbnail != null)
			{
				using IRandomAccessStreamWithContentType stream = await thumbnail.OpenReadAsync();
				ulong size = stream.Size;
				if (size != 0 && size <= 4194304)
				{
					uint byteCount = checked((uint)stream.Size);
					using DataReader reader = new DataReader(stream.GetInputStreamAt(0uL));
					if (await reader.LoadAsync(byteCount) == byteCount)
					{
						byte[] array = new byte[byteCount];
						reader.ReadBytes(array);
						string text = (stream.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? stream.ContentType : "image/jpeg");
						artwork = "data:" + text + ";base64," + Convert.ToBase64String(array);
					}
				}
			}
		}
		catch
		{
		}
		_lastArtworkKey = artworkKey;
		_lastArtworkDataUri = artwork;
		return artwork;
	}

	private static string FormatSource(string? source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return "WINDOWS 미디어";
		}
		string text = source.ToLowerInvariant();
		if (text.Contains("whale"))
		{
			return "네이버 웨일";
		}
		if (text.Contains("firefox"))
		{
			return "파이어폭스";
		}
		if (text.Contains("chrome"))
		{
			return "구글 크롬";
		}
		if (text.Contains("edge"))
		{
			return "마이크로소프트 엣지";
		}
		string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(source);
		if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension))
		{
			return fileNameWithoutExtension.Replace('.', ' ').ToUpperInvariant();
		}
		return source.ToUpperInvariant();
	}

	public void Dispose()
	{
		lock (_subscriptionSync)
		{
			if (!_disposed)
			{
				_disposed = true;
				ResetManagerCore();
				this.Changed = null;
			}
		}
	}

	private void ManagerOnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs eventArgs)
	{
		if (RewireSessionEvents(sender))
		{
			RaiseChangedForManager(sender);
		}
	}

	private void ManagerOnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs eventArgs)
	{
		if (RewireSessionEvents(sender))
		{
			RaiseChangedForManager(sender);
		}
	}

	private void SessionOnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs eventArgs)
	{
		RaiseChangedForSession(sender);
	}

	private void SessionOnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs eventArgs)
	{
		RaiseChangedForSession(sender);
	}

	private void SessionOnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs eventArgs)
	{
		RaiseChangedForSession(sender);
	}

	private bool RewireSessionEvents(GlobalSystemMediaTransportControlsSessionManager manager)
	{
		GlobalSystemMediaTransportControlsSession[] array;
		try
		{
			array = manager.GetSessions().OfType<GlobalSystemMediaTransportControlsSession>().ToArray();
		}
		catch
		{
			return false;
		}
		lock (_subscriptionSync)
		{
			if (_disposed || (object)_manager != manager)
			{
				return false;
			}
			HashSet<object> currentSet = array.ToHashSet(ReferenceEqualityComparer.Instance);
			GlobalSystemMediaTransportControlsSession[] array2 = _subscribedSessions.Where((GlobalSystemMediaTransportControlsSession session) => !currentSet.Contains(session)).ToArray();
			foreach (GlobalSystemMediaTransportControlsSession session2 in array2)
			{
				UnsubscribeSessionCore(session2);
			}
			array2 = array;
			foreach (GlobalSystemMediaTransportControlsSession session3 in array2)
			{
				SubscribeSessionCore(session3);
			}
			return true;
		}
	}

	private void SubscribeSessionCore(GlobalSystemMediaTransportControlsSession? session)
	{
		if ((object)session == null || !_subscribedSessions.Add(session))
		{
			return;
		}
		try
		{
			session.MediaPropertiesChanged += SessionOnMediaPropertiesChanged;
			session.PlaybackInfoChanged += SessionOnPlaybackInfoChanged;
			session.TimelinePropertiesChanged += SessionOnTimelinePropertiesChanged;
		}
		catch
		{
			_subscribedSessions.Remove(session);
			TryDetachSessionEvents(session);
		}
	}

	private void UnsubscribeSessionCore(GlobalSystemMediaTransportControlsSession? session)
	{
		if ((object)session != null && _subscribedSessions.Remove(session))
		{
			TryDetachSessionEvents(session);
		}
	}

	private void ResetManager()
	{
		lock (_subscriptionSync)
		{
			ResetManagerCore();
		}
	}

	private void ResetManagerCore()
	{
		GlobalSystemMediaTransportControlsSessionManager manager = _manager;
		_manager = null;
		GlobalSystemMediaTransportControlsSession[] array = _subscribedSessions.ToArray();
		_subscribedSessions.Clear();
		GlobalSystemMediaTransportControlsSession[] array2 = array;
		foreach (GlobalSystemMediaTransportControlsSession session in array2)
		{
			TryDetachSessionEvents(session);
		}
		if ((object)manager != null)
		{
			TryDetachManagerEvents(manager);
		}
	}

	private void RaiseChangedForManager(GlobalSystemMediaTransportControlsSessionManager manager)
	{
		EventHandler changed;
		lock (_subscriptionSync)
		{
			if (_disposed || (object)_manager != manager)
			{
				return;
			}
			changed = this.Changed;
		}
		changed?.Invoke(this, EventArgs.Empty);
	}

	private void RaiseChangedForSession(GlobalSystemMediaTransportControlsSession session)
	{
		EventHandler changed;
		lock (_subscriptionSync)
		{
			if (_disposed || !_subscribedSessions.Contains(session))
			{
				return;
			}
			changed = this.Changed;
		}
		changed?.Invoke(this, EventArgs.Empty);
	}

	private void TryDetachManagerEvents(GlobalSystemMediaTransportControlsSessionManager manager)
	{
		try
		{
			manager.CurrentSessionChanged -= ManagerOnCurrentSessionChanged;
		}
		catch
		{
		}
		try
		{
			manager.SessionsChanged -= ManagerOnSessionsChanged;
		}
		catch
		{
		}
	}

	private void TryDetachSessionEvents(GlobalSystemMediaTransportControlsSession session)
	{
		try
		{
			session.MediaPropertiesChanged -= SessionOnMediaPropertiesChanged;
		}
		catch
		{
		}
		try
		{
			session.PlaybackInfoChanged -= SessionOnPlaybackInfoChanged;
		}
		catch
		{
		}
		try
		{
			session.TimelinePropertiesChanged -= SessionOnTimelinePropertiesChanged;
		}
		catch
		{
		}
	}
}
public sealed class WindowsMonitorService : IMonitorService
{
	public IReadOnlyList<DisplayInfo> GetDisplays()
	{
		List<DisplayInfo> displays = new List<DisplayInfo>();
		if (!NativeMethods.EnumDisplayMonitors(callback: delegate(nint monitor, nint _, nint _, nint _)
		{
			NativeMethods.MonitorInfoEx monitorInfoEx = default(NativeMethods.MonitorInfoEx);
			monitorInfoEx.Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>();
			monitorInfoEx.DeviceName = string.Empty;
			NativeMethods.MonitorInfoEx monitorInfo = monitorInfoEx;
			if (NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo))
			{
				displays.Add(new DisplayInfo
				{
					Id = monitorInfo.DeviceName,
					Name = monitorInfo.DeviceName.Replace("\\\\.\\", string.Empty),
					X = monitorInfo.Monitor.Left,
					Y = monitorInfo.Monitor.Top,
					Width = monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
					Height = monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top,
					IsPrimary = ((monitorInfo.Flags & 1) != 0)
				});
			}
			return true;
		}, deviceContext: IntPtr.Zero, clipRectangle: IntPtr.Zero, parameter: IntPtr.Zero))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에서 연결된 모니터 목록을 가져오지 못했습니다.");
		}
		return (from display in displays
			orderby display.IsPrimary descending, display.X
			select display).ToArray();
	}
}
internal readonly record struct ExclusiveGpuProcessSnapshot(bool IsRunning, int ProcessId, string ProcessName);
internal readonly record struct ExclusiveGpuIsolationDecision(ExclusiveGpuProcessSnapshot Snapshot, TimeSpan? RecheckAfter);
internal sealed class ExclusiveGpuIsolationStabilizer
{
	private readonly TimeSpan _releaseDelay;

	private DateTimeOffset? _releaseCandidateSince;

	private ExclusiveGpuProcessSnapshot _lastRunningSnapshot;

	internal ExclusiveGpuIsolationStabilizer(TimeSpan releaseDelay)
	{
		if (releaseDelay < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException("releaseDelay");
		}
		_releaseDelay = releaseDelay;
	}

	internal ExclusiveGpuIsolationDecision Update(ExclusiveGpuProcessSnapshot rawSnapshot, bool currentlyIsolated, DateTimeOffset now)
	{
		if (rawSnapshot.IsRunning)
		{
			_lastRunningSnapshot = rawSnapshot;
			_releaseCandidateSince = null;
			return new ExclusiveGpuIsolationDecision(rawSnapshot, null);
		}
		if (!currentlyIsolated || !_lastRunningSnapshot.IsRunning || _releaseDelay == TimeSpan.Zero)
		{
			Reset();
			return new ExclusiveGpuIsolationDecision(default(ExclusiveGpuProcessSnapshot), null);
		}
		DateTimeOffset valueOrDefault = _releaseCandidateSince.GetValueOrDefault();
		if (!_releaseCandidateSince.HasValue)
		{
			valueOrDefault = now;
			_releaseCandidateSince = valueOrDefault;
		}
		TimeSpan timeSpan = now - _releaseCandidateSince.Value;
		if (timeSpan >= _releaseDelay)
		{
			Reset();
			return new ExclusiveGpuIsolationDecision(default(ExclusiveGpuProcessSnapshot), null);
		}
		return new ExclusiveGpuIsolationDecision(_lastRunningSnapshot, _releaseDelay - timeSpan);
	}

	internal void Reset()
	{
		_releaseCandidateSince = null;
		_lastRunningSnapshot = default(ExclusiveGpuProcessSnapshot);
	}
}
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
public enum QuickSlotActivationResult
{
	Started,
	Activated
}
public readonly record struct QuickSlotRuntimeState(bool IsRunning, bool HasAttention);
public sealed record QuickSlotRuntimeSnapshot(IReadOnlyDictionary<int, QuickSlotRuntimeState> QuickSlots, IReadOnlyDictionary<SystemShortcutKind, QuickSlotRuntimeState> SystemShortcuts);
public sealed class WindowsQuickSlotRuntimeService : IDisposable
{
	private delegate bool EnumWindowsCallback(nint window, nint parameter);

	private const int ShellWindowActivated = 4;

	private const int ShellRudeAppActivated = 32772;

	private const int ShellFlash = 32774;

	private const int ShowRestore = 9;

	private const int GetWindowOwner = 4;

	private const int WindowLongExtendedStyle = -20;

	private const long WindowStyleToolWindow = 128L;

	private readonly object _attentionLock = new object();

	private readonly HashSet<string> _attentionProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private nint _messageWindow;

	private bool _disposed;

	public int ShellHookMessage { get; }

	public event EventHandler? StateChanged;

	public WindowsQuickSlotRuntimeService()
	{
		ShellHookMessage = RegisterWindowMessageW("SHELLHOOK");
	}

	public void Attach(nint messageWindow)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_messageWindow != messageWindow)
		{
			Detach();
			if (messageWindow != IntPtr.Zero && RegisterShellHookWindow(messageWindow))
			{
				_messageWindow = messageWindow;
			}
		}
	}

	public bool TryHandleShellMessage(int message, nint wParam, nint lParam)
	{
		if (_disposed || ShellHookMessage == 0 || message != ShellHookMessage)
		{
			return false;
		}
		int num = (int)((IntPtr)wParam).ToInt64();
		this.StateChanged?.Invoke(this, EventArgs.Empty);
		string text = TryGetProcessName(TryGetExecutablePathForWindow(lParam));
		if (string.IsNullOrWhiteSpace(text))
		{
			return true;
		}
		lock (_attentionLock)
		{
			bool flag;
			switch (num)
			{
			case 32774:
				_attentionProcessNames.Add(text);
				goto end_IL_005a;
			case 4:
			case 32772:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				_attentionProcessNames.Remove(text);
			}
			end_IL_005a:;
		}
		return true;
	}

	public QuickSlotRuntimeSnapshot Capture(IReadOnlyList<QuickSlotItem> quickSlots, IReadOnlyList<SystemShortcutSetting> systemShortcuts)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ClearForegroundAttention();
		Dictionary<string, QuickSlotRuntimeState> cache = new Dictionary<string, QuickSlotRuntimeState>(StringComparer.OrdinalIgnoreCase);
		Dictionary<int, QuickSlotRuntimeState> dictionary = new Dictionary<int, QuickSlotRuntimeState>();
		foreach (QuickSlotItem quickSlot in quickSlots)
		{
			QuickSlotRuntimeState targetState = GetTargetState(quickSlot.TargetPath, cache);
			dictionary[quickSlot.Slot] = targetState;
		}
		Dictionary<SystemShortcutKind, QuickSlotRuntimeState> dictionary2 = new Dictionary<SystemShortcutKind, QuickSlotRuntimeState>();
		foreach (SystemShortcutSetting systemShortcut in systemShortcuts)
		{
			Dictionary<SystemShortcutKind, QuickSlotRuntimeState> dictionary3 = dictionary2;
			SystemShortcutKind kind = systemShortcut.Kind;
			QuickSlotRuntimeState value;
			switch (systemShortcut.Kind)
			{
			case SystemShortcutKind.PowerShell:
				value = GetTargetState(SystemShortcutSetting.GetDefaultTargetPath(SystemShortcutKind.PowerShell), cache);
				break;
			case SystemShortcutKind.ThisPc:
			case SystemShortcutKind.FileExplorer:
				value = GetExplorerState();
				break;
			default:
				value = default(QuickSlotRuntimeState);
				break;
			}
			dictionary3[kind] = value;
		}
		return new QuickSlotRuntimeSnapshot(dictionary, dictionary2);
	}

	public async Task<QuickSlotActivationResult> ActivateOrLaunchAsync(QuickSlotItem item, bool forceNewInstance, CancellationToken cancellationToken = default(CancellationToken))
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellationToken.ThrowIfCancellationRequested();
		if (!forceNewInstance && TryActivateExecutable(item.TargetPath))
		{
			ClearAttention(item.TargetPath);
			return QuickSlotActivationResult.Activated;
		}
		await LaunchTargetAsync(item.TargetPath);
		ClearAttention(item.TargetPath);
		return QuickSlotActivationResult.Started;
	}

	public bool TryActivateSystemShortcut(SystemShortcutKind kind)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		bool flag;
		switch (kind)
		{
		case SystemShortcutKind.PowerShell:
		{
			string defaultTargetPath = SystemShortcutSetting.GetDefaultTargetPath(kind);
			bool num = TryActivateExecutable(defaultTargetPath);
			if (num)
			{
				ClearAttention(defaultTargetPath);
			}
			return num;
		}
		case SystemShortcutKind.ThisPc:
		case SystemShortcutKind.FileExplorer:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			nint num2 = FindTopLevelWindow((uint _) => true, "CabinetWClass");
			if (num2 != IntPtr.Zero)
			{
				ActivateWindow(num2);
				string text = TryGetExecutablePathForWindow(num2);
				if (!string.IsNullOrWhiteSpace(text))
				{
					ClearAttention(text);
				}
				return true;
			}
		}
		return false;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		Detach();
		lock (_attentionLock)
		{
			_attentionProcessNames.Clear();
		}
	}

	private QuickSlotRuntimeState GetTargetState(string targetPath, Dictionary<string, QuickSlotRuntimeState> cache)
	{
		if (!TryNormalizeExecutablePath(targetPath, out string executablePath))
		{
			return default(QuickSlotRuntimeState);
		}
		if (cache.TryGetValue(executablePath, out var value))
		{
			return value;
		}
		string text = TryGetProcessName(executablePath);
		HashSet<uint> processIds = GetProcessIds(executablePath);
		bool hasAttention;
		lock (_attentionLock)
		{
			hasAttention = text != null && _attentionProcessNames.Contains(text);
			if (processIds.Count == 0)
			{
				if (text != null)
				{
					_attentionProcessNames.Remove(text);
				}
				hasAttention = false;
			}
		}
		return cache[executablePath] = new QuickSlotRuntimeState(processIds.Count > 0, hasAttention);
	}

	private QuickSlotRuntimeState GetExplorerState()
	{
		nint num = FindTopLevelWindow((uint _) => true, "CabinetWClass");
		if (num == IntPtr.Zero)
		{
			return default(QuickSlotRuntimeState);
		}
		string text = TryGetExecutablePathForWindow(num);
		bool hasAttention = false;
		if (!string.IsNullOrWhiteSpace(text))
		{
			string text2 = TryGetProcessName(text);
			lock (_attentionLock)
			{
				hasAttention = text2 != null && _attentionProcessNames.Contains(text2);
			}
		}
		return new QuickSlotRuntimeState(IsRunning: true, hasAttention);
	}

	private bool TryActivateExecutable(string targetPath)
	{
		if (!TryNormalizeExecutablePath(targetPath, out string executablePath))
		{
			return false;
		}
		HashSet<uint> processIds = GetProcessIds(executablePath);
		if (processIds.Count == 0)
		{
			return false;
		}
		nint num = FindTopLevelWindow(processIds.Contains, null);
		if (num == IntPtr.Zero)
		{
			return false;
		}
		ActivateWindow(num);
		return true;
	}

	private static void ActivateWindow(nint window)
	{
		ShowWindowAsync(window, 9);
		SetForegroundWindow(window);
	}

	private static async Task LaunchTargetAsync(string targetPath)
	{
		bool isShellTarget = targetPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
		bool flag = !isShellTarget;
		if (flag)
		{
			flag = await PackagedAppLauncher.TryLaunchAsync(targetPath);
		}
		if (!flag)
		{
			if (!isShellTarget && !File.Exists(targetPath) && !Directory.Exists(targetPath))
			{
				throw new FileNotFoundException("The quick-slot target no longer exists.", targetPath);
			}
			Process.Start(isShellTarget ? new ProcessStartInfo
			{
				FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
				Arguments = targetPath,
				UseShellExecute = true
			} : new ProcessStartInfo
			{
				FileName = targetPath,
				UseShellExecute = true
			});
		}
	}

	private static HashSet<uint> GetProcessIds(string executablePath)
	{
		string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(executablePath);
		HashSet<uint> hashSet = new HashSet<uint>();
		if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
		{
			return hashSet;
		}
		Process[] processesByName = Process.GetProcessesByName(fileNameWithoutExtension);
		foreach (Process process in processesByName)
		{
			using (process)
			{
				try
				{
					string text = process.MainModule?.FileName;
					if (!string.IsNullOrWhiteSpace(text) && ExecutablePathIdentity.AreEquivalent(executablePath, text))
					{
						hashSet.Add((uint)process.Id);
					}
				}
				catch
				{
				}
			}
		}
		return hashSet;
	}

	private static nint FindTopLevelWindow(Func<uint, bool> processPredicate, string? className)
	{
		Func<uint, bool> processPredicate2 = processPredicate;
		string className2 = className;
		nint result = IntPtr.Zero;
		EnumWindows(delegate(nint window, nint _)
		{
			if (!IsTaskWindow(window))
			{
				return true;
			}
			GetWindowThreadProcessId(window, out var processId);
			if (!processPredicate2(processId) || (className2 != null && !GetWindowClass(window).Equals(className2, StringComparison.Ordinal)))
			{
				return true;
			}
			result = window;
			return false;
		}, IntPtr.Zero);
		return result;
	}

	private static bool IsTaskWindow(nint window)
	{
		if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero)
		{
			return false;
		}
		if ((((IntPtr)GetWindowLongPtrW(window, -20)).ToInt64() & 0x80) != 0L)
		{
			return false;
		}
		bool flag;
		switch (GetWindowClass(window))
		{
		case "Shell_TrayWnd":
		case "Shell_SecondaryTrayWnd":
		case "Progman":
		case "WorkerW":
		case "Windows.UI.Core.CoreWindow":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		return !flag;
	}

	private static string GetWindowClass(nint window)
	{
		StringBuilder stringBuilder = new StringBuilder(128);
		if (GetClassNameW(window, stringBuilder, stringBuilder.Capacity) <= 0)
		{
			return string.Empty;
		}
		return stringBuilder.ToString();
	}

	private static string? TryGetExecutablePathForWindow(nint window)
	{
		if (window == IntPtr.Zero)
		{
			return null;
		}
		GetWindowThreadProcessId(window, out var processId);
		if (processId == 0)
		{
			return null;
		}
		try
		{
			using Process process = Process.GetProcessById((int)processId);
			string text = process.MainModule?.FileName;
			return string.IsNullOrWhiteSpace(text) ? null : System.IO.Path.GetFullPath(text);
		}
		catch
		{
			return null;
		}
	}

	private static bool TryNormalizeExecutablePath(string targetPath, out string executablePath)
	{
		executablePath = string.Empty;
		try
		{
			if (!File.Exists(targetPath) || !System.IO.Path.GetExtension(targetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			executablePath = System.IO.Path.GetFullPath(targetPath);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void ClearForegroundAttention()
	{
		string text = TryGetExecutablePathForWindow(GetForegroundWindow());
		if (!string.IsNullOrWhiteSpace(text))
		{
			ClearAttention(text);
		}
	}

	private void ClearAttention(string targetPath)
	{
		string text = TryGetProcessName(targetPath);
		if (text == null)
		{
			return;
		}
		lock (_attentionLock)
		{
			_attentionProcessNames.Remove(text);
		}
	}

	private static string? TryGetProcessName(string? targetPath)
	{
		try
		{
			string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(targetPath);
			return string.IsNullOrWhiteSpace(fileNameWithoutExtension) ? null : fileNameWithoutExtension;
		}
		catch
		{
			return null;
		}
	}

	private void Detach()
	{
		if (_messageWindow != IntPtr.Zero)
		{
			DeregisterShellHookWindow(_messageWindow);
			_messageWindow = IntPtr.Zero;
		}
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterShellHookWindow(nint window);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeregisterShellHookWindow(nint window);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int RegisterWindowMessageW(string messageName);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll")]
	private static extern nint GetWindow(nint window, int command);

	[DllImport("user32.dll")]
	private static extern nint GetWindowLongPtrW(nint window, int index);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(nint window, StringBuilder className, int maximumCount);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindowAsync(nint window, int command);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(nint window);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();
}
public sealed class WindowsSystemEventMonitor : IDisposable
{
	private struct ShellChangeNotifyEntry
	{
		internal nint Pidl;

		[MarshalAs(UnmanagedType.Bool)]
		internal bool Recursive;
	}

	private delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime);

	private enum PendingEvent
	{
		Foreground,
		WindowInventory,
		DesktopShell,
		RecycleBin
	}

	private const uint EventSystemForeground = 3u;

	private const uint EventSystemMinimizeStart = 22u;

	private const uint EventSystemMinimizeEnd = 23u;

	private const uint WinEventOutOfContext = 0u;

	private const uint WinEventSkipOwnProcess = 2u;

	private const int CsidlBitBucket = 10;

	private const int RecycleBinMessage = 32854;

	private const int ShellChangeSources = 3;

	private const int ShellChangeAllEvents = int.MaxValue;

	private const int WindowStylePopup = int.MinValue;

	private const int WindowStyleExToolWindow = 128;

	private const int WindowStyleExNoActivate = 134217728;

	private readonly Dispatcher _dispatcher;

	private readonly WinEventCallback _winEventCallback;

	private readonly List<nint> _winEventHooks = new List<nint>();

	private HwndSource? _messageSource;

	private uint _recycleBinRegistration;

	private int _taskbarCreatedMessage;

	private int _foregroundEventPending;

	private int _windowInventoryEventPending;

	private int _desktopShellEventPending;

	private int _recycleBinEventPending;

	private bool _disposed;

	public bool RecycleBinEventsAvailable => _recycleBinRegistration != 0;

	public event EventHandler? ForegroundWindowChanged;

	public event EventHandler? WindowInventoryChanged;

	public event EventHandler? DesktopShellChanged;

	public event EventHandler? RecycleBinChanged;

	public WindowsSystemEventMonitor(Dispatcher dispatcher)
	{
		_dispatcher = dispatcher;
		_winEventCallback = WinEventReceived;
		CreateMessageWindow();
		RegisterWinEvents();
		RegisterRecycleBinEvents();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (_recycleBinRegistration != 0)
		{
			SHChangeNotifyDeregister(_recycleBinRegistration);
			_recycleBinRegistration = 0u;
		}
		foreach (nint winEventHook in _winEventHooks)
		{
			if (winEventHook != IntPtr.Zero)
			{
				UnhookWinEvent(winEventHook);
			}
		}
		_winEventHooks.Clear();
		if (_messageSource != null)
		{
			_messageSource.RemoveHook(MessageWindowHook);
			_messageSource.Dispose();
			_messageSource = null;
		}
	}

	private void CreateMessageWindow()
	{
		HwndSourceParameters hwndSourceParameters = new HwndSourceParameters("Vellume Windows Event Monitor");
		hwndSourceParameters.Width = 0;
		hwndSourceParameters.Height = 0;
		hwndSourceParameters.WindowStyle = int.MinValue;
		hwndSourceParameters.ExtendedWindowStyle = 134217856;
		HwndSourceParameters parameters = hwndSourceParameters;
		_messageSource = new HwndSource(parameters);
		_messageSource.AddHook(MessageWindowHook);
		_taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
	}

	private void RegisterWinEvents()
	{
		AddWinEventHook(3u, 3u);
		AddWinEventHook(22u, 23u);
	}

	private void AddWinEventHook(uint minimum, uint maximum)
	{
		nint num = SetWinEventHook(minimum, maximum, IntPtr.Zero, _winEventCallback, 0u, 0u, 2u);
		if (num != IntPtr.Zero)
		{
			_winEventHooks.Add(num);
		}
	}

	private void RegisterRecycleBinEvents()
	{
		if (_messageSource == null || SHGetSpecialFolderLocation(IntPtr.Zero, 10, out var itemIdList) < 0 || itemIdList == IntPtr.Zero)
		{
			return;
		}
		try
		{
			ShellChangeNotifyEntry shellChangeNotifyEntry = default(ShellChangeNotifyEntry);
			shellChangeNotifyEntry.Pidl = itemIdList;
			shellChangeNotifyEntry.Recursive = true;
			ShellChangeNotifyEntry entries = shellChangeNotifyEntry;
			_recycleBinRegistration = SHChangeNotifyRegister(_messageSource.Handle, 3, int.MaxValue, 32854, 1, ref entries);
		}
		finally
		{
			Marshal.FreeCoTaskMem(itemIdList);
		}
	}

	private nint MessageWindowHook(nint window, int message, nint wParam, nint lParam, ref bool handled)
	{
		if (_disposed)
		{
			return IntPtr.Zero;
		}
		if (message == 32854)
		{
			QueueEvent(PendingEvent.RecycleBin, this.RecycleBinChanged);
		}
		else if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
		{
			QueueEvent(PendingEvent.DesktopShell, this.DesktopShellChanged);
		}
		return IntPtr.Zero;
	}

	private void WinEventReceived(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime)
	{
		if (!_disposed && ((eventType == 3 || eventType - 22 <= 1) ? true : false))
		{
			QueueEvent(PendingEvent.Foreground, this.ForegroundWindowChanged);
			QueueEvent(PendingEvent.WindowInventory, this.WindowInventoryChanged);
		}
	}

	private void QueueEvent(PendingEvent pendingEvent, EventHandler? handler)
	{
		EventHandler handler2 = handler;
		ref int pendingFlag = ref GetPendingFlag(pendingEvent);
		if (handler2 == null || Interlocked.Exchange(ref pendingFlag, 1) != 0)
		{
			return;
		}
		_dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			Interlocked.Exchange(ref GetPendingFlag(pendingEvent), 0);
			if (!_disposed)
			{
				handler2(this, EventArgs.Empty);
			}
		});
	}

	private ref int GetPendingFlag(PendingEvent pendingEvent)
	{
		return pendingEvent switch
		{
			PendingEvent.Foreground => ref _foregroundEventPending, 
			PendingEvent.WindowInventory => ref _windowInventoryEventPending, 
			PendingEvent.DesktopShell => ref _desktopShellEventPending, 
			_ => ref _recycleBinEventPending, 
		};
	}

	[DllImport("user32.dll")]
	private static extern nint SetWinEventHook(uint eventMinimum, uint eventMaximum, nint module, WinEventCallback callback, uint processId, uint threadId, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWinEvent(nint hook);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int RegisterWindowMessageW(string messageName);

	[DllImport("shell32.dll")]
	private static extern int SHGetSpecialFolderLocation(nint ownerWindow, int folderId, out nint itemIdList);

	[DllImport("shell32.dll")]
	private static extern uint SHChangeNotifyRegister(nint window, int sources, int events, int message, int entryCount, ref ShellChangeNotifyEntry entries);

	[DllImport("shell32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SHChangeNotifyDeregister(uint registrationId);
}
public sealed class WindowsSystemShortcutService : ISystemShortcutService
{
	[StructLayout(LayoutKind.Sequential, Pack = 8)]
	private struct RecycleBinInfo
	{
		internal int Size;

		internal long TotalSize;

		internal long ItemCount;
	}

	private const int CancelledHResult = -2147023673;

	public bool RecycleBinHasItems()
	{
		RecycleBinInfo recycleBinInfo = default(RecycleBinInfo);
		recycleBinInfo.Size = Marshal.SizeOf<RecycleBinInfo>();
		RecycleBinInfo queryInfo = recycleBinInfo;
		if (SHQueryRecycleBinW(null, ref queryInfo) >= 0)
		{
			return queryInfo.ItemCount > 0;
		}
		return false;
	}

	public Task LaunchAsync(SystemShortcutKind kind, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		string fileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
		Process.Start(kind switch
		{
			SystemShortcutKind.ThisPc => new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = "shell:MyComputerFolder",
				UseShellExecute = true
			}, 
			SystemShortcutKind.RecycleBin => new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = "shell:RecycleBinFolder",
				UseShellExecute = true
			}, 
			SystemShortcutKind.FileExplorer => new ProcessStartInfo
			{
				FileName = fileName,
				UseShellExecute = true
			}, 
			SystemShortcutKind.PowerShell => new ProcessStartInfo
			{
				FileName = SystemShortcutSetting.GetDefaultTargetPath(SystemShortcutKind.PowerShell),
				UseShellExecute = true
			}, 
			_ => throw new ArgumentOutOfRangeException("kind", kind, "Unknown Windows system shortcut."), 
		});
		return Task.CompletedTask;
	}

	public async Task EmptyRecycleBinAsync(nint ownerWindow, CancellationToken cancellationToken = default(CancellationToken))
	{
		int num = await Task.Run(() => SHEmptyRecycleBinW(ownerWindow, null, 0u), cancellationToken);
		if (num < 0 && num != -2147023673)
		{
			Marshal.ThrowExceptionForHR(num);
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHQueryRecycleBinW(string? rootPath, ref RecycleBinInfo queryInfo);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHEmptyRecycleBinW(nint ownerWindow, string? rootPath, uint flags);
}
[CompilerGenerated]
internal sealed class <>z__ReadOnlySingleElementList<T> : IEnumerable, ICollection, IList, IEnumerable<T>, IReadOnlyCollection<T>, IReadOnlyList<T>, ICollection<T>, IList<T>
{
	private sealed class Enumerator : IDisposable, IEnumerator, IEnumerator<T>
	{
		object IEnumerator.Current => _item;

		T IEnumerator<T>.Current => _item;

		public Enumerator(T item)
		{
			_item = item;
		}

		bool IEnumerator.MoveNext()
		{
			if (!_moveNextCalled)
			{
				return _moveNextCalled = true;
			}
			return false;
		}

		void IEnumerator.Reset()
		{
			_moveNextCalled = false;
		}

		void IDisposable.Dispose()
		{
		}
	}

	int ICollection.Count => 1;

	bool ICollection.IsSynchronized => false;

	object ICollection.SyncRoot => this;

	object? IList.this[int index]
	{
		get
		{
			if (index != 0)
			{
				throw new IndexOutOfRangeException();
			}
			return _item;
		}
		set
		{
			throw new NotSupportedException();
		}
	}

	bool IList.IsFixedSize => true;

	bool IList.IsReadOnly => true;

	int IReadOnlyCollection<T>.Count => 1;

	T IReadOnlyList<T>.this[int index]
	{
		get
		{
			if (index != 0)
			{
				throw new IndexOutOfRangeException();
			}
			return _item;
		}
	}

	int ICollection<T>.Count => 1;

	bool ICollection<T>.IsReadOnly => true;

	T IList<T>.this[int index]
	{
		get
		{
			if (index != 0)
			{
				throw new IndexOutOfRangeException();
			}
			return _item;
		}
		set
		{
			throw new NotSupportedException();
		}
	}

	public <>z__ReadOnlySingleElementList(T item)
	{
		_item = item;
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return new Enumerator(_item);
	}

	void ICollection.CopyTo(Array array, int index)
	{
		array.SetValue(_item, index);
	}

	int IList.Add(object? value)
	{
		throw new NotSupportedException();
	}

	void IList.Clear()
	{
		throw new NotSupportedException();
	}

	bool IList.Contains(object? value)
	{
		return EqualityComparer<T>.Default.Equals(_item, (T)value);
	}

	int IList.IndexOf(object? value)
	{
		if (!EqualityComparer<T>.Default.Equals(_item, (T)value))
		{
			return -1;
		}
		return 0;
	}

	void IList.Insert(int index, object? value)
	{
		throw new NotSupportedException();
	}

	void IList.Remove(object? value)
	{
		throw new NotSupportedException();
	}

	void IList.RemoveAt(int index)
	{
		throw new NotSupportedException();
	}

	IEnumerator<T> IEnumerable<T>.GetEnumerator()
	{
		return new Enumerator(_item);
	}

	void ICollection<T>.Add(T item)
	{
		throw new NotSupportedException();
	}

	void ICollection<T>.Clear()
	{
		throw new NotSupportedException();
	}

	bool ICollection<T>.Contains(T item)
	{
		return EqualityComparer<T>.Default.Equals(_item, item);
	}

	void ICollection<T>.CopyTo(T[] array, int arrayIndex)
	{
		array[arrayIndex] = _item;
	}

	bool ICollection<T>.Remove(T item)
	{
		throw new NotSupportedException();
	}

	int IList<T>.IndexOf(T item)
	{
		if (!EqualityComparer<T>.Default.Equals(_item, item))
		{
			return -1;
		}
		return 0;
	}

	void IList<T>.Insert(int index, T item)
	{
		throw new NotSupportedException();
	}

	void IList<T>.RemoveAt(int index)
	{
		throw new NotSupportedException();
	}
}
