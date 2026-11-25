# Project Harbor

Port Calloway의 항만과 시가지를 무대로 한 오리지널 3인칭 오픈월드 액션 게임이다. Harbor Front를 차로 탐색하고, 항만 봉쇄 임무를 시작하거나 수배 상황에서 경찰의 추적을 따돌릴 수 있다.

## 플레이

[GitHub Release](https://github.com/sangsangbaksa-dev/GTA-/releases/tag/v0.1.0)에서 Windows 또는 Linux x86_64 아카이브를 내려받아 모두 푼다. `ProjectHarbor` 실행 파일을 열고 **Play locally**를 선택하면 같은 서버 권한형 게임을 로컬에서 플레이할 수 있다. 전용 서버를 실행 중이라면 **Join a server**에서 호스트와 포트를 입력한다. 기본 포트는 `7788`이다.

| 입력 | 동작 |
|---|---|
| WASD 또는 방향키, 마우스 | 이동과 3인칭 카메라 |
| 마우스 오른쪽 / 왼쪽 | 조준 / 사격 |
| R / 1과 2 | 재장전 / 무기 전환 |
| Shift / C | 달리기 / 웅크리기 |
| E | 차량 승하차, 임무 시작 |
| W/S · A/D · Space | 가속과 후진 · 조향 · 브레이크 |
| M / Esc | 지역 지도 / 메뉴 |

게임패드는 게임 내 **Controls**에서 입력을 볼 수 있다. 전용 서버 실행과 접속 방법은 [서버 README](server/README.md)에 있다.

## 개발

```bash
git lfs install
git lfs pull
dotnet test core/tests/Harbor.Core.Tests
```

Godot 4.4.1 .NET에서 `game/project.godot`을 열고 F5를 누른다. [구조](ARCHITECTURE.md), [데스크톱 빌드](docs/desktop-build.md), [공용 3D 모델](https://github.com/sangsangbaksa-dev/Set)을 참고한다.
