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

웹판은 월드의 3D 디테일을 128m 격자로 나눠 `godot/chunks/`에서 내려받는다. 시작 지점 주변을 먼저 불러오고, 이동 중 진행 방향의 청크를 준비한다. 멀어진 청크는 해제하고 해안선과 큰 건물 윤곽은 유지한다. `world/harbor_front.json`과 시뮬레이션은 전체 지도와 충돌을 계속 처리한다.

도로 표면과 차선은 청크 경계를 지나도 유지된다. Alderwash의 숲 언덕, Cinder Flats의 사구, Lantern Point의 해안 언덕에서는 보행자와 차량의 높이가 지형을 따라간다.

지도에서 ◆로 표시된 Customs Exchange Cafe, Atlantic Repair, Alderwash Lodge에는 열린 출입구로 걸어 들어갈 수 있다. Atlantic Repair의 작업 통로는 차로 양방향 통과할 수 있다.

## 실내 모델 수정

실내 모델은 Blender 4.3.2에서 편집한다. 바닥 평면과 지붕을 다시 생성하려면 공식 [Building Tools](https://github.com/ranjian0/building_tools)의 `0216534645b15b3f8aac0bd1d7e6674e2972b9b3` 커밋을 체크아웃한다. 저장소 루트에서 다음을 실행하면 게임용 GLB와 충돌 데이터를 다시 만든다. `--` 뒤의 디렉터리에는 편집용 `.blend`를 저장한다.

```bash
HARBOR_BUILDING_TOOLS=/path/to/building_tools blender -b --factory-startup --python-exit-code 1 \
  --python tools/build_harbor_interiors.py -- /path/to/interior-blends
python3 tools/build_harbor_scene.py
```
