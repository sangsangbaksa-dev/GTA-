# game/assets/

모델 바이너리는 Git LFS에 저장한다. `tools/blender/`의 Blender 헤드리스 생성 스크립트를 수정하고 다시 내보낸다.

```
blender -b --factory-startup -P tools/blender/build_assets.py -- \
    --out game/assets
blender -b --factory-startup -P tools/blender/build_props.py -- \
    --out ../set/props --manifest data/props/props.json
blender -b --factory-startup -P tools/blender/build_vehicles.py -- \
    --out game/assets/vehicles
```

| 폴더 | 내용 | 규칙 |
|---|---|---|
| `characters/` | 플레이어·적·NPC (`chr_*`) | 스킨드 메시 1개(~5만 면) + 아마추어 `*_rig`(51본, 손가락 포함, A포즈 42°), 발 = 원점 |
| `weapons/` | 총기 (`wpn_*`) | 원점 = 손잡이, 총구 -Y(Blender) → glTF +Z. 빈 노드 `muzzle`(총구 이펙트), `grip_off`(보조손 IK) |
| `buildings/` | 건물 (`bldg_*`) | 바닥 중심 원점, 정면 -Y. 메시 이름 `-col` → Godot가 트라이메시 충돌 생성 |
| `vehicles/` | 차량 (`veh_*`) | 바닥 중심 원점, 차 앞쪽 glTF +Z. `build_vehicles.py`가 생성하며 바퀴별 회전 메시를 포함 |
| `props/` | Harbor Front에 배치한 소품과 항만 랜드마크 (`prop_*`) | Set과 동일한 GLB. 충돌 전용 메시는 `-colonly` |

- 소품 전체 목록은 **sangsangbaksa-dev/Set**의 `props/`에서 관리한다 (ADR 0003). 부두 소품은 `tools/blender/build_props.py`로 다시 생성하고, 두 저장소의 GLB를 같은 산출물로 갱신한다.
- 본 이름: `hips, spine, chest, neck, head, shoulder/upper_arm/forearm/hand.{L,R}, {thumb,index,middle,ring,pinky}_{1..3}.{L,R}, thigh/shin/foot/toe.{L,R}` — 애니메이션 리타깃 기준.
- 캐릭터 제작 방식: `tools/blender/human.py` (메타볼 몸 → 보셀 리메시 → 자동 웨이트 → 몸을 복제해 옷 레이어 생성, 웨이트 상속). 로스터는 `characters.py`.
- 아트 스타일: 스타일라이즈드 미드폴리, 단색 머티리얼(텍스처 없음). 실존 게임/브랜드 디자인 금지.
- 미리보기: `docs/art/preview_*.png` (`render_preview.py`), 캐릭터 클로즈업 `docs/art/characters/` (`render_characters.py`).
