# ADR 0008: Port Calloway 공용 지도

- 상태: 채택 (2026-11-26)

## 맥락

항구를 중심으로 연결된 샌드박스에서 Godot과 브라우저가 같은 도로, 건물, 경계를 보여주고 권한 시뮬레이션이 같은 위치의 충돌을 판정해야 한다.

## 결정

- `tools/harbor_expansion.py`에서 추가 구역과 중심선, 지형, 건물을 배치한다. `python tools/build_harbor_scene.py`로 기존 항구와 새 구역을 한 번에 생성한다.
- `data/world/harbor_front.json`은 도로 중심선, 물과 육지 범위, 구역과 랜드마크, 권한 충돌을 공유한다. 지도의 2차원 좌표는 XZ, 공간 좌표는 XYZ다. 회전한 도로는 로컬 Z에 길이를 두고 Y축 각도를 도 단위로 저장한다.
- Godot 씬과 `data/world/harbor_visuals.json`은 재질과 배치가 같다. 브라우저는 섹터, 형태, 재질이 같은 기하를 묶어 그릴 수 있다.
- 현재 코어 차량과 보행자의 주행면은 Y=0이다. 도로는 해안 둑과 교량을 통해 이어지고 지형의 높낮이는 같은 충돌 경계를 갖는 바위와 수목으로 표현한다.

## 검증

- 코어 테스트에서 출발지부터 일곱 구역을 지나는 경로를 차량 크기로 검사하고, 새 도로와 산책로의 중심선이 충돌을 통과하지 않는지 확인한다.
- 공용 지도와 표현 데이터 스키마는 각각 `data/schema/harbor-layout.schema.json`, `data/schema/harbor-visuals.schema.json`에 둔다.
