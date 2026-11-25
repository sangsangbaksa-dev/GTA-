# Project Harbor

Port Calloway의 항만과 시가지를 무대로 한 오리지널 1인용 3인칭 오픈월드 액션 게임이다. 도로를 달리거나 시민과 차량이 오가는 항만에서 의뢰를 시작한다. 전투·운전·의뢰 판정은 게임 내부의 로컬 권한형 코어가 맡는다.

## 플레이

[GitHub Release](https://github.com/sangsangbaksa-dev/GTA-/releases/tag/v0.2.0)에서 Windows 또는 Linux x86_64 아카이브를 내려받아 모두 푼다. `ProjectHarbor` 실행 파일을 열고 **Start game**을 선택한다.

- **Harbor characters**에서 Rook, 항만 경찰, 라이벌 크루, 항만 전기 기술자의 3D 모델을 돌려보고 얼굴을 확대할 수 있다.
- **City jobs**에서 항만 봉쇄, 화물 회수, 야드 전투, 주행 체크포인트, 배달 등 6개 의뢰를 확인하고 지도에 표시한다.
- **City operations**에서 Port Calloway의 기존 도시 의뢰와 활동을 싱글 플레이로 연다.

| 입력 | 동작 |
|---|---|
| WASD 또는 방향키, 마우스 | 이동과 3인칭 카메라 |
| 마우스 오른쪽 / 왼쪽 | 조준 / 사격 |
| R / 1과 2 | 재장전 / 무기 전환 |
| Shift / C | 달리기 / 웅크리기 |
| E | 차량 승하차, 의뢰 시작, 화물 회수 |
| W/S · A/D · Space | 가속과 후진 · 조향 · 브레이크 |
| M / J / Esc | 지역 지도 / 의뢰 목록 / 메뉴 |

## 개발

```bash
git lfs install
git lfs pull
dotnet test core/tests/Harbor.Core.Tests
```

Godot 4.4.1 .NET에서 `game/project.godot`을 열고 F5를 누른다. [구조](ARCHITECTURE.md), [데스크톱 빌드](docs/desktop-build.md), [공용 3D 모델](https://github.com/sangsangbaksa-dev/Set)을 참고한다.
