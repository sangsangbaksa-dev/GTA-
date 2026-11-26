# ADR 0008: MakeHuman(MPFB2) 기반 인간 캐릭터 생성 파이프라인

- 상태: 채택 (2026-11-26)

## 맥락

기존 `tools/blender/human.py`는 메타볼과 보셀 리메시로 인체를 절차 생성한다. 실루엣과 골격 리그는 동작하지만, 클로즈업과 전신 실루엣에서 얼굴 이목구비(눈·눈썹·입술·귀), 손가락 비례, 의복 주름 표현이 거칠어 자연스러운 인간 캐릭터를 만들기 어렵다.

## 결정

- 인간형 캐릭터 생성에 Blender 4.2 확장 **MPFB2**(`makehumancommunity/mpfb2` v2.0.18, GPL-3.0 에셋 생성 도구)와 **CC0 라이선스 MakeHuman 에셋 팩**만 사용한다. CC-BY 계열 에셋 팩은 포함하지 않는다.
- `tools/blender/setup_makehuman.sh`로 MPFB2 확장과 CC0 시스템·의상·헤어·안경·신발 에셋을 로컬 사용자 확장 경로에 설치하여 헤드리스 환경에서도 동일하게 재현한다.
- `tools/blender/makehuman_person.py`가 헤드리스 Blender에서 MakeHuman 기본 인체·체형 타깃·피부·눈·눈썹·치아·헤어·의상·안경을 조합하고, MakeHuman `game_engine` 53본 리그의 스킨 웨이트를 프로젝트 공용 **51본 리그(`*_rig`)** 및 **42° A포즈(발바닥 원점)** 규격으로 리매핑·베이크하여 단일 스킨드 메시 `.glb`로 내보낸다.
- 런타임 색상 변형이 필요한 의상 슬롯(예: `chr_harbor_clerk`의 `clerk_ochre_coat`)은 직물 명암 텍스처에 `baseColorFactor`를 곱해 내보내고, Godot 임포트 시 내장 텍스처는 `.godot/imported/` 내부에 포함(`gltf/embedded_image_handling=3`)하여 `game/assets/characters/`에 개별 PNG가 풀리지 않도록 유지한다.

## 결과

- 첫 적용 대상인 `chr_harbor_clerk`(Tomas Reyes)는 기존 `RookGait`, `StreetPopulationView`(하버 슬레이트 `#3d5a63` 코트 변형 포함), `HarborRoster` 코드를 수정하지 않고 동일한 51본 골격 규격으로 동작한다.
- 캐릭터 GLB에 PBR 피부·의상 텍스처가 내장되므로 단색 미드폴리 모델보다 파일 크기(~6.6MB, Git LFS 추적)가 커지지만, 전신 비례와 얼굴 가독성이 크게 개선된다.
