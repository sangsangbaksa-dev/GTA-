# tools/blender/

블렌더(4.2 LTS) 헤드리스 스크립트. GUI 없이 에셋을 만든다.

- `build_assets.py` — 캐릭터·총기·건물 → `game/assets/`. 새 에셋 = 함수 하나 + `GROUPS`에 등록.
- `human.py` — 절차적 사람 생성기: 메타볼 몸(손가락·얼굴), 리메시, 51본 리그+자동 웨이트, 옷/머리 레이어(`garment`), 강체 장비(`bind_rigid`).
- `setup_makehuman.sh` — Blender 4.2용 MPFB2 확장, CC0 MakeHuman 에셋 팩, Mixamo 모션 스트림 캐시를 헤드리스 환경에 설치한다 (ADR 0008).
- `makehuman_person.py` — MakeHuman(MPFB2) 인체·체형 타깃·피부·헤어·의상을 조합하고 공용 51본 리그(`*_rig`, 42° A포즈)로 변환하는 항구 인물 8종 생성기 (`chr_harbor_clerk`, `chr_harbor_officer`, `chr_harbor_crew`, `chr_harbor_rook`, `chr_harbor_dockhand`, `chr_harbor_stevedore`, `chr_npc_civilian`, `chr_harbor_broker`).
- `mixamo_mocap.py` — Mixamo 30FPS 모션 스트림 9종(`Idle`, `Walk`, `Jog`, `Run`, `TurnLeft`, `TurnRight`, `Dance`, `CrouchIdle`, `CrouchWalk`)을 Harbor 51본 리그로 리타겟팅해 주인공(`chr_harbor_clerk`) NLA 트랙으로 내장하고, 단독 FBX/glTF 모션 번들 내보내기도 지원한다.
- `characters.py` — 기본 캐릭터 로스터 5종(`chr_npc_civilian`은 `makehuman_person.py` 사용, 전투 적 3종과 `chr_player_rook`는 `human.py` 절차 생성).
- `harbor_people.py` — Harbor City 인물 7종 로스터(`makehuman_person.py` 빌더 연결). `build_assets.py -- --out game/assets --only harbor_people`로 묶음 생성하거나 `--only chr_harbor_clerk`처럼 모델 ID로 골라 생성할 수 있다.
- `build_props.py` — 소품 46종 → Set 리포 `props/` + `--manifest`(태그 목록, `data/props/props.json`에도 복사). 새 소품 = 함수 하나 + `PROPS`에 등록(분류·태그·설명). 작업선 `prop_harbor_pilot_tug`의 원점은 수선이다.
- `build_vehicles.py` — 소형차 Calloway Pip → `--out game/assets/vehicles`. 앞바퀴 조향 및 네 바퀴 회전 노드를 포함한다.
- `render_characters.py` — 캐릭터 전신·얼굴 리뷰 렌더.
- `render_preview.py` — `.glb`들을 나란히 놓고 Cycles(CPU)로 미리보기 PNG 렌더. `*-colonly` 충돌 메시는 숨김.

확장 포인트: 체형 파라미터(`human.DEFAULT`: bulk·muscle·fem·belly…), 영역 함수(`characters.torso_band/legs_band/feet/hand_region`), `box/cyl/sphere` 헬퍼, 공용 팔레트 `P()`. 소품 쪽은 `profile`(단면 돌출), `lathe`(회전체), `col/col_cyl`(충돌 헐), `marker`(상호작용 지점).
