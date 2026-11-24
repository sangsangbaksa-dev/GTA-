# ARCHITECTURE

> 코드네임 **Project Harbor** — 가상의 해안 도시 **Port Calloway**를 무대로 한 오리지널 오픈월드 액션 게임.
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

상세: [`docs/adr/0001-engine-godot.md`](docs/adr/0001-engine-godot.md), [`docs/adr/0002-portable-core.md`](docs/adr/0002-portable-core.md)

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
| `Harbor.Core.Combat` | 체력·데미지·무기 규칙, 범죄 판정 |
| `Harbor.Core.Heat` | 수배 레벨, 경찰 응답 규칙 |
| `Harbor.Core.Missions` | 데이터 기반 미션 실행기, 목표 타입, 스토리 플래그 |
| `Harbor.Core.Save` | 세이브 모델, 버전·마이그레이션, 직렬화 |

> 처음엔 하나의 `Harbor.Core` 어셈블리 안에 네임스페이스로 나누고, 커지면 위 표대로 분리한다. 의존 방향은 처음부터 지킨다.

**코어 내부 의존 방향** (아래로만)
```
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
| `game/scenes/`, `game/assets/` | 씬·에셋 |

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

### 3.5 전투
- 코어: 체력·방어·데미지 계산, 무기 스펙(`data/weapons/*.json`), 탄약.
- Godot: 조준, 히트 판정용 레이캐스트(결과를 커맨드로 코어에 전달), 이펙트.

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

### 3.10 네트워킹
- **Phase 1: 싱글플레이.** 단, 코어가 "커맨드 입력 → 고정 틱 → 상태"라서 나중에 서버 권한형 멀티로 확장하기 좋다.
- 멀티 도입은 별도 ADR.

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
│  └─ assets/                ← 바이너리 에셋 (Git LFS)
├─ data/                     ← JSON 게임 데이터 + schema/
├─ docs/
│  ├─ adr/
│  └─ lore/                  ← 오리지널 세계관
└─ tools/
```

---

## 5. 로드맵

| 단계 | 목표 | 완료 기준 |
|---|---|---|
| M0 | 스켈레톤 | 코어 빌드·테스트 통과, Godot에서 빈 씬 + CoreHost 틱 |
| M1 | 걷기 + 운전 | 3인칭 캐릭터, 차량 1종, 테스트 구역 1km² |
| M2 | 스트리밍 | 청크 로딩/언로드, 원점 이동, 4km² |
| M3 | 살아있는 거리 | 보행자·교통 에이전트, District 1개 |
| M4 | 전투 + 수배 | 무기 2종, Heat 3단계, 경찰 추격 |
| M5 | 미션 + 세이브 | JSON 미션 3개, 세이브/로드 |

---

## 6. 변경 규칙
- 구조 변경(모듈 추가, 의존 방향, 외부 라이브러리 도입) → `docs/adr/NNNN-*.md`.
- 데이터 포맷 변경 → `data/schema/` 갱신.
- 세이브 포맷 변경 → 버전 증가 + 마이그레이션.
- 코어에 새 엔진 기능이 필요하면 → 포트 인터페이스 추가, 직접 호출 금지.
