# tools/blender/

블렌더(4.2 LTS) 헤드리스 스크립트. GUI 없이 에셋을 만든다.

- `build_assets.py` — 캐릭터·총기·건물·소품 생성 → `.glb`. 새 에셋 = 함수 하나 + `GROUPS`에 등록.
- `render_preview.py` — `.glb`들을 나란히 놓고 Cycles(CPU)로 미리보기 PNG 렌더.

확장 포인트: `Body`(본에 파츠를 강체 바인딩), `box/cyl/sphere/limb` 헬퍼, 공용 팔레트 `P()`.
