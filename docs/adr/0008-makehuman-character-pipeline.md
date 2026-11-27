# ADR 0008: MakeHuman(MPFB2) 기반 인간 캐릭터 생성 파이프라인

- 상태: 채택 (2026-11-26)

## 맥락

기존 `tools/blender/human.py`는 메타볼과 보셀 리메시로 인체를 절차 생성한다. 실루엣과 골격 리그는 동작하지만, 클로즈업과 전신 실루엣에서 얼굴 이목구비(눈·눈썹·입술·귀), 손가락 비례, 의복 주름 표현이 거칠어 자연스러운 인간 캐릭터를 만들기 어렵다.

## 결정

- 인간형 캐릭터 생성에 Blender 4.2 확장 **MPFB2**(`makehumancommunity/mpfb2` v2.0.18, GPL-3.0 에셋 생성 도구)와 **CC0 라이선스 MakeHuman 에셋 팩**만 사용한다. CC-BY 계열 에셋 팩은 포함하지 않는다.
- `tools/blender/setup_makehuman.sh`로 MPFB2 확장과 CC0 시스템·의상·헤어·안경·신발 에셋을 로컬 사용자 확장 경로에 설치하여 헤드리스 환경에서도 동일하게 재현한다.
- `tools/blender/makehuman_person.py`가 헤드리스 Blender에서 MakeHuman 기본 인체·체형 타깃·피부·눈·눈썹·치아·헤어·의상·안경을 조합하고, MakeHuman `game_engine` 53본 리그의 스킨 웨이트를 프로젝트 공용 **51본 리그(`*_rig`)** 및 **42° A포즈(발바닥 원점)** 규격으로 리매핑·베이크하여 단일 스킨드 메시 `.glb`로 내보낸다.
- 런타임 색상 변형이 필요한 의상 슬롯(예: `chr_harbor_clerk`의 `clerk_ochre_coat`)은 직물 명암 텍스처에 `baseColorFactor`를 곱해 내보내고, Godot 임포트 시 내장 텍스처는 `.godot/imported/` 내부에 포함(`gltf/embedded_image_handling=3`)하여 `game/assets/characters/`에 개별 PNG가 풀리지 않도록 유지한다.
- `tools/blender/mixamo_mocap.py`가 Mixamo 공용 모션 카탈로그(`Y Bot` 골격 + 30FPS `sjson` 모션 스트림)에서 `Idle`, `Walk`, `Jog`, `Run`, `TurnLeft`, `TurnRight`, `Dance`, `CrouchIdle`, `CrouchWalk` 9종 클립을 가져와 Harbor 51본 리그에 리타겟팅하고 NLA 트랙으로 GLB에 내장한다. (Mixamo의 커스텀 메시 업로드 `POST /api/v1/characters` 및 서버 FBX 변환 `POST /api/v1/animations/export`는 대화형 Adobe OAuth 로그인 없이는 `HTTP 401 / 401013`으로 차단되므로, 대화형 업로드용 FBX/OBJ 내보내기와 헤드리스 스트림 리타겟팅을 함께 제공한다.)

## 결과

- Port Calloway의 모든 시민·로스터 인물 8종(`chr_harbor_clerk`, `chr_harbor_officer`, `chr_harbor_crew`, `chr_harbor_rook`, `chr_harbor_dockhand`, `chr_harbor_stevedore`, `chr_npc_civilian`, `chr_harbor_broker`)이 각기 다른 체형·얼굴·피부·헤어·의상 조합을 가진 MakeHuman(MPFB2) 캐릭터로 생성된다.
- `chr_harbor_clerk`(Tomas Reyes)는 플레이어 주인공(`ThirdPersonController`, `CombatPresentation`, `ActorViews`)으로 사용되며, 거리 보행자(`StreetPopulationView`) 4개 슬롯은 각기 독립된 시민 모델 4종(`chr_harbor_dockhand` Amara Cole, `chr_harbor_stevedore` Mateo Silva, `chr_npc_civilian` Linnea Park, `chr_harbor_broker` Arthur Pendelton)으로 채워 주인공 고유성을 유지한다. 명명된 로스터 역할 역시 각각 전용 모델(`chr_harbor_officer` Mara Vale, `chr_harbor_crew` Niko Voss, `chr_harbor_rook` Rook, `chr_harbor_dockhand` Amara Cole)을 사용한다.
- `game/scripts/Player/MixamoGait.cs`가 내장된 Mixamo 클립 9종을 이동 속도·웅크리기·제자리 회전·댄스(`B` 키 / 패드 D-pad 아래) 상태에 맞춰 크로스페이드 재생하며, 비전투 시에는 `CombatPresentation.WeaponPose`의 팔 IK를 해제해 자연스러운 팔 흔들림과 댄스 모션을 보존하고 조준·사격·재장전 시에만 총기 파지 자세로 부드럽게 블렌딩한다.
- 캐릭터 GLB에 PBR 피부·의상 텍스처(주인공에는 9종의 51본 모션 클립 포함)가 내장되므로 단색 미드폴리 모델보다 파일 크기(캐릭터당 ~3.6~7.3MB, Git LFS 추적)가 커지지만, 전신 비례·얼굴 가독성·모션 자연스러움이 크게 개선된다.
