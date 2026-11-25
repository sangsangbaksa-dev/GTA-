# 데스크톱 및 전용 서버 배포 빌드

Linux에서 Godot **4.4.1 .NET** 에디터, 같은 버전의 **.NET export templates**, .NET 8 SDK, Git LFS, `curl`, `zip`을 준비한다. Godot 에디터에서 **Editor → Manage Export Templates**를 열어 템플릿을 설치한다. 초기 빌드에는 NuGet과 GitHub에 접속할 수 있어야 한다.

```bash
git lfs pull
GODOT_BIN=/path/to/Godot_v4.4.1-stable_mono_linux.x86_64 \
  tools/package-release.sh /tmp/harbor-release
cd /tmp/harbor-release
sha256sum -c SHA256SUMS
```

Windows와 Linux 데스크톱 아카이브에 실행 파일, Godot 리소스 팩, .NET 런타임, 실행 안내, 제3자 라이선스가 들어 있다. Linux 전용 서버 아카이브에는 .NET 런타임과 실행에 필요한 항구 데이터가 들어 있다. 각 아카이브 전체를 같은 디렉터리에 풀어 실행한다.

전용 서버만 빌드하려면 `tools/package-server.sh /tmp/harbor-release`를 실행한다. 서버 시작과 LAN 접속 방법은 [전용 서버 안내](../server/README.md)에 있다.
