# Project Harbor 로컬 테스트 호스트

데스크톱 플레이는 게임 프로세스 안에서 `HarborAuthority`를 실행한다. 여기의 .NET 호스트는 개발자가 같은 규칙과 입력 프로토콜을 엔진 없이 검증할 때 사용한다. 루프백에서 한 연결을 받는다.

```bash
dotnet run --project server/Harbor.Server.csproj -- --port 7788
```

`Ctrl+C`로 종료한다.
