# ADR 0006: 활동 = Missions 실행기 + Activities 규칙 + Heat

- 상태: 채택 (2026-11-24)

## 맥락
기획 §6(의뢰·습격·구역·보급·현상금)을 데이터로 계속 늘려야 하고, 싱글 스토리 미션(M5)도 같은 목표 타입을 쓴다. 현상금·의뢰는 수배 레벨이 필요한데 Heat는 아직 스켈레톤이었다.

## 결정
- **Missions**: 보상을 모르는 범용 실행기. 목표 타입 5개(reach/interact/hold/escape/deliver)와 운반 아이템(`CarrySystem`). 끝나면 `MissionEnded`만 발행.
- **Activities**: 멀티 활동 규칙. 의뢰 정의(JSON 한 파일 = 의뢰 하나)를 Missions로 돌리고, 결과를 Economy 원장으로 정산. 습격 몫은 에스크로 계정 → 크루장 분배.
- **Heat**: 액터별 열기 → 레벨. 범죄 보고는 GameServer(발사·살인)와 목표의 `crime` 필드. Heat·Activities는 Combat를 직접 참조하지 않는다 — 전투 이벤트는 GameServer가 옮긴다.
- 운반 중 무기 제한은 Combat 규칙(`carryAllowedClasses`)으로, 플래그(`ActorFlags.Carrying`)만 공유.
- 결정론: 사이트 순환·준비 손실 선택은 고정 공식(무작위 없음) → 서버 재현·테스트 가능.

## 결과
- 의존: `Activities → Missions, Economy, Heat`. `Missions → Sim`. 규칙 모듈은 Net을 모른다.
- 새 의뢰는 데이터만, 새 목표 타입은 Missions에 case 하나.
- NPC 개입(Ironclad 분대, 구역 NPC 공격, 경찰 출동)은 Agents가 생기면 이벤트로 붙인다(미구현).
