# tools/blender/

블렌더(4.2 LTS) 헤드리스 스크립트. GUI 없이 에셋을 만든다.

- `build_assets.py` — 캐릭터·총기·건물·소품 생성 → `.glb`. 새 에셋 = 함수 하나 + `GROUPS`에 등록.
- `human.py` — 사람 생성기: 메타볼 몸(손가락·얼굴), 리메시, 51본 리그+자동 웨이트, 옷/머리 레이어(`garment`), 강체 장비(`bind_rigid`).
- `characters.py` — 캐릭터 로스터 5종. 새 캐릭터 = `dress(ctx)` 함수 하나 + `ROSTER` 등록.
- `render_characters.py` — 캐릭터 전신·얼굴 리뷰 렌더.
- `render_preview.py` — `.glb`들을 나란히 놓고 Cycles(CPU)로 미리보기 PNG 렌더.

확장 포인트: 체형 파라미터(`human.DEFAULT`: bulk·muscle·fem·belly…), 영역 함수(`characters.torso_band/legs_band/feet/hand_region`), `box/cyl/sphere` 헬퍼, 공용 팔레트 `P()`.
