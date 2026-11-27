# tools/blender/

블렌더(4.2 LTS) 헤드리스 스크립트. GUI 없이 에셋을 만든다.

- `build_assets.py` — 캐릭터·총기·건물 → `game/assets/`. 새 에셋 = 함수 하나 + `GROUPS`에 등록.
- `human.py` — 사람 생성기: 메타볼 몸(손가락·얼굴), 리메시, 51본 리그+자동 웨이트, 옷/머리 레이어(`garment`), 강체 장비(`bind_rigid`).
- `characters.py` — 캐릭터 로스터 5종. 새 캐릭터 = `dress(ctx)` 함수 하나 + `ROSTER` 등록.
- `harbor_people.py` — Harbor City 의상과 얼굴 4종. `build_assets.py -- --out game/assets --only harbor_people`로 이 묶음만 생성할 수 있다. `--only chr_harbor_rook`처럼 모델 ID로 골라서 생성할 수도 있다.
- `makehuman_person.py` — MPFB2와 CC0 에셋 팩으로 Amara Cole, Mateo Silva, Linnea Park, Arthur Pendelton을 제작한다. 브라우저와 데스크톱이 51본 리그와 공용 이동 애니메이션을 내장한 GLB 네 개를 사용한다.
- `build_props.py` — 소품 46종 → Set 리포 `props/` + `--manifest`(태그 목록, `data/props/props.json`에도 복사). 새 소품 = 함수 하나 + `PROPS`에 등록(분류·태그·설명). 작업선 `prop_harbor_pilot_tug`의 원점은 수선이다.
- `build_vehicles.py` — 소형차 Calloway Pip → `--out game/assets/vehicles`. 앞바퀴 조향 및 네 바퀴 회전 노드를 포함한다.
- `render_characters.py` — 캐릭터 전신·얼굴 리뷰 렌더.
- `render_preview.py` — `.glb`들을 나란히 놓고 Cycles(CPU)로 미리보기 PNG 렌더. `*-colonly` 충돌 메시는 숨김.

확장 포인트: 체형 파라미터(`human.DEFAULT`: bulk·muscle·fem·belly…), 영역 함수(`characters.torso_band/legs_band/feet/hand_region`), `box/cyl/sphere` 헬퍼, 공용 팔레트 `P()`. 소품 쪽은 `profile`(단면 돌출), `lathe`(회전체), `col/col_cyl`(충돌 헐), `marker`(상호작용 지점).

## MakeHuman 시민 재생성

Blender 4.3에서 검증했다. MPFB2 확장과 MakeHuman CC0 에셋 팩은 지정한 캐시에 설치된다.

```bash
export BLENDER_USER_RESOURCES="${TMPDIR:-/tmp}/harbor-makehuman/blender"
tools/blender/setup_makehuman.sh
blender -b --factory-startup --python-exit-code 1 -P tools/blender/build_assets.py -- \
  --out game/assets --only makehuman_civilians
```
