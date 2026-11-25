# Geometry

서버용 정적 충돌 월드(`StaticWorld`: AABB + 재질 + 태그, 공간 해시). 전용 서버에는 엔진 물리가 없으므로 커버·벽·소품 충돌과 사격 레이캐스트를 여기서 한다. `IPhysicsQuery` 포트도 구현.
레벨 데이터(소품 배치 + Set 리포 `-colonly` 헐 크기)에서 만들어진다.
