# web/ — Project Harbor 웹 클라이언트

Godot 4.4.1 `.NET`(`mono`) 빌드는 업스트림 엔진 제약으로 직접 `Web` 내보내기를 지원하지 않는다 (`docs/adr/0001-engine-godot.md`, `docs/adr/0009-browser-wasm-client.md`).
대신 `Harbor.Core`가 `Godot` 참조 0의 순수 `.NET 8` 라이브러리(`ADR 0002`)인 점을 활용해 두 런타임을 브라우저 안에서 결합한다.

1. **`web/runtime/` (`Harbor.Browser.csproj`)**: `Harbor.Core`와 `data/`·`game/data/missions/` JSON 리소스를 `.NET 8 browser-wasm`으로 컴파일해 30Hz 권한 시뮬레이션(`HarborAuthority` + `ClientSession`)을 구동한다.
2. **`web/engine/` (표준 비-.NET Godot 4.4.1 WebGL2)**: `game/`의 실제 3D 항구 셸(`HarborFront.tscn`), 64m 청크 씬(`scenes/world/chunks/chunk_X_Z.tscn`), MakeHuman 인물 8종(`chr_harbor_*`), Tomas Reyes Mixamo 모션 9종, 차량(`veh_calloway_compact.glb`), 총기·사운드를 WebGL2로 렌더링하고 `JavaScriptBridge`로 시뮬레이션 스냅샷을 소비한다.
3. **`web/src/` + Vite**: HUD, 레이더·전체 항구 지도(`chart.js`), 의뢰 게시판(`J`), 항구 인물 뷰어(`Harbor characters`), 입력(`input.js`)을 정적 번들(`web/dist/`)로 묶는다.

## 로컬 빌드

필수 도구:
- `.NET 8 SDK` + `wasm-tools` 워크로드 (`dotnet workload install wasm-tools`)
- 표준(비-.NET) **Godot 4.4.1** 실행 파일 (`GODOT` 환경 변수 또는 `godot4`) + 공식 `4.4.1.stable` Web 내보내기 템플릿
- Node.js 20+

```bash
cd web
npm install
npm run build      # prepare.mjs (.NET WASM + Godot 4.4.1 WebGL2 + 64m 청크) -> vite build (web/dist)
npm run preview    # http://127.0.0.1:4173
```

CI/Vercel 원클릭 빌드는 루트의 `tools/vercel-build.sh`가 필요한 툴체인을 캐시·설치한 뒤 `web/dist`를 생성한다.
