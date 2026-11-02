# AnotherSurface.Renderer.Native

Native Windows wallpaper compositor used by Another Surface for image and video
wallpapers. It owns one DWM-redirected WorkerW child HWND per display and a
D3D11 composition swap chain for static images. Video uses Media Foundation's
legacy HWND playback on that redirected surface so DWM keeps one stable
composed presentation path while foreground D3D applications are active. GIF and color-corrected video use the managed Windows renderer. HTML wallpapers
and browser rendering are not supported.

Set `ANOTHER_SURFACE_NATIVE_LEGACY_NOREDIRECTION=1` before launch only to A/B test the
former `WS_EX_NOREDIRECTIONBITMAP` window path.

The renderer is a private child process. Commands and responses use a small
versioned binary protocol over inherited standard input/output handles; logs
go only to standard error.
