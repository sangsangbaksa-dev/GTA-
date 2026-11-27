# Streaming

항구 플레이테스트의 지형·건물·소품을 플레이어 주변 청크만 올리고 나머지는 내린다.

- 설정은 `data/world/harbor_streaming.json`. 청크 64m, 로드 반경 2칸(체비쇼프), 언로드 여유 1칸, 속도 방향 1.5초.
- 필요한 칸은 코어 `ChunkGrid`가 정한다. 칸 좌표는 `floor(x / 크기)`, `floor(z / 크기)`.
- 씬은 `scenes/world/chunks/chunk_X_Z.tscn`. 하늘·해수면·충돌은 `HarborFront.tscn`에 남긴다.
- `ChunkStreamer`가 스폰 주변을 먼저 읽고, 이동 후에는 `ResourceLoader.LoadThreadedRequest`로 올리며 반경 밖은 내린다.
- 도시 계획의 256m는 `data/world/districts.json`에 있다. 360×290m 항구에서는 맵 전체가 상주하므로 쓰지 않는다.

청크 씬은 `tools/map/split_harbor_chunks.py`가 만든다. `tools/build_harbor_scene.py`로 항구 씬을 다시 쓰면 이어서 분할한다.
