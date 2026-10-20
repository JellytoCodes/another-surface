<p align="center">
  <img src="Vellume.App/Assets/Brand/vellume-dock-icon-v3.png" width="112" alt="Vellume 아이콘" />
</p>
<h1 align="center">Vellume</h1>
<p align="center">
  <strong>월페이퍼, 위젯, Dock을 한곳에서 관리하는 Windows 데스크톱 앱</strong><br />
  0.9.0 · Windows x64 · C# / WPF + C++20
</p>

Vellume은 로컬 이미지와 영상을 바탕화면에 적용하고, 시계·시스템 정보·앱 실행 Dock을 함께 구성하는 데스크톱 커스터마이징 앱입니다. 설정과 앱 수명 주기는 WPF가 담당하고, 이미지·영상 월페이퍼는 별도 C++ 렌더러를 우선 사용합니다.

[주요 기능](#주요-기능) · [시작하기](#시작하기) · [소스에서 빌드](#소스에서-빌드) · [렌더링 구조](#렌더링-구조) · [문제 해결](#문제-해결)

## 주요 기능

| 영역 | 기능 |
| --- | --- |
| 로컬 월페이퍼 | 이미지, GIF, MP4·WebM, 로컬 HTML 등록. 화면 채우기·맞춤·늘이기, 밝기·대비·채도, 음소거·볼륨 조절 |
| 다중 모니터 | 모니터별 월페이퍼와 재생 옵션 저장·복원. 전체 또는 화면별 재생·일시정지·중지 |
| 데스크톱 위젯 | 색상을 지정하는 시계 5종, SysDash 스타일의 CPU·GPU·RAM 추이와 디스크 사용량. 항목·색상·크기·배경 조절, 위젯별 Lock·드래그 이동, 9방향 배치와 모니터 선택 |
| 스티커 | 기본 PNG 25종을 여러 장 배치. 드래그 이동·크기 조절·회전, 스티커별 모니터·표시·Lock 설정과 자동 저장 |
| 앱 Dock | 앱 고정·실행·창 전환, 드래그 정렬, 실행 중인 앱 표시, 휴지통 상태와 비우기, Windows 작업표시줄 표시 여부 설정 |
| Codex 컴패니언 | 로컬 Codex 실행·작업 상태를 캐릭터와 패널로 표시. 상세 작업 상태 연결은 선택 기능 |

월페이퍼와 각 위젯의 모니터를 개별적으로 선택할 수 있습니다. Dock은 주 모니터에 한 세트만 표시합니다. Windows 로그인 자동 실행을 설정하면 마지막 구성을 복원합니다.

> [!IMPORTANT]
> 현재 버전은 다른 앱의 최대화·전체 화면 여부에 따라 월페이퍼를 자동으로 일시정지하지 않습니다. 구버전의 전경 앱 절전·앱별 자동 정지 규칙은 실행 시 적용하지 않으며, 수동 재생 제어는 계속 지원합니다.

## 시작하기

### 실행 환경

| 구분 | 요구 사항 |
| --- | --- |
| 운영체제 | Windows 10 1809 이상 또는 Windows 11, x64 — 프로젝트 대상 기준 |
| 앱 런타임 | .NET 8 Desktop Runtime, x64 |
| HTML·호환 재생 | Microsoft Edge WebView2 Runtime |
| 영상 최적화 — 선택 | FFmpeg와 FFprobe. 없거나 변환에 실패하면 원본 영상으로 재생 |

일반 실행에는 관리자 권한이 필요하지 않습니다. 영상 재생 가능 여부는 파일의 코덱과 Windows 미디어 환경에 따라 달라집니다.

저장소에 동봉된 실행 파일은 [`artifacts/Vellume-0.9.0/`](artifacts/Vellume-0.9.0/)에 있습니다. EXE만 분리하지 말고 DLL·Assets·네이티브 렌더러를 포함한 폴더 전체를 유지한 상태로 `Vellume.App.exe`를 실행합니다. `BuildInfo.json`에서 배포본의 소스 커밋과 파일 해시를 확인할 수 있습니다.

### 기본 사용 흐름

1. **월페이퍼** 화면에서 이미지·GIF·영상 파일 또는 HTML 폴더를 등록합니다. 위쪽 콘텐츠 목록은 세로로 스크롤하며, 형식 필터와 이름 검색으로 항목을 찾습니다.
2. 콘텐츠와 아래쪽 디스플레이 카드를 선택하고 **선택 화면에 적용**을 누릅니다. 디스플레이 목록은 가로 스크롤과 마우스 휠을 지원하며, 각 화면의 현재 배경과 재생 상태를 함께 표시합니다. 콘텐츠 행의 **적용** 버튼이나 행 더블 클릭도 선택한 디스플레이에 적용합니다.
3. 선택한 디스플레이의 화면 맞춤·음량은 설정에서 조정합니다. **위젯**에서 시계·시스템 정보·스티커를, **Dock**에서 앱 고정·크기·작업표시줄 표시를 구성합니다.

위젯 화면에서 아날로그 2종·디지털 3종 중 시계를 선택합니다. **시계 색상**에서 색상 선택창을 열거나 `#RRGGBB` 코드를 입력해 적용합니다. 색상은 미리보기와 바탕화면에 반영되고 저장됩니다. 각 위젯의 배치와 모니터를 선택하거나 **Lock**을 해제한 뒤 바탕화면의 위젯을 드래그합니다. 드롭한 위치는 저장되며, **전체 Lock**으로 이동을 잠글 수 있습니다. 저장한 자유 배치는 화면 크기가 바뀌어도 상대 위치를 유지합니다.

**지원 형식:** `.jpg`, `.jpeg`, `.png`, `.bmp`, `.gif`, `.mp4`, `.webm`, `.html`, `.htm`, `index.html`이 포함된 폴더.

**시스템 정보:** [SysDash](https://github.com/marcopixel/SysDash)의 배치를 참고해 Vellume의 WPF 위젯으로 구현했습니다. CPU·GPU·RAM은 최근 60회 측정 추이 그래프, C:·D:는 디스크 사용률 막대를 표시합니다. 표시 항목, 강조색·글자색, 크기(50–200%), 배경 불투명도(0–100%)를 조절하며 미리보기와 바탕화면에 적용·저장됩니다. GPU 측정이나 드라이브를 사용할 수 없으면 `N/A`로 표시합니다. Rainmeter나 외부 센서 플러그인 설치는 필요하지 않습니다.

**스티커:** 위젯 화면의 기본 이미지 25종 중 하나를 누르면 한 장씩 추가됩니다. 바탕화면에서 드래그로 이동하고, 오른쪽 아래 손잡이로 크기, 위쪽 손잡이로 회전을 조절합니다. 스티커 목록에서 모니터·표시·Lock을 바꾸거나 크기·각도를 입력해 적용할 수 있습니다. 같은 이미지도 여러 장 붙일 수 있으며, 각 목록 행의 ▲·▼로 겹침 우선순위를 바꿉니다. 목록 위쪽이 앞에 표시되며, Lock 상태에서도 순서 변경과 저장이 가능합니다. 설정은 자동 저장됩니다. **전체 Lock**은 스티커에도 적용됩니다.

등록한 콘텐츠는 `%LocalAppData%\Vellume\Library`의 항목별 폴더로 복사합니다. 라이브러리에서 항목을 삭제해도 가져온 원본 파일은 삭제하지 않습니다. CSS·JavaScript·이미지를 상대 경로로 참조하는 HTML은 파일 하나가 아니라 **HTML 폴더 추가**로 등록해야 관련 리소스가 함께 복사됩니다.

Dock은 앱을 클릭해 실행하거나 열린 창으로 전환할 수 있습니다. 창이 여러 개면 클릭으로 순환하거나 우클릭의 `열린 창` 목록에서 선택합니다. 다른 창 뒤로 가려진 Dock은 **Alt + 백틱 키(숫자 1 왼쪽)** 으로 앞으로 불러올 수 있습니다.

월페이퍼의 **중지**는 앱 종료와 다릅니다. Vellume 전체를 종료할 때는 설정의 **Vellume 종료**를 사용합니다. Windows 작업표시줄을 숨겨 사용한 경우 정상 종료 시 복원됩니다.

> [!WARNING]
> 로컬 HTML도 JavaScript를 실행할 수 있습니다. 신뢰할 수 있는 콘텐츠만 등록하세요.

## 소스에서 빌드

### 개발 환경

| 구성요소 | 저장소 설정 |
| --- | --- |
| .NET SDK | [`global.json`](global.json): `9.0.308`, `rollForward: latestPatch` |
| 실행 대상 | `net8.0-windows10.0.17763.0`, x64 |
| C++ 도구 | Visual Studio 2022 또는 Build Tools의 C++ 데스크톱 개발 도구, MSVC v143 x64, Windows SDK |
| 테스트 | xUnit — Windows 대상 테스트와 Win32 통합 테스트 포함 |

**SDK 버전과 실행 런타임 버전은 다릅니다.** 저장소가 지정한 .NET SDK와 앱 실행에 필요한 .NET 8 Desktop Runtime을 구분해 준비합니다.

Windows PowerShell에서 저장소를 받은 뒤 테스트 프로젝트의 패키지를 먼저 복원합니다.

```powershell
git clone https://github.com/JellytoCodes/Vellume.git
Set-Location Vellume
dotnet restore .\Vellume.Tests\Vellume.Tests.csproj
```

복원이 성공하면 저장소 루트에서 빌드합니다.

```powershell
.\build-release.ps1
```

[`build-release.ps1`](build-release.ps1)은 **C++ 네이티브 렌더러 → WPF 앱 → 테스트** 순서로 실행합니다. 테스트 단계가 `--no-restore`를 사용하므로 최초 빌드 전 위 복원 단계가 필요합니다. 네이티브 EXE는 앱 출력 폴더에 복사됩니다.

배포 폴더까지 갱신할 때는 Vellume을 종료하고 `.\build-release.ps1 -Package`를 실행합니다. 빌드·테스트 성공 후 기존 배포본을 `tmp/`에 백업하고 `artifacts/Vellume-0.9.0/` 전체를 교체합니다. `BuildInfo.json`에 소스 커밋과 SHA-256을 기록하고 복사된 파일을 검증합니다.

빌드가 성공한 다음 실행합니다.

```powershell
& '.\Vellume.App\bin\Release\net8.0-windows10.0.17763.0\Vellume.App.exe'
```

테스트만 다시 실행하거나 테스트를 생략해 빌드할 때는 다음 명령을 사용합니다.

```powershell
# 테스트만 실행
dotnet test .\Vellume.Tests\Vellume.Tests.csproj -c Release

# 테스트를 생략하고 빌드
.\build-release.ps1 -SkipTests
```

앱 프로젝트만 `dotnet build`하면 C++ 프로젝트가 함께 빌드되지는 않습니다. 네이티브 렌더러를 포함한 실행 환경을 준비할 때는 `build-release.ps1`을 사용합니다.

## 렌더링 구조

| 콘텐츠·역할 | 처리 경로 |
| --- | --- |
| 설정·트레이·Dock·앱 수명 주기 | .NET 8 / WPF 및 Win32 |
| 정적 이미지 | C++ 렌더러: WIC 디코딩 → D3D11·Direct2D·DirectComposition |
| 기본 화면 보정의 MP4·WebM | C++ 렌더러: Media Foundation Media Engine의 HWND 출력 |
| GIF | 관리형 렌더 호스트의 WIC 기반 재생 |
| 로컬 HTML·호환 재생 | 관리형 렌더 호스트와 WebView2 등 콘텐츠별 호환 경로 |
| 시계·시스템 정보·컴패니언 | 월페이퍼와 분리된 작은 WPF/Win32 위젯 표면 |

월페이퍼 창은 Explorer의 WorkerW/Progman에 연결합니다. 네이티브 렌더러는 별도 프로세스로 실행하며, 앱과 버전이 지정된 바이너리 IPC로 통신합니다. 네이티브 초기화나 영상 코덱 지원에 문제가 생기면 관리형 렌더러로 전환합니다.

네이티브 렌더러는 IPC 명령을 기다리는 동안에도 Win32 창 메시지를 처리합니다. 전체화면·해상도·DWM 전환 알림이 파이프 입력 대기에 묶이지 않도록 하며, 4K 창 응답과 MP4 재생 중 크기 전환을 회귀 테스트로 확인합니다.

정적 이미지는 표시 크기에 맞춰 디코딩하고, 모니터 크기가 바뀌어 더 큰 텍스처가 필요하면 원본에서 다시 읽습니다. 위젯과 Dock은 월페이퍼 변경과 별도로 유지하며, Explorer 재시작이나 디스플레이 변경 시 데스크톱 연결을 복구합니다.

### 영상 최적화와 색상 보정

**영상 GPU 사용량 자동 최적화**는 고해상도·고프레임률 또는 AV1·VP9 영상에 대해 화면 크기에 맞는 최대 30FPS H.264 프록시를 생성하는 선택 가능한 설정입니다. 원본은 유지하고 재생용 복사본만 사용합니다. FFmpeg 변환은 별도 프로세스에서 수행하며, NVENC를 먼저 시도한 뒤 소프트웨어 인코딩으로 대체합니다.

FFmpeg와 FFprobe는 앱 폴더 또는 `PATH`에서 찾습니다. 별도 경로를 사용할 때는 앱 실행 전에 `VELLUME_FFMPEG_PATH`, `VELLUME_FFPROBE_PATH`를 설정합니다. 도구가 없거나 변환이 실패하면 원본 영상으로 재생합니다.

프록시는 `.vellume-cache`에 저장합니다. 30일 동안 사용하지 않은 캐시를 정리하고, 총량이 2GiB를 넘으면 오래된 항목부터 정리합니다. 재생 중이거나 사용 예정인 항목은 보호하므로 2GiB는 엄격한 상한이 아닙니다.

영상의 밝기·대비·채도를 기본값에서 변경하면 색상 보정을 지원하는 관리형 렌더러로 전환할 수 있습니다. **첫 전환에서 재생 위치가 유지되지 않아 영상이 처음부터 시작될 수 있습니다.** 보정값을 다시 기본값으로 돌려도 즉시 C++ 렌더러로 복귀하지 않으며, 월페이퍼를 다시 적용해야 합니다.

## 프로젝트 구조

| 경로 | 역할 |
| --- | --- |
| [`Vellume.App/`](Vellume.App/) | WPF 설정 UI, 트레이, Dock, 단일 인스턴스와 앱 수명 주기 |
| [`Vellume.Core/`](Vellume.Core/) | 모델·계약, JSON 설정, 관리형 라이브러리, 파일 검증, 영상 프록시 캐시 |
| [`Vellume.Windows/`](Vellume.Windows/) | WorkerW/Win32 연결, 모니터 관리, 렌더러 제어·폴백, 위젯, Codex 브리지 |
| [`Vellume.Renderer.Native/`](Vellume.Renderer.Native/) | C++20 x64 이미지·영상 렌더러와 IPC |
| [`Vellume.Tests/`](Vellume.Tests/) | 설정·파일 관리·캐시·재생 정책·렌더러·Windows 통합 테스트 |
| [`build-release.ps1`](build-release.ps1) | 네이티브 렌더러와 앱 빌드, 테스트 실행 |
| [`build-app-icon.ps1`](build-app-icon.ps1) | 원본 PNG에서 실행 파일용 다중 해상도 ICO 생성 |

## 데이터와 선택 기능

앱 데이터의 기본 위치는 `%LocalAppData%\Vellume`입니다.

| 상대 경로 | 내용 |
| --- | --- |
| `settings.json` | 라이브러리 등록 정보, 모니터별 재생 옵션, 위젯·Dock 설정 |
| `Library\` | 가져온 월페이퍼의 관리 복사본 |
| `Logs\vellume.log` | 앱 동작과 오류 로그 |
| `WebView2\` | HTML·호환 재생용 WebView2 데이터 |
| `Codex\` | 컴패니언 브리지와 `status.json` |
| `session.active` | 비정상 종료 감지 표식 |
| `GpuSafety\` | 구버전 호환을 포함한 Windows 배경 복구용 데이터 |

Codex 컴패니언은 로컬 프로세스와 세션 활동으로 기본 상태를 감지합니다. **상세 상태 연결**은 Hook을 통해 분석·도구 사용·완료 등의 구분을 추가하는 선택 기능입니다. 설치 시 기존 Codex Hook 설정에 Vellume 항목을 병합하므로, 사용 중인 Hook 구성을 확인한 뒤 활성화합니다. 내부 추론이나 정확한 작업 진행률을 보여주는 기능은 아닙니다.

## 문제 해결

| 증상 | 확인할 사항 |
| --- | --- |
| 앱이 실행되지 않음 | x64 .NET 8 Desktop Runtime 설치 여부, EXE와 DLL·Assets가 같은 배포 폴더에 있는지 확인 |
| 빌드할 SDK를 찾지 못함 | `dotnet --list-sdks` 결과와 `global.json`의 SDK 설정 확인 |
| C++ 빌드 도구를 찾지 못함 | Visual Studio Installer에서 MSVC v143 x64와 Windows SDK 설치 확인 |
| 테스트 복원 오류 | `dotnet restore .\Vellume.Tests\Vellume.Tests.csproj` 실행 후 다시 빌드 |
| HTML의 이미지·스타일이 누락됨 | `index.html`과 필요한 리소스를 한 폴더에 모아 HTML 폴더로 다시 등록 |
| 특정 영상이 재생되지 않음 | 로그에서 선택된 렌더러와 오류 확인. 시스템 코덱·WebView2 Runtime 및 다른 영상 재생 여부 비교 |
| 영상 최적화가 적용되지 않음 | FFmpeg·FFprobe 설치/경로, 최적화 옵션, 프록시 생성 로그 확인. 대상이 아닌 영상은 원본 사용 |
| 렌더링 깜빡임·호환 문제 | `VELLUME_DISABLE_NATIVE_RENDERER=1`로 관리형 렌더러와 비교. GPU·드라이버·DPI·파일 형식도 함께 기록 |

환경 변수는 **Vellume을 완전히 종료한 뒤 다시 실행**해야 반영됩니다. 자원 사용을 조사할 때는 `VELLUME_PERFORMANCE_LOG=1`로 진단 로그를 활성화할 수 있습니다.

### 현재 제한사항

- Now Playing 패널·오디오 스펙트럼·데스크톱 미디어 조작 기능은 제거되었습니다. MP4·WebM 월페이퍼 재생은 별개입니다.
- 여러 모니터의 서로 다른 DPI 배율, 특수 Explorer/가상 데스크톱 구성, GPU·드라이버별 호환성은 실기기 검증이 필요합니다. 모든 환경에서 깜빡임이 없다고 보장하지 않습니다.
- Dock의 알림 표시는 Windows Shell의 창 attention 신호이며 알림 센터의 내용이나 개수가 아닙니다. 일부 앱의 새 창 생성은 해당 앱의 실행 정책을 따릅니다.
- 온라인 마켓, 계정·결제, 클라우드 동기화, 자동 업데이트는 제공하지 않습니다.

## 개발 이력

이 README는 현재 사용법과 구조를 기준으로 정리했습니다. 기존 버전별 변경 기록은 [정리 이전 README](https://github.com/JellytoCodes/Vellume/blob/8c316c0cc30cd46d176864879802a9dc160cc8aa/README.md)에서 확인할 수 있습니다. 그 문서에는 이후 변경되거나 제거된 동작도 포함되어 있으므로 현재 기능 안내와 구분해 읽어야 합니다.

이후 문서 변경은 [README 커밋 이력](https://github.com/JellytoCodes/Vellume/commits/main/README.md)에서 확인할 수 있습니다.
