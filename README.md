<p align="center">
  <img src="src/AnotherSurface.App/Assets/Brand/another-surface-dock-icon-v3.png" width="112" alt="초미 Another Surface 아이콘" />
</p>
<h1 align="center">Another Surface</h1>
<p align="center">월페이퍼 · 위젯 · 스티커 · Dock<br />0.10.3 · Windows x64</p>

## 주요 기능

| 기능 | 내용 |
| --- | --- |
| 월페이퍼 | 이미지·GIF·영상 등록, 화면 맞춤·색상·음량 조절 |
| 다중 모니터 | 화면별 배경 지정, 재생 상태와 설정 저장·복원 |
| 시계 | 아날로그 2종·디지털 3종, 색상·크기·위치 변경 |
| 시스템 정보 | CPU·GPU·RAM 추이, 디스크 사용량, 표시 항목·색상 조절 |
| 스티커 | 기본 이미지 25종, 여러 장 배치·이동·크기·회전·겹침 순서 조절 |
| Dock | 앱 고정·실행·창 전환, 드래그 정렬, 휴지통·작업표시줄 관리 |

## 실행

- **환경:** Windows 10 1809 이상 / Windows 11, x64, .NET 8 Desktop Runtime
- **실행 파일:** [`artifacts/AnotherSurface-0.10.3/AnotherSurface.App.exe`](artifacts/AnotherSurface-0.10.3/)
- **배포 폴더 전체 유지:** DLL·Assets·네이티브 렌더러 필요
- **지원 형식:** JPG, JPEG, PNG, BMP, GIF, MP4, WebM
- **영상 최적화:** FFmpeg·FFprobe 선택 사용

## 기본 조작

| 작업 | 방법 |
| --- | --- |
| 배경 적용 | 월페이퍼 등록 → 디스플레이 선택 → 선택 화면에 적용 |
| 위젯 배치 | 위젯에서 선택 → Lock 해제 → 드래그 이동 |
| 잠금 / 해제 | 우클릭 / Alt + 우클릭 |
| 항목 숨기기 | 잠금 해제한 항목을 왼쪽 버튼으로 잡고 Delete |
| Dock 불러오기 | Alt + 백틱 키(숫자 1 왼쪽) |
| 앱 종료 | 설정 또는 Dock 메뉴의 **Another Surface 종료** |

## 빌드

**.NET SDK 9.0.308 · MSVC v143 x64 · Windows SDK**

```powershell
git clone https://github.com/JellytoCodes/Another-Surface.git
Set-Location Another-Surface
.\scripts\build-release.ps1
```

| 명령 | 작업 |
| --- | --- |
| `scripts/build-release.ps1` | C++ 렌더러 → WPF 앱 → 테스트 |
| `scripts/build-release.ps1 -Package` | 빌드·테스트 후 배포 폴더 생성 |
| `dotnet test tests/AnotherSurface.Tests -c Release` | 테스트 실행 |

## 구성

| 경로 | 역할 |
| --- | --- |
| `src/AnotherSurface.App` | WPF 설정 화면·트레이·Dock |
| `src/AnotherSurface.Core` | 설정·라이브러리·영상 캐시 |
| `src/AnotherSurface.Windows` | Win32 연동·위젯·관리형 렌더러 |
| `src/AnotherSurface.Renderer.Native` | C++20 · D3D11 · DirectComposition · Media Foundation |
| `tests/AnotherSurface.Tests` | 단위·Windows 통합 테스트 |

## 참고

- 설정·라이브러리: `%LocalAppData%\Another Surface` — 기존 설치는 `Vellume` 폴더를 이어서 사용
- 월페이퍼의 자동 전체 화면 감지·일시정지는 미지원. 수동 재생 제어 지원
- 영상 재생은 Windows 코덱 환경에 따라 차이 발생
- 시스템 정보 위젯 배치 참고: [SysDash](https://github.com/marcopixel/SysDash)
