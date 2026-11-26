# 브라우저 플레이

Godot 4.4.1은 네이티브 게임과 같은 항만 장면, 셰이더, 캐릭터와 차량을 WebGL 2로 그린다. 브라우저 안에서 `Harbor.Core`의 .NET 8 WebAssembly 빌드가 이동, 차량, 보행자와 수배를 시뮬레이션한다. 혼자 자유롭게 탐험할 수 있다.

## 소스에서 실행

.NET 8 SDK, Node.js 22, Git LFS, 표준 Godot 4.4.1 에디터와 같은 버전의 공식 내보내기 템플릿을 설치한다. 여러 버전의 Godot이 있다면 환경 변수 `GODOT`에 4.4.1 표준 에디터의 실행 파일 경로를 지정한다.

저장소 루트에서 다음 명령을 실행한다.

```bash
git lfs pull
cd web
dotnet workload install wasm-tools
npm ci
npm run dev
```

브라우저에서 터미널에 표시된 주소를 열고 **도시로 들어가기**를 누른다.

## 정적 빌드와 검증

`web/` 디렉터리에서 실행한다.

```bash
npm test
npm run build
npx playwright install chromium
npm run test:browser
npm run preview
```

빌드는 `web/engine/`에 공용 월드와 에셋을 준비하고 Godot의 단일 스레드 Web 프리셋으로 내보낸다. `web/dist/`를 HTTP 서버에 배포하면 된다. CI는 실제 브라우저에서 차량 승하차, 운전과 제동, 사격과 수배, 지도와 일시 정지를 확인한 빌드를 `harbor-web` 아티팩트로 제공한다.
