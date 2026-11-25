# Server

`GameServer` = 컴포지션 루트. Net과 규칙 모듈(Combat·Economy·Activities)을 아는 유일한 계층.

- 접속 → 프로필/액터 생성, 세이프하우스 스폰, 잔고 전송. 이탈 → 로그아웃 세금.
- `Requests.cs` — 클라 요청 타입과 코덱. 모든 요청은 nonce를 가지며 TxId로 변환돼 중복 과금 방지.
- `WireEvents.cs` — 서버→클라 이벤트 id와 인코더.
- 규칙 이벤트(EventBus) → 관련 클라에 와이어 이벤트 릴레이.

## 확장 포인트
- 새 요청: `RequestType`에 상수 추가 → `GameServer` dispatch, 또는 `ExtraRequest` 훅.
- 잘못된 페이로드는 거부만 하고 서버는 계속 돈다(테스트로 보장).
