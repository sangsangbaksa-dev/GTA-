# Vehicles

`ArcadeVehicle`은 고정 틱에서 평면 차량 이동, 후진, 제동, 속도에 따른 조향을 처리한다. `StaticWorld`와 차량의 회전 경계를 검사하여 벽과 배치 소품을 통과하지 않는다.

처음 구현한 차량은 `data/vehicles/calloway_compact.json`의 Calloway Pip이다. 차량의 위치, 방향, 속도, 조향각 및 누적 이동 거리를 복제할 수 있다.
