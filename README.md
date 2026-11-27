# Project Harbor

Port Calloway를 자유롭게 돌아다니는 1인용 3인칭 오픈월드 게임이다. 항만, 도심, 숲, 사막, 군사기지, 부촌, 빈민가를 연결하는 길을 걷거나 차로 달릴 수 있다. 시민과 교통이 도시에 반응하며, 게임 속 범죄에 따라 경찰 수배가 바뀐다.

Customs Exchange Cafe와 Alderwash Lodge는 열린 출입구를 통해 실내외를 오갈 수 있다. Atlantic Repair는 차로 들어가 반대편 출입구로 나올 수 있다. 각 장소는 브라우저 지도에서 ◆로 표시된다.

## 데스크톱 실행

이 브랜치는 .NET 8과 Godot 4.4.1 .NET으로 실행한다. `git lfs pull` 후 Godot에서 `game/project.godot`을 열고 F5를 누른다. **Start game**을 선택하면 항만에서 자유 탐험이 시작된다. **Harbor characters**에서는 캐릭터를 확대하고 돌려볼 수 있다.

## 브라우저 실행

플레이어는 WebGL 2를 지원하는 브라우저에서 웹판을 열 수 있다. 소스에서 빌드할 때는 표준 Godot 4.4.1과 공식 웹 내보내기 템플릿, .NET 8 SDK, Node.js 22를 사용한다. 여러 Godot 버전이 있다면 `GODOT`에 표준 에디터의 실행 파일 경로를 지정한다.

```bash
git lfs pull
cd web
dotnet workload install wasm-tools
npm ci
npm run dev
```

터미널에 표시된 주소에서 **도시로 들어가기**를 누른다. `web/`에서 `npm run build`를 실행하면 `web/dist/`에 정적 파일이 만들어진다. [브라우저 빌드와 검증](web/README.md)

| 입력 | 동작 |
|---|---|
| WASD 또는 방향키, 마우스 | 이동과 3인칭 카메라 |
| 마우스 오른쪽 / 왼쪽 | 조준 / 사격 |
| R / 1과 2 | 재장전 / 무기 전환 |
| Shift / C | 달리기 / 웅크리기 |
| F | 차량 승하차 · 정차하거나 느리게 움직이는 NPC 차량 탈취 |
| G | 가까운 보행자의 소매치기 또는 방치된 화물 훔치기 |
| W/S · A/D · Space | 가속과 후진 · 조향 · 브레이크 |
| M / Esc | 전체 지도 / 메뉴 |

## 개발

```bash
git lfs install
git lfs pull
dotnet test core/tests/Harbor.Core.Tests
dotnet build game/Harbor.csproj
npm --prefix web test
```

[구조](ARCHITECTURE.md), [데스크톱 빌드](docs/desktop-build.md), [공용 3D 모델](https://github.com/sangsangbaksa-dev/Set)을 참고한다.
