# Missions

데이터 기반 미션 실행기. 보상을 모른다 — 소유자(Activities, 싱글 스토리)가 `MissionEnded`를 구독한다.

- `ObjectiveDef` 타입: `reach`, `interact`(홀드, 피격 시 일시정지/리셋), `hold`(구역 방어), `escape`(거리 이탈), `deliver`(운반 아이템 배달).
- `MissionRunner` — 파티, 제한시간, 실패 사유(`timeout`/`wiped`/`abandoned`), 진행률 10% 단위 이벤트.
- `CarrySystem` — 손으로 드는 아이템(현금 가방 등). 들면 `ActorFlags.Carrying`(감속·달리기 불가·권총만). 다운/사망 시 떨어뜨리고 누구나 주울 수 있다.
- `InteractInput` — 호스트가 매 입력 틱 상호작용 버튼 상태를 넣는다(hold/press).

## 확장 포인트
- 새 목표 타입: `ObjectiveType`에 추가 → `MissionRunner.StepObjective`에 case → `ObjectiveDef.Validate`.
- 목표가 범죄면 `crime` 필드로 Heat에 보고된다.
