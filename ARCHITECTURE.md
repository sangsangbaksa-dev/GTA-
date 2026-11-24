# ARCHITECTURE

> 코드네임 **Project Harbor** — 가상의 해안 도시 **Port Calloway**를 무대로 한 오리지널 오픈월드 액션 게임.
> **전투 중심**: 핵심 루프는 3인칭 커버 슈팅. 운전·탐험은 전투 사이를 잇는 수단 ([ADR 0003](docs/adr/0003-combat-focus-and-asset-pipeline.md)).
> 세계관·캐릭터·이름·에셋은 전부 자체 제작한다. 기존 게임의 콘텐츠를 재현하지 않는다.

이 문서는 코드베이스의 **지도**다. 구조를 바꾸면 이 문서와 `docs/adr/`를 같이 갱신한다.

---

## 1. 핵심 결정

| 항목 | 결정 | 근거 |
|---|---|---|
| 엔진 | **Godot 4.x (.NET 빌드)** | 오픈소스, 텍스트 기반 씬(`.tscn`)이라 Claude가 읽고 diff 가능 |
| 언어 | **C# (.NET 8)** | 엔진과 무관하게 돌아가는 코어 라이브러리를 만들기 쉬움, 테스트 용이 |
| 물리 | Godot 내장 **Jolt** | 차량·충돌은 엔진 쪽, 원거리 시뮬레이션은 코어 쪽 |
| 구조 | **헥사고날(포트 & 어댑터)** | 나중에 엔진 없는 다른 게임과 합칠 예정 → 게임 규칙은 엔진 독립 |
| 게임 방향 | **전투 중심** (커버 슈팅 + 적 전술 AI) | 사용자 요청 |
| 에셋 | **블렌더 헤드리스 스크립트 → glTF** | 텍스트 원본이라 diff·재생성 가능 |

상세: [`docs/adr/0001-engine-godot.md`](docs/adr/0001-engine-godot.md), [`docs/adr/0002-portable-core.md`](docs/adr/0002-portable-core.md), [`docs/adr/0003-combat-focus-and-asset-pipeline.md`](docs/adr/0003-combat-focus-and-asset-pipeline.md)

### 가장 중요한 규칙: 코어는 Godot을 모른다

```
 ┌─────────────────────────────────────────────────────────┐
 │  game/  (Godot 프로젝트)                                  │
 │   씬, 렌더링, 입력, 오디오, UI, 근거리 물리, 청크 로딩       │
 │   └─ Harbor.Godot  (어댑터: 코어 포트를 Godot으로 구현)     │
 └───────────────▲──────────────────────────┬──────────────┘
                 │ 이벤트/스냅샷 (읽기)        │ 커맨드 (쓰기)
 ┌───────────────┴──────────────────────────▼──────────────┐
 │  core/  Harbor.Core  (순수 .NET, Godot 참조 0)            │
 │   시뮬레이션 월드, 규칙, AI 결정, 미션, 수배, 경제, 세이브 모델 │
 └─────────────────────────────────────────────────────────┘
```

- `core/`는 `using Godot;` **금지**. CI에서 검사한다 (`tools/check-core-purity.sh`).
- 코어는 **고정 틱**(기본 30Hz)으로 돈다. Godot은 매 프레임 코어 상태를 보간해서 그린다.
- Godot → 코어는 **커맨드**(`PlayerCommand`)로만, 코어 → Godot은 **이벤트 + 상태 스냅샷**으로만.
- 코어가 엔진 기능이 필요하면(레이캐스트, 내비 등) `core/Ports/`에 인터페이스를 정의하고 `game/`이 구현한다.
- 그래서 **엔진 없는 다른 게임과 합칠 때**: 그 게임이 `Harbor.Core`를 참조하고 포트만 구현하면 된다. (반대로 그 게임의 로직을 코어로 가져와도 된다.)

---

## 2. 모듈

### 2.1 코어 (`core/src/`, 엔진 독립)

| 프로젝트 | 책임 |
|---|---|
| `Harbor.Core` | 공통 타입(ID, 벡터, 시간), 이벤트 버스, 고정 틱 루프, `SimWorld` 컨테이너 |
| `Harbor.Core.Data` | `data/` JSON 로딩·검증·레지스트리 |
| `Harbor.Core.World` | 도시 구조: District, 청크 그리드, 도로/인도 그래프, 시간·날씨, 인구 예산 |
| `Harbor.Core.Agents` | NPC·교통 에이전트 (경량 ECS 스타일 배열), 행동 결정(유틸리티/상태머신) |
| `Harbor.Core.Vehicles` | 차량 스펙·소유·손상 상태, 원거리 차량 kinematic 이동 |
| `Harbor.Core.Combat` | 체력·방어구·데미지(거리 감쇠·부위 배율), 무기·탄약, 커버 판정, 범죄 판정 |
| `Harbor.Core.Heat` | 수배 레벨, 경찰 응답 규칙 |
| `Harbor.Core.Missions` | 데이터 기반 미션 실행기, 목표 타입, 스토리 플래그 |
| `Harbor.Core.Save` | 세이브 모델, 버전·마이그레이션, 직렬화 |
| `Harbor.Core.Net` | 서버 권한형 멀티: 전송 포트, 메시지, 세션, 예측·보정, 보간, 랙 보정 |
| `Harbor.Core.Economy` | 화폐·계정·원장, 상점, 세탁, 사망 드롭 |
| `Harbor.Core.Activities` | 의뢰·습격·구역전·보급·현상금 |

> 처음엔 하나의 `Harbor.Core` 어셈블리 안에 네임스페이스로 나누고, 커지면 위 표대로 분리한다. 의존 방향은 처음부터 지킨다.

**코어 내부 의존 방향** (아래로만)
```
Activities ─> Economy ─> Core
Activities ─> Missions
Net ─> (모든 모듈의 커맨드/스냅샷; 규칙 모듈은 Net을 모른다)
Missions ─┬─> Heat ─┐
          ├─> Combat ┼─> Agents ─┬─> World ─> Data ─> Core
          └─> Vehicles ──────────┘
Save ─> (모든 모듈의 ISaveable)
```
같은 층끼리는 이벤트 버스로 통신.

### 2.2 엔진 레이어 (`game/`, Godot)

| 폴더 | 책임 |
|---|---|
| `game/scripts/Bridge/` | `CoreHost` 노드: 코어 생성·틱 구동·커맨드 전달·이벤트 분배 |
| `game/scripts/Adapters/` | 포트 구현 (`IPhysicsQuery`→Godot 레이캐스트, `IFileSystem`→`user://`, `ILog`→`GD.Print`) |
| `game/scripts/Streaming/` | 청크 단위 씬 로딩/언로드 (`ResourceLoader.LoadThreadedRequest`) |
| `game/scripts/Views/` | 코어 엔티티 → 노드 표현 (캐릭터, 차량, 군중 `MultiMeshInstance3D`) |
| `game/scripts/Player/` | 입력 → `PlayerCommand`, 카메라 |
| `game/scripts/Vehicles/` | 근거리 차량 물리 (`VehicleBody3D`/Jolt) ↔ 코어 상태 동기화 |
| `game/scripts/UI/` | HUD, 미니맵, 메뉴, 인게임 폰 |
| `game/scenes/`, `game/assets/` | 씬·에셋. 에셋 규칙은 `game/assets/README.md` |

---

## 3. 시스템별 설계

### 3.1 월드 스트리밍
Godot에는 UE의 World Partition 같은 내장 기능이 없으므로 직접 만든다.
- 도시를 **청크 그리드**(기본 256m × 256m)로 나눈다. 청크 = `game/scenes/world/chunks/chunk_X_Y.tscn`.
- 코어 `World`가 "어떤 청크가 필요한지"(플레이어 위치·속도 기반 반경)를 계산 → Godot `Streaming`이 비동기 로딩.
- 원거리는 청크별 저폴리 **임포스터/HLOD 메시**로 대체.
- **원점 이동(floating origin)**: 플레이어가 원점에서 4km 이상 멀어지면 전체를 재정렬. 코어 좌표는 `double`, Godot 좌표는 원점 상대 `float`.
- 인테리어는 별도 씬, 진입 시 외부 인구 예산 축소.

### 3.2 차량
- 스펙: `data/vehicles/*.json` (질량, 엔진 토크, 기어, 서스펜션, 핸들링).
- **LOD 시뮬레이션**:
  - 근거리(~150m): Godot `VehicleBody3D` + Jolt 풀 물리. 결과 상태를 코어에 다시 써 넣음.
  - 원거리: 코어가 도로 그래프 위에서 kinematic 이동. 근거리 진입 시 물리 노드로 "승격", 이탈 시 "강등".
- 보트·헬기는 `IDrivable` 확장으로 후속.

### 3.3 NPC / AI
- 코어 `Agents`: 수천 개의 에이전트를 구조체 배열(SoA)로 관리. 도로/인도 그래프 위 이동.
- 행동: 보행자(걷기·도망·구경·신고), 운전자(차선 유지·신호), 경찰(추격·포위), 적(교전) — **유틸리티 AI + 상태머신**, 규칙은 `data/ai/`.
- 표현: 원거리 군중은 `MultiMeshInstance3D`, 근거리만 개별 `CharacterBody3D` 노드.
- 인지(시야·소리)는 코어 이벤트 + 필요시 `IPhysicsQuery` 레이캐스트.

### 3.4 수배(Heat)
- 범죄 이벤트(`CrimeCommitted`) → 열기 점수 → 단계(0~5) → 경찰 스폰 규칙. 테이블은 `data/ai/heat.json`.

### 3.5 전투 (핵심 시스템)
게임의 중심. 규칙은 전부 코어, Godot은 조준·표현만.

**무기** — `data/weapons/*.json` (스키마 `weapon.schema.json`): 데미지, 펠릿 수, 부위 배율, 연사 모드·RPM, 탄창·재장전, 거리 감쇠(시작·끝·최소 배율), 탄퍼짐(힙/조준), 반동, 소음 반경.
현재: `kestrel_p10`(권총), `wasp_smg`(SMG), `halberd_ar`(소총), `breaker_12`(산탄총).

**데미지 흐름**
```
Godot: 조준 → 발사 입력 → PlayerCommand.Fire
Core : 탄약·연사 쿨다운 검사 → 탄퍼짐으로 방향 산출 → IPhysicsQuery.Raycast(포트)
       → 히트 부위·거리 → damage × 부위배율 × 감쇠 × (1 - 방어구) → HealthChanged / Died 이벤트
       → 소음 이벤트 → 주변 AI 인지 + Heat
Godot: 이벤트 받아 피격 애니메이션·이펙트·HUD
```

**커버** — 레벨에 커버 지점(`CoverPoint`: 위치, 법선, 높이 low/high)을 배치. 건물·소품 에셋은 커버를 염두에 두고 만든다 (낮은 벽 ~1m, 기둥, 바리케이드).
- 코어: 커버 지점 등록/점유, "이 위치가 위협 방향에서 가려지는가" 판정(레이캐스트 포트).
- Godot: 커버 진입/이동/엿보기 애니메이션.

**적 AI** (`Agents`) — 유틸리티 AI로 행동 선택: `TakeCover`, `Advance`, `Flank`, `Suppress`, `Charge`(근접), `Retreat`, `Reload`. 수치는 `data/ai/`.
| 아키타입 | 모델 | 성향 |
|---|---|---|
| Saltline Crew 브롤러 | `chr_enemy_brawler` | 근접 돌진, 체력 높음, 커버 거의 안 씀 |
| Velvet Hand 건맨 | `chr_enemy_gunman` | 권총/SMG, 커버 사용, 측면 이동 |
| Ironclad 진압요원 | `chr_enemy_enforcer` | 방탄 방패(정면 피해 차단), 천천히 전진, 소총 |

**플레이어** — `chr_player_rook`. 체력 + 방어구 게이지, 무기 슬롯 4개(권총/SMG/소총/산탄총).

### 3.6 미션
- 미션 = 데이터 (`data/missions/*.json`): 시작 조건, 목표 리스트, 실패 조건, 보상, 다음 미션.
- 목표 타입(`GoTo`, `Eliminate`, `Steal`, `Escape`, `Deliver`, `Talk`)은 코어 C# 클래스로 하나씩 추가.
- 스토리 진행은 문자열 플래그 집합 → 세이브 간단.

### 3.7 물리
- Godot 4 내장 Jolt 사용 (프로젝트 설정 `physics/3d/physics_engine = Jolt Physics`).
- 충돌 레이어 목록은 `docs/physics-layers.md`에 유지.

### 3.8 렌더링
- Forward+ 렌더러, SDFGI 또는 VoxelGI(성능 보고 결정), 볼류메트릭 포그.
- 시간·날씨: 코어 `World`가 시간·날씨 값을 내보내면 Godot `WorldEnvironment`가 반영.
- 품질 프리셋 Low/Med/High는 `game/scripts/Settings/`.

### 3.9 세이브
- 코어 `Save`: 각 시스템이 `ISaveable`(키 + 버전) 구현. 파일 = 헤더 + 섹션 맵(JSON, 출시 시 압축).
- 월드는 **변경분만** 저장 (소유 차량, 수집품, 스토리 플래그, 파괴된 오브젝트 ID).
- 포맷 버전 올릴 때 마이그레이션 필수 (`core/src/Harbor.Core/Save/Migrations/`).
- 파일 입출력은 `IFileSystem` 포트로 → 엔진 없이도 동작.

### 3.10 에셋 파이프라인
- 원본 = `tools/blender/build_assets.py` (블렌더 4.2 헤드리스, `bpy`). 산출물 `.glb`는 Git LFS.
- 사람: `human.py` + `characters.py` — 메타볼 몸 → 리메시 → 51본 리그(손가락 포함) 자동 웨이트 → 옷 레이어가 몸 웨이트 상속. 캐릭터당 ~5만 면.
- 캐릭터·총기·건물 → `game/assets/{characters,weapons,buildings}/`. 범용 소품 → **sangsangbaksa-dev/Set** 리포 `props/`.
- 명명: `chr_*`, `wpn_*`, `bldg_*`, `prop_*`. 충돌이 필요한 메시는 이름 끝 `-col`(Godot 임포트가 충돌 생성).
- 미리보기 PNG: `tools/blender/render_preview.py` → `docs/art/`.

### 3.11 네트워킹 (핵심 모듈) — ADR 0004
**서버 권한형 멀티플레이.** 전용 서버는 Godot 없이 `Harbor.Core`만 호스팅하는 .NET 프로세스. 싱글플레이도 로컬 서버 + 클라 1명.

```
 클라이언트(Godot)                              서버(.NET, 엔진 없음)
 입력 → InputCmd(틱, 이동, 조준, 버튼) ──UDP──▶ InputBuffer → SimWorld.Step (30Hz)
 예측: Movement.Step 로컬 적용                    │ 히트 판정(랙 보정 History 되감기)
 보정: 서버 상태 + 미확인 입력 재적용 ◀──Snapshot── │ 경제·의뢰·전투 규칙
 다른 엔티티: 100ms 보간                           └ 관심 영역(청크 반경)별 스냅샷 20Hz
```
- 코어 `Net/`: `ITransport`(포트), `NetWriter/NetReader`(이진 직렬화), `Messages`, `ServerSession`, `ClientSession`, `Prediction`, `Interpolation`, `LagCompHistory`, 테스트용 `InMemoryTransport`(지연·손실).
- `Movement.Step`은 결정적 순수 함수 — 서버·클라가 같은 코드로 이동을 계산.
- 차량 근거리 물리만 예외: 운전자 클라이언트 1차 권한 + 서버 타당성 검사.

### 3.12 경제 — ADR 0005
- 두 화폐(더러운 돈/깨끗한 돈), 계정(지갑·은행·금고·크루·사업체·시스템), **이중 기입 원장 + 멱등 TxId**. 서버 전용.
- 데이터: `data/economy/*.json` (가격, 세탁 수수료, 드롭 비율, 보상 감쇠).

### 3.13 활동
- 의뢰·습격·구역전·보급·현상금. 데이터 `data/activities/`, 코어 `Activities/`. 기획: `docs/CONTENT_DESIGN.md`.

---

## 4. 디렉터리

```
/
├─ ARCHITECTURE.md
├─ CLAUDE.md                 ← Claude 작업 규칙
├─ core/
│  ├─ src/Harbor.Core/        ← 엔진 독립 게임 로직 (Godot 참조 금지)
│  └─ tests/Harbor.Core.Tests/
├─ game/                     ← Godot 프로젝트 (project.godot)
│  ├─ scripts/               ← C# (Bridge, Adapters, Streaming, Views, Player, Vehicles, UI)
│  ├─ scenes/
│  └─ assets/                ← 바이너리 에셋 (Git LFS): characters/ weapons/ buildings/
├─ data/                     ← JSON 게임 데이터 + schema/
├─ docs/
│  ├─ adr/
│  └─ lore/                  ← 오리지널 세계관
└─ tools/
   └─ blender/               ← 에셋 생성 스크립트
```
소품: 별도 리포 `sangsangbaksa-dev/Set` (`props/`).

---

## 5. 로드맵

| 단계 | 목표 | 완료 기준 |
|---|---|---|
| M0 | 스켈레톤 | 코어 빌드·테스트 통과, Godot에서 빈 씬 + CoreHost 틱 |
| M1 | 걷기 + 운전 | 3인칭 캐릭터, 차량 1종, 테스트 구역 1km² |
| M2 | 전투 | 무기 4종, 커버 시스템, 적 아키타입 3종, 전투 테스트 맵(창고·주차장) |
| M3 | 스트리밍 | 청크 로딩/언로드, 원점 이동, 4km² |
| M4 | 살아있는 거리 + 수배 | 보행자·교통 에이전트, District 1개, Heat 3단계, 경찰 추격 |
| M5 | 미션 + 세이브 | JSON 미션 3개, 세이브/로드 |

멀티플레이·경제 트랙 (기획: `docs/CONTENT_DESIGN.md` §10) — 코어+테스트만으로 완결, Godot 연결은 G1:

| 단계 | 목표 |
|---|---|
| N1 | 네트워크 기반: 전송 포트, 직렬화, 서버/클라 세션, 예측·보정, 보간 |
| C1 | 전투 규칙: 방어구·부위·감쇠·관통·폭발·다운, 랙 보정 히트 판정 |
| E1 | 경제: 원장, 지갑/은행/크루 금고, 사망 드롭, 상점, 세탁 |
| A1 | 활동: 의뢰·습격 운반/분배·구역·보급·현상금, 보상 감쇠 |
| G1 | Godot 연결: ENet 어댑터, 예측 표현, HUD |
| S1 | 전용 서버 호스트 + 프로필 저장소 |

---

## 6. 변경 규칙
- 구조 변경(모듈 추가, 의존 방향, 외부 라이브러리 도입) → `docs/adr/NNNN-*.md`.
- 데이터 포맷 변경 → `data/schema/` 갱신.
- 세이브 포맷 변경 → 버전 증가 + 마이그레이션.
- 코어에 새 엔진 기능이 필요하면 → 포트 인터페이스 추가, 직접 호출 금지.
