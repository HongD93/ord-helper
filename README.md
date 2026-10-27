# 원랜디 도우미

원피스 랜덤 디펜스의 유닛·재료 정보를 확인하고 조합과 덱 구성을 돕는 Windows용 도우미다.

## 주요 기능

- 보유 유닛과 자원 실시간 조회
- 조합 재료 진행률 표시와 하위 재료부터 이어지는 연속 조합
- 현재 보유 유닛을 반영한 목표 덱·제작 순서·보강 유닛 추천

## 기술 구성

- C# / .NET 10: 게임 상태 수집, 로컬 서버, 조합 입력·검증
- WinForms / WebView2: 데스크톱 실행 창
- HTML / CSS / JavaScript / Web Worker: 화면과 조합·덱 추천 계산

## 개발 환경

- Windows 및 .NET 10 SDK
- WebView2 Runtime
- 소스 검사에 사용할 PowerShell과 Node.js

현재 게임 연동 기준은 Warcraft III `1.28.5.7680`, 원랜디 `2.323[C]`다.

## 빌드

저장소 루트에서 실행한다.

```powershell
dotnet build .\source\ord-unit-probe\OrdUnitProbe.csproj -c Release
dotnet build .\source\ord-unit-probe\desktop-host\OrdHelperDesktop.csproj -c Release
```

## 소스 검사

```powershell
pwsh -File .\tools\Test-Source.ps1
```

빌드·스크립트 문법 검사와 합성 입력 기반 추천 회귀검사를 실행한다.

게임 연동 실행에는 저장소에 포함되지 않은 호환 게임 데이터와 유닛 아이콘이 별도로 필요하다.
