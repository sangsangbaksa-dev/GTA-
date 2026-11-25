# Project Harbor 전용 서버

전용 서버는 데스크톱 클라이언트와 같은 `HarborAuthority`에서 캐릭터 이동, 전투, 차량, 임무를 실행한다. 클라이언트는 입력을 보내고 서버가 계산한 상태를 받는다. 전용 서버에는 Godot이 필요 없다.

Linux x86_64 서버 아카이브는 .NET 런타임과 플레이테스트 데이터를 포함한다. 아카이브를 모두 푼 뒤 아래 명령으로 실행한다.

```bash
./Harbor.Server --listen 0.0.0.0 --port 7788
```

호스트에서만 접속하려면 `--listen`을 생략한다. 같은 LAN 또는 사설 네트워크에서 최대 4명이 접속할 수 있다. 게임 실행 파일에 `-- --connect 192.168.1.10:7788`을 전달하고 주소를 호스트의 실제 주소로 바꾼다.

소스에서 개발 서버를 실행하려면 리포지토리 루트에서 다음 명령을 사용한다.

```bash
dotnet run --project server/Harbor.Server.csproj -- --listen 0.0.0.0 --port 7788
```

`Ctrl+C`로 정상 종료한다.
