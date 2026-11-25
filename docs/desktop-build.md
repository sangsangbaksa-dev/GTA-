# 데스크톱 배포 빌드

Linux에서 Godot **4.4.1 .NET** 에디터, 같은 버전의 **.NET export templates**, .NET 8 SDK, Git LFS, `curl`, `zip`을 준비한다. Godot 에디터에서 **Editor → Manage Export Templates**를 열어 템플릿을 설치한다. 초기 빌드에는 NuGet과 GitHub에 접속할 수 있어야 한다.

```bash
git lfs pull
GODOT_BIN=/path/to/Godot_v4.4.1-stable_mono_linux.x86_64 \
  tools/package-release.sh /tmp/harbor-release
cd /tmp/harbor-release
sha256sum -c SHA256SUMS
```

Windows와 Linux 데스크톱 아카이브에 실행 파일, Godot 리소스 팩, .NET 런타임, 실행 안내, 제3자 라이선스가 들어 있다. 각 아카이브 전체를 같은 디렉터리에 풀어 실행한다. 로컬 권한형 코어는 데스크톱 실행 파일에서 시작한다.
