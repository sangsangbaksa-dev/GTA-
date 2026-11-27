# World

`ChunkGrid`는 플레이어 위치와 속도로 올려야 할 청크를 고른다. 칸 크기와 반경은 `data/world/harbor_streaming.json`이다. 씬을 올리고 내리는 일은 Godot `ChunkStreamer`가 맡는다.

`StreetPopulation`은 항구 보행자와 교통을 서버에서 시뮬레이션한다. 인구수와 동선은 `data/world/harbor_population.json`에 정의한다. 같은 시드와 입력이면 같은 위치와 동작을 만든다.

보행자는 인도를 왕복하고 끝 지점에서 잠시 멈춘다. 사격 소리가 들리면 반대쪽으로 달린 뒤 평소 속도로 돌아온다. 교통은 도로 순환 동선을 따라가며 앞차와 도로 위 플레이어를 만나면 감속한다. 화면에 표시할 주변 상태는 `HarborAuthority`가 클라이언트에 전달한다.
