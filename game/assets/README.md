# game/assets/

바이너리 에셋 (Git LFS). 지금 있는 모델은 전부 `tools/blender/build_assets.py`가 **블렌더 헤드리스로 절차 생성**한다. 손으로 고치지 말고 스크립트를 고친 뒤 재생성.

```
blender -b --factory-startup -P tools/blender/build_assets.py -- \
    --out game/assets --props-out ../set/props
```

| 폴더 | 내용 | 규칙 |
|---|---|---|
| `characters/` | 플레이어·적·NPC (`chr_*`) | 스킨드 메시 1개 + 아마추어 `*_rig`(17본, T포즈), 발 = 원점, 키 ~1.8m |
| `weapons/` | 총기 (`wpn_*`) | 원점 = 손잡이, 총구 -Y(Blender) → glTF +Z. 빈 노드 `muzzle`(총구 이펙트), `grip_off`(보조손 IK) |
| `buildings/` | 건물 (`bldg_*`) | 바닥 중심 원점, 정면 -Y. 메시 이름 `-col` → Godot가 트라이메시 충돌 생성 |

- 소품(`prop_*`: 상자, 드럼통, 바리케이드, 모래주머니 등)은 별도 리포 **sangsangbaksa-dev/Set**의 `props/`에 둔다 (ADR 0003).
- 본 이름: `hips, spine, chest, neck, head, upper_arm/forearm/hand.{L,R}, thigh/shin/foot.{L,R}` — 애니메이션 리타깃 기준.
- 아트 스타일: 저폴리 플랫 컬러. 실존 게임/브랜드 디자인 금지.
- 미리보기: `docs/art/preview_*.png` (`tools/blender/render_preview.py`).
