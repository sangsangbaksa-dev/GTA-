# CLAUDE.md — 이 리포에서 작업하는 규칙

이 코드베이스는 Claude가 장기적으로 키운다. 다음 세션이 맥락 없이 이어받을 수 있게 유지할 것.

## 먼저 읽기
1. `ARCHITECTURE.md` — 구조와 의존 규칙
2. `docs/adr/` — 왜 그렇게 정했는지

## 절대 규칙
- `core/`에서 `Godot` 네임스페이스 사용 금지. `tools/check-core-purity.sh`가 검사한다.
  엔진 기능이 필요하면 `core/src/Harbor.Core/Ports/`에 인터페이스를 만들고 `game/scripts/Adapters/`에서 구현.
- 게임 규칙(데미지, 수배, 미션, AI 결정, 경제)은 코어에. Godot 쪽은 입력·표현·근거리 물리만.
- 튜닝 값은 코드에 하드코딩하지 말고 `data/` JSON으로. 포맷 바꾸면 `data/schema/`도 갱신.
- 세이브 포맷 변경 시 버전 증가 + `Save/Migrations/`에 마이그레이션.
- 오리지널 콘텐츠만. 실존 게임의 지명·캐릭터·브랜드·에셋을 가져오지 않는다.

## 작업 방식
- 코어 변경엔 테스트 추가: `dotnet test core/tests/Harbor.Core.Tests`
- 구조 결정(모듈 추가, 의존 변경, 외부 라이브러리) → `docs/adr/NNNN-제목.md` 작성 + ARCHITECTURE.md 갱신.
- 각 하위 폴더의 README.md에 책임·확장 포인트를 짧게 유지.
- 문서는 한국어, 코드 식별자·주석은 영어.
