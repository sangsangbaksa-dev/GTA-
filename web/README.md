# 브라우저 플레이

브라우저에서 `Harbor.Core`의 WebAssembly 빌드가 싱글플레이를 실행한다. Three.js는 화면과 입력을 담당한다. 로그인 없이 플레이할 수 있다.

## 소스에서 실행

.NET 8 SDK, Node.js 22, Git LFS가 필요하다. 프로젝트 루트에서 실행한다.

```bash
git lfs pull
dotnet workload install wasm-tools
npm --prefix web ci
npm --prefix web run dev
```

브라우저에서 터미널에 출력된 `http://127.0.0.1:5173/`을 열고 **도시로 들어가기**를 누른다. 다른 프로세스가 포트를 쓰고 있으면 Vite가 다음 포트를 출력한다.

## 완성된 빌드 실행

```bash
npm --prefix web run build
npm --prefix web run preview
```

`web/dist/`는 자체 포함 정적 빌드다. 이 폴더를 HTTP로 제공하면 플레이어는 WebGL 2 지원 브라우저에서 페이지를 열 수 있다. CI의 `harbor-web` 아티팩트도 같은 파일이다. 아티팩트를 푼 디렉터리에서 이미 설치된 Python을 사용하려면 `python3 -m http.server 4173`을 실행하고 `http://127.0.0.1:4173/`을 연다.

## 검증

```bash
npm --prefix web test
cd web
npx playwright install chromium
npm run test:browser
```

브라우저 테스트는 실제 입력으로 이동, 차량 승하차·가속·제동, 사격과 수배, 지도, 일시 정지와 재개를 확인한다.
