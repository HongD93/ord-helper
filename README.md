# 원랜디 도우미

원피스 랜덤 디펜스에서 내 유닛과 자원을 읽고, 재료 진행률·목표 덱·제작 순서를 보여주는 Windows 프로그램이다. 조합 버튼은 현재 재료와 게임 조건을 다시 확인한 뒤 실제 게임 입력으로 연결한다.

## 구성

| 위치 | 역할 |
| --- | --- |
| `source/ord-unit-probe` | C# 수집기, 로컬 서버, 조합 입력과 검증, 자체검사 |
| `source/ord-unit-probe/wwwroot` | HTML·CSS·JavaScript 화면, 조합 그래프와 추천 Worker |
| `source/ord-unit-probe/desktop-host` | WinForms·WebView2 실행 창 |
| `analysis/tests/nightmare-strategy-portable-regression.test.cjs` | 합성 입력을 사용하는 추천 회귀검사 |
| `tools/Test-Source.ps1` | 데이터 없는 소스 빌드·문법·회귀검사 |
| `portable-layout.json` | 로컬 실행 패키지의 상대 경로 설정 |

## 관리 범위

이 저장소에는 소스, 프로젝트 설정, 문서와 합성 테스트 코드만 넣는다. 데이터·유닛 아이콘·실행파일·빌드 백업·로그·개인 플레이 기록은 로컬에 보관한다.

현재 데이터 로딩은 로컬 파일 방식이다. 서버에서 데이터를 다운로드하는 기능은 아직 구현하지 않았다. 저장소를 복제한 것만으로 전체 게임 연동과 추천을 실행할 수는 없다.

## 개발 환경

- Windows, .NET 10 SDK
- 데스크톱 실행에는 WebView2 Runtime 필요
- 소스 검사에는 PowerShell과 Node.js 필요
- 게임 연동 기준은 Warcraft III `1.28.5.7680`, 맵 데이터는 `2.323[C]`

게임 프로세스에는 읽기 권한으로 접근하며, 조합은 입력 경로를 사용한다. 조합 조건·플레이어·세션·재료를 확인할 수 없으면 실행을 막는다.

## 빌드와 소스 검사

저장소 루트에서 실행한다.

```powershell
pwsh -File .\tools\Test-Source.ps1
```

이 명령은 두 C# 프로젝트 빌드, JavaScript·HTML 스크립트 문법 검사와 합성 추천 회귀검사를 실행한다. 게임 입력을 보내지 않는다. 전체 C# `--self-test`에는 로컬의 한글 조합 명령 데이터가 필요하다.

프로젝트를 따로 빌드할 수도 있다.

```powershell
dotnet build .\source\ord-unit-probe\OrdUnitProbe.csproj -c Release
dotnet build .\source\ord-unit-probe\desktop-host\OrdHelperDesktop.csproj -c Release
```

## 로컬 데이터로 실행

기존 로컬 데이터는 다음 위치에 둔다. `.gitignore`가 이 경로들을 추적에서 제외한다.

| 위치 | 내용 |
| --- | --- |
| `data/unit_catalog_2.323C.json` | 수집기의 유닛 식별·맵 정보 |
| `source/ord-unit-probe/wwwroot/data/` | 조합식·명령·전투 수치·규칙·보상·클리어 기록 |
| `source/ord-unit-probe/wwwroot/assets/` | 화면용 유닛 아이콘 |
| `source/ord-unit-probe/wwwroot/nightmare-records-live-*.json` | 현재 추천 기록 snapshot과 manifest |

서로 다른 맵 버전의 데이터를 섞지 않는다. 필요한 데이터를 배치한 뒤 루트에서 서버를 실행한다.

```powershell
New-Item -ItemType Directory -Path .\logs -Force | Out-Null
dotnet run --project .\source\ord-unit-probe\OrdUnitProbe.csproj -c Release -- --serve --port 8765 --catalog .\data\unit_catalog_2.323C.json --output .\logs\live-counter.json
```

화면은 `http://127.0.0.1:8765/`에서 확인한다. 별도 터미널에서 데스크톱 창을 실행할 수도 있다.

```powershell
dotnet run --project .\source\ord-unit-probe\desktop-host\OrdHelperDesktop.csproj -c Release
```

## 브랜치

`dev`에서 개발·검증하고, 배포할 소스를 `main`에 반영한다.
