# Activities

멀티플레이 활동 (CONTENT_DESIGN §6). 규칙 모듈 — 네트워크를 모르고 `Server/GameServer`가 입력·요청을 넣고 이벤트를 클라에 전달한다.

| 클래스 | 내용 | 데이터 |
|---|---|---|
| `ContractBoard` | 의뢰 시작·보상(남은 시간 비례, 균등 분배, 반복 감쇠), 습격 준비/피날레, 가방 → 에스크로 → 크루장 분배(최소 15%) | `data/activities/contracts/*.json`, `heists.json` |
| `TurfWar` | 구역 점유(단독 크루 60초), 시간당 수입 → 크루 금고, 공격 창(사전 공지), 오프라인 보호, 상점 할인 | `turf.json` |
| `SupplyDrops` | 공지 → 낙하 → 홀드로 개봉(피격 시 취소) → 현금+물자 | `supply_drops.json` |
| `Bounties` | 수배 4+ 60초 / 반복 살해 → 지갑 20% 압수 + 기본금, 사냥꾼은 깨끗한 돈 | `economy.json` bounty |

- 돈은 전부 원장 경유. 습격 몫은 `escrow:heist:<run>`, 현상금 압수분은 `escrow:bounty:<id>`.
- 바쁜 플레이어(`IsBusy`)는 수동 모드를 켤 수 없다.

## 확장 포인트
- 새 의뢰: JSON 한 파일 추가(코드 변경 없음). 목표 타입은 `Missions/`.
- NPC 세력 개입(보급 상자 Ironclad 분대, 구역 NPC 공격)은 Agents 구현 후 이벤트 구독으로 붙인다.
