# game/assets/

바이너리 에셋 (Git LFS). 지금 있는 모델은 전부 `tools/blender/build_assets.py`가 **블렌더 헤드리스로 절차 생성**한다. 손으로 고치지 말고 스크립트를 고친 뒤 재생성.

```
blender -b --factory-startup -P tools/blender/build_assets.py -- \
    --out game/assets --props-out ../set/props
```

| 폴더 | 내용 | 규칙 |
|---|---|---|
| `characters/` | 플레이어·적·NPC (`chr_*`) | 스킨드 메시 1개(~5만 면) + 아마추어 `*_rig`(51본, 손가락 포함, A포즈 42°), 발 = 원점 |
| `weapons/` | 총기 (`wpn_*`) | 원점 = 손잡이, 총구 -Y(Blender) → glTF +Z. 빈 노드 `muzzle`(총구 이펙트), `grip_off`(보조손 IK) |
| `buildings/` | 건물 (`bldg_*`) | 바닥 중심 원점, 정면 -Y. 메시 이름 `-col` → Godot가 트라이메시 충돌 생성 |

- 소품(`prop_*`: 상자, 드럼통, 바리케이드, 모래주머니 등)은 별도 리포 **sangsangbaksa-dev/Set**의 `props/`에 둔다 (ADR 0003).
- 본 이름: `hips, spine, chest, neck, head, shoulder/upper_arm/forearm/hand.{L,R}, {thumb,index,middle,ring,pinky}_{1..3}.{L,R}, thigh/shin/foot/toe.{L,R}` — 애니메이션 리타깃 기준.
- 캐릭터 제작 방식: `tools/blender/human.py` (메타볼 몸 → 보셀 리메시 → 자동 웨이트 → 몸을 복제해 옷 레이어 생성, 웨이트 상속). 로스터는 `characters.py`.
- 아트 스타일: 스타일라이즈드 미드폴리, 단색 머티리얼(텍스처 없음). 실존 게임/브랜드 디자인 금지.
- 미리보기: `docs/art/preview_*.png` (`render_preview.py`), 캐릭터 클로즈업 `docs/art/characters/` (`render_characters.py`).
