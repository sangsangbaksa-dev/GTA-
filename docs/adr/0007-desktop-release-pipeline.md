# ADR 0007: 데스크톱 자동 배포 파이프라인

- 상태: 채택 (2026-11-25)

## 맥락
"지금 바로 배포" 요청. Godot 4 .NET(C#) 프로젝트는 웹(HTML5) export를 지원하지 않는다. 데스크톱 빌드를 매번 손으로 만들면 다음 세션이 재현하기 어렵다.

## 결정
- `.github/workflows/release.yml`이 Godot 4.4.1 .NET 에디터 + export 템플릿을 내려받아 헤드리스로 export한다 (캐시).
- 대상: Windows x86_64, Linux x86_64. 프리셋은 `game/export_presets.cfg`에 커밋 (서명·비밀값 없음).
- `.sln`은 CI에서 생성한다 (`dotnet new sln` + `game`, `Harbor.Core` 추가). 리포에는 두지 않는다.
- `data/`는 실행 파일 옆 `data/`로 복사해 함께 배포한다.
- PR: 빌드 + 워크플로 아티팩트. `main` 푸시: 롤링 프리릴리스 `latest`를 지우고 다시 만든다.

## 결과
- main이 머지될 때마다 Releases의 `latest`에서 받는다. 버전 태그 릴리스는 플레이 가능한 마일스톤부터.
- Godot 버전을 올리면 `GODOT_VERSION`, `game/Harbor.csproj`의 SDK 버전을 같이 바꾼다.
- 웹 배포가 필요해지면 Godot의 C# 웹 지원을 다시 검토한다.
