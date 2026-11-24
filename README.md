# Project Harbor

가상의 해안 도시 Port Calloway를 무대로 한 오리지널 오픈월드 액션 게임.

- 엔진: Godot 4 (.NET) / 언어: C#
- 게임 규칙은 엔진 독립 코어(`core/`)에, 표현은 Godot(`game/`)에.

시작은 [ARCHITECTURE.md](ARCHITECTURE.md).

## 빌드
```bash
dotnet test core/tests/Harbor.Core.Tests   # 코어 (엔진 없이)
# Godot 4.4+ .NET 에디터로 game/project.godot 열기
```
