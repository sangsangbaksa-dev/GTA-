# ADR 0009 — 브라우저 배포: .NET 8 WebAssembly 코어 + 표준 Godot 4.4.1 WebGL2 프레젠테이션

- 상태: 승인
- 일자: 2026-11-27

## 배경

Project Harbor를 웹 브라우저(Vercel 정적 호스팅)에서 바로 플레이할 수 있게 배포해야 한다.
그러나 데스크톱 빌드가 사용하는 **Godot 4.4.1 `.NET`(`mono`) 에디터**는 업스트림 제약으로 인해 Web 내보내기를 거부한다 (`Exporting to Web is currently not supported in Godot 4 when using C#/.NET`, [ADR 0001](0001-engine-godot.md) 위험 항목).

한편 [ADR 0002](0002-portable-core.md) 덕분에 전투·수배·운전·의뢰·거리 인구·64m 청크 선택(`ChunkGrid`) 등 모든 게임 규칙은 `Godot` 네임스페이스 참조가 전혀 없는 순수 `.NET 8` 어셈블리(`Harbor.Core`)에 들어 있다.

## 결정

코어나 캐릭터·스트리밍 규칙 코드를 수정하지 않고, `web/` 아래에 하이브리드 웹 클라이언트 파이프라인을 둔다.

1. **시뮬레이션 권한 (`web/runtime/Harbor.Browser.csproj`)**
   - `Harbor.Core`를 `RuntimeIdentifier=browser-wasm`으로 게시한다.
   - `BrowserHarbor`(`[JSExport]`)가 데스크톱 `CoreHost`와 동일하게 `InMemoryNetwork` 위에서 `HarborAuthority`와 `ClientSession`을 30Hz 고정 틱으로 구동하고, 매 애니메이션 프레임 JSON 스냅샷을 내보낸다.
2. **3D 월드·캐릭터 렌더링 (`web/engine/`)**
   - 표준(비-.NET) **Godot 4.4.1**(`gl_compatibility` / WebGL2, 단일 스레드 `variant/thread_support=false`) 프로젝트를 사용한다. 단일 스레드 템플릿을 써서 `SharedArrayBuffer`/COOP·COEP 헤더 제약 없이 일반 정적 CDN과 임베디드 환경에서도 동작하게 한다.
   - 빌드 시 `web/prepare-godot.mjs`가 `game/scenes/world/HarborFront.tscn`에서 배경(`HarborBackdrop.scn`)을 추출하고, `game/scenes/world/chunks/chunk_X_Z.tscn` 64m 청크들을 압축 이진 씬(`public/godot/chunks/chunk_X_Z.<hash>.scn` + `chunk_index.json`)으로 분리한다.
   - 런타임에 GDScript(`world_stream.gd`, `game.gd`, `mixamo_gait.gd`, `rook_gait.gd`)가 플레이어 주변 64m 청크만 HTTP로 스트리밍·언로드하고, 주인공 Tomas Reyes(`chr_harbor_clerk.glb` Mixamo 9종 모션) 및 항구 인물 8종·차량·무기·전투 이펙트를 그린다.
3. **웹 셸 및 번들링 (`web/src/`, Vite, `vercel.json`)**
   - HUD, 미니맵/항구 전체 지도, 도시 의뢰 보드(`J`), 8인 캐릭터 턴테이블 뷰어를 HTML/Canvas 오버레이로 구성하고 `web/dist/` 정적 산출물로 빌드해 Vercel에 배포한다.

## 결과

- **장점**: `core/`와 데스크톱 `game/scripts/` 코드를 건드리지 않으면서도 데스크톱과 100% 동일한 `Harbor.Core` 시뮬레이션 규칙, 64m 청크 스트리밍, MakeHuman 캐릭터·Mixamo 모션을 웹에서 그대로 실행한다.
- **비용**: `game/scripts/`의 C# 뷰 계층 대신 웹 전용 얇은 GDScript 프레젠테이션(`web/engine/*.gd`)과 HTML HUD(`web/src/`)를 유지해야 한다. 향후 Godot 업스트림에서 `.NET 8+` Web 내보내기가 안정화되면 단일 내보내기로 통합할 수 있다.
