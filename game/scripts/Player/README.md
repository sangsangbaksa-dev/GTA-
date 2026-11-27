# Player

`ThirdPersonController`는 마우스와 게임패드 입력을 `InputCmd`로 전달한다.
코어가 결정한 이동, 차량과 전투 상태를 주인공 Tomas Reyes(`chr_harbor_clerk`)와 3인칭 카메라에 표시하며,
조준 시 화면 중앙에서 찾은 목표 방향을 전달해 어깨 카메라와 판정을 맞춘다.
모델에 Mixamo NLA 클립이 내장되어 있으면 `MixamoGait`가 `Idle`, `Walk`, `Jog`, `Run`, `TurnLeft`, `TurnRight`,
`Dance`(`B` 키 / 패드 D-pad 아래), `CrouchIdle`, `CrouchWalk`를 부드럽게 크로스페이드하고, 클립이 없는 모델은
절차적 `RookGait`로 폴백한다.

로컬에서도 전용 서버와 같은 `HarborAuthority`를 구동한다. `data/world/harbor_front.json`은
플레이어 이동, 차량 충돌, 가시선과 카메라 가림에 사용하는 공용 충돌 데이터다.
